import json
from pathlib import Path
import unittest
from gliner2.inference.schema import Schema
from probe import build_schema, span_audit


class EmptyModel:
    @staticmethod
    def create_schema():
        return Schema()


class ProbeRegression(unittest.TestCase):
    def test_record_choices_are_retained_in_inference_object(self):
        spec = json.loads(Path(__file__).with_name("schema-v1.json").read_text(encoding="utf-8"))
        schema = build_schema(EmptyModel(), spec, "records")
        self.assertIsInstance(schema, Schema)
        self.assertIn("computer_requirement.owner_scope", schema._field_metadata)
        self.assertEqual(len(schema._field_metadata["computer_requirement.owner_scope"]["choices"]), 4)
        restored = Schema.from_dict(schema.to_dict())
        self.assertEqual(restored._field_metadata, schema._field_metadata)

    def test_attribute_groups_are_not_returned_as_ordinary_entities(self):
        spec = json.loads(Path(__file__).with_name("schema-attributes-v1.json").read_text(encoding="utf-8"))
        schema = build_schema(EmptyModel(), spec, "attributes")
        self.assertIsInstance(schema, Schema)
        self.assertEqual(set(schema._entity_attribute_groups), set(spec["attributes"]))
        restored = Schema.from_dict(schema.to_dict())
        self.assertEqual(set(restored._entity_attribute_groups), set(schema._entity_attribute_groups))

    def test_unicode_position_match_is_distinct_from_semantic_correctness(self):
        text = "🐱内存32GB"
        value = {"text": "32GB", "start": 3, "end": 7, "owner_scope": {"label": "wrong-person"}}
        self.assertEqual(span_audit(value, text), [{"location": "root", "valid": True}])
        self.assertFalse(span_audit(dict(value, start=4), text)[0]["valid"])
        classified = {"subject": value, "owner_scope": {"text": "another person", "start": 3, "end": 7}}
        self.assertEqual(span_audit(classified, text, categorical_fields={"owner_scope"}), [{"location": "root.subject", "valid": True}])

    def test_invalid_offsets_cannot_be_counted_as_located_spans(self):
        for start, end in [(True, 1), (-1, 1), (0, 9), (1, 0)]:
            self.assertFalse(span_audit({"text": "a", "start": start, "end": end}, "a")[0]["valid"])


if __name__ == "__main__":
    unittest.main()
