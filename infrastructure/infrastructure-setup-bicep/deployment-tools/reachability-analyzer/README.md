# Foundry Reachability Analyzer

Inspect the Azure control-plane configuration between a Foundry agent subnet
and a hostname or IP address. The analyzer explains visible DNS, NSG, route,
and private-endpoint gates using an ASCII network path and a self-contained HTML
report.

This is a **read-only, static tool**, not a hosted agent. It deploys nothing and
does not connect to the target endpoint. It queries Azure through the Azure CLI
and can perform a DNS lookup using the local machine's resolver.

For actual DNS, TCP, TLS, and HTTP checks **from inside the hosted-agent runtime**,
use the existing [diagnostic agent](../../../../samples/python/hosted-agents/bring-your-own/invocations/diagnostic-agent/README.md).
The standalone live probe, Container Apps Job template, and `live`/`both` modes
from the original analyzer are intentionally not included here. A probe in a
different subnet is not proof of connectivity from the agent's actual runtime.

## Prerequisites

- Bash and Python 3.10 or later. Python code uses only the standard library.
- [Azure CLI](https://learn.microsoft.com/cli/azure/install-azure-cli), signed in
  with `az login`.
- Reader access to the Foundry account/project and the relevant networking and
  destination resources. Resources outside your access scope limit the result.

No model deployment, Python package installation, or infrastructure deployment
is required to run the analyzer.

## Usage

From this directory:

```bash
./reachability.sh \
  --account /subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.CognitiveServices/accounts/<account> \
  --endpoint <storage-account>.blob.core.windows.net --port 443
```

Use `--project` instead of `--account` to select a project:

```bash
./reachability.sh \
  --project /subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.CognitiveServices/accounts/<account>/projects/<project> \
  --endpoint <search-service>.search.windows.net --port 443
```

Account/project inputs derive the network mode and the injected agent subnet
when one is exposed. For BYO VNets, you can also select a subnet directly:

```bash
./reachability.sh \
  --subnet-id /subscriptions/<subscription-id>/resourceGroups/<resource-group>/providers/Microsoft.Network/virtualNetworks/<vnet>/subnets/<agent-subnet> \
  --endpoint api.contoso.com --port 443

./reachability.sh -g <resource-group> --vnet <vnet> --subnet <agent-subnet> \
  --endpoint api.contoso.com --port 443
```

Pass hostnames or IP literals, not URLs. The default report is
`reachability-<endpoint>_<port>.html` in the working directory. Use `--html FILE`
to select another path and `--no-open` for headless environments. `--subscription`
accepts a subscription ID or name; it controls discovery, not cross-subscription
access. `--mode static` is accepted for compatibility.

The Python entry point provides the same analysis without opening a browser:

```bash
python3 analyze.py --subnet-id <subnet-resource-id> \
  --endpoint api.contoso.com --port 443 --html report.html
```

Generated reports can contain resource names, IPs, and network configuration.
Keep them private and review them before sharing.

## Interpreting the report

| Verdict | Exit code | Meaning |
|---------|-----------|---------|
| `REACHABLE` | 0 | The configuration inspected by this tool permits the path. This is not a successful connection test. |
| `NOT REACHABLE` | 1 | A visible configuration gate blocks the path. |
| `INDETERMINATE` | 3 | Insufficient visibility or an unsupported configuration prevents a definitive result. |
| Setup/usage error | 2 | Invalid input or a required configuration read failed. Inspect stderr. |

For BYO VNets, the tool inspects private DNS links and records, outbound NSG
precedence, route selection, private-endpoint approval, and peering context.
For Microsoft-managed networks, no customer subnet/NSG/route table is available;
the report uses the exposed Foundry configuration and target access controls,
and flags paths it cannot establish.

Capability-host information is contextual. Basic hosted-agent setups do not
universally require an explicit `capabilityHosts` resource; its absence is not
proof that networking is blocked.

Static analysis cannot reproduce the runtime resolver or fully evaluate custom
DNS forwarding, effective BGP routes, remote peered networks, third-party
firewalls/NVAs, service-tag membership, or all service-specific access controls.
Public DNS resolution alone does not mean a PaaS endpoint is blocked. An
approved private endpoint on a target does not prove that it belongs to the
agent's network.

Use the report to narrow the investigation, then invoke the diagnostic agent
from the affected Foundry project to confirm the actual runtime path. Neither
tool changes networking configuration or grants application access.

## Development

- `reachability.sh`: static CLI wrapper and optional browser launch.
- `analyze.py`: configuration discovery and ASCII/HTML reports.
- `diagnose.py`: NSG and route evaluation.
- `tests/`: offline regression coverage using synthetic configuration and
  mocked CLI/DNS responses.

Run the offline tests from this directory:

```bash
python3 -m unittest discover -s tests -v
bash -n reachability.sh
```
