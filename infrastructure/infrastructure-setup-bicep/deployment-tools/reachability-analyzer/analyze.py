#!/usr/bin/env python3
"""Static path reports ported from the original foundry-reachability-analyzer.

Reads Azure DNS, NSG, route, Private Endpoint, and target configuration using az.
No packets or deployments; local DNS is only a cross-check, not subnet evidence.
Exit codes: permitted 0, blocked 1, setup/read error 2, indeterminate 3.
For runtime evidence use the repository's existing diagnostic agent.
"""

import argparse
import ipaddress
import json
import re
import socket
import subprocess
import sys

from diagnose import evaluate_nsg, evaluate_routes


DIAGNOSTIC_AGENT = (
    "samples/python/hosted-agents/bring-your-own/invocations/diagnostic-agent"
)


# --------------------------------------------------------------------------- #
# az helpers
# --------------------------------------------------------------------------- #
class AzError(Exception):
    pass


def az(args, sub=None, allow_fail=False):
    """Read Azure JSON; failed or empty reads always raise, even for optional gates."""
    cmd = ["az"] + args + ["-o", "json"]
    if sub:
        cmd += ["--subscription", sub]
    try:
        out = subprocess.run(cmd, capture_output=True, text=True, timeout=120)
    except FileNotFoundError as e:
        raise AzError("az CLI not found") from e
    except subprocess.TimeoutExpired as e:
        raise AzError(f"az timed out: {' '.join(args)}") from e
    if out.returncode != 0:
        raise AzError(out.stderr.strip() or f"az failed: {' '.join(args)}")
    txt = (out.stdout or "").strip()
    try:
        data = json.loads(txt)
    except json.JSONDecodeError as e:
        raise AzError(
            f"az returned invalid JSON for {' '.join(args)}: {out.stderr.strip()}"
        ) from e
    if data is None or data == {} or not isinstance(data, (list, dict)):
        raise AzError(
            f"az returned no resource data for {' '.join(args)}: {out.stderr.strip()}"
        )
    return data


def sub_of(resource_id):
    m = re.search(r"/subscriptions/([^/]+)/", resource_id or "", re.IGNORECASE)
    return m.group(1) if m else None


def rg_of(resource_id):
    m = re.search(r"/resourceGroups/([^/]+)/", resource_id or "", re.IGNORECASE)
    return m.group(1) if m else None


# --------------------------------------------------------------------------- #
# Report accumulation
# --------------------------------------------------------------------------- #
class Report:
    """Accumulates the ordered gates (hops) a packet traverses, each with a
    pass/block/unknown status, so we can both explain and *draw* the path."""

    def __init__(self, source_label, target_label, source_sub="Foundry agent subnet"):
        self.source_label = source_label
        self.target_label = target_label
        self.source_sub = source_sub
        self.hops = []  # ordered: [{gate,status,title,detail}]
        self.blockers = []  # things that make it NOT REACHABLE
        self.opaque = []  # things that make it INDETERMINATE
        self.fixes = []  # actionable remediation

    def hop(self, gate, status, title, detail="", block=None, unknown=None, fix=None):
        """Record one gate. status in {'ok','block','unknown','info'}."""
        self.hops.append(
            {"gate": gate, "status": status, "title": title, "detail": detail}
        )
        if block:
            self.blockers.append(block)
        if unknown:
            self.opaque.append(unknown)
        if fix:
            self.fixes.append(fix)

    # Back-compat helpers used by the gate logic.
    def block(self, msg, fix=None):
        self.blockers.append(msg)
        if fix:
            self.fixes.append(fix)

    def unknown(self, msg, fix=None):
        self.opaque.append(msg)
        if fix:
            self.fixes.append(fix)

    def verdict(self):
        if self.blockers:
            return "NOT REACHABLE", 1
        if self.opaque:
            return "INDETERMINATE", 3
        return "REACHABLE", 0


# --------------------------------------------------------------------------- #
# Visual path diagram
# --------------------------------------------------------------------------- #
_GLYPH = {"ok": "[ OK ]", "block": "[BLOCK]", "unknown": "[ ?? ]", "info": "[ -- ]"}


def render_ascii_diagram(rpt):
    """Draw the packet path top-to-bottom, one gate per node, marking the first
    blocking / opaque hop."""
    verdict, _ = rpt.verdict()
    width = 66

    def box(label, sub=""):
        text = label if not sub else f"{label}  {sub}"
        text = text[: width - 4]
        bar = "+" + "-" * (width - 2) + "+"
        return [bar, "| " + text.ljust(width - 4) + " |", bar]

    out = ["", "NETWORK PATH", "=" * width]
    out += box("SOURCE: " + rpt.source_label)

    first_break = None
    for i, h in enumerate(rpt.hops):
        if h["status"] in ("block", "unknown") and first_break is None:
            first_break = i

    for i, h in enumerate(rpt.hops):
        out.append("      |")
        out.append("      v")
        glyph = _GLYPH.get(h["status"], "[ -- ]")
        head = f"  {glyph} {h['gate']:<6} {h['title']}"
        out.append(head)
        if h["detail"]:
            for seg in _wrap(h["detail"], width - 12):
                out.append("           " + seg)
        if i == first_break:
            marker = (
                "<<<< BLOCKED HERE"
                if h["status"] == "block"
                else "<<<< CANNOT VERIFY PAST HERE"
            )
            out.append("      " + marker)

    out.append("      |")
    out.append("      v")
    out += box("TARGET: " + rpt.target_label)
    out.append(f"        =>  {verdict}")
    out.append("=" * width)
    return "\n".join(out)


def _wrap(text, width):
    words, line, lines = text.split(), "", []
    for w in words:
        if len(line) + len(w) + 1 > width and line:
            lines.append(line)
            line = w
        else:
            line = (line + " " + w).strip()
    if line:
        lines.append(line)
    return lines


