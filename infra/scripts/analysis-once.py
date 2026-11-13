"""Set up, publish, import new sessions, verify delivery and remove runtime.

Run with no arguments from any directory after Azure CLI login. Existing
infra/terraform.tfvars selects the deployment. Backend recovery/migration and
image publication are idempotent; captured source storage is never changed.
"""
import argparse
import base64
import datetime as dt
import fcntl
import hashlib
import json
import os
import re
import shutil
import signal
import sqlite3
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
import sqlite_export
from importlib import util
spec = util.spec_from_file_location("plan_guard", Path(__file__).with_name("check-analysis-plan.py"))
guard = util.module_from_spec(spec)
spec.loader.exec_module(guard)

ROOT = Path(__file__).resolve().parents[2]
INFRA = ROOT / "infra"
PRIVATE = INFRA / '.analysis'


def private_write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    if PRIVATE in path.parents:
        path.parent.chmod(0o700)
    with tempfile.NamedTemporaryFile(mode='w', dir=path.parent, delete=False) as output:
        output.write(value)
        output.flush()
        os.fsync(output.fileno())
        temporary = Path(output.name)
    temporary.chmod(0o600)
    temporary.replace(path)


def read_variable(path, name, default=None):
    text = path.read_text()
    if path.suffix == '.json':
        return json.loads(text).get(name, default)
    match = re.search(r'^\s*' + re.escape(name) + r'\s*=\s*("(?:[^"\\]|\\.)*")\s*(?:#.*)?$', text, re.M)
    if match:
        return json.loads(match[1])
    if re.search(r'^\s*' + re.escape(name) + r'\s*=', text, re.M):
        raise RuntimeError('Use a literal string for ' + name + ' in the selected tfvars')
    return default


def set_variables(path, values):
    text = path.read_text()
    if path.suffix == '.json':
        document = json.loads(text)
        document.update(values)
        private_write(path, json.dumps(document, indent=2) + '\n')
        return
    for name, value in values.items():
        line = name + ' = ' + json.dumps(value)
        pattern = r'^\s*' + re.escape(name) + r'\s*=.*$'
        text, count = re.subn(pattern, lambda _: line, text, flags=re.M)
        if count > 1:
            raise RuntimeError('Duplicate variable: ' + name)
        if not count:
            text = text.rstrip() + '\n' + line + '\n'
    private_write(path, text)


def snapshot(path):
    if path.exists():
        target = PRIVATE / 'backups' / (str(time.time_ns()) + '-' + path.name)
        private_write(target, path.read_text())


def validate_state(document, subscription):
    if not document.get('lineage') or not document.get('resources'):
        raise RuntimeError('No authoritative existing Terraform state was found')
    for kind, name in [('azurerm_resource_group', 'main'), ('azurerm_linux_virtual_machine', 'honeypot')]:
        resource = next((r for r in document['resources'] if r['type'] == kind and r['name'] == name), None)
        if not resource or len(resource['instances']) != 1:
            raise RuntimeError('State has no unambiguous live deployment anchor: ' + kind)
        identity = resource['instances'][0]['attributes']['id']
        if not identity.lower().startswith('/subscriptions/' + subscription.lower() + '/'):
            raise RuntimeError('State belongs to a different subscription')
        actual = command(['az', 'resource', 'show', '--ids', identity, '--query', 'id', '-o', 'tsv'], True).strip()
        if actual.lower() != identity.lower():
            raise RuntimeError('State anchor does not match Azure')


