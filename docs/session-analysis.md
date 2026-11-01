# Query captured sessions in Azure Portal

This optional service reads existing private JSON session blobs into custom Log
Analytics tables. A manual Azure Container Apps Job runs in its own subnet with
its own identity. The honeypot VM, container, identity, and storage write-only
role remain unchanged. Storage public access remains disabled.

Terraform creates infrastructure and query definitions. The image workflow
builds the independent importer. Neither applying Terraform nor publishing an
image starts an import. Normal investigation is entirely in Azure Portal.

## Deployment and runtime removal

`analysis_enabled = true` keeps the query tables, ingestion rule, saved queries,
and Workbook. `analysis_runtime_enabled = true` additionally creates the
container environment, manual job, subnet, and dedicated identity/roles.
The runtime switch defaults to false because the VNet environment creates a
Standard load balancer with hourly charges even when no import is running,
outside any applicable free allowance. Stopping a job does not remove that
load balancer. This is not an idle-free networking design.

1. Review `infra/analysis.tf`. The existing VNet must have `10.50.0.128/27`
   available, and `Microsoft.App` must be registered. Use the existing Terraform
   state/backend. The Terraform deployment identity needs resource creation and
   role-assignment rights.
2. Publish the image before enabling the runtime to avoid paying for network
   infrastructure while building. Run GitHub Actions **Build session importer**
   manually on the reviewed branch. It uses the existing `prod` environment,
   OIDC identity, ACR variables, and Azure ID variables; it reports the digest in
   the run summary. It does not deploy to the honeypot VM or run an import.
3. Set `analysis_enabled = true`, `analysis_runtime_enabled = true`, and
   `analysis_image_digest = "sha256:..."` in untracked `infra/terraform.tfvars`.
   From `infra/`, run `terraform init`, `terraform plan`, then apply the reviewed
   plan. Environment creation can take many minutes; the initial live
   deployment took about 14 minutes.
4. Open `analysis.job_portal_url`. Verify Manual trigger, no automatic retries,
   one replica, and a 600-second timeout. Wait for initial role propagation if
   needed rather than widening permissions. Run the bounded import below.
5. After the imported records are verified, set only
   `analysis_runtime_enabled = false`, leaving `analysis_enabled = true` and the
   image digest recorded. Review/apply the plan: it should remove only the job,
   environment, analysis subnet, dedicated identity, and its three role
   assignments. Azure also removes the managed environment resource group and
   its load balancer. The source blobs, query tables, DCR, saved queries, and
   Workbook remain. Confirm the managed resource group actually disappears.
6. For another import, temporarily enable the runtime again and apply, run the
   import, then disable/apply again. Terraform can run in Azure Portal Cloud
   Shell using the reviewed repository and existing state/backend. There is no
   **Run now** button while the job has been removed. Opening graphs or querying
   already imported records never requires recreating the runtime.

Do not disable `analysis_enabled` just to stop compute: disabling that switch
removes the analysis tables and their imported data too. Do not create a second
Terraform state or upload state, credentials, or captured records into Git.
The current deployment uses local state in the existing workspace; Cloud Shell
cannot automatically access it. Arrange secure access to that same state before
applying from Cloud Shell, or redeploy from the existing workspace. A fresh
clone with an empty state must not be used to recreate existing infrastructure.

## Import a date range (while the runtime is enabled)

1. In Azure Portal, open **Container Apps Jobs → job-telemoq-session-import**
   (the name uses your configured prefix), then **Run now**.
2. Override environment variables for this execution: `START_DATE` and
   `END_DATE` as `YYYY-MM-DD` in UTC, both inclusive, at most 31 days. Start with
   one day. Keep `DRY_RUN=true` initially. Preserve the other template settings.
3. Inspect the execution's console logs and terminal status. A dry-run reads
   and validates sources and estimates generated ingestion bytes; it sends no
   records to Log Analytics. It still uses compute and blob reads.
4. If bounds and estimates are suitable, run again with the same dates and
   `DRY_RUN=false`. Wait for termination. API acceptance is not immediate query
   visibility; inspect **Import delivery status** in the Workbook after ingestion.
5. Open **Log Analytics workspace → Logs**, use KQL mode, and select a time
   range covering the import time. Run `HoneypotRows()` or a saved query under
   **Honeypot sessions**. Open **Workbooks → Honeypot session analysis** for graphs.

The job has no default dates and exits without reading blobs if none are given.
Execution overrides are ephemeral; they do not modify Terraform configuration.
The importer has a second internal deadline in addition to the platform timeout.
Use **Stop execution** to terminate an unwanted run. Do not launch overlapping
runs: one replica per execution does not prohibit multiple concurrent executions.

