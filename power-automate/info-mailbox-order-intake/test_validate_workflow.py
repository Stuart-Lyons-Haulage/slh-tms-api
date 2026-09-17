import json
import pathlib
import unittest

from validate_workflow import validate


ROOT = pathlib.Path(__file__).parent


def load_workflow():
    return json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))


class WorkflowValidationTests(unittest.TestCase):
    def test_production_workflow_contract(self):
        self.assertEqual([], validate(load_workflow()))

    def test_rejects_live_order_endpoint_and_unbounded_retry(self):
        unsafe = {
            "properties": {
                "definition": {
                    "triggers": {},
                    "actions": {
                        "POST_Live_Order": {
                            "type": "Http",
                            "inputs": {"uri": "https://example/api/v1/orders"},
                            "runtimeConfiguration": {"retryPolicy": {"type": "until-success"}},
                        }
                    },
                }
            }
        }
        errors = validate(unsafe)
        self.assertTrue(any("live-order" in item for item in errors))
        self.assertTrue(any("bounded exponential" in item for item in errors))

    def test_rejects_microsoft_list_or_sharepoint_storage(self):
        workflow = load_workflow()
        workflow["properties"]["connectionReferences"]["shared_sharepoint"] = {}
        errors = validate(workflow)
        self.assertTrue(any("Lists/SharePoint" in item for item in errors))

    def test_rejects_attachment_presence_trigger_filter(self):
        workflow = load_workflow()
        trigger_parameters = workflow["properties"]["definition"]["triggers"]["When_New_Email_Arrives_Info_Shared_Mailbox"]["inputs"]["parameters"]
        trigger_parameters["hasAttachments"] = False
        errors = validate(workflow)
        self.assertTrue(any("attachment presence" in item for item in errors))

    def test_rejects_any_trigger_condition(self):
        workflow = load_workflow()
        trigger = workflow["properties"]["definition"]["triggers"]["When_New_Email_Arrives_Info_Shared_Mailbox"]
        trigger["conditions"] = [{"expression": "@contains(triggerOutputs()?['body/subject'],'order')"}]
        errors = validate(workflow)
        self.assertTrue(any("must not filter sender, subject or order type" in item for item in errors))

    def test_rejects_trigger_attachment_string_loop(self):
        workflow = load_workflow()
        workflow["properties"]["definition"]["actions"]["For_Each_Source_Attachment"]["foreach"] = "@triggerOutputs()?['body/attachments']"
        errors = validate(workflow)
        self.assertTrue(any("trigger attachments string" in item for item in errors))

    def test_rejects_dropping_email_when_attachment_receive_fails(self):
        workflow = load_workflow()
        workflow["properties"]["definition"]["actions"]["POST_To_TMS_Staging"]["runAfter"] = {
            "For_Each_Source_Attachment": ["Succeeded"],
            "Record_Attachment_List_Failure": ["Skipped"],
        }
        errors = validate(workflow)
        self.assertTrue(any("attachment-loop success/failure/timeout/skipped" in item for item in errors))

    def test_rejects_missing_attachment_content_fetch(self):
        workflow = load_workflow()
        attachment_actions = workflow["properties"]["definition"]["actions"]["For_Each_Source_Attachment"]["actions"]
        attachment_actions["Get_Attachment_Content"]["inputs"]["host"]["operationId"] = "GetAttachments_V2"
        errors = validate(workflow)
        self.assertTrue(any("GetAttachment_V2" in item for item in errors))

    def test_rejects_attachment_metadata_without_file_bytes(self):
        workflow = load_workflow()
        attachment_actions = workflow["properties"]["definition"]["actions"]["For_Each_Source_Attachment"]["actions"]
        attachment_actions["Append_Original_Attachment"]["inputs"]["value"]["contentBase64"] = ""
        errors = validate(workflow)
        self.assertTrue(any("source attachment bytes" in item for item in errors))

    def test_rejects_staging_request_that_does_not_submit_attachment_array(self):
        workflow = load_workflow()
        workflow["properties"]["definition"]["actions"]["POST_To_TMS_Staging"]["inputs"]["body"]["attachments"] = []
        errors = validate(workflow)
        self.assertTrue(any("varAttachments array" in item for item in errors))

    def test_rejects_wrong_body_mapping(self):
        workflow = load_workflow()
        body = workflow["properties"]["definition"]["actions"]["POST_To_TMS_Staging"]["inputs"]["body"]
        body["bodyText"] = "@triggerOutputs()?['body/body']"
        body["bodyFormat"] = "@triggerOutputs()?['body/isHtml']"
        errors = validate(workflow)
        self.assertTrue(any("bodyText must come from Outlook bodyPreview" in item for item in errors))
        self.assertTrue(any("bodyFormat must convert Outlook isHtml" in item for item in errors))


if __name__ == "__main__":
    unittest.main()