def render_html(rpt):
    """Emit a self-contained HTML report (no external assets, no JS libraries).
    Customers just open the file in any browser to see the path, the blocking
    hop, and the fixes - no Mermaid or tooling knowledge required."""
    import html as _h

    verdict, _ = rpt.verdict()
    vmeta = {
        "REACHABLE": ("#1b5e20", "#e8f5e9", "&#10004;"),
        "NOT REACHABLE": ("#b71c1c", "#ffebee", "&#10008;"),
        "INDETERMINATE": ("#e65100", "#fff8e1", "&#63;"),
    }[verdict]
    node_color = {
        "ok": "#2e7d32",
        "block": "#c62828",
        "unknown": "#f9a825",
        "info": "#607d8b",
    }
    node_bg = {
        "ok": "#f1f8f2",
        "block": "#fdecec",
        "unknown": "#fffaf0",
        "info": "#f5f7f8",
    }
    node_icon = {
        "ok": "&#10004;",
        "block": "&#10008;",
        "unknown": "&#63;",
        "info": "&#8226;",
    }

    first_break = next(
        (i for i, h in enumerate(rpt.hops) if h["status"] in ("block", "unknown")), None
    )

    def endpoint_card(kind, title, sub):
        return (
            f'<div class="endpoint"><div class="kind">{kind}</div>'
            f'<div class="etitle">{_h.escape(title)}</div>'
            f'<div class="esub">{_h.escape(sub)}</div></div>'
        )

    parts = []
    parts.append(endpoint_card("SOURCE", rpt.source_label, rpt.source_sub))
    for i, h in enumerate(rpt.hops):
        parts.append('<div class="arrow">&#8595;</div>')
        c = node_color.get(h["status"], "#607d8b")
        bg = node_bg.get(h["status"], "#f5f7f8")
        icon = node_icon.get(h["status"], "&#8226;")
        broke = i == first_break
        ribbon = ""
        if broke:
            label = (
                "BLOCKED HERE" if h["status"] == "block" else "CANNOT VERIFY PAST HERE"
            )
            ribbon = f'<div class="ribbon" style="background:{c}">{label}</div>'
        detail = (
            f'<div class="detail">{_h.escape(h["detail"])}</div>' if h["detail"] else ""
        )
        parts.append(
            f'<div class="node" style="border-color:{c};background:{bg}'
            + ("" if not broke else f";box-shadow:0 0 0 3px {c}55")
            + '">'
            f"{ribbon}"
            f'<div class="node-head"><span class="icon" style="color:{c}">{icon}</span>'
            f'<span class="gate">{_h.escape(h["gate"])}</span>'
            f'<span class="ntitle">{_h.escape(h["title"])}</span></div>'
            f"{detail}</div>"
        )
    parts.append('<div class="arrow">&#8595;</div>')
    parts.append(endpoint_card("TARGET", rpt.target_label, verdict))
    path_html = "\n".join(parts)

    def ul(items):
        return "<ul>" + "".join(f"<li>{_h.escape(x)}</li>" for x in items) + "</ul>"

    sections = ""
    if rpt.blockers:
        sections += f'<div class="sec block"><h2>&#10008; What is blocking reachability</h2>{ul(rpt.blockers)}</div>'
    if rpt.opaque:
        sections += f'<div class="sec warn"><h2>&#63; Cannot confirm statically</h2>{ul(rpt.opaque)}</div>'
    if rpt.fixes:
        seen = list(dict.fromkeys(rpt.fixes))
        sections += (
            f'<div class="sec fix"><h2>&#128295; Suggested fixes</h2>{ul(seen)}</div>'
        )
    if verdict == "REACHABLE":
        sections += (
            '<div class="sec ok"><h2>&#10004; Modeled path is open</h2><p>The inspected '
            "configuration permits this path; this is not a connectivity test. "
            f"For runtime evidence use {_h.escape(DIAGNOSTIC_AGENT)} in the relevant "
            "network context.</p></div>"
        )

    legend = (
        '<div class="legend">'
        '<span><i style="background:#2e7d32"></i>Allowed</span>'
        '<span><i style="background:#c62828"></i>Blocked</span>'
        '<span><i style="background:#f9a825"></i>Cannot verify</span>'
        "</div>"
    )

    return f"""<!doctype html>
<html lang="en"><head><meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>Foundry reachability - {_h.escape(rpt.target_label)}</title>
<style>
 :root{{font-family:-apple-system,Segoe UI,Roboto,Helvetica,Arial,sans-serif}}
 body{{margin:0;background:#eef1f4;color:#1f2933;padding:24px}}
 .wrap{{max-width:820px;margin:0 auto}}
 .banner{{background:{vmeta[1]};border:1px solid {vmeta[0]}33;border-left:8px solid {vmeta[0]};
   border-radius:10px;padding:18px 22px;margin-bottom:22px}}
 .banner .v{{font-size:26px;font-weight:700;color:{vmeta[0]}}}
 .banner .t{{font-size:15px;color:#3b4653;margin-top:4px}}
 .legend{{display:flex;gap:18px;margin:8px 0 20px;font-size:13px;color:#52606d}}
 .legend i{{display:inline-block;width:12px;height:12px;border-radius:3px;margin-right:6px;vertical-align:middle}}
 .path{{display:flex;flex-direction:column;align-items:stretch}}
 .endpoint{{background:#37474f;color:#fff;border-radius:10px;padding:14px 18px;text-align:center}}
 .endpoint .kind{{font-size:11px;letter-spacing:2px;opacity:.8}}
 .endpoint .etitle{{font-size:17px;font-weight:600;margin-top:2px;word-break:break-all}}
 .endpoint .esub{{font-size:12px;opacity:.85;margin-top:2px}}
 .arrow{{text-align:center;font-size:22px;color:#90a4ae;line-height:1.1}}
 .node{{position:relative;border:2px solid;border-radius:10px;padding:12px 16px}}
 .node-head{{display:flex;align-items:center;gap:10px}}
 .node .icon{{font-size:18px;font-weight:700}}
 .node .gate{{font-size:11px;font-weight:700;letter-spacing:1px;background:#0000000d;
   padding:2px 8px;border-radius:20px;color:#37474f}}
 .node .ntitle{{font-size:15px;font-weight:600}}
 .node .detail{{font-size:13px;color:#52606d;margin-top:6px;line-height:1.4;word-break:break-word}}
 .ribbon{{position:absolute;top:-11px;right:14px;color:#fff;font-size:11px;font-weight:700;
   letter-spacing:1px;padding:2px 10px;border-radius:20px}}
 .sec{{border-radius:10px;padding:14px 20px;margin-top:18px;border:1px solid #0000000f}}
 .sec h2{{font-size:15px;margin:0 0 8px}}
 .sec ul{{margin:0;padding-left:20px}} .sec li{{margin:5px 0;line-height:1.45}}
 .sec.block{{background:#fdecec}} .sec.warn{{background:#fff8e1}}
 .sec.fix{{background:#eef4fb}} .sec.ok{{background:#e8f5e9}}
 .foot{{margin-top:24px;font-size:12px;color:#7b8794;text-align:center}}
</style></head>
<body><div class="wrap">
 <div class="banner"><div class="v">{vmeta[2]} {verdict}</div>
   <div class="t">{_h.escape(rpt.target_label)} &nbsp;from&nbsp; {_h.escape(rpt.source_label)}</div></div>
 {legend}
 <div class="path">{path_html}</div>
 {sections}
 <div class="foot">Static reachability analysis &middot; read-only, no packets sent &middot; generated by foundry-reachability-analyzer</div>
</div></body></html>"""