def recover_local_state(subscription):
    state = INFRA / 'terraform.tfstate'
    if state.exists() and state.stat().st_size:
        document = json.loads(state.read_text())
        validate_state(document, subscription)
        return document
    backup = INFRA / 'terraform.tfstate.backup'
    if not backup.exists() or not backup.stat().st_size:
        raise RuntimeError('Neither a nonempty local state nor its backup exists; refusing a new empty deployment')
    document = json.loads(backup.read_text())
    validate_state(document, subscription)
    snapshot(state)
    snapshot(backup)
    with state.open('a+') as output:
        fcntl.flock(output, fcntl.LOCK_EX | fcntl.LOCK_NB)
        output.seek(0)
        if output.read().strip():
            raise RuntimeError('Local state changed during recovery; stop and inspect it')
        output.seek(0)
        output.truncate()
        output.write(json.dumps(document))
        output.flush()
        os.fsync(output.fileno())
        state.chmod(0o600)
    print('Recovered the empty local state from its validated, preserved backup.', flush=True)
    return document


def prepare_backend(var_file):
    subscription = str(uuid.UUID(read_variable(var_file, 'subscription_id')))
    account = json.loads(command(['az', 'account', 'show', '-o', 'json'], True))
    if account['id'].lower() != subscription.lower():
        raise RuntimeError('Azure CLI subscription differs from terraform.tfvars; select the intended dedicated subscription')
    metadata_path = INFRA / '.terraform/terraform.tfstate'
    metadata = json.loads(metadata_path.read_text()) if metadata_path.exists() else {}
    initialized = metadata.get('backend', {}).get('type') == 'azurerm'
    backend_file = INFRA / 'backend.hcl'
    if backend_file.exists():
        settings = {key: read_variable(backend_file, key) for key in
                    ('resource_group_name', 'storage_account_name', 'container_name', 'key')}
    elif initialized:
        config = metadata['backend']['config']
        settings = {key: config[key] for key in
                    ('resource_group_name', 'storage_account_name', 'container_name', 'key')}
    else:
        prefix = read_variable(var_file, 'name_prefix', 'telemoq')
        suffix = hashlib.sha256((subscription + '/' + prefix).encode()).hexdigest()[:10]
        settings = dict(resource_group_name='rg-' + prefix + '-tfstate',
                        storage_account_name=prefix + 'tf' + suffix, container_name='tfstate', key='telemoqnet.tfstate')
    if not all(settings.values()):
        raise RuntimeError('Backend configuration is incomplete')
    storage, group = settings['storage_account_name'], settings['resource_group_name']
    location = read_variable(var_file, 'location', 'westeurope')
    if not initialized:
        # Resolve and validate local state before creating any paid resources.
        recover_local_state(subscription)
    print('Preparing the separate Entra-authenticated Terraform backend.', flush=True)
    command(['az', 'group', 'create', '-n', group, '-l', location, '--tags', 'project=telemoqnet',
             'purpose=terraform-state', '-o', 'none'])
    accounts = json.loads(command(['az', 'storage', 'account', 'list', '-g', group, '-o', 'json'], True))
    existing = next((x for x in accounts if x['name'] == storage), None)
    if not existing:
        command(['az', 'storage', 'account', 'create', '-g', group, '-n', storage, '-l', location,
                 '--sku', 'Standard_LRS', '--kind', 'StorageV2', '--https-only', 'true',
                 '--min-tls-version', 'TLS1_2', '--allow-blob-public-access', 'false',
                 '--allow-shared-key-access', 'false',
                 '--tags', 'project=telemoqnet', 'purpose=terraform-state', '-o', 'none'])
    else:
        if existing.get('allowSharedKeyAccess') is not False or existing.get('allowBlobPublicAccess') is not False:
            raise RuntimeError('Existing backend must disable shared keys and anonymous Blob access')
    command(['az', 'storage', 'account', 'blob-service-properties', 'update', '-g', group, '-n', storage,
             '--enable-versioning', 'true', '--enable-delete-retention', 'true', '--delete-retention-days', '7',
             '--enable-container-delete-retention', 'true', '--container-delete-retention-days', '7', '-o', 'none'])
    command(['az', 'storage', 'container-rm', 'create', '-g', group, '--storage-account', storage,
             '-n', settings['container_name'], '--public-access', 'off', '-o', 'none'])
    # Obtain only the current caller's object ID; never print or persist its token.
    token = command(['az', 'account', 'get-access-token', '--resource', 'https://management.azure.com/',
                     '--query', 'accessToken', '-o', 'tsv'], True).strip()
    payload = token.split('.')[1]
    principal = str(uuid.UUID(json.loads(base64.urlsafe_b64decode(payload + '=' * (-len(payload) % 4)))['oid']))
    scope = '/subscriptions/' + subscription + '/resourceGroups/' + group + '/providers/Microsoft.Storage/storageAccounts/' + storage
    assignments = json.loads(command(['az', 'role', 'assignment', 'list', '--scope', scope,
        '--query', "[?principalId=='" + principal + "' && roleDefinitionName=='Storage Blob Data Contributor']", '-o', 'json'], True))
    if not assignments:
        command(['az', 'role', 'assignment', 'create', '--assignee-object-id', principal,
            '--assignee-principal-type', 'User' if account['user']['type'] == 'user' else 'ServicePrincipal',
            '--scope', scope, '--role', 'Storage Blob Data Contributor',
            '--name', str(uuid.uuid5(uuid.NAMESPACE_URL, scope + principal + '/state')), '-o', 'none'])
    snapshot(backend_file)
    private_write(backend_file, ''.join(key + ' = ' + json.dumps(value) + '\n' for key, value in settings.items()) + 'use_azuread_auth = true\n')
    blob_args = ['--auth-mode', 'login', '--account-name', storage, '--container-name', settings['container_name'],
                 '--name', settings['key']]
    for attempt in range(18):
        try:
            exists = json.loads(command(['az', 'storage', 'blob', 'exists', *blob_args, '-o', 'json'], True))['exists']
            break
        except subprocess.CalledProcessError:
            if attempt == 17:
                raise
            print('Waiting for backend role propagation...', flush=True)
            time.sleep(10)
    if exists and not initialized:
        with tempfile.TemporaryDirectory(dir=PRIVATE) as directory:
            target = Path(directory) / 'remote-state.json'
            command(['az', 'storage', 'blob', 'download', *blob_args, '--file', str(target), '-o', 'none'])
            remote = json.loads(target.read_text())
            validate_state(remote, subscription)
            local = json.loads((INFRA / 'terraform.tfstate').read_text())
            if remote['lineage'] != local['lineage']:
                raise RuntimeError('Existing remote and local state have different lineages; refusing to overwrite either')
            if remote.get('serial', 0) < local.get('serial', 0):
                raise RuntimeError('Remote state is older than the validated local state; refusing to discard newer local changes')
        mode = '-reconfigure'
    elif initialized:
        mode = '-reconfigure'
    else:
        mode = '-force-copy'
    for attempt in range(6):
        try:
            terraform('init', '-input=false', '-lockfile=readonly', mode, '-backend-config=' + str(backend_file))
            break
        except subprocess.CalledProcessError:
            if attempt == 5:
                raise
            # Migration can copy the state successfully before an eventually
            # consistent RBAC check fails. Never recopy an empty local state.
            present = json.loads(command(['az', 'storage', 'blob', 'exists', *blob_args, '-o', 'json'], True))['exists']
            if present:
                mode = '-reconfigure'
            elif not (INFRA / 'terraform.tfstate').stat().st_size:
                raise RuntimeError('Backend initialization failed before a verifiable state copy; preserved backups are available')
            print('Waiting for backend authorization to settle before retrying initialization...', flush=True)
            time.sleep(15)
    document = json.loads(terraform('state', 'pull', capture=True))
    validate_state(document, subscription)
    return document


