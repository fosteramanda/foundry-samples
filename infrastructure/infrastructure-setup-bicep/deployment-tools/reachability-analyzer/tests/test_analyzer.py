"""Offline regressions for the port of foundry-reachability-analyzer."""

import contextlib
import copy
import importlib.util
import io
import ipaddress
import json
from pathlib import Path
import socket
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]


def load_module(name):
    spec = importlib.util.spec_from_file_location(name, ROOT / f"{name}.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


diagnose = load_module("diagnose")
with patch.dict(sys.modules, {"diagnose": diagnose}):
    analyzer = load_module("analyze")

BASE = "/subscriptions/00000000-0000-0000-0000-000000000001/resourceGroups/test-rg/providers"
VNET = BASE + "/Microsoft.Network/virtualNetworks/source"
SUBNET = VNET + "/subnets/agents"
ACCOUNT = BASE + "/Microsoft.CognitiveServices/accounts/test-account"
PROJECT = ACCOUNT + "/projects/selected"
NSG = BASE + "/Microsoft.Network/networkSecurityGroups/test-nsg"
ROUTES = BASE + "/Microsoft.Network/routeTables/test-routes"
TARGET = BASE + "/Microsoft.Storage/storageAccounts/teststorage"


def rule(name="allow", access="Allow", priority=100, **overrides):
    data = {
        "name": name,
        "priority": priority,
        "direction": "Outbound",
        "protocol": "Tcp",
        "access": access,
        "sourceAddressPrefix": "*",
        "sourcePortRange": "*",
        "destinationAddressPrefix": "*",
        "destinationPortRange": "*",
    }
    data.update(overrides)
    return data


class AzureFixture:
    """Strict command dispatcher: unexpected reads fail instead of touching Azure."""

    def __init__(self):
        self.subnet = {"name": "agents", "addressPrefix": "10.0.1.0/24"}
        self.vnet = {
            "name": "source",
            "addressSpace": {"addressPrefixes": ["10.0.0.0/16"]},
            "dhcpOptions": {"dnsServers": []},
            "virtualNetworkPeerings": [],
        }
        self.nsg = {"securityRules": [rule()]}
        self.routes = {"routes": [], "disableBgpRoutePropagation": True}
        self.zones = []
        self.links = []
        self.records = {"a": [], "aaaa": []}
        self.pes = []
        self.nic = {"ipConfigurations": []}
        self.hosts = []
        self.account = {
            "name": "test-account",
            "properties": {
                "networkInjections": [{"scenario": "agent", "subnetArmId": SUBNET}]
            },
        }
        self.target = {
            "name": "teststorage",
            "properties": {
                "publicNetworkAccess": "Enabled",
                "networkAcls": {"defaultAction": "Allow"},
            },
        }
        self.target_found = [{"id": TARGET}]
        self.calls = []
        self.fail_prefix = None

    def attach_nsg(self, *rules):
        self.subnet["networkSecurityGroup"] = {"id": NSG}
        self.nsg["securityRules"] = list(rules)

    def attach_route(self, prefix, hop="None", **properties):
        self.subnet["routeTable"] = {"id": ROUTES}
        self.routes.update(properties)
        self.routes["routes"] = [
            {"name": "test-route", "addressPrefix": prefix, "nextHopType": hop}
        ]

    def private_dns(self, ips, zone="internal.test"):
        self.zones = [
            {"name": zone, "id": BASE + "/Microsoft.Network/privateDnsZones/" + zone}
        ]
        self.links = [
            {
                "virtualNetwork": {"id": VNET},
                "provisioningState": "Succeeded",
                "virtualNetworkLinkState": "Completed",
            }
        ]
        for family, version, field, address in (
            ("a", 4, "aRecords", "ipv4Address"),
            ("aaaa", 6, "aaaaRecords", "ipv6Address"),
        ):
            self.records[family] = [
                {
                    "name": "service",
                    field: [
                        {address: ip}
                        for ip in ips
                        if ipaddress.ip_address(ip).version == version
                    ],
                }
            ]

    def private_endpoint(self, ip="10.0.2.4", state="Approved", vnet=VNET):
        pe = {
            "name": "test-pe",
            "id": BASE + "/Microsoft.Network/privateEndpoints/" + str(len(self.pes)),
            "subnet": {"id": vnet + "/subnets/endpoints"},
            "customDnsConfigs": [{"ipAddresses": [ip]}],
            "privateLinkServiceConnections": [
                {
                    "privateLinkServiceId": TARGET,
                    "privateLinkServiceConnectionState": (
                        {"status": state} if state else {}
                    ),
                }
            ],
        }
        self.pes.append(pe)
        return pe

    def az(self, args, sub=None, allow_fail=False):
        self.calls.append((list(args), sub))
        if self.fail_prefix and args[: len(self.fail_prefix)] == self.fail_prefix:
            raise analyzer.AzError("AuthorizationFailed: reader permission denied")
        if args[:4] == ["network", "vnet", "subnet", "show"]:
            return copy.deepcopy(self.subnet)
        if args[:3] == ["network", "vnet", "show"]:
            return copy.deepcopy(self.vnet)
        if args[:3] == ["network", "nsg", "show"]:
            return copy.deepcopy(self.nsg)
        if args[:3] == ["network", "route-table", "show"]:
            return copy.deepcopy(self.routes)
        if args[:4] == ["network", "private-dns", "zone", "list"]:
            return copy.deepcopy(self.zones)
        if args[:4] == ["network", "private-dns", "link", "vnet"]:
            return copy.deepcopy(self.links)
        if args[:3] == ["network", "private-dns", "record-set"]:
            return copy.deepcopy(self.records[args[3]])
        if args[:3] == ["network", "private-endpoint", "list"]:
            return copy.deepcopy(self.pes)
        if args[:3] == ["network", "nic", "show"]:
            return copy.deepcopy(self.nic)
        if args[:2] == ["resource", "list"]:
            return copy.deepcopy(self.target_found)
        if args[:2] == ["resource", "show"]:
            rid = args[args.index("--ids") + 1]
            return copy.deepcopy(self.account if rid == ACCOUNT else self.target)
        if args[0] == "rest":
            url = args[args.index("--url") + 1]
            if "/capabilityHosts?" not in url:
                raise AssertionError(f"Unrelated project enumeration: {args}")
            return {"value": copy.deepcopy(self.hosts)}
        raise AssertionError(f"Unexpected Azure command: {args}")


class ReportTests(unittest.TestCase):
    def setUp(self):
        self.azure = AzureFixture()
        self.last_report = None

    def run_report(
        self, endpoint="10.0.2.4", managed=False, project_id=None, account=False
    ):
        output = io.StringIO()
        finish = analyzer._finish

        def capture(rpt, *args, **kwargs):
            self.last_report = rpt
            return finish(rpt, *args, **kwargs)

        with (
            patch.object(analyzer, "az", side_effect=self.azure.az),
            patch.object(
                analyzer.socket,
                "getaddrinfo",
                return_value=[(socket.AF_INET, 1, 6, "", ("20.30.40.50", 443))],
            ),
            patch.object(analyzer, "_finish", side_effect=capture),
            contextlib.redirect_stdout(output),
        ):
            if managed:
                code = analyzer.analyze_managed(
                    ACCOUNT,
                    endpoint,
                    443,
                    "subscription-name",
                    {"name": "test-account"},
                    project_id=project_id,
                )
            else:
                code = analyzer.analyze(
                    SUBNET,
                    endpoint,
                    443,
                    "subscription-name",
                    account_id=ACCOUNT if account or project_id else None,
                    project_id=project_id,
                )
        self.assertIn("NETWORK PATH", output.getvalue())
        self.assertIn(f"is {self.last_report.verdict()[0]}", output.getvalue())
        self.assertNotIn("--mode live", output.getvalue())
        return code

    def test_approved_local_private_endpoint_has_modeled_path(self):
        self.azure.private_endpoint()
        self.assertEqual(self.run_report(), 0)
        self.assertEqual(self.last_report.verdict(), ("REACHABLE", 0))

    def test_basic_without_capability_host_is_not_blocked(self):
        self.azure.private_endpoint()
        self.assertEqual(self.run_report(account=True), 0)
        self.assertTrue(any("Basic" in h["detail"] for h in self.last_report.hops))

    def test_project_only_reads_its_own_capability_hosts(self):
        self.azure.private_endpoint()
        self.assertEqual(self.run_report(project_id=PROJECT), 0)
        urls = [
            args[args.index("--url") + 1]
            for args, _ in self.azure.calls
            if args[0] == "rest"
        ]
        self.assertEqual(
            urls,
            [
                f"https://management.azure.com{PROJECT}/capabilityHosts?api-version=2025-06-01"
            ],
        )

    def test_unready_selected_capability_host_is_unknown(self):
        self.azure.private_endpoint()
        self.azure.hosts = [
            {
                "name": "agents",
                "properties": {
                    "capabilityHostKind": "Agents",
                    "provisioningState": "Failed",
                },
            }
        ]
        self.assertEqual(self.run_report(project_id=PROJECT), 3)

    def test_optional_capability_host_read_error_is_visible(self):
        self.azure.private_endpoint()
        self.azure.fail_prefix = ["rest"]
        self.assertEqual(self.run_report(account=True), 3)
        self.assertIn("AuthorizationFailed", "\n".join(self.last_report.opaque))

    def test_subnet_mode_never_enumerates_accounts_or_projects(self):
        self.run_report()
        self.assertFalse(
            any(
                args[0] in ("rest", "cognitiveservices") for args, _ in self.azure.calls
            )
        )

    def test_explicit_deny_blocks(self):
        self.azure.attach_nsg(rule("deny", "Deny"))
        self.assertEqual(self.run_report(), 1)

    def test_protocol_case_is_insensitive(self):
        self.azure.attach_nsg(
            rule("deny", "deny", protocol="tCp", direction="outbound")
        )
        self.assertEqual(self.run_report(), 1)

    def test_earlier_unresolved_rule_prevents_later_hard_deny(self):
        variants = [
            {"destinationAddressPrefix": "Storage"},
            {
                "destinationAddressPrefix": "",
                "destinationApplicationSecurityGroups": [{"id": "asg"}],
            },
            {"sourceAddressPrefix": "AzureCloud"},
            {
                "sourceAddressPrefix": "",
                "sourceApplicationSecurityGroups": [{"id": "asg"}],
            },
            {"sourcePortRange": "1024-65535"},
            {"sourceAddressPrefix": "10.0.1.0/25"},
            {"sourceAddressPrefix": ""},
        ]
        for constraints in variants:
            with self.subTest(constraints=constraints):
                self.azure.attach_nsg(
                    rule("possible-allow", **constraints), rule("deny", "Deny", 200)
                )
                self.assertEqual(self.run_report(), 3)
                self.assertFalse(self.last_report.blockers)

    def test_disjoint_source_constraint_does_not_match(self):
        self.azure.private_endpoint()
        self.azure.attach_nsg(
            rule("other-source", "Deny", sourceAddressPrefix="10.5.0.0/24"),
            rule("allow", priority=200),
        )
        self.assertEqual(self.run_report(), 0)

    def test_source_subnet_contained_by_rule_is_definite(self):
        self.azure.attach_nsg(rule("deny", "Deny", sourceAddressPrefix="10.0.0.0/16"))
        self.assertEqual(self.run_report(), 1)

    def test_virtual_network_tag_does_not_mean_all_private_ips(self):
        self.azure.attach_nsg(
            rule("vnet", destinationAddressPrefix="VirtualNetwork"),
            rule("deny", "Deny", 200),
        )
        self.assertEqual(self.run_report("10.55.1.4"), 3)
        self.assertFalse(self.last_report.blockers)

    def test_missing_nsg_rule_data_is_unknown(self):
        self.azure.attach_nsg()
        self.assertEqual(self.run_report(), 3)
        self.assertFalse(self.last_report.blockers)

    def test_known_local_route_beats_broad_blackhole_udr(self):
        self.azure.private_endpoint()
        self.azure.attach_route("0.0.0.0/0")
        self.assertEqual(self.run_report(), 0)
        self.assertTrue(any("VnetLocal" in h["title"] for h in self.last_report.hops))

    def test_more_specific_and_equal_prefix_udrs_beat_local_route(self):
        for prefix in ("10.0.0.0/16", "10.0.2.0/24", "10.0.2.4/32"):
            with self.subTest(prefix=prefix):
                self.azure.attach_route(prefix)
                self.assertEqual(self.run_report(), 1)

    def test_bgp_and_peering_can_supersede_broad_external_blackhole(self):
        self.azure.attach_route("0.0.0.0/0", disableBgpRoutePropagation=False)
        self.assertEqual(self.run_report("20.30.40.50"), 3)
        self.assertFalse(self.last_report.blockers)
        self.azure.routes["disableBgpRoutePropagation"] = True
        self.azure.vnet["virtualNetworkPeerings"] = [{"name": "peer"}]
        self.assertEqual(self.run_report("20.30.40.50"), 3)
        self.assertFalse(self.last_report.blockers)

    def test_host_blackhole_is_not_superseded_by_hidden_routes(self):
        self.azure.attach_route("20.30.40.50/32", disableBgpRoutePropagation=False)
        self.assertEqual(self.run_report("20.30.40.50"), 1)

    def test_private_endpoint_effective_route_policy_is_not_assumed(self):
        self.azure.private_endpoint()
        self.azure.attach_route("10.0.2.0/24")
        self.assertEqual(self.run_report(), 3)
        self.assertFalse(self.last_report.blockers)
        self.assertIn(
            "Private Endpoint route precedence", "\n".join(self.last_report.opaque)
        )

    def test_missing_route_list_is_not_an_empty_route_table(self):
        self.azure.attach_route("10.0.2.0/24")
        del self.azure.routes["routes"]
        self.assertEqual(self.run_report(), 3)
        self.assertFalse(self.last_report.blockers)

    def test_arbitrary_private_ip_has_no_invented_local_route(self):
        self.assertEqual(self.run_report("10.55.1.4"), 3)
        routes = [h for h in self.last_report.hops if h["gate"] == "ROUTE"]
        self.assertTrue(all(h["status"] == "unknown" for h in routes))

    def test_unresolved_route_service_tag_is_unknown(self):
        self.azure.attach_route("Storage")
        self.assertEqual(self.run_report(), 3)

    def test_nva_remains_opaque(self):
        self.azure.attach_route("10.0.2.4/32", "VirtualAppliance")
        self.assertEqual(self.run_report(), 3)
        self.assertIn(analyzer.DIAGNOSTIC_AGENT, " ".join(self.last_report.fixes))

    def test_local_public_dns_is_not_subnet_evidence_or_paas_block(self):
        self.assertEqual(self.run_report("teststorage.blob.core.windows.net"), 3)
        self.assertFalse(self.last_report.blockers)
        self.assertTrue(
            any("local DNS cross-check" in h["title"] for h in self.last_report.hops)
        )
        self.assertTrue(
            any(
                h["gate"] == "TARGET" and h["status"] == "ok"
                for h in self.last_report.hops
            )
        )

    def test_empty_linked_zone_does_not_fall_through_to_public_dns(self):
        self.azure.private_dns([])
        with patch.object(
            analyzer, "public_resolve", side_effect=AssertionError("Must not fall back")
        ):
            self.assertEqual(self.run_report("service.internal.test"), 3)
        self.assertFalse(any(h["gate"] == "PATH" for h in self.last_report.hops))

    def test_unlinked_zone_does_not_imply_public_access_blocked(self):
        self.azure.private_dns(["10.0.2.4"])
        self.azure.links = []
        self.assertEqual(self.run_report("service.internal.test"), 3)
        self.assertFalse(self.last_report.blockers)

    def test_local_dns_failure_is_explicit_unknown(self):
        with patch.object(
            analyzer, "public_resolve", side_effect=analyzer.AzError("DNS unavailable")
        ):
            self.assertEqual(self.run_report("service.example.test"), 3)
        self.assertIn("DNS unavailable", "\n".join(self.last_report.opaque))

    def test_linked_private_dns_uses_all_addresses(self):
        self.azure.private_dns(["10.0.2.4", "10.0.2.5"])
        self.azure.private_endpoint("10.0.2.4")
        self.azure.private_endpoint("10.0.2.5")
        self.assertEqual(self.run_report("service.internal.test"), 0)
        self.assertEqual(sum(h["gate"] == "PATH" for h in self.last_report.hops), 2)

    def test_mixed_ip_outcomes_are_unknown_not_global_block(self):
        self.azure.private_dns(["10.0.2.4", "10.0.2.5"])
        self.azure.private_endpoint("10.0.2.4")
        self.azure.private_endpoint("10.0.2.5")
        self.azure.attach_nsg(
            rule("one-deny", "Deny", destinationAddressPrefix="10.0.2.4"),
            rule("other-allow", priority=200),
        )
        self.assertEqual(self.run_report("service.internal.test"), 3)
        self.assertFalse(self.last_report.blockers)
        self.assertEqual(sum(h["gate"] == "PATH" for h in self.last_report.hops), 2)

    def test_all_resolved_addresses_blocked_is_definite(self):
        self.azure.private_dns(["10.0.2.4", "10.0.2.5"])
        self.azure.attach_nsg(rule("deny", "Deny"))
        self.assertEqual(self.run_report("service.internal.test"), 1)

    def test_custom_dns_downgrades_candidate_address_denials(self):
        self.azure.private_dns(["10.0.2.4"])
        self.azure.vnet["dhcpOptions"]["dnsServers"] = ["10.0.0.4"]
        self.azure.attach_nsg(rule("deny", "Deny"))
        self.assertEqual(self.run_report("service.internal.test"), 3)
        self.assertFalse(self.last_report.blockers)
        self.assertIn("custom DNS", "\n".join(self.last_report.opaque))

    def test_paas_alias_chain_not_assumed_from_zone_suffix(self):
        self.azure.private_dns(["10.0.2.4"], "privatelink.blob.core.windows.net")
        self.azure.private_endpoint()
        self.assertEqual(self.run_report("service.blob.core.windows.net"), 3)
        self.assertIn("CNAME", "\n".join(self.last_report.opaque))

    def test_unknown_link_state_does_not_confirm_dns(self):
        self.azure.private_dns(["10.0.2.4"])
        self.azure.private_endpoint()
        del self.azure.links[0]["virtualNetworkLinkState"]
        self.assertEqual(self.run_report("service.internal.test"), 3)

    def test_ipv6_is_evaluated_not_silently_skipped(self):
        self.azure.attach_nsg(
            rule("deny", "Deny", destinationAddressPrefix="2001:db8::/32")
        )
        self.assertEqual(self.run_report("2001:db8::1"), 1)
        self.assertTrue(any(h["gate"] == "NSG" for h in self.last_report.hops))
        self.assertTrue(any(h["gate"] == "ROUTE" for h in self.last_report.hops))
        self.azure.subnet.pop("networkSecurityGroup")
        self.assertEqual(self.run_report("2001:db8::1"), 3)

    def test_ipv6_aaaa_answer_is_evaluated(self):
        self.azure.private_dns(["2001:db8::1"])
        self.assertEqual(self.run_report("service.internal.test"), 3)
        self.assertTrue(any(h["gate"] == "IPV6" for h in self.last_report.hops))

    def test_unrelated_vnet_overlapping_private_endpoint_is_ignored(self):
        self.azure.private_endpoint(state="Rejected", vnet=VNET + "-unrelated")
        self.azure.private_endpoint()
        self.assertEqual(self.run_report(), 0)

    def test_only_unrelated_private_endpoint_does_not_allow(self):
        self.azure.private_endpoint(vnet=VNET + "-unrelated")
        self.assertEqual(self.run_report(), 3)
        self.assertFalse(
            any(
                h["gate"] == "PE" and h["status"] == "ok" for h in self.last_report.hops
            )
        )

    def test_missing_private_endpoint_state_is_unknown_not_denial(self):
        self.azure.private_endpoint(state=None)
        self.assertEqual(self.run_report(), 3)
        self.assertFalse(self.last_report.blockers)

    def test_rejected_private_endpoint_is_definite_block(self):
        self.azure.private_endpoint(state="Rejected")
        self.assertEqual(self.run_report(), 1)

    def test_private_endpoint_nic_fallback(self):
        pe = self.azure.private_endpoint()
        pe.pop("customDnsConfigs")
        pe["networkInterfaces"] = [
            {"id": BASE + "/Microsoft.Network/networkInterfaces/pe-nic"}
        ]
        self.azure.nic["ipConfigurations"] = [{"privateIPAddress": "10.0.2.4"}]
        self.assertEqual(self.run_report(), 0)

    def test_missing_target_fields_never_default_allow(self):
        for properties in (
            {},
            {"publicNetworkAccess": "Enabled"},
            {"networkAcls": {"defaultAction": "Allow"}},
        ):
            with self.subTest(properties=properties):
                self.azure.target["properties"] = properties
                self.assertEqual(
                    self.run_report("teststorage.blob.core.windows.net", managed=True),
                    3,
                )
                targets = [h for h in self.last_report.hops if h["gate"] == "TARGET"]
                self.assertTrue(all(h["status"] == "unknown" for h in targets))

    def test_unmodeled_firewall_type_never_assumes_allow(self):
        self.assertEqual(
            self.run_report("teststorage.database.windows.net", managed=True), 3
        )
        self.assertTrue(
            any(
                h["gate"] == "TARGET" and h["status"] == "unknown"
                for h in self.last_report.hops
            )
        )

    def test_managed_private_endpoint_is_not_assumed_to_belong_to_source(self):
        self.azure.target["properties"] = {
            "publicNetworkAccess": "Disabled",
            "privateEndpointConnections": [
                {
                    "properties": {
                        "privateLinkServiceConnectionState": {"status": "Approved"}
                    }
                }
            ],
        }
        self.assertEqual(
            self.run_report("teststorage.blob.core.windows.net", managed=True), 3
        )
        self.assertFalse(self.last_report.blockers)

    def test_managed_public_target_does_not_prove_egress(self):
        self.assertEqual(
            self.run_report("teststorage.blob.core.windows.net", managed=True), 3
        )
        self.assertTrue(
            any(
                h["gate"] == "EGRESS" and h["status"] == "unknown"
                for h in self.last_report.hops
            )
        )

    def test_byo_target_public_access_disabled_is_conditional_with_local_dns(self):
        self.azure.target["properties"]["publicNetworkAccess"] = "Disabled"
        self.assertEqual(self.run_report("teststorage.blob.core.windows.net"), 3)
        self.assertTrue(
            any("conditional block" in h["title"] for h in self.last_report.hops)
        )

    def test_html_escapes_all_untrusted_report_surfaces(self):
        payload = '<script>alert("test")</script>&'
        report = analyzer.Report(payload, payload, payload)
        report.hop(payload, "unknown", payload, payload, unknown=payload, fix=payload)
        report.block(payload)
        html = analyzer.render_html(report)
        self.assertNotIn(payload, html)
        self.assertNotIn("<script>", html)
        self.assertIn("&lt;script&gt;", html)
        self.assertIn("&amp;", html)
        self.assertNotIn("src=", html)


class CliTests(unittest.TestCase):
    def run_main(self, argv, azure=None):
        stdout, stderr = io.StringIO(), io.StringIO()
        with (
            patch.object(sys, "argv", ["analyze.py", *argv]),
            patch.object(
                analyzer,
                "az",
                side_effect=azure.az
                if azure
                else AssertionError("Unexpected Azure read"),
            ),
            patch.object(
                analyzer.socket,
                "getaddrinfo",
                side_effect=AssertionError("Unexpected DNS"),
            ),
            contextlib.redirect_stdout(stdout),
            contextlib.redirect_stderr(stderr),
        ):
            try:
                result = analyzer.main()
            except SystemExit as exc:
                result = exc.code
        return result, stdout.getvalue(), stderr.getvalue()

    def test_input_validation_before_any_azure_read(self):
        for endpoint in (
            "https://example.test",
            "example.test:443",
            "--help",
            "host/path",
            "a..b",
            "999.1.2.3",
            "[::1]",
            "fe80::1%eth0",
        ):
            with self.subTest(endpoint=endpoint):
                code, _, stderr = self.run_main(
                    ["--subnet-id", SUBNET, f"--endpoint={endpoint}", "--port", "443"]
                )
                self.assertEqual(code, 2)
                self.assertNotIn("Traceback", stderr)
        for port in ("0", "65536", "-1", "abc", "443.0"):
            with self.subTest(port=port):
                code, _, _ = self.run_main(
                    [
                        "--subnet-id",
                        SUBNET,
                        "--endpoint",
                        "example.test",
                        "--port",
                        port,
                    ]
                )
                self.assertEqual(code, 2)
        for flag, value in (
            ("--account", SUBNET),
            ("--project", ACCOUNT),
            ("--subnet-id", "agents"),
            ("--account", ACCOUNT + "?bad"),
        ):
            with self.subTest(flag=flag, value=value):
                code, _, stderr = self.run_main(
                    [flag, value, "--endpoint", "example.test", "--port", "443"]
                )
                self.assertEqual(code, 2)
                self.assertNotIn("Traceback", stderr)

    def test_source_required_and_exclusive(self):
        for source in ([], ["--account", ACCOUNT, "--subnet-id", SUBNET]):
            code, _, _ = self.run_main(
                [*source, "--endpoint", "example.test", "--port", "443"]
            )
            self.assertEqual(code, 2)

    def test_subscription_name_preserved_and_html_persisted(self):
        azure = AzureFixture()
        azure.private_endpoint()
        with tempfile.TemporaryDirectory() as tmp:
            html = Path(tmp) / "report.html"
            code, stdout, stderr = self.run_main(
                [
                    "--subnet-id",
                    SUBNET,
                    "--endpoint",
                    "10.0.2.4",
                    "--port",
                    "443",
                    "--subscription",
                    "My Subscription",
                    "--html",
                    str(html),
                ],
                azure,
            )
            self.assertEqual((code, stderr), (0, ""))
            self.assertIn("REACHABLE", stdout)
            self.assertIn("<!doctype html>", html.read_text())
        self.assertTrue(all(sub == "My Subscription" for _, sub in azure.calls))

    def test_account_byo_and_project_managed_dispatch(self):
        azure = AzureFixture()
        azure.private_endpoint()
        code, _, _ = self.run_main(
            ["--account", ACCOUNT, "--endpoint", "10.0.2.4", "--port", "443"], azure
        )
        self.assertEqual(code, 0)
        azure.account["properties"]["networkInjections"] = [
            {"scenario": "agent", "useMicrosoftManagedNetwork": True}
        ]
        code, _, _ = self.run_main(
            ["--project", PROJECT, "--endpoint", "10.0.2.4", "--port", "443"], azure
        )
        self.assertEqual(code, 3)
        self.assertTrue(
            any(
                PROJECT + "/capabilityHosts?" in " ".join(args)
                for args, _ in azure.calls
            )
        )

    def test_failed_reads_surface_in_complete_main_path(self):
        for prefix in (
            ["network", "vnet", "show"],
            ["network", "nsg", "show"],
            ["network", "route-table", "show"],
            ["network", "private-endpoint", "list"],
        ):
            with self.subTest(prefix=prefix):
                azure = AzureFixture()
                azure.attach_nsg(rule())
                azure.attach_route("10.0.2.4/32", "VnetLocal")
                azure.fail_prefix = prefix
                code, _, stderr = self.run_main(
                    ["--subnet-id", SUBNET, "--endpoint", "10.0.2.4", "--port", "443"],
                    azure,
                )
                self.assertEqual(code, 2)
                self.assertIn("AuthorizationFailed", stderr)
                self.assertNotIn("Traceback", stderr)

    def test_azure_stderr_and_invalid_json_are_not_absence(self):
        for stdout, stderr, status in (
            ("", "AuthorizationFailed: denied", 1),
            ("not JSON", "warning", 0),
            ("null", "", 0),
            ("{}", "", 0),
        ):
            with self.subTest(stdout=stdout, status=status):
                with patch.object(
                    analyzer.subprocess,
                    "run",
                    return_value=subprocess.CompletedProcess(
                        ["az"], status, stdout, stderr
                    ),
                ):
                    with self.assertRaises(analyzer.AzError) as error:
                        analyzer.az(["network", "nsg", "show"], allow_fail=True)
                    if stderr:
                        self.assertIn(stderr, str(error.exception))

    def test_subprocess_failure_exits_two_without_traceback(self):
        stdout, stderr = io.StringIO(), io.StringIO()
        with (
            patch.object(
                sys,
                "argv",
                [
                    "analyze.py",
                    "--subnet-id",
                    SUBNET,
                    "--endpoint",
                    "10.0.2.4",
                    "--port",
                    "443",
                ],
            ),
            patch.object(
                analyzer.subprocess,
                "run",
                return_value=subprocess.CompletedProcess(
                    ["az"], 1, "", "Azure read denied"
                ),
            ),
            contextlib.redirect_stdout(stdout),
            contextlib.redirect_stderr(stderr),
        ):
            self.assertEqual(analyzer.main(), 2)
        self.assertIn("Azure read denied", stderr.getvalue())
        self.assertNotIn("REACHABLE", stdout.getvalue())

    def test_missing_az_and_timeout_are_setup_errors(self):
        for error in (FileNotFoundError("az"), subprocess.TimeoutExpired(["az"], 120)):
            with self.subTest(error=error):
                with patch.object(analyzer.subprocess, "run", side_effect=error):
                    with self.assertRaises(analyzer.AzError):
                        analyzer.az(["account", "show"])

    def test_diagnose_cli_errors_and_uncertainty(self):
        with tempfile.TemporaryDirectory() as tmp:
            path = Path(tmp) / "nsg.json"
            path.write_text(
                json.dumps(
                    {
                        "securityRules": [
                            rule("possible", sourcePortRange="1234"),
                            rule("deny", "Deny", 200),
                        ]
                    }
                )
            )
            stdout = io.StringIO()
            with (
                patch.object(
                    sys,
                    "argv",
                    [
                        "diagnose.py",
                        "--nsg-file",
                        str(path),
                        "--ip",
                        "10.0.2.4",
                        "--vnet-prefixes",
                        "10.0.0.0/16",
                        "--port",
                        "443",
                    ],
                ),
                contextlib.redirect_stdout(stdout),
            ):
                self.assertEqual(diagnose.main(), 3)
            self.assertIn("INDETERMINATE", stdout.getvalue())
            self.assertNotIn("BLOCKS THIS TRAFFIC", stdout.getvalue())
            path.write_text("invalid")
            with (
                patch.object(
                    sys,
                    "argv",
                    [
                        "diagnose.py",
                        "--nsg-file",
                        str(path),
                        "--ip",
                        "10.0.2.4",
                        "--port",
                        "443",
                    ],
                ),
                contextlib.redirect_stderr(io.StringIO()),
            ):
                self.assertEqual(diagnose.main(), 2)


if __name__ == "__main__":
    unittest.main()
