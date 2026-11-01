# Query captured sessions in Azure Portal

For Workbook filters and copyable KQL/CLI examples, see
[Query captured sessions yourself](querying-sessions.md).

Terraform manages retained analysis tables, a DCR, saved KQL and a Workbook.
An independent bounded Python importer reads private session blobs, using a
separate managed identity and subnet. Source access stays read-only, and no
attacker payload is executed. Mutable import state is stored only in the separate
private `analysis-checkpoints` container. Its identity can query the workspace
to verify delivery and write checkpoints, but cannot write captured evidence.

## Recommended: run a script when you want new data

No analysis runtime needs to remain deployed between investigations. With Azure
CLI, Terraform, Docker and Python installed, sign in with `az login` and keep the
existing `infra/terraform.tfvars`. From the repository root run:

```bash
python3 infra/scripts/analysis-once.py
```

The same command performs first-time setup and subsequent imports: it validates
the selected subscription and live state anchors, securely backs up local state
and settings, recovers an empty local state from its intact validated backup if
needed, creates a separate Entra-authenticated LRS state account/container,
enables state versioning/soft delete, assigns the current operator state access,
and migrates the existing state. It never creates a fresh honeypot deployment
from an empty state. The backend has no anonymous access or shared-key access;
its authenticated HTTPS endpoint is reachable by the operator. It is separate
from the source account, whose public network access stays disabled.

The script builds only the Dockerfile and importer source, pushes a linux/amd64
image to the existing ACR, and pins its digest in the untracked tfvars. It caches
images by source hash, preserves other settings, and leaves both runtime and
scheduling disabled in those settings. It refreshes recovered state against ARM
before applying any guarded analysis-only plan. Setup resources are reused on
subsequent calls. Bootstrap needs permission to create the state resource group,
storage and the operator role assignment; ordinary invocation also needs the
permissions described below. State storage has a small separate usage charge.
Sensitive local backups remain under ignored `infra/.analysis/backups/`.

If an earlier run was interrupted during teardown, finish cleanup without
building an image or importing again:

```bash
python3 infra/scripts/analysis-once.py --cleanup-only
```

Read-only Azure calls retry transient connection failures. Cleanup retries up to
three times, generating a fresh guarded Terraform plan after a partial apply.
Job starts are never automatically replayed after an ambiguous network failure.

Additional bounded options:

```bash
# Provision runtime, validate sources, and remove runtime without ingesting.
python3 infra/scripts/analysis-once.py --var-file infra/terraform.tfvars --dry-run-only

# Provision, dry-run, import new versions from the last seven UTC days,
# verify their delivery, and remove runtime.
python3 infra/scripts/analysis-once.py --var-file infra/terraform.tfvars

# Historical range, or more bounded batches for a backlog:
python3 infra/scripts/analysis-once.py --var-file infra/terraform.tfvars \
  --start 2026-10-01 --end 2026-10-07 --max-executions 3
```

The operator needs Terraform apply/role-assignment rights and Azure CLI login.
Use `--skip-setup` only when the backend and image are already prepared. It refuses a new/empty deployment,
an existing runtime, unrelated infrastructure changes, or deletion of retained data.
The script serializes the complete create/import/remove cycle using an infinite
lease on `<backend-state-key>.analysis-operation.lock` in the backend container,
separate from the Terraform state blob. Terraform also locks individual applies.
The importer additionally uses one renewable 60-second Blob lease across manual
and scheduled executions. A killed process releases its lease by expiry.