def publish_importer(state):
    outputs = state['outputs']
    registry = outputs['acr_name']['value']
    server = outputs['acr_login_server']['value']
    source = ROOT / 'tools/session-importer'
    digest = hashlib.sha256((source / 'Dockerfile').read_bytes() + (source / 'importer.py').read_bytes()).hexdigest()
    tag = 'telemoqnet-session-importer:source-' + digest[:24]
    result = subprocess.run(['az', 'acr', 'repository', 'show', '-n', registry, '--image', tag,
                             '--query', 'digest', '-o', 'tsv'], text=True, capture_output=True)
    if result.returncode == 0:
        image_digest = result.stdout.strip()
        print('Reusing the importer image for this exact source version.', flush=True)
    else:
        print('Building and publishing only the importer (linux/amd64).', flush=True)
        command(['docker', 'info', '--format', '{{.OSType}}'])
        command(['az', 'acr', 'login', '-n', registry])
        # Upload/build only these two known files, never state, credentials or captures.
        with tempfile.TemporaryDirectory(prefix='session-importer-build-') as directory:
            context = Path(directory)
            relative = context / 'tools/session-importer'
            relative.mkdir(parents=True)
            shutil.copy2(source / 'importer.py', relative / 'importer.py')
            shutil.copy2(source / 'Dockerfile', context / 'Dockerfile')
            command(['docker', 'build', '--platform', 'linux/amd64', '-t', server + '/' + tag, str(context)])
            command(['docker', 'push', server + '/' + tag])
        image_digest = command(['az', 'acr', 'repository', 'show', '-n', registry, '--image', tag,
                                '--query', 'digest', '-o', 'tsv'], True).strip()
    if not re.fullmatch(r'sha256:[a-f0-9]{64}', image_digest):
        raise RuntimeError('Registry did not return a valid immutable image digest')
    return image_digest


