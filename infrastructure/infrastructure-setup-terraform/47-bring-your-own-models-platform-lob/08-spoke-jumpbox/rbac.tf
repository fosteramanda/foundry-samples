# RBAC for the human running the demo notebook.
#
# The notebook uses `az login` on the VM. That token carries the human's
# oid. To author agents on the project data plane the human needs
# "Azure AI User" (or higher) at the project scope.
#
# The VM's System-Assigned MI is created for future scenarios (e.g. a
# scripted invocation using IMDS) but no role is granted to it here —
# adding one would enlarge the agent-oid allowlist story and is not
# required by demo.ipynb.

resource "azurerm_role_assignment" "human_ai_user_on_project" {
  scope                = local.project_id
  role_definition_name = "Azure AI Developer"
  principal_id         = var.human_object_id
}
