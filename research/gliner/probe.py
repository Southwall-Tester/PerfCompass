"""Isolated CPU research. Never emits confirmed product facts or purchase advice."""
import argparse
from datetime import datetime, timezone
import hashlib
import importlib.metadata
import json
import os
from pathlib import Path
import re
import sys
import time


def process_memory():
    """Windows process working set, not unique physical machine memory."""
    if sys.platform != "win32":
        return None
    import ctypes
    from ctypes import wintypes
    class Counters(ctypes.Structure):
        _fields_ = [("cb", wintypes.DWORD), ("faults", wintypes.DWORD)] + [
            (name, ctypes.c_size_t) for name in ("peak_ws", "ws", "peak_paged", "paged", "peak_nonpaged", "nonpaged", "pagefile", "peak_pagefile")]
    current = ctypes.windll.kernel32.GetCurrentProcess
    current.restype = ctypes.c_void_p
    read = ctypes.windll.psapi.GetProcessMemoryInfo
    read.argtypes = [ctypes.c_void_p, ctypes.POINTER(Counters), wintypes.DWORD]
    counters = Counters()
    counters.cb = ctypes.sizeof(counters)
    if not read(current(), ctypes.byref(counters), counters.cb):
        return None
    return {"working_set_bytes": counters.ws, "peak_working_set_bytes": counters.peak_ws}


def digest(path):
    h = hashlib.sha256()
    with path.open("rb") as f:
        while block := f.read(1024 * 1024):
            h.update(block)
    return h.hexdigest()


def span_audit(value, source, location="root", categorical_fields=frozenset()):
    results = []
    if isinstance(value, dict):
        if {"text", "start", "end"} <= value.keys():
            start, end = value["start"], value["end"]
            valid = (type(start) is int and type(end) is int and 0 <= start < end <= len(source)
                     and source[start:end] == value["text"])
            results.append({"location": location, "valid": valid})
        for key, item in value.items():
            # Enum labels can carry their anchor's offsets; they are not quotations.
            if key not in categorical_fields:
                results.extend(span_audit(item, source, location + "." + key, categorical_fields))
    elif isinstance(value, list):
        for index, item in enumerate(value):
            results.extend(span_audit(item, source, location + f"[{index}]", categorical_fields))
    return results


def build_schema(model, spec, strategy):
    schema = model.create_schema().entities(spec["entities"])
    if strategy == "records":
        builder = schema.structure(spec["record"], mode="natural", anchor=spec["anchor"])
        for field in spec["fields"]:
            builder.field(field["name"], dtype="str", description=field["description"], choices=field.get("choices"), cardinality=field.get("cardinality", "optional_one"))
        schema.build()  # Finalize records without dropping object metadata.
    elif strategy == "attributes":
        from gliner2 import AttributeGroup
        schema.entity_attributes({name: AttributeGroup(labels, applies_to=list(spec["entities"]), qualify_labels=True) for name, labels in spec["attributes"].items()})
    else:
        raise ValueError("Unknown strategy")
    return schema