def setup(var_file):
    for executable in ('az', 'terraform', 'docker'):
        if not shutil.which(executable):
            raise RuntimeError('Required executable is missing: ' + executable)
    PRIVATE.mkdir(parents=True, exist_ok=True)
    PRIVATE.chmod(0o700)
    # Serialize local recovery/bootstrap before the remote lifecycle lease exists.
    with (PRIVATE / 'setup.lock').open('a+') as lock:
        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
        state = prepare_backend(var_file)
        image_digest = publish_importer(state)
        snapshot(var_file)
        set_variables(var_file, dict(analysis_enabled=True, analysis_runtime_enabled=False,
                                    analysis_auto_ingest_enabled=False, analysis_image_digest=image_digest))
        terraform('fmt', str(var_file), capture=True)
    print('Backend and immutable importer image are ready.', flush=True)


def command(args, capture=False, env=None):
    # Read retries are safe even if a response was lost. Never automatically
    # retry job starts or other mutations with an ambiguous outcome here.
    reads = [('account', 'show'), ('resource', 'show'), ('resource', 'list'), ('group', 'exists'),
             ('storage', 'account', 'list'), ('storage', 'blob', 'exists'), ('storage', 'blob', 'show'),
             ('storage', 'blob', 'download'), ('role', 'assignment', 'list'),
             ('containerapp', 'env', 'show'), ('containerapp', 'job', 'show'),
             ('containerapp', 'job', 'execution', 'list'), ('containerapp', 'job', 'logs', 'show')]
    safe_read = args[0] == 'az' and any(tuple(args[1:1 + len(prefix)]) == prefix for prefix in reads)
    for attempt in range(4 if safe_read else 1):
        result = subprocess.run(args, check=False, text=True,
                                stdout=subprocess.PIPE if capture else None, stderr=subprocess.PIPE, env=env)
        if result.returncode == 0:
            if result.stderr:
                sys.stderr.write(result.stderr)
            return result.stdout if capture else None
        error = subprocess.CalledProcessError(result.returncode, args, result.stdout, result.stderr)
        if not safe_read or not transient_failure(error) or attempt == 3:
            raise error
        print('Transient Azure read failure; retrying.', flush=True)
        time.sleep(2 ** attempt)