# --------------------------------------------------------------------------- #
# DNS: what does the subnet resolve the name to?
# --------------------------------------------------------------------------- #
def public_resolve(host, port):
    """Query the local machine's resolver, which may itself use private DNS."""
    try:
        infos = socket.getaddrinfo(host, port, proto=socket.IPPROTO_TCP)
        return sorted({i[4][0] for i in infos})
    except socket.gaierror as exc:
        raise AzError(f"Local DNS cross-check failed for {host}: {exc}") from exc


def find_private_dns_answer(endpoint, vnet_id, sub):
    """Return (ip, zone_name, linked, detail) using Private DNS zones linked to
    the VNet. `linked` is True only when a matching zone is linked to the VNet."""
    # Enumerate zones in the subscription once.
    zones = az(["network", "private-dns", "zone", "list"], sub=sub) or []
    host = endpoint.lower().rstrip(".")

    # Candidate (zone_name, record_label) pairs, longest base first.
    candidates = []
    for z in zones:
        zname = z["name"].lower().rstrip(".")
        base = (
            zname[len("privatelink.") :] if zname.startswith("privatelink.") else zname
        )
        # host must sit inside this zone's namespace.
        for suffix in {zname, base}:
            if host == suffix:
                candidates.append((z, ""))
            elif host.endswith("." + suffix):
                label = host[: -(len(suffix) + 1)]
                candidates.append((z, label))
    # Prefer the most specific (longest zone name) match.
    candidates.sort(key=lambda c: len(c[0]["name"]), reverse=True)

    unlinked = None
    for z, label in candidates:
        zrg = rg_of(z["id"]) or None
        zsub = sub_of(z["id"]) or sub
        links = (
            az(
                [
                    "network",
                    "private-dns",
                    "link",
                    "vnet",
                    "list",
                    "-g",
                    zrg,
                    "-z",
                    z["name"],
                ],
                sub=zsub,
            )
            or []
        )
        matching_links = [
            link
            for link in links
            if (link.get("virtualNetwork") or {}).get("id", "").lower()
            == vnet_id.lower()
        ]
        linked = bool(matching_links)
        want = label or "@"
        ips = []
        for family, field, address in (
            ("a", "aRecords", "ipv4Address"),
            ("aaaa", "aaaaRecords", "ipv6Address"),
        ):
            recs = az(
                [
                    "network",
                    "private-dns",
                    "record-set",
                    family,
                    "list",
                    "-g",
                    zrg,
                    "-z",
                    z["name"],
                ],
                sub=zsub,
            )
            for r in recs:
                if r.get("name", "").lower() == want.lower():
                    ips.extend(a[address] for a in (r.get(field) or []))
        if linked:
            ready = all(
                link.get("provisioningState") == "Succeeded"
                and link.get("virtualNetworkLinkState") == "Completed"
                for link in matching_links
            )
            detail = "linked" if ready else "link state not confirmed"
            if any(
                link.get("resolutionPolicy") == "NxDomainRedirect"
                for link in matching_links
            ):
                detail += "; NXDOMAIN fallback configured"
            return sorted(set(ips)), z["name"], True, detail
        if ips and unlinked is None:
            unlinked = (ips, z["name"], False, "NOT linked")
    if unlinked:
        return unlinked
    return [], None, False, "no matching private zone"


def resolve(endpoint, port, vnet_id, sub, rpt, custom_dns=False):
    """Return configured or candidate addresses, explicitly labeling DNS uncertainty."""
    # IP literal?
    try:
        ipaddress.ip_address(endpoint)
        rpt.hop(
            "DNS", "info", "endpoint is an IP literal", f"{endpoint} - no DNS needed."
        )
        return [endpoint]
    except ValueError:
        pass

    priv_ips, zone, linked, detail = find_private_dns_answer(endpoint, vnet_id, sub)
    if custom_dns:
        rpt.hop(
            "DNS",
            "unknown",
            "custom DNS servers",
            "Linked Azure zones may be overridden or require forwarding by custom DNS.",
            unknown="Subnet DNS answers cannot be inferred with custom DNS servers.",
        )
    if linked and priv_ips:
        # PaaS public names need a CNAME chain; a matching privatelink suffix alone
        # does not establish that this particular service uses the private zone.
        direct = endpoint.lower().rstrip(".").endswith("." + zone.lower())
        confirmed = detail == "linked" and direct and not custom_dns
        rpt.hop(
            "DNS",
            "ok" if confirmed else "unknown",
            "linked Private DNS records",
            f"Zone '{zone}': {endpoint} -> {', '.join(priv_ips)} ({detail}).",
            unknown=None
            if confirmed
            else "Private DNS link/forwarding or the public-name CNAME chain is not verified.",
        )
        return priv_ips
    if linked:
        rpt.hop(
            "DNS",
            "unknown",
            "linked private zone has no address record",
            f"Zone '{zone}' is linked but has no A/AAAA record for '{endpoint}' ({detail}). "
            "CNAME, wildcard records, or NXDOMAIN fallback have not been evaluated.",
            unknown="Linked private namespace has no modeled answer; public DNS is not a substitute.",
            fix="Inspect the linked zone's records and any custom DNS forwarding.",
        )
        return []
    if priv_ips:
        rpt.hop(
            "DNS",
            "info",
            "Private DNS zone not linked",
            f"Zone '{zone}' has {', '.join(priv_ips)} but is not linked to this VNet. "
            "This alone does not block public access or custom DNS forwarding.",
        )
    try:
        pub = public_resolve(endpoint, port)
    except AzError as exc:
        rpt.hop(
            "DNS", "unknown", "local DNS cross-check failed", str(exc), unknown=str(exc)
        )
        return []
    if pub:
        rpt.hop(
            "DNS",
            "unknown",
            "local DNS cross-check (not subnet DNS)",
            f"Local resolver: '{endpoint}' -> {', '.join(pub)}. Public PaaS addresses "
            "alone do not imply a block; target access controls must be inspected.",
            unknown="Local DNS answers are candidate paths, not proof of subnet resolution.",
        )
        return pub
    rpt.hop(
        "DNS",
        "unknown",
        "no local DNS answer",
        detail,
        unknown=f"No candidate address for '{endpoint}'; subnet resolution is unknown.",
    )
    return []


