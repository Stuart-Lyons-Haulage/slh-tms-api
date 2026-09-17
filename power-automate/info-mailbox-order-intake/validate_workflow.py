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


def _find_action(actions, name):
    if name in actions:
        return actions[name]
    for node in actions.values():
        if isinstance(node, dict):
            nested = node.get("actions")
            if isinstance(nested, dict):
                found = _find_action(nested, name)
                if found is not None:
                    return found
    return None


def validate(workflow):
    errors = []
    properties = workflow.get("properties", {})
    definition = properties.get("definition", {})
    actions = definition.get("actions", {})
    triggers = definition.get("triggers", {})

    trigger = triggers.get("When_New_Email_Arrives_Info_Shared_Mailbox")
    if not isinstance(trigger, dict):
        errors.append("shared-mailbox trigger is missing")
        trigger = {}

    trigger_parameters = trigger.get("inputs", {}).get("parameters", {})
    if "hasAttachments" in trigger_parameters:
        errors.append("shared-mailbox trigger must not filter by attachment presence")
    if trigger.get("conditions"):
        errors.append("shared-mailbox trigger must not filter sender, subject or order type; every inbox email must reach TMS intake")
    if trigger_parameters.get("mailboxAddress") not in (
        "@parameters('SLH_InfoMailboxUPN')",
        "info@lyonshaulage.com",
    ):
        errors.append("shared-mailbox trigger must target the Info mailbox")
    if trigger_parameters.get("includeAttachments") is not True:
        errors.append("shared-mailbox trigger must include attachment metadata")

    serialized = json.dumps(workflow, separators=(",", ":"))
    if "/api/v1/orders" in serialized or '"operationId":"CreateOrder"' in serialized:
        errors.append("live-order endpoint/action is forbidden")
    forbidden_external_stores = ("shared_sharepoint", "CreateItem", "Microsoft List", "SharePoint")
    if any(value.lower() in serialized.lower() for value in forbidden_external_stores):
        errors.append("Microsoft Lists/SharePoint storage is forbidden; TMS SQL is authoritative")
    if "IntakeInfoMailboxEmail" not in serialized:
        errors.append("Pending Review intake operation IntakeInfoMailboxEmail is missing")

    submit = _find_action(actions, "POST_To_TMS_Staging") or {}
    body = submit.get("inputs", {}).get("body", {})
    missing = sorted(REQUIRED_REQUEST_FIELDS - set(body))
    if missing:
        errors.append("request evidence fields missing: " + ", ".join(missing))

    if body.get("attachments") != "@variables('varAttachments')":
        errors.append("TMS staging request must submit the varAttachments array containing retained attachment copies")
    if "bodyPreview" not in str(body.get("bodyText", "")):
        errors.append("bodyText must come from Outlook bodyPreview")
    if "body/body" not in str(body.get("bodyHtml", "")):
        errors.append("bodyHtml must contain the full Outlook body")
    body_format = str(body.get("bodyFormat", ""))
    if "isHtml" not in body_format or "'html'" not in body_format or "'text'" not in body_format:
        errors.append("bodyFormat must convert Outlook isHtml to the API string values html/text")

    get_attachment_list = _find_action(actions, "Get_Attachment_List") or {}
    if get_attachment_list.get("inputs", {}).get("host", {}).get("operationId") != "GetAttachments_V2":
        errors.append("Get_Attachment_List must call Outlook GetAttachments_V2")

    attachment_loop = _find_action(actions, "For_Each_Source_Attachment") or {}
    if "Get_Attachment_List" not in str(attachment_loop.get("foreach", "")):
        errors.append("attachment loop must enumerate GetAttachments_V2 results")

    attachment_actions = attachment_loop.get("actions", {})
    get_attachment = attachment_actions.get("Get_Attachment_Content", {})
    if get_attachment.get("inputs", {}).get("host", {}).get("operationId") != "GetAttachment_V2":
        errors.append("Get_Attachment_Content must call Outlook GetAttachment_V2")

    append_attachment = attachment_actions.get("Append_Original_Attachment", {})
    append_value = append_attachment.get("inputs", {}).get("value", {})
    content_expression = str(append_value.get("contentBase64", ""))
    if "Get_Attachment_Content" not in content_expression or "contentBytes" not in content_expression:
        errors.append("source attachment bytes must be mapped from Get_Attachment_Content body/contentBytes into contentBase64")

    failed_metadata = attachment_actions.get("Append_Failed_Attachment_Metadata", {}).get("inputs", {}).get("value", {})
    if failed_metadata.get("contentUnavailable") is not True:
        errors.append("failed attachment fetches must retain contentUnavailable metadata")

    list_failure = _find_action(actions, "Record_Attachment_List_Failure") or {}
    if list_failure.get("inputs", {}).get("value", {}).get("contentUnavailable") is not True:
        errors.append("attachment-list failure must still be represented in the intake evidence")

    submit_run_after = submit.get("runAfter", {})
    submit_after_loop = set(submit_run_after.get("For_Each_Source_Attachment", []))
    if submit_after_loop and not {"Succeeded", "Failed", "TimedOut", "Skipped"}.issubset(submit_after_loop):
        errors.append("TMS submission must continue after attachment-loop success/failure/timeout/skipped")

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

    trigger_concurrency = trigger.get("runtimeConfiguration", {}).get("concurrency", {})
    if trigger_concurrency.get("runs") != 4:
        errors.append("trigger concurrency must be 4")

    for node in _walk(actions):
        if isinstance(node, dict) and node.get("type") == "Foreach":
            foreach_expression = str(node.get("foreach", ""))
            if "triggerOutputs" in foreach_expression and "attachments" in foreach_expression:
                errors.append("attachment loop must use the GetAttachments_V2 result, not the trigger attachments string")

    if "Get_Attachment_Content" not in serialized:
        errors.append("attachment content retrieval is missing")
    if "secureData" not in serialized:
        errors.append("secure input/output protection is missing")
    connection_names = set(properties.get("connectionReferences", {}))
    if connection_names != {"shared_office365", "shared_slhtms"}:
        errors.append("flow must use only the Outlook and existing TMS connection references")

    obsolete_placeholder = "Placeholder only - replace with SLH TMS API"
    if obsolete_placeholder.lower() in serialized.lower():
        errors.append("obsolete placeholder Compose action must not remain in the production flow")

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