def transient_failure(error):
    message = (getattr(error, 'stderr', '') or '').lower()
    return any(text in message for text in ('connection reset', 'connectionreseterror',
        'connection aborted', 'response was nil', 'context deadline exceeded',
        'temporarily unavailable', 'serviceunavailable', 'unexpected eof', 'tls handshake timeout'))


def terraform(*args, capture=False):
    return command(["terraform", "-chdir=" + str(INFRA), *args], capture)


class ControlLock:
    """Lease a separate blob in the authoritative backend for the full lifecycle.

    An infinite lease prevents long provisioning from outliving its lock. Hard
    kills require deliberate recovery after checking for active executions.
    """
    def __enter__(self):
        metadata = json.loads((INFRA / '.terraform/terraform.tfstate').read_text())
        backend = metadata.get('backend', {})
        config = backend.get('config', {})
        if backend.get('type') != 'azurerm':
            raise RuntimeError('Migrate the authoritative state to AzureRM before running this operation')
        self.args = ['--auth-mode', 'login', '--account-name', config['storage_account_name'],
                     '--container-name', config['container_name'], '--blob-name',
                     config['key'] + '.analysis-operation.lock']
        blob_args = ['--auth-mode', 'login', '--account-name', config['storage_account_name'],
                     '--container-name', config['container_name'], '--name',
                     config['key'] + '.analysis-operation.lock']
        exists = json.loads(command(['az', 'storage', 'blob', 'exists', *blob_args, '-o', 'json'], True))
        if not exists['exists']:
            with tempfile.NamedTemporaryFile(mode='w') as empty:
                empty.write('{}')
                empty.flush()
                try:
                    command(['az', 'storage', 'blob', 'upload', *blob_args, '--file', empty.name,
                             '--overwrite', 'false', '--if-none-match', '*', '-o', 'none'])
                except subprocess.CalledProcessError:
                    if not json.loads(command(['az', 'storage', 'blob', 'exists', *blob_args, '-o', 'json'], True))['exists']:
                        raise
        self.lease = str(uuid.uuid4())
        command(['az', 'storage', 'blob', 'lease', 'acquire', *self.args,
                 '--lease-duration', '-1', '--proposed-lease-id', self.lease, '-o', 'none'])
        return self

    def __exit__(self, *unused):
        command(['az', 'storage', 'blob', 'lease', 'release', *self.args,
                 '--lease-id', self.lease, '-o', 'none'])


def apply_runtime(enabled, var_file):
    for attempt in range(3 if not enabled else 1):
        try:
            return apply_runtime_plan(enabled, var_file)
        except subprocess.CalledProcessError as error:
            if enabled or attempt == 2 or not transient_failure(error):
                raise
            # A failed apply may already have removed resources. Generate a new
            # guarded plan against current state instead of replaying a stale plan.
            print('Transient cleanup failure; replanning remaining analysis resources.', flush=True)
            time.sleep(2 ** attempt)


def apply_runtime_plan(enabled, var_file):
    with tempfile.TemporaryDirectory(prefix="analysis-plan-") as directory:
        plan = str(Path(directory) / "runtime.tfplan")
        terraform("plan", "-input=false", "-lock-timeout=60s", "-var-file=" + var_file,
                  "-var=analysis_enabled=true", "-var=analysis_auto_ingest_enabled=false",
                  "-var=analysis_runtime_enabled=" + str(enabled).lower(), "-out=" + plan, capture=True)
        document = json.loads(terraform("show", "-json", plan, capture=True))
        guard.check(document, runtime_only=True)
        if enabled and not any(r['type'] == 'azurerm_container_app_job' and r['name'] == 'analysis'
                for r in document.get('planned_values', {}).get('root_module', {}).get('resources', [])):
            raise RuntimeError('A published analysis_image_digest is required before creating billable runtime')
        changes = [r for r in document.get('resource_changes', []) if r['change']['actions'] != ['no-op']]
        print(('Creating' if enabled else 'Removing') + ' analysis runtime: ' + str(len(changes)) + ' guarded resource changes.', flush=True)
        terraform("apply", "-input=false", plan)