PAAS_SUFFIXES = (
    "database.windows.net",
    "blob.core.windows.net",
    "table.core.windows.net",
    "queue.core.windows.net",
    "file.core.windows.net",
    "dfs.core.windows.net",
    "vault.azure.net",
    "azurecr.io",
    "servicebus.windows.net",
    "documents.azure.com",
    "cognitiveservices.azure.com",
    "openai.azure.com",
    "search.windows.net",
    "azurewebsites.net",
    "azureml.ms",
    "api.azureml.ms",
    "services.ai.azure.com",
)


def _looks_like_paas(host):
    h = host.lower()
    return "privatelink" in h or any(h.endswith("." + s) for s in PAAS_SUFFIXES)


# Map a PaaS host suffix -> (ARM resource type, api-version) so we can read the
# target service's own access controls (publicNetworkAccess / firewall / PE
# connections). Used by the managed-VNet path, where the only gates that matter
# are the managed network's egress and the *target's* own controls.
_PAAS_RESOURCE = [
    ("blob.core.windows.net", "Microsoft.Storage/storageAccounts", "2023-05-01"),
    ("file.core.windows.net", "Microsoft.Storage/storageAccounts", "2023-05-01"),
    ("table.core.windows.net", "Microsoft.Storage/storageAccounts", "2023-05-01"),
    ("queue.core.windows.net", "Microsoft.Storage/storageAccounts", "2023-05-01"),
    ("dfs.core.windows.net", "Microsoft.Storage/storageAccounts", "2023-05-01"),
    ("vault.azure.net", "Microsoft.KeyVault/vaults", "2023-07-01"),
    ("azurecr.io", "Microsoft.ContainerRegistry/registries", "2023-07-01"),
    ("documents.azure.com", "Microsoft.DocumentDB/databaseAccounts", "2024-05-15"),
    ("search.windows.net", "Microsoft.Search/searchServices", "2023-11-01"),
    ("database.windows.net", "Microsoft.Sql/servers", "2023-08-01"),
    ("servicebus.windows.net", "Microsoft.ServiceBus/namespaces", "2022-10-01-preview"),
    (
        "cognitiveservices.azure.com",
        "Microsoft.CognitiveServices/accounts",
        "2025-06-01",
    ),
    ("openai.azure.com", "Microsoft.CognitiveServices/accounts", "2025-06-01"),
    ("services.ai.azure.com", "Microsoft.CognitiveServices/accounts", "2025-06-01"),
]


def _target_resource_name(host):
    """The first DNS label is the resource name for the PaaS types we map."""
    return host.split(".")[0]


def inspect_target(host, sub):
    """Best-effort read of an Azure PaaS target's own access controls.

    Absent access-control fields are unknown, never implicit allow.
    Failed Azure reads raise AzError rather than masquerading as absent resources.
    """
    out = {
        "found": False,
        "id": None,
        "type": None,
        "name": None,
        "pna": None,
        "firewall": None,
        "pe": [],
    }
    h = host.lower().rstrip(".")
    h = h.replace("privatelink.", "")
    match = next((m for m in _PAAS_RESOURCE if h.endswith("." + m[0])), None)
    if not match:
        return out
    suffix, rtype, apiver = match
    name = _target_resource_name(h)
    found = (
        az(["resource", "list", "--name", name, "--resource-type", rtype], sub=sub)
        or []
    )
    if len(found) != 1:
        return out
    rid = found[0].get("id")
    res = az(["resource", "show", "--ids", rid, "--api-version", apiver], sub=sub) or {}
    props = res.get("properties") or {}
    out.update(found=True, id=rid, type=rtype, name=name)
    out["pna"] = props.get("publicNetworkAccess")
    # Firewall / selected-networks default action varies by RP.
    acls = props.get("networkAcls") or props.get("networkRuleSet") or {}
    default_action = acls.get("defaultAction")
    modeled_firewalls = {
        "Microsoft.Storage/storageAccounts",
        "Microsoft.KeyVault/vaults",
        "Microsoft.ContainerRegistry/registries",
        "Microsoft.CognitiveServices/accounts",
    }
    if default_action and rtype in modeled_firewalls:
        out["firewall"] = default_action  # 'Allow' or 'Deny'
    for pec in props.get("privateEndpointConnections") or []:
        p = pec.get("properties") or pec
        status = (p.get("privateLinkServiceConnectionState") or {}).get("status")
        out["pe"].append(
            {
                "name": pec.get("name")
                or (p.get("privateEndpoint") or {}).get("id", ""),
                "status": status or "Unknown",
            }
        )
    return out


# --------------------------------------------------------------------------- #
# Foundry account network-mode detection (managed VNet vs BYO VNet)
# --------------------------------------------------------------------------- #
def read_foundry_network(account_id, sub):
    """Read a Foundry (AIServices) account's agent network injection.

    Returns {name, managed(bool), subnet_id, pna}. `managed` is True when the
    agent runs in the Microsoft-managed network (useMicrosoftManagedNetwork),
    in which case there is no customer subnet to walk.
    """
    acct = az(
        ["resource", "show", "--ids", account_id, "--api-version", "2025-06-01"],
        sub=sub,
        allow_fail=False,
    )
    if not acct:
        raise AzError(f"could not read Foundry account {account_id}")
    props = acct.get("properties") or {}
    injections = props.get("networkInjections") or []
    agent = next((i for i in injections if i.get("scenario") == "agent"), None)
    managed = bool(agent and agent.get("useMicrosoftManagedNetwork"))
    return {
        "name": acct.get("name"),
        "managed": managed,
        "subnet_id": (agent or {}).get("subnetArmId"),
        "pna": props.get("publicNetworkAccess"),
        "has_injection": agent is not None,
    }


