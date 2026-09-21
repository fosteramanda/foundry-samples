# Copyright (c) Microsoft. All rights reserved.

import json
import os
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
SUBNET_ID = (
    "/subscriptions/sub/resourceGroups/rg/providers/"
    "Microsoft.Network/virtualNetworks/vnet/subnets/agents"
)


class CliTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.work = Path(self.temp.name)
        self.bin = self.work / "bin"
        self.bin.mkdir()
        self.env = {
            **os.environ,
            "PATH": str(self.bin) + os.pathsep + os.defpath,
            "CALL_LOG": str(self.work / "calls.jsonl"),
            "SUBNET_ID": SUBNET_ID,
            "ANALYZER_EXIT": "0",
            "AZ_EXIT": "0",
            "BROWSER_EXIT": "0",
            "WRITE_REPORT": "1",
        }
        self.executable(
            "az",
            """
record("az", sys.argv[1:])
if os.environ["AZ_EXIT"] != "0":
    print("Azure read failed", file=sys.stderr)
else:
    print(os.environ["SUBNET_ID"])
sys.exit(int(os.environ["AZ_EXIT"]))
""",
        )
        self.executable(
            "python3",
            """
record("analyzer", sys.argv[1:])
if os.environ["WRITE_REPORT"] == "1":
    path = Path(sys.argv[sys.argv.index("--html") + 1])
    path.write_text("<!doctype html><title>Test report</title>")
sys.exit(int(os.environ["ANALYZER_EXIT"]))
""",
        )
        self.executable(
            "wslview",
            """
record("browser", sys.argv[1:])
sys.exit(int(os.environ["BROWSER_EXIT"]))
""",
        )

    def executable(self, name, body):
        script = self.bin / name
        script.write_text(
            f"#!{sys.executable}\n"
            "import json, os, sys\n"
            "from pathlib import Path\n"
            "def record(tool, args):\n"
            '    with open(os.environ["CALL_LOG"], "a") as log:\n'
            '        log.write(json.dumps({"tool": tool, "args": args}) + "\\n")\n'
            + body
        )
        script.chmod(0o755)

    def run_cli(self, *args):
        return subprocess.run(
            ["bash", str(ROOT / "reachability.sh"), *args],
            cwd=self.work,
            env=self.env,
            text=True,
            capture_output=True,
            check=False,
        )

    def base_args(self):
        return [
            "--subnet-id",
            SUBNET_ID,
            "--endpoint",
            "api.contoso.com",
            "--port",
            "443",
        ]

    def calls(self):
        log = Path(self.env["CALL_LOG"])
        if not log.exists():
            return []
        return [json.loads(line) for line in log.read_text().splitlines()]

    def test_help_does_not_invoke_azure_or_analyzer(self):
        result = self.run_cli("--help")
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertIn("diagnostic-agent", result.stdout)
        self.assertEqual([], self.calls())

    def test_live_and_both_modes_are_rejected_without_deployment(self):
        for mode in ("live", "both"):
            with self.subTest(mode=mode):
                result = self.run_cli(*self.base_args(), "--mode", mode)
                self.assertEqual(2, result.returncode)
                self.assertIn("diagnostic agent", result.stderr)
        self.assertEqual([], self.calls())

    def test_missing_value_is_a_usage_error(self):
        for args in (["--account"], ["--account", "--endpoint", "example.com"]):
            with self.subTest(args=args):
                result = self.run_cli(*args)
                self.assertEqual(2, result.returncode)
                self.assertIn("requires a value", result.stderr)
        self.assertEqual([], self.calls())

    def test_invalid_ports_are_rejected_before_azure(self):
        for port in ("0", "65536", "-1", "443x", "999999999999999999999"):
            with self.subTest(port=port):
                result = self.run_cli(*self.base_args()[:-1], port)
                self.assertEqual(2, result.returncode)
        self.assertEqual([], self.calls())

    def test_zero_padded_port_is_decimal(self):
        result = self.run_cli(*self.base_args()[:-1], "00080", "--no-open")
        self.assertEqual(0, result.returncode, result.stderr)
        args = self.calls()[0]["args"]
        self.assertEqual("80", args[args.index("--port") + 1])

    def test_conflicting_or_incomplete_sources_are_rejected(self):
        cases = [
            [*self.base_args(), "--account", "/account"],
            [*self.base_args(), "-g", "rg"],
            ["-g", "rg", "--endpoint", "example.com", "--port", "443"],
            ["--endpoint", "example.com", "--port", "443"],
        ]
        for args in cases:
            with self.subTest(args=args):
                result = self.run_cli(*args)
                self.assertEqual(2, result.returncode)
        self.assertEqual([], self.calls())

    def test_named_subnet_is_resolved_and_subscription_preserved(self):
        result = self.run_cli(
            "-g",
            "my-rg",
            "--vnet",
            "my-vnet",
            "--subnet",
            "agents",
            "--endpoint",
            "example.com",
            "--port",
            "443",
            "--subscription",
            "Subscription With Spaces",
            "--no-open",
        )
        self.assertEqual(0, result.returncode, result.stderr)
        az_call, analyzer_call = self.calls()
        self.assertEqual("az", az_call["tool"])
        self.assertEqual("analyzer", analyzer_call["tool"])
        for call in (az_call, analyzer_call):
            args = call["args"]
            self.assertEqual(
                "Subscription With Spaces", args[args.index("--subscription") + 1]
            )
        args = analyzer_call["args"]
        self.assertEqual(SUBNET_ID, args[args.index("--subnet-id") + 1])

    def test_azure_failure_is_not_a_successful_analysis(self):
        self.env["AZ_EXIT"] = "1"
        result = self.run_cli(
            "-g",
            "rg",
            "--vnet",
            "vnet",
            "--subnet",
            "agents",
            "--endpoint",
            "example.com",
            "--port",
            "443",
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("Azure read failed", result.stderr)
        self.assertEqual(["az"], [call["tool"] for call in self.calls()])

    def test_empty_subnet_lookup_is_an_error(self):
        self.env["SUBNET_ID"] = ""
        result = self.run_cli(
            "-g",
            "rg",
            "--vnet",
            "vnet",
            "--subnet",
            "agents",
            "--endpoint",
            "example.com",
            "--port",
            "443",
        )
        self.assertEqual(2, result.returncode)
        self.assertIn("no subnet resource ID", result.stderr)
        self.assertEqual(["az"], [call["tool"] for call in self.calls()])

    def test_exit_codes_are_preserved_in_headless_mode(self):
        for code in (0, 1, 2, 3):
            with self.subTest(code=code):
                self.env["ANALYZER_EXIT"] = str(code)
                result = self.run_cli(
                    *self.base_args(), "--no-open", "--mode", "static"
                )
                self.assertEqual(code, result.returncode, result.stderr)
        self.assertTrue(all(call["tool"] == "analyzer" for call in self.calls()))

    def test_report_path_with_spaces_is_passed_to_browser(self):
        report = self.work / "a report.html"
        result = self.run_cli(*self.base_args(), "--html", str(report))
        self.assertEqual(0, result.returncode, result.stderr)
        self.assertTrue(report.is_file())
        analyzer_call, browser_call = self.calls()
        args = analyzer_call["args"]
        self.assertEqual(str(report), args[args.index("--html") + 1])
        self.assertEqual({"tool": "browser", "args": [str(report)]}, browser_call)

    def test_setup_error_does_not_open_a_stale_report(self):
        report = self.work / "old.html"
        report.write_text("old report")
        self.env["ANALYZER_EXIT"] = "2"
        self.env["WRITE_REPORT"] = "0"
        result = self.run_cli(*self.base_args(), "--html", str(report))
        self.assertEqual(2, result.returncode)
        self.assertEqual(["analyzer"], [call["tool"] for call in self.calls()])
        self.assertEqual("old report", report.read_text())

    def test_browser_failure_warns_without_changing_verdict(self):
        self.env["ANALYZER_EXIT"] = "3"
        self.env["BROWSER_EXIT"] = "1"
        result = self.run_cli(*self.base_args())
        self.assertEqual(3, result.returncode)
        self.assertIn("Could not open the browser", result.stderr)


if __name__ == "__main__":
    unittest.main()