The script also compiles the saved queries and deployed Workbook panels against
synthetic datatables before importing, without reading captured payloads.
It waits for each execution, verifies actual deduplicated row counts
against a private operation manifest, then stops any remaining execution and
applies runtime removal in `finally`, including on errors or SIGTERM. When Azure
reports the managed resource-group name, it waits up to five minutes for that
group to disappear and fails explicitly if networking remains. Verification
never treats an empty query result as proof that a submitted import arrived.
A hard kill or runner timeout can leave the operation lease locked. Confirm no
operation or execution is running before administratively breaking the lease
on that separate `.analysis-operation.lock` blob; never break the state blob
lease for this purpose. A hard kill, runner timeout or Azure deletion failure
can also leave billable resources: inspect the Terraform state and managed resource group after failures.
The script refuses to take over an existing active runtime. If Azure reports a
previous environment as `ScheduledForDelete`, a subsequent invocation first
resumes its guarded teardown and verifies network removal before creating new
runtime. An Azure resource-provider deletion fault can still block this step;
the command reports failure rather than claiming full ingestion or cleanup.

Each execution is bounded. `--max-executions` is 1..10 and defaults to ten as an
overall ceiling, following one dry-run. The script stops early when a verified
batch completes the date-range scan. It reads only the importer's verification
metadata from execution logs to decide whether another batch is needed. If the
overall ceiling is reached, it removes runtime and exits with saved progress
and an explicit continuation message. A new invocation resumes that progress. Daily polling cannot catch
late blobs outside its configured lookback; import their dates explicitly.

## Setup performed by the script (and manual alternatives)

The default command handles these steps automatically. For a manually managed
backend/image, or to inspect each step separately:

1. Publish the reviewed importer image using **Build session importer**. Record
   its immutable digest in the existing untracked `terraform.tfvars`.
2. The root now explicitly uses an AzureRM backend. Provision a separate private,
   Entra-authenticated state account/container outside this stack, or use your
   existing authoritative remote backend. Enable state versioning/soft delete
   and give only trusted operators Blob Data Contributor access to state.
3. For the existing local deployment, make a secure backup and run:
   `terraform -chdir=infra init -migrate-state -backend-config=/absolute/path/backend.hcl`.
   Terraform asks before copying state. Use `infra/backend.hcl.example`; this is
   an explicit migration, not an apply. Never initialize an empty backend and
   try to recreate existing resources. For an already remote deployment use
   `terraform -chdir=infra init -backend-config=/absolute/path/backend.hcl`.
4. Set `analysis_enabled=true`, `analysis_runtime_enabled=false`, and
   `analysis_auto_ingest_enabled=false`. Plan the baseline once, inspect it,
   run the plan guard below, and apply it. The baseline provisions retained
   checkpoints, query updates and the honeypot/analysis subnet deny rules.
   Existing analysis tables must be preserved. A scheduled runtime also needs
   the `Microsoft.App` resource provider registered.
5. Retain that same reviewed tfvars and backend for every script invocation.
   The signed provider lockfile is versioned for macOS ARM64 and Linux AMD64.
   The first baseline deployment may change the existing environment if it is
   currently active; stop its executions and review the plan before applying.

```bash
terraform -chdir=infra plan -out=/tmp/analysis-baseline.tfplan
terraform -chdir=infra show -json /tmp/analysis-baseline.tfplan | \
  python3 infra/scripts/check-analysis-plan.py
terraform -chdir=infra apply /tmp/analysis-baseline.tfplan
```

`prevent_destroy` protects the tables and checkpoint container even if the
feature toggle is disabled. Deliberate evidence-copy teardown requires removing
that protection explicitly and reviewing a separate destructive plan. Removing
resource blocks also removes Terraform's lifecycle protection. The plan guard
is an additional check, not an Azure authorization boundary.

## Buttons and optional automation

With runtime deployed manually (`analysis_runtime_enabled=true`), Azure Portal
**Container Apps Jobs → job-<prefix>-session-import → Run now** supports the
usual `START_DATE`, `END_DATE` and `DRY_RUN` execution overrides. Dates are UTC,
inclusive, at most 31 days. Defaults are blank dates and dry-run; a normal manual
job cannot accidentally ingest everything. Keep other environment settings.
The Portal button cannot create an environment that has been removed. Use the
script above for creation plus automatic cleanup.

For frequent ingestion with the runtime left available, opt in separately:

```hcl
analysis_enabled             = true
analysis_runtime_enabled     = true
analysis_auto_ingest_enabled = true
analysis_schedule            = "*/15 * * * *"
analysis_lookback_days        = 7
```

This is a scheduled Container Apps Job, not a continuously running replica.
Dates are derived each execution unless explicitly overridden. It scans the
rolling lookback, remembers scan progress, and skips delivered source versions.
Disable the schedule before removing runtime. Use the entry script for on-demand
imports or retain a scheduled runtime; the script refuses to take over an existing
runtime.

## Recovery and delivery guarantees

A source-version receipt is keyed by a hash of blob name plus ETag. Conditional
`If-Match` reads prevent importing a different version than the one listed.
Pending uploads reuse their original import UUID on retry; queries deduplicate
physical duplicate rows. A submitted receipt becomes Complete only after an
actual workspace query confirms all expected rows including its status marker.
Submissions younger than 15 minutes can settle before being retried.

Completed receipts are reused for 25 days, within the tables' 30-day retention.
A storage lifecycle rule expires receipts, scan cursors and operation manifests
after 35 days without modification; it excludes captured evidence and lock blobs.
Malformed or oversized sources are reported individually, with only a hashed
source identifier in logs, and rejected versions are held for one day. They do
not block later valid blobs. To replay despite receipts or after a normalizer
change, override `FORCE_REIMPORT=true` for a bounded date range. This also resets
the scan cursor for that execution and incurs ingestion charges again.

The persisted cursor includes the day, Azure listing marker and last name on
that page. Finishing a sweep resets it, so subsequent sweeps discover late
arrivals whose names sort before an earlier cursor. Each run permits at most
200 listing pages. Count/source/ingestion budgets stop at a resumable boundary;
a single entity too large for the execution's entire ingestion budget requires
raising that budget within the hard ceiling. Transport and authorization failures
abort the run and keep pending receipts for recovery. Summaries include run ID,
counts, skipped sources, failed sources and continuation status, even on failure.

Normal ingestion and raw storage are not exactly-once: ambiguous HTTP retries
can create physical duplicates and charges. Source JSON is never modified.
API acceptance does not establish delivery. The on-demand script starts a second
verification execution against its exact bounded operation manifest before
removing runtime. Large backlogs may need more than one invocation.

## Cost and alerts

Retail USD checked on 2026-11-01 using Azure's public retail pricing API
(global regional-LB meter; public-IP rates checked in Austria East and West Europe):

| Additional resource | Retail rate | At 730 hours/month |
| --- | --- | --- |
| Standard regional load balancer, first five rules | $0.025/hour | $18.25 |
| Standard public IPv4, if provisioned/billed | $0.005/hour | $3.65 each |
| Load balancer processed data | $0.005/GB | Depends on traffic |

The two-rule load balancer documented in the initial deployment falls in the
first-five-rules rate; two rules do not double that rate. Leaving managed
networking available is therefore about $18.25/month for that load balancer
alone, or about $21.90 with one billed public IP, before data, ingestion and
other services. Check the actual managed resource inventory and Cost Management
for your deployment. Free offers, taxes, currency and subscription discounts
can change the bill. This is not a $1/month always-available design.

An ephemeral run pays for its provisioned hours instead of a full month.
Load balancer and public-IP partial hours round up. Thirty one-hour provisioned
windows would be about $0.75 for the load balancer, plus about $0.15 for one
public IP if applicable, but creation/deletion delays can extend those windows.
Compute, Blob reads/checkpoints, state hosting, image storage/building, logging,
alerts and Logs Ingestion remain separate; this estimate is not a total-cost
promise. Existing ACR, private endpoints, DNS and VM costs remain in the base
stack. Merely stopping job execution does not remove the load balancer.

Defaults per execution: 1000 examined blobs, 10 MiB source, 2 MiB per source,
20 MiB serialized ingestion attempts (including retries), 20,000 normalized rows
per source, and 240 seconds in the saved job definition; the on-demand operator overrides
the internal deadline to 540 seconds, below the platform's 600-second timeout.
The importer stops admitting new sources with less than 30 seconds remaining,
commits its cursor/manifest and allows the operator to continue safely in another
batch. There are no platform retries. Overrides stay within hard-coded ceilings.

