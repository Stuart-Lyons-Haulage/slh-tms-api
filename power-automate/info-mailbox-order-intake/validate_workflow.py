#!/usr/bin/env python3
import json
import pathlib
import sys


REQUIRED_REQUEST_FIELDS = {
    "messageId", "internetMessageId", "conversationId", "mailbox",
    "senderAddress", "senderName", "toRecipients", "ccRecipients", "subject",
    "receivedAtUtc", "bodyText", "bodyHtml", "bodyFormat", "importance",
    "webLink", "correlationId", "attachments",
}


def _walk(value):
    yield value
    if isinstance(value, dict):
        for child in value.values():
            yield from _walk(child)
    elif isinstance(value, list):
        for child in value:
            yield from _walk(child)


def validate(workflow):
    errors = []
    properties = workflow.get("properties", {})
    definition = properties.get("definition", {})
    actions = definition.get("actions", {})
    triggers = definition.get("triggers", {})

    if "When_New_Email_Arrives_Info_Shared_Mailbox" not in triggers:
        errors.append("shared-mailbox trigger is missing")

    serialized = json.dumps(workflow, separators=(",", ":"))
    if "/api/v1/orders" in serialized or '"operationId":"CreateOrder"' in serialized:
        errors.append("live-order endpoint/action is forbidden")
    forbidden_external_stores = ("shared_sharepoint", "CreateItem", "Microsoft List", "SharePoint")
    if any(value.lower() in serialized.lower() for value in forbidden_external_stores):
        errors.append("Microsoft Lists/SharePoint storage is forbidden; TMS SQL is authoritative")
    if "IntakeInfoMailboxEmail" not in serialized:
        errors.append("Pending Review intake operation IntakeInfoMailboxEmail is missing")

    submit = actions.get("Scope_Submit_To_TMS", {}).get("actions", {}).get("POST_To_TMS_Staging", {})
    body = submit.get("inputs", {}).get("body", {})
    missing = sorted(REQUIRED_REQUEST_FIELDS - set(body))
    if missing:
        errors.append("request evidence fields missing: " + ", ".join(missing))

    for node in _walk(actions):
        if not isinstance(node, dict) or "runtimeConfiguration" not in node:
            continue
        policy = node["runtimeConfiguration"].get("retryPolicy", {})
        if policy and not (
            policy.get("type") == "exponential"
            and isinstance(policy.get("count"), int)
            and 1 <= policy["count"] <= 4
        ):
            errors.append("API retry must be bounded exponential with 1-4 attempts")

    trigger_concurrency = triggers.get("When_New_Email_Arrives_Info_Shared_Mailbox", {}).get("runtimeConfiguration", {}).get("concurrency", {})
    if trigger_concurrency.get("runs") != 4:
        errors.append("trigger concurrency must be 4")

    if "Get_Attachment_Content" not in serialized:
        errors.append("attachment content retrieval is missing")
    if "secureData" not in serialized:
        errors.append("secure input/output protection is missing")
    connection_names = set(properties.get("connectionReferences", {}))
    if connection_names != {"shared_office365", "shared_slhtms"}:
        errors.append("flow must use only the Outlook and existing TMS connection references")
    return errors


def main():
    path = pathlib.Path(__file__).with_name("workflow.json")
    errors = validate(json.loads(path.read_text(encoding="utf-8")))
    if errors:
        print("\n".join(f"ERROR: {item}" for item in errors))
        return 1
    print(f"Validated {path.name}: production intake contract satisfied")
    return 0


if __name__ == "__main__":
    sys.exit(main())
