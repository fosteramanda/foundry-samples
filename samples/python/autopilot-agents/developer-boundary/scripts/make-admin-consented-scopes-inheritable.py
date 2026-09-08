#!/usr/bin/env python
"""Mark every tenant-wide admin-consented delegated scope inheritable."""

from __future__ import annotations

import argparse
import json
import os
import shutil
import subprocess
import time
import urllib.error
import urllib.parse
import urllib.request

_API_VERSION = "2025-11-15-preview"
_ENUMERATED_SCOPES_ODATA_TYPE = "#microsoft.graph.enumeratedScopes"
_FOUNDRY_FEATURES = (
    "HostedAgents=V1Preview,AgentEndpoints=V1Preview,DigitalWorker=V1Preview"
)
_GRAPH = "https://graph.microsoft.com"
_AZD_ENVIRONMENT: str | None = None


def _run(command: list[str], failure_message: str) -> str:
    try:
        result = subprocess.run(
            command,
            check=True,
            capture_output=True,
            text=True,
        )
    except FileNotFoundError as error:
        raise SystemExit(
            f"'{command[0]}' was not found on PATH."
        ) from error
    except subprocess.CalledProcessError as error:
        details = (error.stderr or error.stdout or "").strip()
        suffix = f"\n{details}" if details else ""
        raise SystemExit(f"{failure_message}{suffix}") from error
    return result.stdout.strip()


def _access_token(resource: str) -> str:
    az = shutil.which("az")
    if not az:
        raise SystemExit(
            "'az' was not found on PATH. Install Azure CLI and run 'az login'."
        )
    token = _run(
        [
            az,
            "account",
            "get-access-token",
            "--resource",
            resource,
            "--query",
            "accessToken",
            "--output",
            "tsv",
        ],
        f"Failed to acquire an access token for {resource}. Run 'az login'.",
    )
    if not token:
        raise SystemExit(f"Azure CLI returned an empty token for {resource}.")
    return token


