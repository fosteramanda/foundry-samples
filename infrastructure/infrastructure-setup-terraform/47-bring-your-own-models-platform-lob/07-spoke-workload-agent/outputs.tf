output "public_endpoint_url" {
  description = "Public URL end-users hit. AGW HTTP:80 with host header."
  value       = "http://${local.listener_hostname}/"
}

output "backend_fqdn" {
  description = "The private data-plane FQDN the AGW forwards to."
  value       = local.agent_endpoint_backend_fqdn
}