def refresh_state(var_file):
    """Reconcile old backup entries with ARM without changing Azure resources."""
    with tempfile.TemporaryDirectory(prefix='analysis-refresh-') as directory:
        plan = str(Path(directory) / 'refresh.tfplan')
        terraform('plan', '-refresh-only', '-input=false', '-lock-timeout=60s',
                  '-var-file=' + var_file, '-out=' + plan, capture=True)
        terraform('apply', '-input=false', plan, capture=True)
        print('Terraform state reconciled with the existing deployment.', flush=True)


def recover_pending_cleanup(var_file):
    state = json.loads(terraform('state', 'pull', capture=True))
    environments = [r for r in state.get('resources', [])
                    if r['type'] == 'azurerm_container_app_environment' and r['name'] == 'analysis']
    for resource in environments:
        for instance in resource.get('instances', []):
            values = instance['attributes']
            status = command(['az', 'containerapp', 'env', 'show', '-g', values['resource_group_name'],
                '-n', values['name'], '--query', 'properties.provisioningState', '-o', 'tsv'], True).strip()
            if status == 'ScheduledForDelete':
                print('Resuming Azure cleanup from the previous run before creating new runtime.', flush=True)
                apply_runtime(False, var_file)
                group = values.get('infrastructure_resource_group_name')
                if group:
                    wait_network_removed(group)


def wait_execution(group, job, execution, seconds=720):
    deadline = time.monotonic() + seconds
    last_status = None
    while time.monotonic() < deadline:
        executions = json.loads(command(["az", "containerapp", "job", "execution", "list",
            "-g", group, "-n", job, "-o", "json"], True))
        match = next((x for x in executions if x["name"] == execution), None)
        status = (match or {}).get("properties", {}).get("status", "Pending")
        if status != last_status:
            print('Execution ' + execution + ': ' + status, flush=True)
            last_status = status
        if status == "Succeeded":
            return
        if status in ("Failed", "Stopped", "Degraded"):
            raise RuntimeError("Importer execution " + status)
        time.sleep(10)
    raise RuntimeError("Importer execution did not finish before the operator timeout")


def wait_network_removed(group, seconds=300):
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        exists = command(['az', 'group', 'exists', '--name', group, '-o', 'tsv'], True).strip().lower()
        if exists == 'false':
            return
        time.sleep(10)
    raise RuntimeError('Managed resource group still exists after runtime removal; inspect Azure cleanup and charges')


def validate_workbook(analysis):
    """Compile deployed panels against synthetic datatables, never captured data."""
    url = analysis['workbook_id'] + '?api-version=2022-04-01&canFetchContent=true'
    workbook = json.loads(command(['az', 'rest', '--method', 'get', '--url', url, '-o', 'json'], True))
    content = workbook['properties'].get('serializedData')
    if not content:
        raise RuntimeError('Azure did not return the deployed Workbook content for validation')
    with tempfile.TemporaryDirectory(prefix='analysis-workbook-') as directory:
        path = Path(directory) / 'workbook.json'
        private_write(path, content)
        command([sys.executable, '-B', str(ROOT / 'tools/session-importer/validate_queries.py'),
                 analysis['workspace_customer_id'], str(path)])


def operation_result(group, job, execution, operation):
    """Read only our verification event; importer logs contain no payloads."""
    output = command(['az', 'containerapp', 'job', 'logs', 'show', '-g', group, '-n', job,
        '--execution', execution, '--container', 'session-importer', '--tail', '50'], True,
        dict(os.environ, AZURE_EXTENSION_USE_DYNAMIC_INSTALL='yes_without_prompt'))
    decoder = json.JSONDecoder()
    for line in output.splitlines():
        try:
            line = json.loads(line).get('Log', line)
        except (ValueError, AttributeError):
            pass  # Some CLI versions do not escape the embedded Log JSON.
        position = line.find('{"event":')
        if position < 0:
            continue
        try:
            event, _ = decoder.raw_decode(line[position:])
        except ValueError:
            continue
        if event.get('event') == 'delivery_verified' and event.get('operationId') == operation:
            return event
    raise RuntimeError('Verification succeeded but its bounded result could not be read; inspect the execution logs')


