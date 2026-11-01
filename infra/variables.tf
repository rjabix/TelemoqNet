variable "subscription_id" {
  type        = string
  description = "Dedicated honeypot subscription. Do not reuse a work or shared subscription."
}

variable "location" {
  type    = string
  default = "westeurope"
}

variable "name_prefix" {
  type        = string
  default     = "telemoq"
  description = "Lowercase letters and digits only; used in globally unique resource names."

  validation {
    condition     = can(regex("^[a-z0-9]{3,10}$", var.name_prefix))
    error_message = "name_prefix must be 3-10 lowercase letters or digits."
  }
}

variable "github_repo" {
  type        = string
  description = "GitHub repository allowed to deploy, as owner/repo."
}

variable "github_oidc_subject" {
  type        = string
  default     = null
  description = "Optional GitHub OIDC subject prefix. Set this when GitHub includes owner and repository IDs in the subject claim."
}

variable "github_environment" {
  type        = string
  default     = "prod"
  description = "GitHub environment whose workflows may deploy (set required reviewers on it)."
}

variable "admin_ssh_public_key" {
  type        = string
  description = "Public key required by Azure for the VM admin user. No inbound SSH is opened."
}

variable "vm_size" {
  type    = string
  default = "Standard_B2s"
}

variable "container_name" {
  type    = string
  default = "honeypot-sessions"
}

variable "allow_bootstrap_egress" {
  type        = bool
  default     = true
  description = "Allow outbound 80/443 to the internet so cloud-init can install Docker. Set to false after first boot."
}

variable "log_reader_principal_id" {
  type        = string
  default     = null
  nullable    = true
  description = "Optional Entra object ID for a human or group allowed to read captured session blobs."
}

variable "analysis_enabled" {
  type        = bool
  default     = false
  description = "Create isolated on-demand session analysis resources. Execution and ingestion may incur charges."
}

variable "analysis_image_digest" {
  type        = string
  default     = null
  nullable    = true
  description = "Published telemoqnet-session-importer sha256 digest. Null provisions analysis infrastructure without creating the job."
  validation {
    condition     = var.analysis_image_digest == null ? true : can(regex("^sha256:[a-f0-9]{64}$", var.analysis_image_digest))
    error_message = "analysis_image_digest must be null or a sha256: digest (64 lowercase hex characters)."
  }
}

variable "analysis_runtime_enabled" {
  type        = bool
  default     = false
  description = "Temporarily provision the importer job, VNet environment, and runtime identity. Disable after import to remove ongoing managed network charges while retaining tables and Workbook. Requires analysis_enabled."
}
