"""Verify research provenance and report structure, not semantics or rendering."""
import hashlib
from html.parser import HTMLParser
import json
from pathlib import Path
import statistics
from probe import span_audit

root = Path(__file__).resolve().parents[2]
sha = lambda p: hashlib.sha256(p.read_bytes()).hexdigest()
corpus_path = root / "tests/nlp-extended-cases.json"
corpus = {c["id"]: c["text"] for c in json.loads(corpus_path.read_text(encoding="utf-8-sig"))}
summary = json.loads((root / "docs/experiments/gliner-scope-research.json").read_text(encoding="utf-8"))
assert summary["corpus_sha256"] == sha(corpus_path)
assert len(summary["variants"]) == 8
expected = set()
for variant in summary["variants"]:
    flavor, method = variant["key"].split("-", 1)
    source_dir = root / "tests/semantic-results/gliner2.5-research" / flavor / method
    manifest = json.loads((source_dir / "manifest.json").read_text(encoding="utf-8"))
    assert manifest["completed"] and manifest["actual_process_exit_code"] == 0
    assert manifest["probe_sha256"] == sha(Path(__file__).with_name("probe.py")) == variant["probe_sha256"]
    if flavor == "wide":
        spec_path = Path(__file__).with_name("schema-wide-" + manifest["strategy"] + "-v1.json")
    else:
        spec_path = Path(__file__).with_name("schema-v1.json" if manifest["strategy"] == "records" else "schema-attributes-v1.json")
    assert sha(spec_path) == manifest["schema_sha256"] == variant["schema_sha256"]
    spec = json.loads(spec_path.read_text(encoding="utf-8"))
    categorical = {f["name"] for f in spec.get("fields", []) if f.get("choices")} | set(spec.get("attributes", {}))
    assert set(manifest["ids"]) == set(corpus) == {c["id"] for c in variant["case_files"]}
    times, entities, records = [], 0, 0
    for case in variant["case_files"]:
        path = source_dir / ("case-" + case["id"] + ".json")
        assert sha(path) == case["sha256"]
        data = json.loads(path.read_text(encoding="utf-8"))
        assert data["source"] == corpus[case["id"]]
        assert data["span_audit"] == span_audit(data["raw"], data["source"], categorical_fields=categorical)
        entities += sum(map(len, data["raw"].get("entities", {}).values()))
        records += len(data["raw"].get("computer_requirement", []))
        times.append(data["seconds"])
        expected.add((variant["key"], case["id"]))
    assert variant["entities"] == entities and variant["records"] == records
    assert variant["median_case_seconds"] == statistics.median(times)
notes = json.loads((root / "tests/semantic-reviews/gliner-scope-findings.json").read_text(encoding="utf-8"))
assert notes["corpus_sha256"] == sha(corpus_path)
for finding in notes["findings"]:
    assert finding["source_sha256"] == hashlib.sha256(corpus[finding["id"]].encode()).hexdigest()
    for output in finding["outputs"]:
        path = (root / output["file"]).resolve()
        assert path.is_relative_to(root / "tests/semantic-results") and sha(path) == output["sha256"]

class Structure(HTMLParser):
    def __init__(self):
        super().__init__()
        self.cards, self.scripts, self.selection = [], 0, None
        self.options = {"variant": [], "case": []}
    def handle_starttag(self, tag, attrs):
        attrs = dict(attrs)
        if tag == "article":
            self.cards.append((attrs["data-variant"], attrs["data-case"]))
        if tag == "script":
            self.scripts += 1
        if tag == "select":
            self.selection = attrs["id"]
        if tag == "option":
            self.options[self.selection].append(attrs["value"])
    def handle_endtag(self, tag):
        if tag == "select":
            self.selection = None

structure = Structure()
structure.feed((root / "docs/gliner-scope-review.html").read_text(encoding="utf-8"))
assert len(structure.cards) == len(expected) == 192 and set(structure.cards) == expected
assert set(structure.options["case"]) == set(corpus)
assert set(structure.options["variant"]) == {v["key"] for v in summary["variants"]}
assert structure.scripts == 1
print("Verified 192 raw files, 8 manifests, 6 selected-case reviews bound to 24 outputs and report structure. Rendering/interaction and semantic accuracy remain unverified.")