# --------------------------------------------------------------------------- #
# Optional Standard setup capability host context
# --------------------------------------------------------------------------- #
def _list_capability_hosts(account_id, sub, project_id=None):
    """Read only the requested scope, never enumerate unrelated projects."""
    api = "2025-06-01"
    scope = project_id or account_id
    base = f"https://management.azure.com{scope}"
    acct = (
        az(
            [
                "rest",
                "--method",
                "GET",
                "--url",
                f"{base}/capabilityHosts?api-version={api}",
            ],
            sub=sub,
        )
        or {}
    )
    if not isinstance(acct.get("value"), list) or acct.get("nextLink"):
        raise AzError(f"Incomplete capability host listing for {scope}")
    label = f"project '{project_id.split('/')[-1]}'" if project_id else "account"
    return [(label, h) for h in acct["value"]]


def find_account_for_subnet(subnet_id, sub):
    """Find an account with explicit network injection in the subnet's RG."""
    rg = rg_of(subnet_id)
    if not rg:
        return None
    accts = az(["cognitiveservices", "account", "list", "-g", rg], sub=sub) or []
    for a in accts:
        for injection in (a.get("properties") or {}).get("networkInjections") or []:
            cs = injection.get("subnetArmId") or ""
            if cs.lower() == subnet_id.lower():
                return a.get("id")
    return None


def check_capability_host(account_id, sub, rpt, project_id=None):
    """Report explicit Standard hosts; Basic setup legitimately omits them."""
    try:
        hosts = _list_capability_hosts(account_id, sub, project_id)
    except AzError as exc:
        rpt.hop(
            "CAPHOST",
            "unknown",
            "optional capability host context unavailable",
            str(exc),
            unknown=str(exc),
        )
        return
    agent_hosts = [
        (s, h)
        for s, h in hosts
        if (h.get("properties") or {}).get("capabilityHostKind") == "Agents"
    ]
    if not agent_hosts:
        rpt.hop(
            "CAPHOST",
            "info",
            "no explicit Agents capability host",
            "Basic hosted-agent setups legitimately omit capabilityHosts. Absence is not "
            "a network blocker; this does not verify that a runtime is deployed.",
        )
        return
    for s, h in agent_hosts:
        st = (h.get("properties") or {}).get("provisioningState")
        rpt.hop(
            "CAPHOST",
            "info" if st == "Succeeded" else "unknown",
            "explicit Agents capability host",
            f"{s} capability host '{h.get('name')}' state={st}.",
            unknown=None
            if st == "Succeeded"
            else f"Explicit Agents capability host state={st}; runtime readiness is unknown.",
        )


# --------------------------------------------------------------------------- #
# Private Endpoint approval state for a resolved private IP
# --------------------------------------------------------------------------- #
def check_private_endpoint(ip, vnet_id, sub, rpt):
    """If `ip` belongs to a Private Endpoint NIC in this VNet, report its
    connection approval state."""
    vnet_rg = rg_of(vnet_id)
    # PEs are commonly in the VNet's RG; search there first, then the whole sub.
    scopes = [(["-g", vnet_rg], vnet_rg)] if vnet_rg else []
    scopes.append(([], None))
    seen = set()
    for scope_args, _ in scopes:
        pes = az(["network", "private-endpoint", "list"] + scope_args, sub=sub) or []
        for pe in pes:
            pid = pe.get("id")
            if pid in seen:
                continue
            seen.add(pid)
            pe_subnet = (pe.get("subnet") or {}).get("id", "")
            pe_vnet = re.sub(r"/subnets/[^/]+/?$", "", pe_subnet, flags=re.IGNORECASE)
            if pe_vnet.lower() != vnet_id.lower():
                continue
            nic_ips = []
            for conf in pe.get("customDnsConfigs") or []:
                nic_ips += conf.get("ipAddresses") or []
            # Only read the NIC directly when the PE object didn't expose IPs.
            if not nic_ips:
                for nic in pe.get("networkInterfaces") or []:
                    nic_obj = (
                        az(["network", "nic", "show", "--ids", nic["id"]], sub=sub)
                        or {}
                    )
                    for ipconf in nic_obj.get("ipConfigurations") or []:
                        if ipconf.get("privateIPAddress"):
                            nic_ips.append(ipconf["privateIPAddress"])
            if ip in nic_ips:
                conns = (pe.get("privateLinkServiceConnections") or []) + (
                    pe.get("manualPrivateLinkServiceConnections") or []
                )
                states = []
                target = None
                for c in conns:
                    st = (c.get("privateLinkServiceConnectionState") or {}).get(
                        "status"
                    )
                    states.append((st or "Unknown").lower())
                    target = c.get("privateLinkServiceId") or target
                tname = target.split("/")[-1] if target else pe.get("name")
                state = ", ".join(states) or "Unknown"
                if states and all(s == "approved" for s in states):
                    rpt.hop(
                        "PE",
                        "ok",
                        "Private Endpoint approved",
                        f"{ip} is Private Endpoint '{pe.get('name')}' -> {tname}, connection APPROVED.",
                    )
                elif states and all(
                    s in ("pending", "rejected", "disconnected") for s in states
                ):
                    rpt.hop(
                        "PE",
                        "block",
                        f"Private Endpoint {state}",
                        f"{ip} is Private Endpoint '{pe.get('name')}', connection state = {state}.",
                        block=f"Private Endpoint '{pe.get('name')}' connection is '{state}', not "
                        f"'Approved' - traffic to {ip} will be dropped.",
                        fix=f"Approve the private endpoint connection on the target resource ({tname}).",
                    )
                else:
                    rpt.hop(
                        "PE",
                        "unknown",
                        "Private Endpoint state unknown",
                        f"{ip} is Private Endpoint '{pe.get('name')}': {state}.",
                        unknown=f"Private Endpoint approval for {ip} is not confirmed.",
                    )
                return True
    return False


