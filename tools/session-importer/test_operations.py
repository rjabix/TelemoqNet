import contextlib
import importlib.util
import io
import json
from pathlib import Path
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

import importer
import test_importer as fixtures

SCRIPTS = Path(__file__).resolve().parents[2] / 'infra/scripts'
spec = importlib.util.spec_from_file_location('analysis_once', SCRIPTS / 'analysis-once.py')
operations = importlib.util.module_from_spec(spec)
spec.loader.exec_module(operations)


class PlanGuardTests(unittest.TestCase):
    def plan(self, address, actions):
        return {'resource_changes': [{'address': address, 'name': address.split('.')[1].split('[')[0],
                                     'change': {'actions': actions}}]}

    def test_runtime_plan_rejects_honeypot_changes_and_retained_deletions(self):
        for address, actions in [('azurerm_linux_virtual_machine.honeypot', ['update']),
                                  ('azapi_resource.analysis_table["Sessions"]', ['delete']),
                                  ('azurerm_storage_container.analysis_checkpoints[0]', ['delete', 'create'])]:
            with self.assertRaises(ValueError):
                operations.guard.check(self.plan(address, actions), runtime_only=True)

    def test_runtime_plan_allows_only_analysis_operations(self):
        operations.guard.check(self.plan('azurerm_container_app_environment.analysis[0]', ['delete']), True)
        operations.guard.check(self.plan('azurerm_network_security_rule.honeypot_analysis["Inbound"]', ['create']), True)


class CheckpointTransportTests(unittest.TestCase):
    def state(self):
        azure = fixtures.TransportTests().azure()
        azure.env['CHECKPOINT_CONTAINER'] = 'analysis-checkpoints'
        return azure, importer.Checkpoints(azure, fixtures.config())

    def test_finite_lease_renews_and_releases(self):
        azure, state = self.state()
        with patch.object(azure, 'blob_headers', return_value={}), patch.object(azure, 'request', return_value=(b'', {})) as request:
            state.acquire()
            self.assertEqual(request.call_args.args[1]['x-ms-lease-duration'], '60')
            state.renew_at = 0
            state.renew()
            self.assertEqual(request.call_args.args[1]['x-ms-lease-action'], 'renew')
            state.release()
            self.assertEqual(request.call_args.args[1]['x-ms-lease-action'], 'release')

    def test_competing_execution_is_rejected(self):
        azure, state = self.state()
        with patch.object(azure, 'blob_headers', return_value={}), patch.object(azure, 'request',
                side_effect=[(b'', {}), importer.AzureHttpFailure(409)]):
            with self.assertRaisesRegex(importer.ImportFailure, 'another import'):
                state.acquire()
        self.assertIsNone(state.lock)

    def test_checkpoint_container_cannot_be_evidence_container(self):
        azure, _ = self.state()
        azure.env['CHECKPOINT_CONTAINER'] = azure.container
        with self.assertRaises(importer.ImportFailure):
            importer.Checkpoints(azure, fixtures.config())

    def test_delivery_verification_does_not_accept_empty_results(self):
        azure, _ = self.state()
        azure.env['WORKSPACE_CUSTOMER_ID'] = '00000000-0000-0000-0000-000000000001'
        runs = [{'runId': '00000000-0000-0000-0000-000000000002', 'expected': 4}]
        with patch.object(azure, 'token', return_value='synthetic'), patch.object(azure, 'request',
                return_value=(json.dumps({'tables': [{'rows': [[0]]}]}).encode(), {})):
            self.assertFalse(azure.delivered_many(runs))
        with patch.object(azure, 'token', return_value='synthetic'), patch.object(azure, 'request',
                return_value=(json.dumps({'tables': [{'rows': [[1]]}]}).encode(), {})):
            self.assertTrue(azure.delivered_many(runs))