If your portal does not offer the overrides, open Azure Portal Cloud Shell,
obtain the reviewed repository revision, and use:

```bash
bash infra/scripts/start-analysis.sh rg-telemoq job-telemoq-session-import 2026-10-31 2026-10-31 true
# Repeat with false only after reviewing the dry-run.
```

The script retrieves the whole job template through ARM, changes only dates and
dry-run, and invokes the execution. Optional `ANALYSIS_MAX_BLOBS`,
`ANALYSIS_MAX_SOURCE_BYTES`, `ANALYSIS_MAX_BLOB_BYTES`,
`ANALYSIS_MAX_INGESTION_BYTES`, and `ANALYSIS_MAX_SECONDS` environment variables
override the same bounded job settings. It never reads private blobs from Cloud
Shell. It checks for active executions, but that check is not a distributed lock.
Use a storage-free/ephemeral Cloud Shell when available to avoid creating an
unnecessary persistent share. The CLI may prompt to install its Container Apps
extension. For repeated operation, bookmark the job and Workbook in the portal.

## Limits and cost

Default job execution limits: 1000 blobs, 10 MiB total source, 2 MiB per source blob,
20 MiB serialized ingestion requests, 20,000 normalized rows per source, and
240 seconds. Limits can be lowered
or raised within hard-coded ceilings using execution overrides. A run that
hits a limit stops; already accepted sessions remain available. A day with more than 1000 files requires increasing `MAX_BLOBS` for that execution. Narrow the
date range rather than repeatedly importing the same large range.

The ingestion upload budget also counts retries; a response lost after Azure
accepted a batch can otherwise consume the allowance more than once. The
summary distinguishes generated bytes from attempted upload bytes. Neither
number is an exact Azure billing measurement.

This is not a guaranteed-free solution. Consumption execution, platform
infrastructure/networking, source reads, image storage/building, and Log Analytics
ingestion/retention can incur charges. Account-wide free allowances may already
be consumed. Dry-runs do not estimate every Azure charge. No shared workspace
daily cap is changed. A per-execution limit is not a spending cap, and repeated
or concurrent runs increase costs. Restrict job start/update rights to trusted
operators: execution overrides can run code with the job identity.

Only the Consumption workload profile is configured. There is no NAT gateway,
new environment private endpoint, dedicated workload profile, inbound app, or
schedule. VNet integration reaches the existing Blob private endpoint; it is
different from adding a private endpoint to the Container Apps environment.
The initial live deployment created one Standard internal load balancer with
two rules. These network charges are independent of Consumption job execution.
Remove the runtime after imports to eliminate that ongoing load balancer; do
not delete or modify individual resources in the managed resource group.

If you choose to retain the runtime, Azure can delete Container Apps environments idle for over 90 days. If that
happens, use the existing Terraform state to plan/recreate the environment and
job, then run another bounded import. Do not keep a replica running merely to
prevent cleanup. Analysis tables retain data for 30 days; source blobs are not
modified or deleted. Reimport historical blobs as needed.

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

`HoneypotRows()` deduplicates retries by source version, run, entity kind, record
index, and chunk index. It includes only runs whose observed rows exactly match
the status marker's expected count, then chooses the latest complete run for
each source version. Concurrent/repeated imports can create physical duplicates
and ingestion charges; this is not exactly-once ingestion. Different ETags are
kept as distinct source versions, so overwrites can appear as separate captures.

If a send fails before the status marker, orphan rows remain excluded from
graphs. Check job status/logs and replay the requested range. If a marker arrives
before other streams, the delivery query shows partial data until all rows
arrive. The API does not provide a cross-table transaction. Stop executions
before disabling resources; removal of a table deletes its analysis copy.

The Workbook has session-date, protocol, and session-ID filters. Workspace time
is import time; `StartedAt` is the original session time. Charts cover traffic
over time, authentication outcomes, source addresses (client ports removed),
usernames, commands, MQTT event types, session details, and import delivery.
Default views omit captured passwords, but authorized readers of raw login
payloads can access them. Grant workspace access accordingly.

## Validation and maintenance

```bash
python3 -m unittest discover -s tools/session-importer -p 'test_*.py' -v
bash -n infra/scripts/start-analysis.sh
terraform -chdir=infra fmt -check
terraform -chdir=infra validate
dotnet build TelemoqNet.slnx
```

For a live KQL syntax/behavior check without ingesting or reading captured data:

```bash
python3 tools/session-importer/validate_queries.py WORKSPACE_CUSTOMER_ID
```

This requires an Azure CLI login with workspace query access. It submits only
synthetic `datatable` queries and checks duplicate/partial-run selection and
payload reconstruction as well as compiling every saved query.

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