# --------------------------------------------------------------------------- #
# Managed-VNet analysis (Microsoft-managed agent network)
# --------------------------------------------------------------------------- #
def check_target(endpoint, sub, rpt, ip=None, private_endpoint=False):
    """Inspect target controls without assuming missing fields permit access."""
    if private_endpoint:
        rpt.hop(
            "TARGET",
            "info",
            "Private Endpoint path",
            "Approval is reported above. Public access controls do not govern this private "
            "path; service authentication and application behavior are outside this model.",
        )
        return
    tgt = inspect_target(endpoint, sub)
    if not tgt["found"]:
        rpt.hop(
            "TARGET",
            "unknown",
            "target access controls unavailable",
            f"No unique modeled Azure resource for '{endpoint}' in subscription {sub}.",
            unknown="Target firewall and inbound controls are not verified.",
        )
        return
    pna = (tgt.get("pna") or "").lower()
    fw = (tgt.get("firewall") or "").lower()
    detail = (
        f"'{tgt['name']}' publicNetworkAccess={tgt.get('pna') or 'Unknown'}, "
        f"firewall defaultAction={tgt.get('firewall') or 'Unknown'}."
    )
    if pna == "disabled" and ip is not None and ip.is_global:
        rpt.hop(
            "TARGET",
            "block",
            "target disables public access",
            detail,
            block=f"Target '{tgt['name']}' disables access through public IP {ip}.",
            fix="Verify an approved private endpoint and its DNS path for this source.",
        )
    elif pna == "enabled" and fw == "allow" and (ip is None or ip.is_global):
        rpt.hop("TARGET", "ok", "explicit public access controls permit access", detail)
    else:
        connections = ", ".join(p["status"] for p in tgt["pe"]) or "none reported"
        rpt.hop(
            "TARGET",
            "unknown",
            "target access path not confirmed",
            detail
            + f" Private Endpoint states: {connections}. A target-side connection "
            "does not establish that it belongs to this source network.",
            unknown="Target restrictions, private path, or unmodeled firewall require verification.",
        )


def analyze_managed(
    account_id, endpoint, port, sub, net, html_path=None, project_id=None
):
    """Report managed network context without claiming visibility of its data path."""
    acct = net["name"]
    source_label = f"{acct} - Microsoft-managed agent network"
    rpt = Report(
        source_label, f"{endpoint}:{port}", source_sub="Microsoft-managed agent network"
    )

    print(f"Static reachability analysis (managed VNet): {endpoint}:{port}")
    print(f"  from Foundry account '{acct}' (Microsoft-managed agent network)")
    print()

    rpt.hop(
        "MODE",
        "info",
        "Foundry managed VNet",
        f"Account '{acct}' runs agents in a Microsoft-managed network "
        f"(useMicrosoftManagedNetwork=true). There is no customer subnet, NSG, or route "
        f"table; reachability is governed by the managed network's egress and the target's "
        f"own access controls.",
    )

    check_capability_host(account_id, sub, rpt, project_id)
    rpt.hop(
        "EGRESS",
        "unknown",
        "managed network data path not visible",
        "Customer-side ARM reads do not expose this runtime's effective DNS, routes, "
        "egress policies, or association with target Private Endpoints.",
        unknown="Managed-network reachability cannot be proven from target controls alone.",
    )
    check_target(endpoint, sub, rpt)
    return _finish(rpt, endpoint, port, html_path, managed=True)


def _finish(rpt, endpoint, port, html_path, managed=False):
    """Shared verdict printing + optional HTML output for both paths."""
    verdict, code = rpt.verdict()
    print(render_ascii_diagram(rpt))
    print()
    print(f"VERDICT: {endpoint}:{port} is {verdict} (static analysis)")
    if rpt.blockers:
        print("\nBLOCKING CONFIGURATION:")
        for i, b in enumerate(rpt.blockers, 1):
            print(f"  {i}. {b}")
    if rpt.opaque:
        print("\nCANNOT CONFIRM STATICALLY (opaque hop / limited visibility):")
        for i, o in enumerate(rpt.opaque, 1):
            print(f"  {i}. {o}")
    if rpt.fixes:
        print("\nSUGGESTED FIXES:")
        for i, f in enumerate(dict.fromkeys(rpt.fixes), 1):
            print(f"  {i}. {f}")
    if verdict == "REACHABLE":
        print(
            "\nThe inspected configuration permits this modeled path, not a runtime connectivity test."
        )
    if verdict == "INDETERMINATE" and managed:
        print(
            "\nThe managed network is Microsoft-owned; the opaque hop above can't be resolved "
            "from the control plane. Test from the target's side or check its access logs."
        )
    if verdict in ("REACHABLE", "INDETERMINATE"):
        print(
            f"\nFor runtime evidence, use the existing diagnostic agent: {DIAGNOSTIC_AGENT} "
            "(in the relevant network context)."
        )
    if html_path:
        with open(html_path, "w", encoding="utf-8") as f:
            f.write(render_html(rpt))
        print(f"\nVisual report written to: {html_path}")
        print(
            "Open it in any browser (double-click) to see the network path and blocking hop."
        )
    return code


