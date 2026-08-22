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


if __name__ == "__main__":
    unittest.main()
