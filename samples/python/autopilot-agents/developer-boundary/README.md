# Developer Boundary agent

This sample enables you to build a Foundry Autopilot blueprint and configure it
with the Developer Access Boundary. The boundary limits interactions to people
who have developer access while you develop and test the agent.

To learn how Autopilots use their own identities to work as persistent, named
members of an organization, see
[What is an Autopilot in Microsoft Foundry?](https://learn.microsoft.com/en-us/azure/foundry/agents/concepts/autopilot-overview).

## Step 1: Install the prerequisites

Install:

1. [Azure Developer CLI (`azd`)](https://learn.microsoft.com/azure/developer/azure-developer-cli/install-azd)
   1.27.1 or later.
2. [Azure CLI (`az`)](https://learn.microsoft.com/cli/azure/install-azure-cli)
   2.80 or later.
3. Python 3.11 or later.
4. PowerShell 7 or later for the developer-boundary and package-download steps.

To reuse an existing resource group, you need **Owner** on that resource group
because provisioning may create child resources and required role assignments.
To create the resource group, you need equivalent access at subscription scope.
You also need the Microsoft 365 licenses and tenant permissions required by
your organization's agent installation process.

Install the Foundry agent extension so `azd` can provision and deploy hosted
agents. If the extension is already installed, upgrade it instead:

```powershell
azd ext install azure.ai.agents
# If it is already installed:
azd ext upgrade azure.ai.agents
```

## Step 2: Sign in

Authenticate Azure CLI and Azure Developer CLI against the same tenant. These
credentials are used to inspect existing resources, provision missing
resources, and deploy the hosted agent:

```powershell
az login
azd auth login
```

Run all remaining commands from this sample directory.

## Step 3: Create an `azd` environment

Create a local `azd` environment to hold the settings and outputs used by this
sample:

```powershell
azd env new
```

Enter a unique environment name when prompted.

## Step 4: Provision or connect to Foundry resources

Run the guided provisioning workflow:

```powershell
azd provision
```

Select an Azure subscription and location when prompted. Choose a region that
supports both your model and
[hosted agents](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agents#region-availability).

For the resource group, Foundry resource, Foundry project, and model deployment,
choose whether to:

1. **Reuse an existing object.** The command lists objects from the selected
   parent scope and asks you to select one.
2. **Create a new object.** The command asks for the new object's name.

Selections proceed in that order, so each reuse list is limited to the parent
you selected. A new parent requires new child resources. Existing model
deployments can use any supported model and SKU. For a new deployment, enter
only the OpenAI model name. The sample selects the current default version that
supports the `GlobalStandard` SKU in your region and uses capacity `1`. Use the
[Foundry model catalog](https://ai.azure.com/explore/models) to choose values
supported by the selected region.

Reused resources keep their existing configuration. Provisioning creates only
missing resources, ensures the required role assignments, and saves the
resulting values in the active `azd` environment for the next steps.

## Step 5: Deploy the agent

Deploy the Python code to the selected Foundry project. The command creates the
hosted agent with the name `developer-boundary`. Direct code deployment doesn't
require Docker or Azure Container Registry:

```powershell
azd deploy
```

After deployment completes, inspect the hosted agent:

```powershell
azd ai agent show
```

## Step 6: Configure the Developer Access Boundary

Configure the boundary before downloading the app package. The boundary limits
one-to-one, group-chat, and channel interactions to users who have developer
access to the agent.

Run the configuration script:

```powershell
.\scripts\configure-developer-boundary.ps1
```

The script reads the agent configuration back and displays the verified
Developer Access Boundaries.

## Step 7: Download the Microsoft 365 app package

Generate a ZIP package directly from the agent endpoint.

> [!NOTE]
> This sample illustrates how to download the app package, which may only be
> necessary for certain organization requirements. If your organization
> expects you to publish the agent directly to MAC, run
> `azd ai agent publish` from this sample directory. See the
> [hello-world publishing instructions](../hello-world/README.md#sample-specific-commands)
> for the equivalent direct-publication flow. Before publishing, customize the
> values under `activity.publish` in `azure.yaml` for your agent and
> organization.

The developer website must identify who supports the app and how users can
contact them. Use privacy and terms URLs that apply to your app and
organization.

Run the package-download script:

```powershell
.\scripts\download-m365-package.ps1
```

The script prompts for the app-package descriptions, developer information,
privacy statement, and terms-of-use links. It downloads
`<agent-name>-1.0.0.zip` to the current directory.

Provide the downloaded ZIP to the administrator or process responsible for
installing custom Microsoft 365 apps in your organization.

## Step 8: Create an instance of the agent

Before you can create an instance, an **AI Administrator** or **Global
Administrator** must approve the submitted blueprint in
[Agents in the Microsoft 365 admin center](https://admin.cloud.microsoft/?#/agents/all/requested)
and verify that it appears in the Agent 365 registry. This approval is
performed by your organization's administrator.

After your administrator confirms approval, open **Apps** > **Agents for your
team** in Teams, select the approved blueprint, and create an instance.

Tenant app policies can restrict which users discover or use the approved
agent.

## Optional: Change the agent's code and behavior

If you want to make changes to the agent's code or behavior, deploy the updated
version:

```powershell
azd deploy
```

Existing sessions can remain on an older sandbox. Stop them when you want
subsequent messages to use the latest active version:

```powershell
$agentName = azd env get-value AZURE_AI_AGENT_NAME
..\scripts\stop-agent-sessions.ps1 -AgentName $agentName
```

### Make new delegated permissions inheritable

Features that call additional APIs on behalf of a user can require new
delegated permission scopes. An administrator must grant tenant-wide admin
consent for those scopes on the agent's managed blueprint before an agent
instance can inherit them. Follow your organization's approval process or the
[Microsoft Entra admin-consent guidance](https://learn.microsoft.com/entra/identity/enterprise-apps/grant-admin-consent)
to complete that step.

After consent is granted, run:

```powershell
python .\scripts\make-admin-consented-scopes-inheritable.py
```

The script reads the blueprint associated with the deployed agent, queries
Microsoft Entra for all of its delegated permission grants whose consent type
is `AllPrincipals`, groups their scopes by resource API, and sends the complete
set to the blueprint as inheritable permissions. It then reads the blueprint
permissions back and verifies that every admin-consented scope is inheritable.
The script does not request or grant admin consent.

Your signed-in account must be able to read the blueprint's delegated
permission grants and update the managed blueprint. To inspect what the script
would apply without changing the blueprint, run:

```powershell
python .\scripts\make-admin-consented-scopes-inheritable.py --dry-run
```

If the deployed agent is not in your default `azd` environment, add
`--environment <environment-name>` to either command.

## Troubleshooting

- If provisioning can't resolve a new model deployment, verify that the OpenAI
  model has a default version supporting `GlobalStandard` in the selected
  region.
- If deployment can't resolve the agent or model name, rerun `azd provision`
  and confirm it completed successfully before running `azd deploy`.
- If the boundary or ZIP request returns 401 or 403, verify that `az` is signed
  in to the correct tenant and that your account can manage the Foundry
  project.
- If the boundary request returns 404, confirm `FOUNDRY_PROJECT_ENDPOINT` and
  `AZURE_AI_AGENT_NAME` belong to the active `azd` environment.

## Understand how the agent works

`agent/app.py` hosts the M365 Agents SDK application at
`POST /activity/messages`. It routes supported Teams messages to the model
deployment selected during provisioning:

- Teams direct messages
- Teams group-chat messages
- Teams channel messages that tag the agent

The platform injects the Foundry project endpoint into the hosted runtime. The
sample passes `AZURE_AI_MODEL_DEPLOYMENT_NAME` from the active `azd`
environment.

## Observability

The agent initializes the
[Microsoft OpenTelemetry Distro](https://learn.microsoft.com/microsoft-agent-365/developer/microsoft-opentelemetry?tabs=python)
before importing the application stack. Foundry supplies Azure Monitor
configuration to the hosted container when it is available.

## References

- [Quickstart: Deploy a hosted agent](https://learn.microsoft.com/azure/foundry/agents/quickstarts/quickstart-hosted-agent)
- [Hosted-agent permissions](https://learn.microsoft.com/azure/foundry/agents/concepts/hosted-agent-permissions)
- [Create a Foundry project](https://learn.microsoft.com/azure/foundry/how-to/create-projects)
- [Deploy Foundry models](https://learn.microsoft.com/azure/foundry/foundry-models/how-to/create-model-deployments)
- [Add custom prompts to `azd`](https://learn.microsoft.com/azure/developer/azure-developer-cli/custom-prompts)