Set `analysis_alert_emails` to enable an action group, importer/stale-delivery
log alerts, failed-environment-deletion activity alerts, and a monthly **dedicated
subscription** budget. `analysis_monthly_budget_amount` defaults to 30 in your
subscription's billing currency. It covers VM/ACR and managed networking in its
separate resource group too. Budget notifications at 80% actual and 100%
forecast do not stop spending. Log alert rules have their own charges. Empty
email recipients leave these optional paid alert resources unprovisioned.
The importer logs no attacker payloads or passwords, but workspace query access
still permits reading raw captured login data. Grant workspace RBAC accordingly.

Pricing sources: [Load Balancer](https://azure.microsoft.com/en-us/pricing/details/load-balancer/),
[Public IP](https://azure.microsoft.com/en-us/pricing/details/ip-addresses/),
[Retail pricing API](https://learn.microsoft.com/en-us/rest/api/cost-management/retail-prices/azure-retail-prices),
and [Container Apps managed resources](https://learn.microsoft.com/en-us/azure/container-apps/custom-virtual-networks#managed-resources).

## Schema and exact payloads

All tables have common provenance fields: `SessionId`, original `StartedAt`,
`Protocol`, `SourceBlob`, `SourceETag`, `ImportRunId`, and import-time
`TimeGenerated`. `RecordIndex` is the stable source-array index, not import order.

| Table | Contents |
| --- | --- |
| `HoneypotSessions_CL` | Top-level metadata and unknown fields, excluding the three known child arrays |
| `HoneypotLogins_CL` | One `Payload` object per login attempt |
| `HoneypotCommands_CL` | One `Payload` object per command and response |
| `HoneypotEvents_CL` | One `Payload` object per protocol event |
| `HoneypotPayloadChunks_CL` | Base64 chunks of entities exceeding the payload bound |
| `HoneypotImportStatus_CL` | Expected normalized row count, submitted last per session |

`Payload` normally preserves the complete entity including nulls and unknown
fields. Canonical JSON is serialized with ASCII escaping, compact separators,
and finite numeric values. A payload over 20,000 serialized bytes is stored
losslessly as base64 chunks; the entity row then contains a **preview** of known
fields and `HasChunks=true`. Preview text may be abbreviated to 256 characters.
Do not treat previews as full evidence or exact command text; command grouping
can combine previews of different long commands. Use the reconstruction query
and `PayloadHash` to retrieve exact parsed JSON. Canonical JSON preserves parsed
values, not the original blob's whitespace or byte representation.

Chunk rows carry `EntityKind` of `CommandsPayloadChunks`, `EventsPayloadChunks`,
`LoginsPayloadChunks`, or `SessionsPayloadChunks`; link to the owning entity by
source version, import run, record index, and payload hash. Concatenate
`ChunkData` by `ChunkIndex`, decode the combined base64 once, then verify its
SHA-256 against `PayloadHash`. A saved reconstruction query does this for
records within KQL result/string limits. For larger entities, query bounded
chunk ranges and retain order; the source blob remains the original evidence.

The importer accepts known schema versions 1 and 2. Invalid metadata, malformed
JSON, unsupported schema versions, non-object children, or oversized source
blobs produce a failed/partial run; valid oversized entities within the source
limit are chunked instead of silently truncated. Operational logs contain only
counts and hashes of failed source paths, never attacker payloads or passwords.
Use a bounded single-day replay to isolate failures. Use the source-path hash
to correlate an administrative inspection; it is deliberately not a blob URL.

The administrative `tools/session-importer/smoke_test.py` execution override
validates private source access and submits synthetic records under
`__analysis_validation__/`. `HoneypotRows()` excludes that reserved prefix from
normal charts. Synthetic validation never writes or changes source blobs.

## Queries, graphs, and partial delivery

`HoneypotRows(SessionStart, SessionEnd, SelectedProtocol, SelectedSession,
SelectedIp, SelectedCountry, SelectedCity, SelectedAuthentication)` has optional
parameters; existing zero- and four-argument calls remain compatible. Time,
protocol and session ID filters apply before completeness checks. IP, country,
city and authentication filters select complete sessions afterwards, retaining
their child records and chunks. Only narrow keys and session metadata are
materialized. It deduplicates retries by source version, run, entity kind, record
index, and chunk index. It includes only runs whose observed rows exactly match
the status marker's expected count, then chooses the latest complete run for
each source version. Concurrent/repeated imports can create physical duplicates
and ingestion charges; this is not exactly-once ingestion. Different ETags are
kept as distinct source versions, so overwrites can appear as separate captures.

If a send fails before the status marker, orphan rows remain excluded from
graphs and appear explicitly in Import delivery status after 15 minutes. Replay the requested range; pending receipts reuse the original source import ID. If a marker arrives
before other streams, the delivery query shows partial data until all rows
arrive. The API does not provide a cross-table transaction. Stop executions
before disabling resources; removal of a table deletes its analysis copy.

The Workbook retains session-date, protocol, and session-ID filters, and adds
source IP, country, city and session authentication outcome. All session panels
use the combined filters; Import delivery status remains unfiltered. Source IPs
are normalized without client ports, including IPv6/mapped IPv4 endpoints.
Country, city and approximate coordinates come from Azure Monitor's built-in
[IP geolocation function](https://learn.microsoft.com/en-us/kusto/query/geo-info-from-ip-address-function)
at query time, including for previously imported data, without extra API keys
or importer egress. Unresolved locations display `Unknown`; coordinates may be
absent. Locations describe the network endpoint, not a person's physical address.
Workspace time is import time; `StartedAt` is the original session time. Charts cover traffic
over time, authentication outcomes, source addresses (client ports removed),
usernames, commands, MQTT event types, country/city locations, session details,
and import delivery.
Default views omit captured passwords, but authorized readers of raw login
payloads can access them. Grant workspace access accordingly.

## Validation and maintenance

```bash
python3 -m unittest discover -s tools/session-importer -p 'test_*.py' -v
bash -n infra/scripts/start-analysis.sh
terraform -chdir=infra fmt -check
terraform -chdir=infra validate
terraform -chdir=infra test
dotnet build TelemoqNet.slnx
```

For a live KQL syntax/behavior check without ingesting or reading captured data:

```bash
python3 tools/session-importer/validate_queries.py WORKSPACE_CUSTOMER_ID
```

This requires an Azure CLI login with workspace query access. It submits only
synthetic `datatable` queries and checks duplicate/partial-run selection, endpoint
normalization, session filters (including child records), unresolved locations
and payload reconstruction as well as compiling every saved query.

Before a real-data import, deploy a bounded synthetic fixture through an
administrative identity with source write permission, verify private DNS,
authenticated registry pull, dry-run, complete delivery, graphs, and large
payload hash reconstruction. Verify the honeypot identity still cannot list or
read blobs. Do not use its write credentials for analysis. These live checks
require an actual deployment; passing local tests does not establish them.

## References

- [Manual job execution and overrides](https://learn.microsoft.com/en-us/azure/container-apps/jobs)
- [Container Apps networking](https://learn.microsoft.com/en-us/azure/container-apps/networking)
- [Managed network resources and charges](https://learn.microsoft.com/en-us/azure/container-apps/custom-virtual-networks#managed-resources)
- [Environment lifecycle](https://learn.microsoft.com/en-us/azure/container-apps/environment)
- [Logs Ingestion API](https://learn.microsoft.com/en-us/azure/azure-monitor/logs/logs-ingestion-api-overview)
- [Ingestion service limits](https://learn.microsoft.com/en-us/azure/azure-monitor/fundamentals/service-limits)
- [Azure Monitor pricing](https://azure.microsoft.com/en-us/pricing/details/monitor/)
