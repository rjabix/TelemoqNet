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
