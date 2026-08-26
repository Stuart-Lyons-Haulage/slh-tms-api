#!/usr/bin/env python3
import json
import pathlib
import sys


def validate(workflow):
    errors = []
    properties = workflow.get("properties", {})
    definition = properties.get("definition", {})
    triggers = definition.get("triggers", {})
    actions = definition.get("actions", {})

    trigger = triggers.get("Every_5_Minutes", {})
    recurrence = trigger.get("recurrence", {})
    if trigger.get("type") != "Recurrence" or recurrence.get("frequency") != "Minute" or recurrence.get("interval") != 5:
        errors.append("heartbeat must run every 5 minutes")
    if trigger.get("runtimeConfiguration", {}).get("concurrency", {}).get("runs") != 1:
        errors.append("heartbeat trigger concurrency must be 1")

    probe = actions.get("Probe_Info_Shared_Mailbox", {})
    probe_inputs = probe.get("inputs", {})
    probe_host = probe_inputs.get("host", {})
    probe_params = probe_inputs.get("parameters", {})
    if probe_host.get("operationId") != "HttpRequest":
        errors.append("heartbeat must probe Outlook through the Office 365 HttpRequest action")
    uri = str(probe_params.get("Uri", ""))
    if "graph.microsoft.com/v1.0/users/" not in uri or "/mailFolders/inbox/messages" not in uri:
        errors.append("heartbeat probe must read the configured shared Inbox through Microsoft Graph")
    if probe_params.get("Method") != "GET":
        errors.append("heartbeat mailbox probe must be a GET")

    record = actions.get("Record_Heartbeat_In_TMS", {})
    record_host = record.get("inputs", {}).get("host", {})
    if record_host.get("operationId") != "RecordInfoMailboxHeartbeat":
        errors.append("heartbeat must use the RecordInfoMailboxHeartbeat TMS operation")
    if set(record.get("runAfter", {}).get("Probe_Info_Shared_Mailbox", [])) != {"Succeeded"}:
        errors.append("TMS heartbeat must only be recorded after a successful Outlook mailbox probe")
    body = record.get("inputs", {}).get("body", {})
    for field in ("mailbox", "flowName", "flowRunId", "checkedAtUtc", "latestInboxReceivedAtUtc"):
        if field not in body:
            errors.append(f"heartbeat body is missing {field}")

    serialized = json.dumps(workflow, separators=(",", ":"))
    if "secureData" not in serialized:
        errors.append("Outlook probe outputs must be protected")
    connection_names = set(properties.get("connectionReferences", {}))
    if connection_names != {"shared_office365", "shared_slhtms"}:
        errors.append("heartbeat flow must use only the Outlook and existing TMS connection references")

    for action_name in ("Probe_Info_Shared_Mailbox", "Record_Heartbeat_In_TMS"):
        policy = actions.get(action_name, {}).get("runtimeConfiguration", {}).get("retryPolicy", {})
        if policy.get("type") != "exponential" or not isinstance(policy.get("count"), int) or not 1 <= policy["count"] <= 4:
            errors.append(f"{action_name} retry must be bounded exponential with 1-4 attempts")

    return errors


def validate_openapi(text):
    errors = []
    if "/order-intake/email/heartbeat:" not in text:
        errors.append("custom connector is missing the mailbox heartbeat path")
    if "operationId: RecordInfoMailboxHeartbeat" not in text:
        errors.append("custom connector is missing RecordInfoMailboxHeartbeat")
    if "InfoMailboxHeartbeatRequest:" not in text or "InfoMailboxHeartbeatResponse:" not in text:
        errors.append("custom connector is missing heartbeat request/response definitions")
    return errors


def main():
    directory = pathlib.Path(__file__).parent
    workflow_path = directory / "workflow.json"
    openapi_path = directory.parents[1] / "openapi-power-automate.yaml"
    errors = validate(json.loads(workflow_path.read_text(encoding="utf-8")))
    errors.extend(validate_openapi(openapi_path.read_text(encoding="utf-8")))
    if errors:
        print("\n".join(f"ERROR: {item}" for item in errors))
        return 1
    print(f"Validated {workflow_path.name}: shared-mailbox heartbeat + connector contract satisfied")
    return 0


if __name__ == "__main__":
    sys.exit(main())
