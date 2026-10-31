# Azure and GitHub deployment setup

This project uses GitHub Actions OIDC federation. Do not create or copy an Azure
client secret into GitHub. GitHub receives a short-lived token, Azure validates
that token against the repository and `prod` environment, and Azure grants the
workflow temporary access through the Terraform-created `github_deploy` identity.

## What you need before starting

- An Azure subscription dedicated to the honeypot.
- Azure CLI, logged in with an account allowed to create the resources and role
  assignments below.
- Terraform 1.9 or newer.
- A GitHub repository containing this project.
- An SSH public key for the VM administrator. SSH is not opened by the NSG, but
  Azure requires a key when creating the VM.

The Azure account running Terraform needs permission to create resources in the
subscription and assign roles, normally `Owner` or `Contributor` plus
`User Access Administrator`.

## 1. Prepare Azure authentication locally

```bash
az login
az account list --output table
az account set --subscription <honeypot-subscription-id>
az account show --query "{name:name,id:id,tenantId:tenantId}" --output table
```

Keep the subscription ID and tenant ID. They will be used as GitHub environment
variables later.

## 2. Configure and apply Terraform

Create a local variables file. It is ignored and must not be committed:

```bash
cd infra
cp terraform.tfvars.example terraform.tfvars
```

Edit `terraform.tfvars`:

```hcl
subscription_id      = "<honeypot-subscription-id>"
github_repo          = "<github-owner>/<github-repository>"
github_environment    = "prod"
github_oidc_subject  = "<github-owner>@<owner-id>/<github-repository>@<repository-id>"
admin_ssh_public_key = "ssh-ed25519 AAAA... user@computer"

# Optional: Entra object ID of the human or group that may read session blobs.
log_reader_principal_id = "<entra-object-id>"
```

The `github_repo` value must match the GitHub repository exactly, including
capitalization as it appears in the repository URL. GitHub may use an
ID-qualified OIDC subject; if so, set `github_oidc_subject` to the exact prefix
shown in the Actions login error. The `github_environment` value must remain
`prod` unless both Terraform and the workflow are changed together.

Initialize, validate, and apply:

```bash
terraform init -backend-config=backend.hcl
terraform fmt -check
terraform validate
terraform plan
terraform apply
```

Review the plan carefully before applying. This configuration creates the VM,
ACR, Blob Storage private endpoint, Application Insights, Log Analytics, the VM
managed identity, and the GitHub OIDC identity and role assignments.

### Bootstrap egress

The first apply intentionally defaults `allow_bootstrap_egress` to `true` so
cloud-init can install Docker and its packages. Wait for cloud-init:

```bash
az vm run-command invoke \
  --resource-group <resource-group-output> \
  --name <vm-name-output> \
  --command-id RunShellScript \
  --scripts "cloud-init status --wait"
```

Then set this in `terraform.tfvars` and apply again:

```hcl
allow_bootstrap_egress = false
```

Do not leave bootstrap egress enabled after first boot. The VM retains only
Azure Monitor egress; ACR and Blob traffic uses private endpoints.

## 3. Capture the Terraform outputs

From `infra/`, print the values needed by GitHub:

```bash
terraform output -raw github_client_id
terraform output -raw tenant_id
terraform output -raw acr_name
terraform output -raw acr_login_server
terraform output -raw resource_group
terraform output -raw vm_name
```

The subscription ID is the value used in `terraform.tfvars`. Do not put
Terraform state, `terraform.tfvars`, storage credentials, or connection strings
in the repository.

## 4. Create the GitHub `prod` environment

In GitHub, open:

`Repository → Settings → Environments → New environment`

Create an environment named exactly:

```text
prod
```

Recommended protection settings:

- Add required reviewers.
- Restrict deployment branches to the production branch or an approved release
  branch.
- Do not add an Azure client secret.

Add these as **Environment variables** under `prod` (not secrets):