class OperatorTests(unittest.TestCase):
    def test_transient_read_is_retried_but_job_start_is_not(self):
        for command, attempts in [(['az', 'containerapp', 'job', 'execution', 'list'], 2),
                                  (['az', 'containerapp', 'job', 'start'], 1)]:
            failure = operations.subprocess.CompletedProcess(command, 1, '', 'ConnectionResetError: connection reset by peer')
            success = operations.subprocess.CompletedProcess(command, 0, '[]', '')
            with patch.object(operations.subprocess, 'run', side_effect=[failure, success]) as run, \
                    patch.object(operations.time, 'sleep'), contextlib.redirect_stdout(io.StringIO()):
                if attempts == 2:
                    self.assertEqual(operations.command(command, True), '[]')
                else:
                    with self.assertRaises(operations.subprocess.CalledProcessError):
                        operations.command(command, True)
            self.assertEqual(run.call_count, attempts)

    def test_cleanup_replans_after_transient_apply_failure(self):
        error = operations.subprocess.CalledProcessError(1, ['terraform', 'apply'], stderr='HTTP response was nil; connection may have been reset')
        with patch.object(operations, 'apply_runtime_plan', side_effect=[error, None]) as plan, \
                patch.object(operations.time, 'sleep'), contextlib.redirect_stdout(io.StringIO()):
            operations.apply_runtime(False, 'config')
        self.assertEqual(plan.call_count, 2)

    def test_cleanup_does_not_retry_authorization_failures(self):
        error = operations.subprocess.CalledProcessError(1, ['terraform', 'apply'], stderr='AuthorizationFailed')
        with patch.object(operations, 'apply_runtime_plan', side_effect=error) as plan:
            with self.assertRaises(operations.subprocess.CalledProcessError):
                operations.apply_runtime(False, 'config')
        self.assertEqual(plan.call_count, 1)

    def test_cleanup_only_does_not_publish_or_import(self):
        with tempfile.NamedTemporaryFile() as config_file, \
                patch.object(sys, 'argv', ['analysis-once.py', '--cleanup-only', '--var-file', config_file.name]), \
                patch.object(operations, 'ControlLock'), \
                patch.object(operations, 'terraform', return_value='{"managed_resource_group":"managed"}'), \
                patch.object(operations, 'refresh_state'), \
                patch.object(operations, 'wait_network_removed') as wait, \
                patch.object(operations, 'apply_runtime') as apply, \
                patch.object(operations, 'setup') as setup, \
                patch.object(operations, 'execute') as execute, contextlib.redirect_stdout(io.StringIO()):
            operations.main()
        setup.assert_not_called()
        execute.assert_not_called()
        apply.assert_called_once_with(False, str(Path(config_file.name).resolve()))
        wait.assert_called_once_with('managed')

    def test_complete_scan_stops_before_batch_ceiling_and_removes_runtime(self):
        def terraform(*args, **kwargs):
            if args[0] == 'state':
                return 'azurerm_linux_virtual_machine.honeypot\n'
            if args[-1] == 'resource_group':
                return 'group'
            return '{"job_name":"job"}'
        def command(args, *unused):
            return '[]' if 'list' in args else '{"name":"execution"}'
        with tempfile.NamedTemporaryFile() as config_file, \
                patch.object(operations, 'terraform', side_effect=terraform), \
                patch.object(operations, 'command', side_effect=command) as requests, \
                patch.object(operations, 'apply_runtime') as apply, \
                patch.object(operations, 'validate_workbook'), \
                patch.object(operations, 'wait_execution'), \
                patch.object(operations, 'operation_result', return_value={'sources': 828, 'continuation': False}), \
                contextlib.redirect_stdout(io.StringIO()):
            operations.execute(SimpleNamespace(start=fixtures.config().start, end=fixtures.config().end,
                var_file=config_file.name, dry_run_only=False, max_executions=10))
        starts = [call for call in requests.call_args_list if call.args[0][0] == 'bash']
        self.assertEqual(len(starts), 3)  # Dry-run, import, verification; no extra batches.
        self.assertEqual(starts[1].args[2]['ANALYSIS_MAX_SECONDS'], '540')
        self.assertEqual([call.args[0] for call in apply.call_args_list], [True, False])

    def test_verification_result_handles_cli_embedded_json(self):
        event = {'event': 'delivery_verified', 'operationId': 'operation', 'sources': 12, 'continuation': False}
        # CLI versions differ in whether embedded container log JSON is escaped.
        for output in [json.dumps({'Log': 'F ' + json.dumps(event)}), 'prefix F ' + json.dumps(event) + '"}']:
            with patch.object(operations, 'command', return_value=output):
                self.assertEqual(operations.operation_result('group', 'job', 'execution', 'operation'), event)

    def test_cleanup_waits_until_managed_network_group_is_gone(self):
        with patch.object(operations, 'command', side_effect=['true\n', 'false\n']) as command, \
                patch.object(operations.time, 'sleep'):
            operations.wait_network_removed('managed-test')
        self.assertEqual(command.call_count, 2)

    def test_lifecycle_lease_is_separate_from_terraform_state_and_released(self):
        metadata = {'backend': {'type': 'azurerm', 'config': {
            'storage_account_name': 'stateaccount', 'container_name': 'tfstate', 'key': 'existing.tfstate'}}}
        with patch.object(Path, 'read_text', return_value=json.dumps(metadata)), \
                patch.object(operations, 'command', return_value='{"exists": true}') as command:
            with operations.ControlLock():
                acquire = command.call_args.args[0]
                self.assertIn('existing.tfstate.analysis-operation.lock', acquire)
                self.assertIn('-1', acquire)
            self.assertIn('release', command.call_args.args[0])

    def test_failed_lifecycle_acquisition_does_not_enter_operation(self):
        metadata = {'backend': {'type': 'azurerm', 'config': {
            'storage_account_name': 'stateaccount', 'container_name': 'tfstate', 'key': 'existing.tfstate'}}}
        with patch.object(Path, 'read_text', return_value=json.dumps(metadata)), \
                patch.object(operations, 'command', side_effect=['{"exists": true}', RuntimeError('leased')]):
            with self.assertRaisesRegex(RuntimeError, 'leased'):
                with operations.ControlLock():
                    self.fail('A competing operation must not enter')

    def test_failed_import_still_removes_runtime(self):
        def tf(*args, **kwargs):
            if args[0] == 'state':
                return 'azurerm_linux_virtual_machine.honeypot\n'
            if args[-1] == 'resource_group':
                return 'rg-test'
            return json.dumps({'job_name': 'job-test'})
        def cmd(args, *unused):
            return '[]' if 'list' in args else '{"name":"execution-test"}'
        with tempfile.NamedTemporaryFile(suffix='.tfvars') as config_file, \
                patch.object(sys, 'argv', ['analysis-once.py', '--skip-setup', '--var-file', config_file.name]), \
                patch.object(operations, 'terraform', side_effect=tf), \
                patch.object(operations, 'command', side_effect=cmd), \
                patch.object(operations, 'ControlLock'), \
                patch.object(operations, 'refresh_state'), \
                patch.object(operations, 'recover_pending_cleanup'), \
                patch.object(operations, 'validate_workbook'), \
                patch.object(operations, 'apply_runtime') as apply, \
                patch.object(operations, 'wait_execution', side_effect=RuntimeError('synthetic failure')), \
                patch.object(operations.time, 'sleep'), \
                contextlib.redirect_stdout(io.StringIO()):
            with self.assertRaises(RuntimeError):
                operations.main()
            self.assertEqual([call.args[0] for call in apply.call_args_list], [True, False])

    def test_existing_runtime_is_not_taken_over(self):
        with tempfile.NamedTemporaryFile(suffix='.tfvars') as config_file, \
                patch.object(sys, 'argv', ['analysis-once.py', '--skip-setup', '--var-file', config_file.name]), \
                patch.object(operations, 'terraform', return_value='azurerm_linux_virtual_machine.honeypot\nazurerm_container_app_job.analysis[0]\n'), \
                patch.object(operations, 'ControlLock'), \
                patch.object(operations, 'refresh_state'), \
                patch.object(operations, 'recover_pending_cleanup'), \
                patch.object(operations, 'validate_workbook'), \
                patch.object(operations, 'apply_runtime') as apply:
            with self.assertRaisesRegex(RuntimeError, 'Runtime already exists'):
                operations.main()
            apply.assert_not_called()


