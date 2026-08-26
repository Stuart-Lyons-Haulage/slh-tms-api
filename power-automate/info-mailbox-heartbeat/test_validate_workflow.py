import copy
import json
import pathlib
import unittest

from validate_workflow import validate

ROOT = pathlib.Path(__file__).parent


class HeartbeatWorkflowValidationTests(unittest.TestCase):
    def load(self):
        return json.loads((ROOT / "workflow.json").read_text(encoding="utf-8"))

    def test_packaged_workflow_is_valid(self):
        self.assertEqual([], validate(self.load()))

    def test_rejects_tms_heartbeat_when_outlook_probe_did_not_succeed(self):
        workflow = self.load()
        workflow["properties"]["definition"]["actions"]["Record_Heartbeat_In_TMS"]["runAfter"] = {
            "Probe_Info_Shared_Mailbox": ["Succeeded", "Failed"]
        }
        self.assertTrue(any("only be recorded" in error for error in validate(workflow)))

    def test_rejects_non_mailbox_self_ping(self):
        workflow = self.load()
        workflow["properties"]["definition"]["actions"]["Probe_Info_Shared_Mailbox"]["inputs"]["parameters"]["Uri"] = "https://graph.microsoft.com/v1.0/me"
        self.assertTrue(any("shared Inbox" in error for error in validate(workflow)))

    def test_rejects_slower_heartbeat_schedule(self):
        workflow = self.load()
        workflow["properties"]["definition"]["triggers"]["Every_5_Minutes"]["recurrence"]["interval"] = 15
        self.assertTrue(any("every 5 minutes" in error for error in validate(workflow)))


if __name__ == "__main__":
    unittest.main()
