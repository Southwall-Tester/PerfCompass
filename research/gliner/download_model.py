"""Download a pinned public GLiNER research snapshot, with no auth or user texts."""
import argparse
import hashlib
import json
from pathlib import Path
import re
import urllib.request

parser = argparse.ArgumentParser()
parser.add_argument("metadata", type=Path)
parser.add_argument("output", type=Path)
args = parser.parse_args()
meta = json.loads(args.metadata.read_text(encoding="utf-8-sig"))
if meta["id"] != "fastino/gliner2.5-multi-v1" or not re.fullmatch(r"[a-f0-9]{40}", meta["sha"]):
    raise ValueError("Expected pinned public model metadata")
args.output.mkdir(parents=True, exist_ok=False)
selected = {"README.md", "config.json", "encoder_config/config.json", "model.safetensors", "tokenizer.json", "tokenizer_config.json"}
entries = {item["rfilename"]: item for item in meta["siblings"] if item["rfilename"] in selected}
if set(entries) != selected:
    raise ValueError("Incomplete snapshot metadata")
manifest = {"format": "nlp-model-snapshot-1.0", "repository": meta["id"], "revision": meta["sha"], "files": [], "complete": False}
target_manifest = args.output / "snapshot.json"
target_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
# Small metadata first, then weights. Never treat an interrupted file as complete.
for name in sorted(selected, key=lambda n: (n == "model.safetensors", n)):
    item = entries[name]
    target = args.output / name
    target.parent.mkdir(parents=True, exist_ok=True)
    partial = target.with_name(target.name + ".partial")
    url = f"https://huggingface.co/{meta['id']}/resolve/{meta['sha']}/{name}"
    sha256 = hashlib.sha256()
    blob = hashlib.sha1(f"blob {item['size']}\0".encode())
    size = 0
    print("Downloading", name, item["size"], flush=True)
    request = urllib.request.Request(url, headers={"User-Agent": "PerfCompass-isolated-research"})
    with urllib.request.urlopen(request, timeout=90) as incoming, partial.open("xb") as outgoing:
        while chunk := incoming.read(1024 * 1024):
            outgoing.write(chunk)
            sha256.update(chunk)
            blob.update(chunk)
            size += len(chunk)
    if size != item["size"]:
        raise ValueError("Downloaded byte length differs: " + name)
    expected = item.get("lfs", {}).get("sha256")
    if (expected and sha256.hexdigest() != expected) or (not expected and blob.hexdigest() != item["blobId"]):
        raise ValueError("Source checksum differs: " + name)
    partial.rename(target)
    manifest["files"].append({"path": name, "bytes": size, "sha256": sha256.hexdigest(), "source_verified": True})
    target_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    print("Verified", name, size, flush=True)
manifest["complete"] = True
target_manifest.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
print("Snapshot complete", meta["sha"], flush=True)
