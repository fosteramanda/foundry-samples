resource "azurerm_api_management_api" "inference" {
  provider              = azurerm.platform
  name                  = "inference"
  resource_group_name   = azurerm_resource_group.platform.name
  api_management_name   = azurerm_api_management.apim.name
  revision              = "1"
  display_name          = "AI Gateway Inference API"
  path                  = local.apim_api_path
  protocols             = ["https"]
  subscription_required = false

  import {
    content_format = "openapi+json"
    content_value = jsonencode({
      openapi = "3.0.1"
      info = {
        title   = "AI Gateway Inference API"
        version = "1.0"
      }
      paths = {
        "/deployments/{deploymentName}/chat/completions" = {
          post = {
            operationId = "ChatCompletions_Create"
            parameters = [
              {
                name     = "deploymentName"
                in       = "path"
                required = true
                schema   = { type = "string" }
              },
              {
                name     = "api-version"
                in       = "query"
                required = false
                schema   = { type = "string" }
              }
            ]
            requestBody = {
              required = true
              content = {
                "application/json" = {
                  schema = { type = "object" }
                }
              }
            }
            responses = {
              "200" = {
                description = "Success"
                content = {
                  "application/json" = {
                    schema = { type = "object" }
                  }
                }
              }
            }
          }
        }
      }
    })
  }
}

resource "azurerm_api_management_api_diagnostic" "appi" {
  provider                  = azurerm.platform
  identifier                = "applicationinsights"
  resource_group_name       = azurerm_resource_group.platform.name
  api_management_name       = azurerm_api_management.apim.name
  api_name                  = azurerm_api_management_api.inference.name
  api_management_logger_id  = azurerm_api_management_logger.appi.id
  sampling_percentage       = 100.0
  always_log_errors         = true
  log_client_ip             = false
  http_correlation_protocol = "W3C"
  verbosity                 = "information"

  frontend_request {
    body_bytes = 0
    headers_to_log = [
      "x-aigw-project-id",
      "x-aigw-model-alias",
      "x-aigw-vendor",
    ]
  }

  frontend_response {
    body_bytes = 0
    headers_to_log = [
      "x-aigw-project-id",
      "x-aigw-model-alias",
      "x-aigw-vendor",
    ]
  }

  backend_request {
    body_bytes = 0
  }

  backend_response {
    body_bytes = 0
  }
}
