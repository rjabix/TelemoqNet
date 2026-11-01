"""Reject destructive retained-data changes and unrelated runtime operations."""
import json
import sys


def check(plan, runtime_only=False):
    for change in plan.get("resource_changes", []):
        if change.get("mode") == "data":
            continue
        actions = change["change"]["actions"]
        if actions == ["no-op"]:
            continue
        address = change["address"]
        retained = address.startswith(("azapi_resource.analysis_table",
            "azurerm_storage_container.analysis_checkpoints", "azurerm_storage_account.logs",
            "azurerm_log_analytics_workspace.main"))
        if retained and "delete" in actions:
            raise ValueError("Retained data destruction rejected: " + address)
        name = change.get("name", "")
        if runtime_only and not (name.startswith("analysis") or name == "honeypot_analysis"):
            raise ValueError("Unrelated infrastructure change rejected: " + address)


if __name__ == "__main__":
    try:
        check(json.load(sys.stdin), "--runtime-only" in sys.argv)
    except ValueError as error:
        raise SystemExit(str(error)) from None
