from pathlib import Path

path = Path(__file__).resolve().parents[1] / "openapi-power-automate.yaml"
text = path.read_text(encoding="utf-8")

if "RecordInfoMailboxHeartbeat" not in text:
    text = text.replace("  version: '1.2'", "  version: '1.3'", 1)

    path_marker = "  /customer-communications/pending:\n"
    heartbeat_path = """  /order-intake/email/heartbeat:\n    post:\n      operationId: RecordInfoMailboxHeartbeat\n      summary: Record a successful scheduled shared-mailbox connectivity probe\n      x-ms-visibility: important\n      parameters:\n        - name: body\n          in: body\n          required: true\n          schema: { $ref: '#/definitions/InfoMailboxHeartbeatRequest' }\n      responses:\n        '202': { description: Shared-mailbox heartbeat recorded, schema: { $ref: '#/definitions/InfoMailboxHeartbeatResponse' } }\n"""
    if path_marker not in text:
        raise SystemExit("OpenAPI path insertion marker not found")
    text = text.replace(path_marker, heartbeat_path + path_marker, 1)

    definition_marker = "  EmailIntakeResponse:\n"
    heartbeat_definitions = """  InfoMailboxHeartbeatRequest:\n    type: object\n    required: [mailbox]\n    properties:\n      mailbox: { type: string, example: info@lyonshaulage.com }\n      flowName: { type: string, example: 'SLH-TMS | Info Mailbox | Heartbeat | PROD' }\n      flowRunId: { type: string }\n      checkedAtUtc: { type: string, format: date-time }\n      latestInboxReceivedAtUtc: { type: string, format: date-time, description: Newest shared-Inbox message observed by the successful Outlook/Graph probe }\n  InfoMailboxHeartbeatResponse:\n    type: object\n    properties:\n      heartbeatAccepted: { type: boolean }\n      mailbox: { type: string }\n      recordedAtUtc: { type: string, format: date-time }\n      latestInboxReceivedAtUtc: { type: string, format: date-time }\n"""
    if definition_marker not in text:
        raise SystemExit("OpenAPI definition insertion marker not found")
    text = text.replace(definition_marker, heartbeat_definitions + definition_marker, 1)

required = [
    "version: '1.3'",
    "/order-intake/email/heartbeat:",
    "operationId: RecordInfoMailboxHeartbeat",
    "InfoMailboxHeartbeatRequest:",
    "InfoMailboxHeartbeatResponse:",
]
missing = [item for item in required if item not in text]
if missing:
    raise SystemExit("Heartbeat OpenAPI patch incomplete: " + ", ".join(missing))

path.write_text(text, encoding="utf-8")
