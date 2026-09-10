# Publish the platform contract as a JSON file under ../contracts/.
# spoke-workload/project consumes this file. No secrets are ever placed
# here — only the gateway private URL, audience, and non-secret catalog.
#
# Content is fully deterministic (no timestamp()) so re-apply is a no-op
# unless a real input changes. That way step-2 re-runs don't churn the
# contract file consumed by spokes.

resource "local_file" "hub_workload_contract" {
  filename = var.contract_output_path
  content = jsonencode({
    "$schema" = "https://example.com/contoso/hub-workload-contract-v1.json"
    version   = "1"

    gateway = {
      private_url           = local.apim_gateway_target
      audience              = local.token_audience
      inference_api_version = var.inference_api_version
    }

    catalog = {
      version = "1"
      aliases = local.catalog_aliases
    }
  })

  depends_on = [
    azurerm_private_endpoint.apim,
    azurerm_api_management_api.inference,
  ]
}
