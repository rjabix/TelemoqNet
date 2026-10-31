# Honeypot infrastructure (Terraform, Azure)

Status: drafted, **not yet validated**. Run `terraform fmt` and `terraform validate` first.

## What it creates

One resource group in a dedicated subscription containing: a VNet with one honeypot subnet and one
private-endpoint subnet, an NSG (inbound 23/2323/1883 only, outbound deny except the container
registry, Storage and an optional bootstrap rule), a static public IP, a Linux VM running the
container via Docker, a Basic Container Registry, a storage account with no public access or
shared keys behind a Blob private endpoint, a write-only custom role for the VM identity, and a
GitHub OIDC identity (federated credential) that can push images and run commands on the VM only.

## First apply

1. Use a dedicated subscription. Copy `terraform.tfvars.example` to `terraform.tfvars` and fill it in.
2. `terraform init -backend-config=backend.hcl` (create the state storage account separately, once, outside this config).
3. `terraform apply` with `allow_bootstrap_egress = true` so cloud-init can install Docker.
4. Once `cloud-init status` reports done, set `allow_bootstrap_egress = false` and apply again.
   OS patching later needs the same temporary toggle, or rebuild the VM.

## Deploy (called from GitHub Actions)

`az vm run-command invoke ... --scripts "/opt/honeypot/deploy.sh <tag>"` after pushing the image to the registry.

## Known gaps

- No Dockerfile, workflows or app changes exist yet (managed-identity Blob client, `AzureStorage:AccountUrl`, container created here instead of in code).
- The Storage service-tag outbound rule is a residual exfiltration path (see `network.tf`).
- The Terraform apply identity (separate from the deploy identity) is bootstrapped outside this config.
- Budgets, Azure Policy and diagnostics/Log Analytics are not included yet.