# --------------------------------------------------------------------------- #
# Main analysis
# --------------------------------------------------------------------------- #
def analyze(
    subnet_id, endpoint, port, sub, html_path=None, account_id=None, project_id=None
):
    sub = sub or sub_of(subnet_id)

    subnet = az(
        ["network", "vnet", "subnet", "show", "--ids", subnet_id],
        sub=sub,
        allow_fail=False,
    )
    if not subnet:
        raise AzError(f"could not read subnet {subnet_id}")
    nsg_id = (subnet.get("networkSecurityGroup") or {}).get("id")
    rt_id = (subnet.get("routeTable") or {}).get("id")
    deleg = [d.get("serviceName") for d in (subnet.get("delegations") or [])]
    vnet_id = re.sub(r"/subnets/[^/]+/?$", "", subnet_id, flags=re.IGNORECASE)

    vnet = az(["network", "vnet", "show", "--ids", vnet_id], sub=sub) or {}
    vnet_prefixes = [
        ipaddress.ip_network(p, strict=False)
        for p in ((vnet.get("addressSpace") or {}).get("addressPrefixes") or [])
    ]
    dns_servers = (vnet.get("dhcpOptions") or {}).get("dnsServers") or []
    peerings = vnet.get("virtualNetworkPeerings") or []
    prefixes = subnet.get("addressPrefixes") or [subnet.get("addressPrefix")]
    source_prefixes = [ipaddress.ip_network(p, strict=False) for p in prefixes if p]
    if not source_prefixes or not vnet_prefixes:
        raise AzError(
            "Subnet or VNet address prefixes are missing from the Azure response"
        )

    source_label = f"{subnet.get('name')} ({subnet.get('addressPrefix')})"
    rpt = Report(source_label, f"{endpoint}:{port}")

    print(f"Static reachability analysis: {endpoint}:{port}")
    print(
        f"  from subnet {subnet.get('name')} ({subnet.get('addressPrefix')}) "
        f"in VNet {vnet.get('name')}"
    )
    if deleg:
        print(f"  subnet delegation: {', '.join(deleg)}")
    if dns_servers:
        print(
            f"  NOTE: VNet uses custom DNS servers {dns_servers}; private-zone "
            f"resolution may be overridden by those servers."
        )
    print()

    if account_id:
        check_capability_host(account_id, sub, rpt, project_id)
    else:
        rpt.hop(
            "CAPHOST",
            "info",
            "capability host not checked",
            "Subnet-only analysis does not enumerate Foundry accounts/projects. "
            "Basic hosted setups need not have explicit capability hosts.",
        )

    # 1. DNS
    ips = resolve(endpoint, port, vnet_id, sub, rpt, custom_dns=bool(dns_servers))
    common = rpt
    dns_uncertain = any(
        h["gate"] == "DNS" and h["status"] == "unknown" for h in rpt.hops
    )
    paths = []
    nsg = (
        az(["network", "nsg", "show", "--ids", nsg_id], sub=sub)
        if ips and nsg_id
        else None
    )
    rt = (
        az(["network", "route-table", "show", "--ids", rt_id], sub=sub)
        if ips and rt_id
        else None
    )
    for dest_ip in sorted(set(ips)):
        rpt = Report(source_label, f"{dest_ip}:{port}")
        paths.append(rpt)
        ipobj = ipaddress.ip_address(dest_ip)
        rpt.hop(
            "PATH",
            "info",
            f"candidate address {dest_ip}",
            "All returned addresses are evaluated; this is not a representative-IP verdict.",
        )
        if ipobj.version == 6:
            rpt.hop(
                "IPV6",
                "unknown",
                "IPv6 runtime support not confirmed",
                "Rules and routes are evaluated, but Foundry runtime IPv6 support is not inferred.",
                unknown="IPv6-only or dual-stack runtime connectivity requires verification.",
            )

        # 2. NSG
        nres = evaluate_nsg(nsg, vnet_prefixes, ipobj, port, source_prefixes)
        if nres["decision"] == "NO_NSG":
            rpt.hop(
                "NSG",
                "ok",
                "no subnet NSG",
                "No subnet-level NSG is attached. NIC-level and target inbound rules "
                "are not part of this outbound subnet check.",
            )
        elif nres["matched"]:
            m = nres["matched"]
            if m["access"] == "Deny":
                rpt.hop(
                    "NSG",
                    "block",
                    f"rule '{m['name']}' denies",
                    f"rule '{m['name']}' (prio {m['priority']}) DENIES outbound to "
                    f"{m['destination']} port {m['ports']}.",
                    block=f"NSG rule '{m['name']}' (priority {m['priority']}) denies outbound to "
                    f"{dest_ip}:{port}.",
                    fix=f"Add a higher-priority Allow rule for {dest_ip}:{port}, or scope the deny.",
                )
            else:
                rpt.hop(
                    "NSG",
                    "ok",
                    f"rule '{m['name']}' allows",
                    f"rule '{m['name']}' (prio {m['priority']}) allows outbound to "
                    f"{m['destination']} port {m['ports']}.",
                )
        elif nres["decision"] == "NO_MATCH":
            rpt.hop(
                "NSG",
                "block",
                "implicit DenyAllOutBound",
                "no outbound rule matched; implicit DenyAllOutBound applies.",
                block=f"No NSG outbound rule permits {dest_ip}:{port}; the implicit "
                f"DenyAllOutBound blocks it.",
                fix=f"Add an outbound Allow rule for {dest_ip}:{port}.",
            )
        if nres.get("ambiguous"):
            tags = ", ".join(f"{n}:{t}" for n, t in nres["ambiguous"])
            rpt.hop(
                "NSG",
                "unknown",
                "earlier rule constraints unresolved",
                f"Earlier rule(s) may match ({tags}); later rules cannot establish a verdict.",
                unknown=f"NSG constraints ({tags}) could affect this flow.",
            )

        pe_report = Report(source_label, f"{dest_ip}:{port}")
        private_endpoint = False
        if any(ipobj in n for n in vnet_prefixes):
            private_endpoint = check_private_endpoint(dest_ip, vnet_id, sub, pe_report)

        # Private Endpoint /32 system routes and destination subnet network
        # policies can change whether a source UDR actually diverts the traffic.
        rres = evaluate_routes(rt, ipobj, vnet_prefixes, peerings)
        if private_endpoint and rres["source"].startswith("UDR"):
            rres.update(
                nextHop="Unknown",
                source=rres["source"] + "; Private Endpoint route precedence and "
                "destination subnet network policies are not evaluated",
            )
        hoptxt = rres["nextHop"] + (
            f" ({rres['nextHopIp']})" if rres.get("nextHopIp") else ""
        )
        src = rres["source"] + (
            f", prefix {rres['prefix']}" if rres.get("prefix") else ""
        )
        if rres["nextHop"] == "None":
            rpt.hop(
                "ROUTE",
                "block",
                "black-hole route",
                f"next hop = None [{src}] -> {dest_ip} is black-holed.",
                block=f"Route ({rres['source']}) sets next hop 'None' -> {dest_ip} is black-holed.",
                fix="Remove/repoint the black-hole route, or add a more specific route.",
            )
        elif rres["nextHop"] in ("VirtualAppliance", "VirtualNetworkGateway"):
            kind = (
                "firewall/NVA"
                if rres["nextHop"] == "VirtualAppliance"
                else "VPN/ER gateway (on-prem)"
            )
            rpt.hop(
                "ROUTE",
                "unknown",
                f"next hop = {kind}",
                f"next hop = {hoptxt} [{src}]. Its rules are outside the Azure network "
                f"control plane we can read.",
                unknown=f"Traffic egresses via a {kind} ({rres.get('nextHopIp') or 'next hop'}); "
                f"whether it permits {dest_ip}:{port} cannot be confirmed statically.",
                fix=f"Review the firewall/NVA policy; use {DIAGNOSTIC_AGENT} for runtime evidence.",
            )
        elif rres["nextHop"] in ("Internet", "VnetLocal"):
            rpt.hop("ROUTE", "ok", f"next hop = {rres['nextHop']}", f"[{src}]")
            if rres["nextHop"] == "Internet":
                rpt.hop(
                    "EGRESS",
                    "unknown",
                    "internet route is not proof of outbound connectivity",
                    "A next hop does not establish an outbound IP/SNAT mechanism for this runtime.",
                    unknown="Internet egress/SNAT must be verified for the runtime.",
                )
        else:
            rpt.hop(
                "ROUTE",
                "unknown",
                "effective next hop not confirmed",
                src,
                unknown=f"Effective route to {dest_ip} is not confirmed: {src}.",
            )

        rpt.hops.extend(pe_report.hops)
        rpt.blockers.extend(pe_report.blockers)
        rpt.opaque.extend(pe_report.opaque)
        rpt.fixes.extend(pe_report.fixes)
        check_target(endpoint, sub, rpt, ipobj, private_endpoint)

        # 5. Peerings context
        if ipobj.is_private and not any(ipobj in n for n in vnet_prefixes):
            if peerings:
                states = ", ".join(
                    f"{p.get('name')}:{p.get('peeringState')}" for p in peerings
                )
                rpt.hop(
                    "PEER",
                    "unknown",
                    "possible peering or gateway path",
                    f"peerings present ({states}); the remote side's routes/NSGs/firewall "
                    f"aren't fully visible from here.",
                    unknown="Peerings exist, but destination membership and the remote side's "
                    "routes/NSGs/firewall cannot be confirmed.",
                )
            else:
                rpt.hop(
                    "PEER",
                    "unknown",
                    "private IP outside VNet, no peering",
                    "destination is a private IP outside this VNet with no peering; it likely "
                    "relies on a UDR/gateway path.",
                    unknown="Destination is a private IP outside this VNet with no peering; "
                    "the egress path can't be confirmed statically.",
                )

    all_blocked = bool(paths) and all(p.blockers for p in paths) and not dns_uncertain
    for path in paths:
        for hop in path.hops:
            hop = dict(hop)
            if hop["status"] == "block" and not all_blocked:
                hop["status"] = "unknown"
                hop["title"] = "conditional block: " + hop["title"]
            common.hops.append(hop)
        common.fixes.extend(path.fixes)
        common.opaque.extend(path.opaque)
        if all_blocked:
            common.blockers.extend(path.blockers)
        elif path.blockers:
            common.opaque.extend("Candidate path only: " + b for b in path.blockers)
    if not paths and not common.opaque:
        common.unknown("No destination addresses could be evaluated.")
    return _finish(common, endpoint, port, html_path)