def _azd_value(name: str) -> str | None:
    azd = shutil.which("azd")
    if not azd:
        raise SystemExit(
            "'azd' was not found on PATH. Install Azure Developer CLI."
        )
    command = [azd, "env", "get-value", name]
    if _AZD_ENVIRONMENT:
        command.extend(["--environment", _AZD_ENVIRONMENT])
    result = subprocess.run(
        command,
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        return None
    return result.stdout.strip() or None


def _configuration_value(
    environment_names: tuple[str, ...],
    azd_names: tuple[str, ...],
) -> str:
    for name in environment_names:
        value = os.environ.get(name)
        if value:
            return value
    for name in azd_names:
        value = _azd_value(name)
        if value:
            return value
    names = ", ".join((*environment_names, *azd_names))
    raise SystemExit(
        f"Could not resolve any of these settings: {names}. Run 'azd "
        "provision' and 'azd deploy' from the sample directory."
    )


def _project_endpoint() -> str:
    return _configuration_value(
        ("FOUNDRY_PROJECT_ENDPOINT", "AZURE_AI_PROJECT_ENDPOINT"),
        (
            "FOUNDRY_PROJECT_ENDPOINT",
            "AZURE_AI_PROJECT_ENDPOINT",
            "AGENT_DEVELOPER_BOUNDARY_PROJECT_ENDPOINT",
        ),
    ).rstrip("/")


def _agent_name() -> str:
    return _configuration_value(
        ("AGENT_NAME", "AZURE_AI_AGENT_NAME"),
        ("AZURE_AI_AGENT_NAME", "AGENT_DEVELOPER_BOUNDARY_NAME"),
    )


def _headers(token: str) -> dict[str, str]:
    return {
        "Authorization": "Bearer " + token,
        "Accept": "application/json",
    }


def _foundry_headers(token: str) -> dict[str, str]:
    return {
        **_headers(token),
        "Foundry-Features": _FOUNDRY_FEATURES,
    }


def _request(
    url: str,
    headers: dict[str, str],
    method: str = "GET",
    body: dict | None = None,
) -> dict:
    data = json.dumps(body).encode("utf-8") if body is not None else None
    request = urllib.request.Request(url, data=data, method=method)
    for key, value in headers.items():
        request.add_header(key, value)
    if data is not None:
        request.add_header("Content-Type", "application/json")
    try:
        with urllib.request.urlopen(request) as response:
            raw = response.read().decode("utf-8")
    except urllib.error.HTTPError as error:
        details = error.read().decode("utf-8", errors="replace")
        raise SystemExit(
            f"{method} failed ({error.code}) for {url}:\n{details}"
        ) from error
    except urllib.error.URLError as error:
        raise SystemExit(f"{method} failed for {url}: {error.reason}") from error
    return json.loads(raw) if raw else {}


def _collection(url: str, headers: dict[str, str]) -> list[dict]:
    values: list[dict] = []
    next_url: str | None = url
    while next_url:
        page = _request(next_url, headers)
        page_values = page.get("value")
        if not isinstance(page_values, list):
            raise SystemExit(
                f"Collection response from {next_url} has no value array."
            )
        values.extend(page_values)
        next_link = page.get("@odata.nextLink")
        if next_link is not None and not isinstance(next_link, str):
            raise SystemExit(
                f"Collection response from {next_url} has an invalid next link."
            )
        next_url = next_link
    return values


def _blueprint_info(foundry_token: str) -> tuple[str, str]:
    escaped_agent_name = urllib.parse.quote(_agent_name(), safe="")
    url = (
        f"{_project_endpoint()}/agents/{escaped_agent_name}"
        f"?api-version={_API_VERSION}"
    )
    agent = _request(url, _foundry_headers(foundry_token))
    try:
        blueprint_name = agent["blueprint_reference"]["blueprint_id"]
        blueprint_sp_id = agent["blueprint"]["principal_id"]
    except (KeyError, TypeError) as error:
        raise SystemExit(
            "The agent definition does not contain its managed blueprint name "
            "and service-principal ID. Run 'azd deploy' before this script."
        ) from error
    if not blueprint_name or not blueprint_sp_id:
        raise SystemExit(
            "The agent definition returned an empty managed blueprint name or "
            "service-principal ID."
        )
    return blueprint_name, blueprint_sp_id


def _permission_entry(resource_app_id: str, scopes: set[str]) -> dict:
    return {
        "resourceAppId": resource_app_id,
        "inheritableScopes": {
            "@odata.type": _ENUMERATED_SCOPES_ODATA_TYPE,
            "scopes": sorted(scopes),
        },
    }


def _admin_consented_permissions(
    graph_token: str,
    blueprint_sp_id: str,
) -> tuple[list[dict], dict[str, str]]:
    headers = _headers(graph_token)
    escaped_sp_id = urllib.parse.quote(blueprint_sp_id, safe="")
    grants_url = (
        f"{_GRAPH}/v1.0/servicePrincipals/{escaped_sp_id}/"
        "oauth2PermissionGrants"
        "?$select=consentType,resourceId,scope"
    )
    grants = [
        grant
        for grant in _collection(grants_url, headers)
        if grant.get("consentType") == "AllPrincipals"
    ]
    if not grants:
        raise SystemExit(
            "No tenant-wide admin-consented delegated permission grants were "
            "found for the blueprint. Ask an administrator to grant consent "
            "before running this script."
        )

    scopes_by_app_id: dict[str, set[str]] = {}
    resource_names: dict[str, str] = {}
    app_id_by_resource_id: dict[str, str] = {}
    for grant in grants:
        resource_id = grant.get("resourceId")
        if not isinstance(resource_id, str) or not resource_id:
            raise SystemExit(
                "Microsoft Graph returned an admin-consent grant without a "
                "resource service-principal ID."
            )
        scope_value = grant.get("scope")
        if not isinstance(scope_value, str):
            raise SystemExit(
                f"Microsoft Graph returned an invalid scope value for resource "
                f"service principal {resource_id}."
            )
        scopes = {scope for scope in scope_value.split() if scope}
        if not scopes:
            continue

        resource_app_id = app_id_by_resource_id.get(resource_id)
        if not resource_app_id:
            escaped_resource_id = urllib.parse.quote(resource_id, safe="")
            resource = _request(
                f"{_GRAPH}/v1.0/servicePrincipals/{escaped_resource_id}"
                "?$select=appId,displayName",
                headers,
            )
            resource_app_id = resource.get("appId")
            if not isinstance(resource_app_id, str) or not resource_app_id:
                raise SystemExit(
                    "Could not resolve resource service principal "
                    f"{resource_id} to an application ID."
                )
            app_id_by_resource_id[resource_id] = resource_app_id
            display_name = resource.get("displayName")
            resource_names[resource_app_id] = (
                display_name
                if isinstance(display_name, str) and display_name
                else resource_app_id
            )
        scopes_by_app_id.setdefault(resource_app_id, set()).update(scopes)

    if not scopes_by_app_id:
        raise SystemExit(
            "The tenant-wide admin-consent grants contain no delegated scopes."
        )
    entries = [
        _permission_entry(app_id, scopes_by_app_id[app_id])
        for app_id in sorted(scopes_by_app_id)
    ]
    return entries, resource_names


def _patch_permissions(
    foundry_token: str,
    blueprint_name: str,
    entries: list[dict],
) -> None:
    escaped_name = urllib.parse.quote(blueprint_name, safe="")
    url = (
        f"{_project_endpoint()}/managedAgentIdentityBlueprints/{escaped_name}"
        f"?api-version={_API_VERSION}"
    )
    _request(
        url,
        _foundry_headers(foundry_token),
        method="PATCH",
        body={"InheritablePermissions": entries},
    )


def _blueprint_application_id(
    graph_token: str,
    blueprint_sp_id: str,
) -> str:
    headers = _headers(graph_token)
    escaped_sp_id = urllib.parse.quote(blueprint_sp_id, safe="")
    service_principal = _request(
        f"{_GRAPH}/v1.0/servicePrincipals/{escaped_sp_id}?$select=appId",
        headers,
    )
    blueprint_app_id = service_principal.get("appId")
    if not isinstance(blueprint_app_id, str) or not blueprint_app_id:
        raise SystemExit(
            "Could not resolve the blueprint service principal to an "
            "application ID."
        )

    filter_value = urllib.parse.quote(
        f"appId eq '{blueprint_app_id}'", safe=""
    )
    applications = _collection(
        f"{_GRAPH}/v1.0/applications?$filter={filter_value}&$select=id",
        headers,
    )
    if len(applications) != 1 or not applications[0].get("id"):
        raise SystemExit(
            "Could not uniquely resolve the blueprint application object."
        )
    return applications[0]["id"]


def _inheritable_permissions(
    graph_token: str,
    blueprint_application_id: str,
) -> dict[str, set[str]]:
    escaped_object_id = urllib.parse.quote(
        blueprint_application_id, safe=""
    )
    url = (
        f"{_GRAPH}/beta/applications/"
        f"microsoft.graph.agentIdentityBlueprint/{escaped_object_id}/"
        "inheritablePermissions"
    )
    permissions: dict[str, set[str]] = {}
    for entry in _collection(url, _headers(graph_token)):
        resource_app_id = entry.get("resourceAppId")
        inheritable_scopes = entry.get("inheritableScopes") or {}
        scopes = inheritable_scopes.get("scopes") or []
        if isinstance(resource_app_id, str) and isinstance(scopes, list):
            permissions.setdefault(resource_app_id, set()).update(scopes)
    return permissions


def _wait_for_permissions(
    graph_token: str,
    blueprint_application_id: str,
    expected_entries: list[dict],
    attempts: int = 10,
    delay_seconds: float = 2,
) -> None:
    expected = {
        entry["resourceAppId"]: set(entry["inheritableScopes"]["scopes"])
        for entry in expected_entries
    }
    actual: dict[str, set[str]] = {}
    for attempt in range(attempts):
        actual = _inheritable_permissions(
            graph_token, blueprint_application_id
        )
        missing = {
            app_id: scopes - actual.get(app_id, set())
            for app_id, scopes in expected.items()
            if not scopes.issubset(actual.get(app_id, set()))
        }
        if not missing:
            return
        if attempt < attempts - 1:
            print("Waiting for inheritable permission propagation ...")
            time.sleep(delay_seconds)
    raise SystemExit(
        "Verification failed. These admin-consented scopes are not "
        f"inheritable:\n{json.dumps({key: sorted(value) for key, value in missing.items()}, indent=2)}"
    )


def main() -> None:
    global _AZD_ENVIRONMENT

    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument(
        "--environment",
        help="Name of the azd environment that contains the deployed agent.",
    )
    parser.add_argument(
        "--dry-run",
        action="store_true",
        help="Display the discovered grants without updating the blueprint.",
    )
    args = parser.parse_args()
    _AZD_ENVIRONMENT = args.environment

    foundry_token = _access_token("https://ai.azure.com")
    graph_token = _access_token("https://graph.microsoft.com")
    blueprint_name, blueprint_sp_id = _blueprint_info(foundry_token)
    entries, resource_names = _admin_consented_permissions(
        graph_token, blueprint_sp_id
    )

    print(f"Managed blueprint: {blueprint_name}")
    print(f"Blueprint service principal: {blueprint_sp_id}")
    print("\nTenant-wide admin-consented delegated permissions:")
    for entry in entries:
        resource_app_id = entry["resourceAppId"]
        print(
            f"  {resource_names.get(resource_app_id, resource_app_id)} "
            f"({resource_app_id})"
        )
        for scope in entry["inheritableScopes"]["scopes"]:
            print(f"    - {scope}")

    if args.dry_run:
        print("\nDry run complete. The blueprint was not changed.")
        return

    print("\nUpdating the blueprint's inheritable permissions ...")
    _patch_permissions(foundry_token, blueprint_name, entries)
    blueprint_application_id = _blueprint_application_id(
        graph_token, blueprint_sp_id
    )
    _wait_for_permissions(
        graph_token, blueprint_application_id, entries
    )
    print(
        "Verified that every tenant-wide admin-consented delegated scope is "
        "inheritable."
    )


if __name__ == "__main__":
    main()
