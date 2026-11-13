# Honeypot infrastructure (Terraform, Azure)

Status: `terraform fmt`, `terraform validate` and mocked `terraform test` are local checks; run `terraform plan`
against the target subscription before applying.

## What it creates

One resource group in a dedicated subscription containing: a VNet with one honeypot subnet and one
private-endpoint subnet, an NSG (inbound 23/2323/1883 only, outbound deny except Azure Monitor
and an optional bootstrap rule), a static public IP, a Linux VM
running the container via Docker, a Premium Container Registry, a storage account with no public
access or shared keys behind Blob and ACR private endpoints, a write-only custom role for the VM identity,
a Log Analytics workspace and Application Insights resource, and a GitHub OIDC identity (federated
credential) that can push images and run commands on the VM only.

## First apply

For the complete Azure bootstrap, GitHub environment variables, OIDC
federation, and deployment steps, see
[`docs/azure-github-deployment.md`](../docs/azure-github-deployment.md).

1. Use a dedicated subscription. Copy `terraform.tfvars.example` to `terraform.tfvars` and fill it in.
2. `terraform init -backend-config=backend.hcl` (create the state storage account separately, once, outside this config).
3. `terraform apply` with `allow_bootstrap_egress = true` so cloud-init can install Docker.
4. Once `cloud-init status` reports done, set `allow_bootstrap_egress = false` and apply again.
   OS patching later needs the same temporary toggle, or rebuild the VM.

## Deploy (called from GitHub Actions)

The `Build and deploy` workflow runs on pushes to `main`. It builds and pushes
an image tagged with the commit SHA, then invokes `/opt/honeypot/deploy.sh
<commit-sha>` through Azure Run Command. It requires the `prod` environment and
environment variables for the Azure IDs, ACR name/login server, resource group
and VM name.

## On-demand captured-session analysis

See [the analysis operating guide](../docs/session-analysis.md) for the optional,
Terraform-managed Container Apps Job, private blob ingestion, saved KQL, and
Workbook. It uses a separate identity and never runs on the honeypot VM.
The feature, runtime, and automatic ingestion are disabled by default. Use
`python3 infra/scripts/analysis-once.py` from the repository root to handle
backend setup/migration and importer publication automatically, then
create runtime, validate/import new versions, verify delivery, and remove runtime.
Append `--sqlite [PATH]` to download a database for local SQL queries after cleanup;
`--sqlite-only --sqlite [PATH]` exports retained workspace data without runtime changes.
The Portal Run now button is available while a job exists. A retained-runtime
15-minute schedule is available separately and disabled by default.

Managed networking costs roughly $18–22/month at normal retail rates before
execution/ingestion; it does not become free when the job stops. See the operating
guide for checked rates, optional alerts/budgets, and exact tradeoffs.

The AzureRM backend is explicit. The entry script migrates the existing
**authoritative state** automatically after validating its live deployment anchors.
For direct Terraform usage, migrate once using `terraform init -migrate-state -backend-config=backend.hcl`;
new clones must initialize the same remote backend. Retained tables/checkpoints
have destruction guards. The signed provider lockfile is committed.

## Other known gaps

- The Terraform apply identity (separate from the deploy identity) is bootstrapped outside this config.
- Set `log_reader_principal_id` to an Entra user or group object ID to grant read-only access to
  the sessions container. The reader still needs network access to the VNet/private endpoint.
- Optional dedicated-subscription budgets are included for analysis operators; Azure Policy is not configured.