def download_sqlite(args):
    analysis = json.loads(terraform('output', '-json', 'analysis', capture=True))
    if not analysis or not analysis.get('workspace_customer_id'):
        raise RuntimeError('An existing analysis workspace is required for SQLite export')
    sqlite_export.export_sqlite(analysis['workspace_customer_id'], args.sqlite, args.start, args.end)


def main():
    signal.signal(signal.SIGTERM, lambda *_: (_ for _ in ()).throw(RuntimeError("Operation interrupted; cleaning up runtime")))
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--var-file", default=str(INFRA / 'terraform.tfvars'), help="Existing deployment settings (defaults to infra/terraform.tfvars)")
    parser.add_argument("--skip-setup", action="store_true", help="Use an already configured backend and published image")
    parser.add_argument("--start", type=dt.date.fromisoformat)
    parser.add_argument("--end", type=dt.date.fromisoformat)
    parser.add_argument("--lookback-days", type=int, default=7)
    parser.add_argument("--max-executions", type=int, default=10, help="Maximum bounded batches (stops early after a complete scan)")
    parser.add_argument("--dry-run-only", action="store_true")
    parser.add_argument("--cleanup-only", action="store_true", help="Finish a prior teardown without building an image or importing again")
    parser.add_argument('--sqlite', nargs='?', const=str(ROOT / 'data/sessions.sqlite'), metavar='PATH',
                        help='Download a SQLite database after import and cleanup (default: data/sessions.sqlite)')
    parser.add_argument('--sqlite-only', action='store_true',
                        help='Export already imported data with --sqlite, without setup, imports or runtime changes')
    args = parser.parse_args()
    if not 1 <= args.lookback_days <= 31 or not 1 <= args.max_executions <= 10:
        parser.error("lookback must be 1..31 days and max-executions must be 1..10")
    end = args.end or dt.datetime.now(dt.timezone.utc).date()
    start = args.start or end - dt.timedelta(days=args.lookback_days - 1)
    if not 0 <= (end - start).days <= 30:
        parser.error("date range must be inclusive and at most 31 days")
    args.start, args.end = start, end
    if args.cleanup_only and args.dry_run_only:
        parser.error('--cleanup-only cannot be combined with --dry-run-only')
    if args.sqlite_only and not args.sqlite:
        parser.error('--sqlite-only requires --sqlite [PATH]')
    if args.sqlite and (args.cleanup_only or args.dry_run_only):
        parser.error('--sqlite cannot be combined with --cleanup-only or --dry-run-only')
    if args.sqlite and Path(args.sqlite).expanduser().exists():
        parser.error('SQLite destination already exists; choose a new filename')
    if args.sqlite_only:
        download_sqlite(args)
        return
    args.var_file = str(Path(args.var_file).resolve(strict=True))
    if not args.skip_setup and not args.cleanup_only:
        setup(Path(args.var_file))
    with ControlLock():
        previous = json.loads(terraform('output', '-json', 'analysis', capture=True)) if args.cleanup_only else None
        refresh_state(args.var_file)
        if args.cleanup_only:
            apply_runtime(False, args.var_file)
            if previous and previous.get('managed_resource_group'):
                wait_network_removed(previous['managed_resource_group'])
            print('Analysis cleanup complete; retained data and monitoring remain available.', flush=True)
            return
        recover_pending_cleanup(args.var_file)
        execute(args)
    if args.sqlite:
        download_sqlite(args)


