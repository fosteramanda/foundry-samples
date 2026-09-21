#!/usr/bin/env python3
"""Conservative NSG and route evaluation, ported from foundry-reachability-analyzer.

Reads Azure CLI JSON without sending traffic. Unknown source ports, service tags,
ASGs, and effective routes remain explicit uncertainties.
"""

import argparse
import ipaddress
import json
import sys


def load_json(path):
    if not path:
        return None
    with open(path) as f:
        txt = f.read().strip()
    data = json.loads(txt)
    if not isinstance(data, dict) or not data:
        raise ValueError(f"{path}: expected a nonempty Azure resource JSON object")
    return data


def parse_ports(rule, direction="destination"):
    ranges = []
    single = rule.get(f"{direction}PortRange")
    multi = rule.get(f"{direction}PortRanges") or []
    for p in ([single] if single else []) + list(multi):
        if not p:
            continue
        p = str(p)
        if p == "*":
            ranges.append((0, 65535))
        elif "-" in p:
            a, b = p.split("-")
            ranges.append((int(a), int(b)))
        else:
            ranges.append((int(p), int(p)))
    return ranges


def port_matches(port, ranges):
    return any(lo <= port <= hi for lo, hi in ranges)


def is_private(ip):
    if isinstance(ip, str):
        try:
            ip = ipaddress.ip_address(ip.split("%")[0])
        except ValueError:
            return False
    return ip.is_private


def prefix_matches(ip, prefixes, vnet_prefixes):
    """Return (matched, ambiguous_tag_or_None)."""
    ambiguous = None
    for p in prefixes:
        if not p:
            continue
        if p == "*":
            return True, None
        if p.lower() == "internet":
            if ip.is_global:
                return True, None
            ambiguous = p
            continue
        if p.lower() == "virtualnetwork":
            if any(ip in n for n in vnet_prefixes):
                return True, None
            ambiguous = p
            continue
        try:
            if ip in ipaddress.ip_network(p, strict=False):
                return True, None
        except ValueError:
            ambiguous = p
    return False, ambiguous


def rule_prefixes(rule, direction="destination"):
    vals = []
    s = rule.get(f"{direction}AddressPrefix")
    if s:
        vals.append(s)
    vals.extend(rule.get(f"{direction}AddressPrefixes") or [])
    return vals


def source_matches(rule, source_prefixes, vnet_prefixes):
    """True for the whole source subnet, False for none, None for a subset."""
    if rule.get("sourceApplicationSecurityGroups"):
        return None
    prefixes = rule_prefixes(rule, "source")
    if "*" in prefixes:
        return True
    if not prefixes or not source_prefixes:
        return None
    networks, unknown = [], False
    for prefix in prefixes:
        if prefix.lower() == "virtualnetwork":
            networks.extend(vnet_prefixes)
            unknown = True
        else:
            try:
                networks.append(ipaddress.ip_network(prefix, strict=False))
            except ValueError:
                unknown = True
    covered, overlaps = [], False
    for source in source_prefixes:
        relevant = [n for n in networks if n.version == source.version]
        covered.append(any(source.subnet_of(n) for n in relevant))
        overlaps |= any(source.overlaps(n) for n in relevant)
    if all(covered):
        return True
    return None if unknown or overlaps else False


def evaluate_nsg(nsg, vnet_prefixes, ip, port, source_prefixes=None):
    if nsg is None:
        return {"decision": "NO_NSG", "matched": None, "ambiguous": None}
    if not nsg:
        return {
            "decision": "UNKNOWN",
            "matched": None,
            "ambiguous": [("NSG", "missing rule data")],
        }
    rules = list(nsg.get("securityRules") or []) + list(
        nsg.get("defaultSecurityRules") or []
    )
    if not rules:
        return {
            "decision": "UNKNOWN",
            "matched": None,
            "ambiguous": [("NSG", "no rules returned")],
        }
    outbound = sorted(
        [r for r in rules if (r.get("direction") or "").lower() == "outbound"],
        key=lambda r: r.get("priority", 65000),
    )
    ambiguous = []
    for r in outbound:
        proto = (r.get("protocol") or "").lower()
        if proto in ("udp", "icmp", "esp", "ah"):
            continue
        ports = parse_ports(r)
        if ports and not port_matches(port, ports):
            continue
        matched, tag = prefix_matches(ip, rule_prefixes(r), vnet_prefixes)
        dest_unknown = bool(
            tag or r.get("destinationApplicationSecurityGroups") or not rule_prefixes(r)
        )
        if not matched and not dest_unknown:
            continue
        source = source_matches(r, source_prefixes or [], vnet_prefixes)
        if source is False:
            continue
        source_ports = parse_ports(r, "source")
        uncertain = (
            (not matched and dest_unknown)
            or bool(r.get("destinationApplicationSecurityGroups"))
            or source is None
            or not ports
            or not any(lo == 0 and hi == 65535 for lo, hi in source_ports)
            or proto not in ("*", "tcp")
            or (r.get("access") or "").lower() not in ("allow", "deny")
        )
        if uncertain:
            ambiguous.append(
                (r.get("name"), tag or "source, port, ASG, or rule fields")
            )
            continue
        if ambiguous:
            return {"decision": "UNKNOWN", "matched": None, "ambiguous": ambiguous}
        if matched:
            access = r["access"].capitalize()
            return {
                "decision": access,
                "matched": {
                    "name": r.get("name"),
                    "priority": r.get("priority"),
                    "access": access,
                    "destination": rule_prefixes(r),
                    "ports": r.get("destinationPortRanges")
                    or r.get("destinationPortRange"),
                },
                "ambiguous": ambiguous or None,
            }
    return {
        "decision": "UNKNOWN" if ambiguous else "NO_MATCH",
        "matched": None,
        "ambiguous": ambiguous or None,
    }