| Variable | Value |
|---|---|
| `AZURE_CLIENT_ID` | `terraform output -raw github_client_id` |
| `AZURE_TENANT_ID` | `terraform output -raw tenant_id` |
| `AZURE_SUBSCRIPTION_ID` | Subscription ID from `terraform.tfvars` |
| `ACR_NAME` | `terraform output -raw acr_name` |
| `ACR_LOGIN_SERVER` | `terraform output -raw acr_login_server` |
| `RESOURCE_GROUP` | `terraform output -raw resource_group` |
| `VM_NAME` | `terraform output -raw vm_name` |

These are identifiers and names, not credentials. The workflow gets temporary
Azure credentials from OIDC.

## 5. Confirm the federation matches

Terraform creates a federated credential with:

```text
issuer:   https://token.actions.githubusercontent.com
audience: api://AzureADTokenExchange
subject:  repo:<github-owner>/<github-repository>:environment:prod
```

The workflow must keep both of these settings:

```yaml
environment: prod

permissions:
  contents: read
  id-token: write
```

If the repository owner, repository name, or environment name differs, Azure
will reject the login with an `AADSTS700213` or similar federated-credential
error. If that happens, correct `github_repo`/`github_environment`, apply
Terraform again, and retry.

## 6. Enable automatic deployment

The workflow is triggered automatically after every push to `main`. It uses
the commit SHA supplied by GitHub as the immutable image tag, so each deployment
is tied to the exact source revision that triggered it.

Push a change to `master`, then open the repository's **Actions** tab and select
the resulting **Build and deploy** run. If the `prod` environment has required
reviewers, approve the environment deployment when GitHub pauses the job.

The workflow:

1. Checks out the repository.
2. Logs in to Azure through OIDC.
3. Logs in to ACR.
4. Builds and pushes the Docker image.
5. Runs `/opt/honeypot/deploy.sh <commit-sha>` on the VM.

The workflow does not receive an Azure password, client secret, storage
connection string, or ACR admin password.

## 7. Verify the deployment

Use the Azure CLI:

```bash
az vm run-command invoke \
  --resource-group <resource-group> \
  --name <vm-name> \
  --command-id RunShellScript \
  --scripts "docker ps; docker logs --tail 50 honeypot"
```

Then test the public listeners:

```bash
telnet <public-ip> 23
nc -vz <public-ip> 1883
```

After a session ends, confirm that a JSON blob appears in the configured
sessions container. Application logs and telemetry should appear in the
Application Insights resource after the container has sent telemetry.

## Troubleshooting

### Azure login fails in GitHub Actions

Check that:

- The job has `environment: prod`.
- The job has `id-token: write`.
- The seven environment variables are defined under the `prod` environment,
  not only as repository variables.
- `github_repo` is exactly `<owner>/<repo>`.
- The federated credential exists on the identity identified by
  `AZURE_CLIENT_ID`.
- The workflow is running from the `main` branch and the `prod` environment.

### ACR login or VM deployment is denied

The GitHub identity receives `AcrPush` on the ACR and the custom VM Run Command
role on the VM. Confirm Terraform applied both role assignments and wait a few
minutes for Azure RBAC propagation before retrying.

### Session blobs cannot be read

Set `log_reader_principal_id` to the Entra object ID of the human or group that
needs `Storage Blob Data Reader`, then run Terraform again. Because Blob Storage
is private, the reader also needs network access to the VNet/private endpoint,
such as an approved VPN or jump-host path. The reader role alone does not make
the storage account public.

## Rollback

The automatic workflow does not accept arbitrary manual tags. To roll back,
open **Actions → Build and deploy**, identify the previously known-good commit,
and push a revert commit to `master`, or temporarily add a manual dispatch
workflow that invokes `/opt/honeypot/deploy.sh <known-good-tag>`. Either approach
changes only the running container image; Terraform is not needed unless the
infrastructure itself changes.