class SetupTests(unittest.TestCase):
    subscription = '00000000-0000-0000-0000-000000000001'

    def document(self):
        base = '/subscriptions/' + self.subscription + '/resourceGroups/test'
        return {'lineage': 'existing-lineage', 'serial': 12, 'resources': [
            {'type': 'azurerm_resource_group', 'name': 'main', 'instances': [{'attributes': {'id': base}}]},
            {'type': 'azurerm_linux_virtual_machine', 'name': 'honeypot', 'instances': [
                {'attributes': {'id': base + '/providers/Microsoft.Compute/virtualMachines/test'}}]}]}

    def test_empty_state_recovery_preserves_backup_and_checks_live_anchors(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            original = json.dumps(self.document())
            (root / 'terraform.tfstate').write_text('')
            (root / 'terraform.tfstate.backup').write_text(original)
            def command(args, capture):
                return args[args.index('--ids') + 1]
            with patch.object(operations, 'INFRA', root), patch.object(operations, 'PRIVATE', root / '.analysis'), \
                    patch.object(operations, 'command', side_effect=command) as request, \
                    contextlib.redirect_stdout(io.StringIO()):
                result = operations.recover_local_state(self.subscription)
            self.assertEqual(result['lineage'], 'existing-lineage')
            self.assertEqual(request.call_count, 2)
            self.assertEqual((root / 'terraform.tfstate.backup').read_text(), original)
            self.assertEqual(json.loads((root / 'terraform.tfstate').read_text()), self.document())
            self.assertEqual((root / 'terraform.tfstate').stat().st_mode & 0o777, 0o600)

    def test_wrong_subscription_backup_cannot_be_recovered(self):
        with tempfile.TemporaryDirectory() as directory:
            root = Path(directory)
            (root / 'terraform.tfstate').write_text('')
            (root / 'terraform.tfstate.backup').write_text(json.dumps(self.document()))
            with patch.object(operations, 'INFRA', root), patch.object(operations, 'command') as request:
                with self.assertRaisesRegex(RuntimeError, 'different subscription'):
                    operations.recover_local_state('00000000-0000-0000-0000-000000000002')
            self.assertEqual((root / 'terraform.tfstate').read_text(), '')
            request.assert_not_called()

    def test_settings_update_preserves_unrelated_values(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'terraform.tfvars'
            path.write_text('location = "austriaeast"\nvm_size = "KeepThis"\nanalysis_image_digest = "old"\n')
            operations.set_variables(path, {'analysis_image_digest': 'sha256:new', 'analysis_runtime_enabled': False})
            self.assertEqual(operations.read_variable(path, 'vm_size'), 'KeepThis')
            self.assertEqual(operations.read_variable(path, 'analysis_image_digest'), 'sha256:new')
            self.assertIn('analysis_runtime_enabled = false', path.read_text())

    def test_settings_parser_supports_json_and_refuses_expressions(self):
        with tempfile.TemporaryDirectory() as directory:
            path = Path(directory) / 'config.json'
            path.write_text('{"location":"austriaeast"}')
            self.assertEqual(operations.read_variable(path, 'location'), 'austriaeast')
            path = Path(directory) / 'terraform.tfvars'
            path.write_text('location = local.selected\n')
            with self.assertRaises(RuntimeError):
                operations.read_variable(path, 'location')

    def test_workbook_validation_fetches_content_and_uses_synthetic_validator(self):
        workbook = {'properties': {'serializedData': '{"items":[]}'}}
        with patch.object(operations, 'command', side_effect=[json.dumps(workbook), None]) as request:
            operations.validate_workbook({'workbook_id': '/example/workbook', 'workspace_customer_id': 'workspace'})
        arguments = request.call_args_list[0].args[0]
        self.assertIn('canFetchContent=true', arguments[arguments.index('--url') + 1])
        self.assertTrue(request.call_args_list[1].args[0][2].endswith('validate_queries.py'))