def evaluate_routes(rt, ip, vnet_prefixes=(), peerings=()):
    local = [n for n in vnet_prefixes if ip in n]
    local_net = max(local, key=lambda n: n.prefixlen) if local else None
    default = {
        "nextHop": "VnetLocal" if local_net else "Unknown",
        "source": "local VNet system route"
        if local_net
        else "effective BGP/peering routes and outbound connectivity not visible",
        "prefix": str(local_net) if local_net else None,
        "nextHopIp": None,
    }
    if rt is None:
        return default
    if not rt or not isinstance(rt.get("routes"), list):
        return dict(default, nextHop="Unknown", source="route table data missing")
    best, best_len = None, -1
    unresolved = []
    for r in rt.get("routes") or []:
        try:
            net = ipaddress.ip_network(r.get("addressPrefix"), strict=False)
        except (ValueError, TypeError):
            unresolved.append(r.get("addressPrefix"))
            continue
        if ip in net and net.prefixlen > best_len:
            best, best_len = r, net.prefixlen
    if unresolved:
        return dict(
            default,
            nextHop="Unknown",
            source=f"unresolved route prefixes/service tags: {unresolved}",
        )
    if best is None or (local_net and local_net.prefixlen > best_len):
        return default
    result = {
        "nextHop": best.get("nextHopType") or "Unknown",
        "nextHopIp": best.get("nextHopIpAddress"),
        "source": f"UDR '{best.get('name')}'",
        "prefix": best.get("addressPrefix"),
    }
    # A host UDR cannot lose to a more-specific hidden route. Otherwise BGP or
    # peering can change the winner outside the known local VNet.
    if (
        not local_net
        and best_len < ip.max_prefixlen
        and (rt.get("disableBgpRoutePropagation") is not True or peerings)
    ):
        result.update(
            nextHop="Unknown",
            source=result["source"]
            + "; more-specific BGP/peering routes are not visible",
        )
    if result["nextHop"].lower() == "vnetlocal" and not local_net:
        result.update(
            nextHop="Unknown",
            source=result["source"]
            + "; destination is outside known local VNet prefixes",
        )
    return result


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--nsg-file")
    ap.add_argument("--routetable-file")
    ap.add_argument("--vnet-prefixes", default="")
    ap.add_argument(
        "--source-prefixes", default="", help="Comma-separated source subnet CIDRs."
    )
    ap.add_argument("--ip", required=True)
    ap.add_argument("--port", required=True, type=int)
    args = ap.parse_args()

    try:
        if not 1 <= args.port <= 65535:
            raise ValueError("port must be between 1 and 65535")
        nsg = load_json(args.nsg_file)
        rt = load_json(args.routetable_file)
        ip = ipaddress.ip_address(args.ip)
        vnet_prefixes = [
            ipaddress.ip_network(p.strip(), strict=False)
            for p in args.vnet_prefixes.split(",")
            if p.strip()
        ]
        source_prefixes = [
            ipaddress.ip_network(p.strip(), strict=False)
            for p in args.source_prefixes.split(",")
            if p.strip()
        ]
        nsg_res = evaluate_nsg(nsg, vnet_prefixes, ip, args.port, source_prefixes)
        route_res = evaluate_routes(rt, ip, vnet_prefixes)
    except (OSError, ValueError, TypeError) as exc:
        print(f"ERROR: {exc}", file=sys.stderr)
        return 2

    print(f"Control-plane diagnosis for {args.ip}:{args.port}")

    # NSG finding
    if nsg_res["decision"] == "NO_NSG":
        print("  NSG   : no subnet NSG supplied; NIC-level rules are not evaluated.")
    elif nsg_res["decision"] == "UNKNOWN":
        print("  NSG   : INDETERMINATE; an earlier rule may match this flow.")
    elif nsg_res["matched"]:
        m = nsg_res["matched"]
        verb = "DENIES" if m["access"] == "Deny" else "allows"
        marker = "  <-- BLOCKS THIS TRAFFIC" if m["access"] == "Deny" else ""
        print(
            f"  NSG   : rule '{m['name']}' (priority {m['priority']}) {verb} outbound to "
            f"{m['destination']} port {m['ports']}{marker}"
        )
    else:
        print(
            "  NSG   : no outbound rule matched (implicit DenyAllOutBound would apply)  <-- BLOCKS THIS TRAFFIC"
        )
    if nsg_res.get("ambiguous"):
        tags = ", ".join(f"{name}:{tag}" for name, tag in nsg_res["ambiguous"])
        print(f"  NOTE  : unresolved rule constraints: {tags}")

    # Route finding
    hop = route_res["nextHop"] + (
        f" ({route_res['nextHopIp']})" if route_res.get("nextHopIp") else ""
    )
    src = route_res["source"] + (
        f", prefix {route_res['prefix']}" if route_res.get("prefix") else ""
    )
    print(f"  ROUTE : next hop = {hop} [{src}]")
    if route_res["nextHop"] == "None":
        print(
            "          ^ next hop 'None' BLACK-HOLES this destination  <-- BLOCKS THIS TRAFFIC"
        )
    elif route_res["nextHop"] in ("VirtualAppliance", "VirtualNetworkGateway"):
        print(
            "          ^ traffic is sent to a firewall/NVA/gateway; verify its rules allow this flow."
        )

    if nsg_res["decision"] in ("Deny", "NO_MATCH") or route_res["nextHop"] == "None":
        return 1
    if nsg_res["decision"] == "UNKNOWN" or route_res["nextHop"] not in (
        "Internet",
        "VnetLocal",
    ):
        return 3
    return 0


if __name__ == "__main__":
    sys.exit(main())