def execute(args):
    start, end = args.start, args.end
    var_file = str(Path(args.var_file).resolve(strict=True))
    state = terraform("state", "list", capture=True).splitlines()
    if "azurerm_linux_virtual_machine.honeypot" not in state:
        raise RuntimeError("Authoritative existing state is required; refusing an empty/new deployment")
    if any(x.startswith(("azurerm_container_app_job.analysis", "azurerm_container_app_environment.analysis")) for x in state):
        raise RuntimeError("Runtime already exists. Use Portal Run now/start-analysis.sh, or remove it deliberately first")
    group = terraform("output", "-raw", "resource_group", capture=True).strip()
    managed_group = None
    try:
        apply_runtime(True, var_file)
        analysis = json.loads(terraform("output", "-json", "analysis", capture=True))
        managed_group = analysis.get('managed_resource_group')
        job = analysis["job_name"]
        validate_workbook(analysis)
        for dry in (["true"] if args.dry_run_only else ["true"] + ["false"] * args.max_executions):
            print('Starting ' + ('dry-run validation' if dry == 'true' else 'incremental import batch') + '.', flush=True)
            operation = str(uuid.uuid4())
            env = dict(os.environ, ANALYSIS_OPERATION_ID=operation, ANALYSIS_VERIFY_OPERATION="",
                       ANALYSIS_MAX_SECONDS="540")
            for attempt in range(3 if dry == "true" else 1):
                result = json.loads(command(["bash", str(INFRA / "scripts/start-analysis.sh"), group,
                    job, start.isoformat(), end.isoformat(), dry], True, env))
                try:
                    wait_execution(group, job, result["name"])
                    break
                except RuntimeError:
                    if dry != "true" or attempt == 2:
                        raise
                    print("Dry-run not ready; waiting 30 seconds for initial role propagation.")
                    time.sleep(30)
            if dry == "false":
                print('Verifying actual Log Analytics delivery for this batch.', flush=True)
                env["ANALYSIS_VERIFY_OPERATION"] = operation
                env["ANALYSIS_MAX_SECONDS"] = "540"
                verification = json.loads(command(["bash", str(INFRA / "scripts/start-analysis.sh"), group,
                    job, start.isoformat(), end.isoformat(), "false"], True, env))
                wait_execution(group, job, verification["name"])
                result = operation_result(group, job, verification["name"], operation)
                print('Delivery verified for ' + str(result['sources']) + ' source versions in this batch.', flush=True)
                if not result['continuation']:
                    print('The date-range scan is complete.', flush=True)
                    break
        else:
            if not args.dry_run_only:
                raise RuntimeError('Bounded batch limit reached; progress is saved. Run the same command to continue')
    finally:
        # Stop executions before removing their environment, including on errors.
        try:
            analysis = json.loads(terraform("output", "-json", "analysis", capture=True))
            job = analysis.get("job_name") if analysis else None
            managed_group = (analysis or {}).get('managed_resource_group') or managed_group
            if job:
                executions = json.loads(command(["az", "containerapp", "job", "execution", "list",
                    "-g", group, "-n", job, "-o", "json"], True))
                for execution in executions:
                    if execution.get("properties", {}).get("status") in ("Running", "Processing"):
                        command(["az", "containerapp", "job", "stop", "-g", group, "-n", job,
                                 "--job-execution-name", execution["name"]])
        finally:
            apply_runtime(False, var_file)
            if managed_group:
                wait_network_removed(managed_group)
        print("Analysis runtime removed; source blobs, checkpoints, tables and Workbook retained.")


if __name__ == "__main__":
    try:
        main()
    except (RuntimeError, ValueError, OSError, sqlite3.Error, subprocess.CalledProcessError) as error:
        if isinstance(error, subprocess.CalledProcessError) and error.stderr:
            print(error.stderr[-3500:], file=sys.stderr)
        print("Analysis operation failed: " + str(error), file=sys.stderr)
        sys.exit(1)
