import json
import pathlib
import unittest

from validate_workflow import validate


ROOT = pathlib.Path(__file__).parent


class WorkflowValidationTests(unittest.TestCase):
    def test_production_workflow_contract(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        self.assertEqual([], validate(workflow))

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
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        workflow["properties"]["connectionReferences"]["shared_sharepoint"] = {}
        errors = validate(workflow)
        self.assertTrue(any("Lists/SharePoint" in item for item in errors))

    def test_rejects_attachment_presence_trigger_filter(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        trigger_parameters = workflow["properties"]["definition"]["triggers"]["When_New_Email_Arrives_Info_Shared_Mailbox"]["inputs"]["parameters"]
        trigger_parameters["hasAttachments"] = False
        errors = validate(workflow)
        self.assertTrue(any("attachment presence" in item for item in errors))

    def test_rejects_skipping_tms_submit_after_attachment_failure(self):
        workflow = json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))
        workflow["properties"]["definition"]["actions"]["Scope_Submit_To_TMS"]["runAfter"] = {
            "Scope_Receive_Source": ["Succeeded"]
        }
        errors = validate(workflow)
        self.assertTrue(any("attachment retrieval fails" in item for item in errors))


if __name__ == "__main__":
    unittest.main()