def account_of_project(project_id):
    """Strip /projects/<name> to get the parent account resource id."""
    return re.sub(r"/projects/[^/]+/?$", "", project_id, flags=re.IGNORECASE)


def validate_endpoint(value):
    """Accept an unadorned DNS name or IP, never URLs, ports, or CLI switches."""
    try:
        address = ipaddress.ip_address(value)
        if "%" in value:
            raise ValueError("scoped IP addresses are not supported")
        return str(address)
    except ValueError:
        pass
    host = value.rstrip(".")
    labels = host.split(".")
    if (
        len(host) > 253
        or not host
        or re.fullmatch(r"[0-9.]+", host)
        or any(
            not re.fullmatch(r"[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?", label)
            for label in labels
        )
    ):
        raise argparse.ArgumentTypeError(
            "endpoint must be a hostname or IP, without URL, port, or switches"
        )
    return host.lower()


def validate_resource_id(value, kind):
    segment = r"[^/\s?#%]+"
    base = rf"/subscriptions/{segment}/resourceGroups/{segment}/providers/"
    types = {
        "account": rf"Microsoft\.CognitiveServices/accounts/{segment}",
        "project": rf"Microsoft\.CognitiveServices/accounts/{segment}/projects/{segment}",
        "subnet": rf"Microsoft\.Network/virtualNetworks/{segment}/subnets/{segment}",
    }
    if not re.fullmatch(base + types[kind], value, flags=re.IGNORECASE):
        raise ValueError(
            f"Invalid {kind} resource ID: expected full /subscriptions/... resource path"
        )
    return value


def valid_port(value):
    try:
        port = int(value)
    except ValueError as exc:
        raise argparse.ArgumentTypeError(
            "port must be an integer between 1 and 65535"
        ) from exc
    if not 1 <= port <= 65535:
        raise argparse.ArgumentTypeError("port must be between 1 and 65535")
    return port


def main():
    ap = argparse.ArgumentParser(
        description="Static (no-deploy) reachability analysis.", allow_abbrev=False
    )
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument(
        "--subnet-id",
        help="Full resource ID of the Foundry agent subnet (BYO-VNet mode).",
    )
    src.add_argument(
        "--account",
        help="Full resource ID of the Foundry (AIServices) account. "
        "Derives the agent subnet + network mode + capability host "
        "from it (auto-detects managed-VNet vs BYO-VNet).",
    )
    src.add_argument(
        "--project",
        help="Full resource ID of the Foundry project "
        "(.../accounts/<acct>/projects/<proj>). Everything is derived "
        "from it: the parent account's subnet + network mode, and the "
        "capability host.",
    )
    ap.add_argument(
        "--endpoint",
        required=True,
        type=validate_endpoint,
        help="Hostname or IP to test.",
    )
    ap.add_argument("--port", required=True, type=valid_port)
    ap.add_argument("--subscription", default=None)
    ap.add_argument(
        "--html",
        default=None,
        metavar="FILE",
        help="Also write a self-contained HTML report (open in a browser).",
    )
    args = ap.parse_args()
    try:
        if args.account:
            validate_resource_id(args.account, "account")
        if args.project:
            validate_resource_id(args.project, "project")
        if args.subnet_id:
            validate_resource_id(args.subnet_id, "subnet")
        account_id = args.account or (
            account_of_project(args.project) if args.project else None
        )
        if account_id:
            sub = args.subscription or sub_of(account_id)
            net = read_foundry_network(account_id, sub)
            if net["managed"]:
                return analyze_managed(
                    account_id,
                    args.endpoint,
                    args.port,
                    sub,
                    net,
                    html_path=args.html,
                    project_id=args.project,
                )
            if net["subnet_id"]:
                validate_resource_id(net["subnet_id"], "subnet")
                print(
                    f"Account '{net['name']}' uses BYO-VNet (injected subnet); "
                    f"analyzing that subnet.\n"
                )
                return analyze(
                    net["subnet_id"],
                    args.endpoint,
                    args.port,
                    sub,
                    html_path=args.html,
                    account_id=account_id,
                    project_id=args.project,
                )
            raise AzError(
                f"account '{net['name']}' has no agent network injection to analyze; "
                f"pass --subnet-id for a specific subnet."
            )
        return analyze(
            args.subnet_id,
            args.endpoint,
            args.port,
            args.subscription,
            html_path=args.html,
        )
    except (AzError, OSError, ValueError, TypeError, KeyError) as e:
        print(f"ERROR: {e}", file=sys.stderr)
        return 2


if __name__ == "__main__":
    sys.exit(main())