def main():
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--model", type=Path, required=True)
    p.add_argument("--corpus", type=Path, required=True)
    p.add_argument("--schema", type=Path, default=Path(__file__).with_name("schema-v1.json"))
    p.add_argument("--output", type=Path, required=True)
    p.add_argument("--splitter", choices=["char", "whitespace"], default="char")
    p.add_argument("--strategy", choices=["records", "attributes"], default="records")
    p.add_argument("--ids", default="all")
    p.add_argument("--threads", type=int, default=4, choices=range(1, 9))
    a = p.parse_args()
    root = Path(__file__).resolve().parents[2]
    output = a.output.resolve()
    if not output.is_relative_to(root / "artifacts") or output.exists():
        raise ValueError("Use a new output directory under this workspace's artifacts")
    corpus = json.loads(a.corpus.read_text(encoding="utf-8-sig"))
    ids = a.ids.split(",")
    cases = [c for c in corpus if "all" in ids or c["id"] in ids]
    if not cases or len({c["id"] for c in cases}) != len(cases):
        raise ValueError("Selected cases are empty or duplicated")
    for case in cases:
        if not re.fullmatch(r"[A-Za-z0-9_-]{1,50}", case["id"]) or not 1 <= len(case["text"]) <= 2000:
            raise ValueError("Invalid case identifier or source length")
    model_root = a.model.resolve()
    snapshot = json.loads((model_root / "snapshot.json").read_text(encoding="utf-8"))
    if not snapshot["complete"] or snapshot["repository"] != "fastino/gliner2.5-multi-v1":
        raise ValueError("Expected a complete verified research snapshot")
    for item in snapshot["files"]:
        target = (model_root / item["path"]).resolve()
        if not target.is_relative_to(model_root) or digest(target) != item["sha256"]:
            raise ValueError("Model snapshot checksum changed")
    spec = json.loads(a.schema.read_text(encoding="utf-8"))
    output.mkdir(parents=True)
    manifest = {
        "format": "gliner-scope-experiment-1.0", "started": datetime.now(timezone.utc).isoformat(),
        "model_repository": snapshot["repository"], "revision": snapshot["revision"],
        "model_weights_sha256": next(f["sha256"] for f in snapshot["files"] if f["path"] == "model.safetensors"),
        "corpus_sha256": digest(a.corpus), "schema_sha256": digest(a.schema), "probe_sha256": digest(Path(__file__)),
        "ids": [c["id"] for c in cases], "splitter": a.splitter, "threshold": 0.5, "max_len": 4096,
        "threads": a.threads, "seed": 42, "device": "cpu", "python": sys.version, "pid": os.getpid(), "strategy": a.strategy,
        "packages": {n: importlib.metadata.version(n) for n in ("gliner2", "torch", "transformers", "tokenizers", "safetensors")},
        "completed": False, "notice": "Synthetic development diagnostics, not independently annotated. Raw spans/confidence do not establish semantic correctness. No product facts, hardware advice or user history written."
    }
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    os.environ.update({"HF_HUB_OFFLINE": "1", "TRANSFORMERS_OFFLINE": "1", "HF_HUB_DISABLE_TELEMETRY": "1", "DO_NOT_TRACK": "1", "TOKENIZERS_PARALLELISM": "false"})
    import torch
    from gliner2 import AutoExtractor
    torch.set_num_threads(a.threads)
    torch.set_num_interop_threads(1)
    torch.manual_seed(42)
    load_started = time.perf_counter()
    model = AutoExtractor.from_pretrained(str(model_root), map_location="cpu", word_splitter=a.splitter, local_files_only=True)
    model.eval()
    manifest["load_seconds"] = time.perf_counter() - load_started
    manifest["memory_after_load"] = process_memory()
    schema = build_schema(model, spec, a.strategy)
    # A bare build() dict drops field/attribute metadata in the upstream runtime.
    # The object is used for inference; to_dict() is a readable audit snapshot.
    (output / "effective-schema.json").write_text(json.dumps(schema.to_dict(), ensure_ascii=False, indent=2), encoding="utf-8")
    records = []
    categorical_fields = {f["name"] for f in spec.get("fields", []) if f.get("choices")} | set(spec.get("attributes", {}))
    for case in cases:
        started, cpu_started = time.perf_counter(), time.process_time()
        with torch.inference_mode():
            raw = model.extract(case["text"], schema, threshold=0.5, include_spans=True, include_confidence=True, max_len=4096)
        record = {"id": case["id"], "source": case["text"], "seconds": time.perf_counter() - started,
                  "cpu_seconds": time.process_time() - cpu_started, "memory": process_memory(), "raw": raw, "span_audit": span_audit(raw, case["text"], categorical_fields=categorical_fields)}
        (output / ("case-" + case["id"] + ".json")).write_text(json.dumps(record, ensure_ascii=False, indent=2), encoding="utf-8")
        records.append(record)
        print("Finished", case["id"], f"{record['seconds']:.3f}s", flush=True)
    (output / "results.json").write_text(json.dumps(records, ensure_ascii=False, indent=2), encoding="utf-8")
    manifest.update(completed=True, finished=datetime.now(timezone.utc).isoformat(), cases=len(records), memory_final=process_memory())
    (output / "manifest.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
