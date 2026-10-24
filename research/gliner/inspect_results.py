"""Readable developer inspection of raw research outputs; no automatic grading."""
import argparse
import json
from pathlib import Path

p = argparse.ArgumentParser(description=__doc__)
p.add_argument("matrix", type=Path)
p.add_argument("--id", required=True)
a = p.parse_args()
for folder in sorted(a.matrix.iterdir()):
    case_path = folder / ("case-" + a.id + ".json")
    if not case_path.is_file():
        continue
    data = json.loads(case_path.read_text(encoding="utf-8"))
    print("\n##", folder.name, a.id)
    print(data["source"])
    def short(value):
        if value is None:
            return "-"
        if isinstance(value, dict):
            label = value.get("label", value.get("text", "?"))
            return str(label) + (" @" + str(round(value["confidence"], 3)) if "confidence" in value else "")
        return str(value)
    for kind, entities in data["raw"].get("entities", {}).items():
        for item in entities:
            print(kind, short(item), "|", " | ".join(n + "=" + short(item.get(n)) for n in ("owner_scope", "source_relation", "timeframe", "execution_environment")))
    for record in data["raw"].get("computer_requirement", []):
        print("record:", " | ".join(k + "=" + short(v) for k, v in record.items()))
