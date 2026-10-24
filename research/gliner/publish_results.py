"""Publish synthetic model outputs and an inspectable report, with process IDs removed."""
import argparse
import hashlib
import html
import json
from pathlib import Path
import shutil
import statistics


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def esc(value):
    return html.escape(str(value), quote=True)


def label(value):
    if value is None:
        return "—"
    if isinstance(value, dict):
        text = value.get("label", value.get("text", "—"))
        return str(text) + (f" · {value['confidence']:.3f}" if "confidence" in value else "")
    return str(value)


p = argparse.ArgumentParser(description=__doc__)
p.add_argument("--matrix", type=Path, action="append", required=True)
p.add_argument("--output", type=Path, required=True)
p.add_argument("--summary", type=Path, required=True)
p.add_argument("--report", type=Path, required=True)
a = p.parse_args()
root = Path(__file__).resolve().parents[2]
for target in (a.output, a.summary, a.report):
    if target.exists() or not target.resolve().is_relative_to(root):
        raise ValueError("Choose new workspace output paths")
a.output.mkdir(parents=True)
corpus_path = root / "tests/nlp-extended-cases.json"
corpus = json.loads(corpus_path.read_text(encoding="utf-8-sig"))
ids = [c["id"] for c in corpus]
sources = {c["id"]: c["text"] for c in corpus}
checks = {c["id"]: c["checks"] for c in corpus}
summary = {"format": "gliner-public-research-1.0", "notice": "Synthetic development diagnostics; no independent annotations or semantic accuracy score. Original Qwen results and scores unchanged. No product settings, model default or user history modified.", "corpus_sha256": sha(corpus_path), "variants": []}
cards, options, rows = [], [], []
for matrix_path in a.matrix:
    matrix = json.loads((matrix_path / "matrix.json").read_text(encoding="utf-8-sig"))
    if not matrix["Completed"] or len(matrix["Runs"]) != 4:
        raise ValueError("Incomplete process matrix")
    flavor = matrix.get("SchemaFlavor", "compact")
    for run in matrix["Runs"]:
        if not run["Completed"] or run["ExitCode"] != 0:
            raise ValueError("Process exit not verified")
        source_dir = matrix_path / run["Variant"]
        manifest = json.loads((source_dir / "manifest.json").read_text(encoding="utf-8"))
        if not manifest["completed"] or manifest["ids"] != ids or manifest["corpus_sha256"].upper() != summary["corpus_sha256"].upper():
            raise ValueError("Corpus or result coverage mismatch")
        key = flavor + "-" + run["Variant"]
        destination = a.output / flavor / run["Variant"]
        destination.mkdir(parents=True)
        public_manifest = {k: v for k, v in manifest.items() if k != "pid"}
        public_manifest.update(actual_process_exit_code=run["ExitCode"], original_manifest_sha256=sha(source_dir / "manifest.json"))
        (destination / "manifest.json").write_text(json.dumps(public_manifest, indent=2), encoding="utf-8")
        shutil.copyfile(source_dir / "effective-schema.json", destination / "effective-schema.json")
        times, peaks, entity_count, record_count, located, invalid = [], [], 0, 0, 0, 0
        files = []
        for case_id in ids:
            case_path = source_dir / ("case-" + case_id + ".json")
            data = json.loads(case_path.read_text(encoding="utf-8"))
            if data["source"] != sources[case_id]:
                raise ValueError("Source mismatch")
            shutil.copyfile(case_path, destination / case_path.name)
            files.append({"id": case_id, "sha256": sha(case_path)})
            times.append(data["seconds"])
            peaks.append((data.get("memory") or {}).get("peak_working_set_bytes", 0))
            entities = data["raw"].get("entities", {})
            records = data["raw"].get("computer_requirement", [])
            entity_count += sum(map(len, entities.values()))
            record_count += len(records)
            located += sum(v["valid"] for v in data["span_audit"])
            invalid += sum(not v["valid"] for v in data["span_audit"])
            table = []
            for kind, mentions in entities.items():
                for mention in mentions:
                    values = [kind, label(mention)] + [label(mention.get(n)) for n in ("owner_scope", "source_relation", "timeframe", "execution_environment")]
                    table.append("<tr>" + "".join("<td>" + esc(v) + "</td>" for v in values) + "</tr>")
            table_html = "<table><thead><tr>" + "".join("<th>" + x + "</th>" for x in ("类型", "原文片段 · 得分", "归属 · 得分", "关系 · 得分", "时间 · 得分", "执行位置 · 得分")) + "</tr></thead><tbody>" + "".join(table) + "</tbody></table>" if table else "<p>未输出实体。</p>"
            record_html = "".join("<details><summary>结构化记录 " + str(i + 1) + "</summary><table>" + "".join("<tr><th>" + esc(k) + "</th><td>" + esc(label(v)) + "</td></tr>" for k, v in record.items()) + "</table></details>" for i, record in enumerate(records))
            cards.append(f'<article data-variant="{esc(key)}" data-case="{esc(case_id)}"><h2>{esc(case_id)} · {esc(key)}</h2><blockquote>{esc(data["source"])}</blockquote><p>原诊断检查点：{esc("；".join(checks[case_id]))}</p>{table_html}{record_html}<details><summary>原始输出与文件摘要</summary><p class="hash">SHA256 {sha(case_path)}</p><pre>{esc(json.dumps(data, ensure_ascii=False, indent=2))}</pre></details></article>')
        item = {"key": key, "model_repository": manifest["model_repository"], "model_revision": manifest["revision"], "model_weights_sha256": manifest["model_weights_sha256"], "probe_sha256": manifest["probe_sha256"], "schema_sha256": manifest["schema_sha256"], "packages": manifest["packages"], "splitter": manifest["splitter"], "strategy": manifest["strategy"], "actual_exit_code": 0, "cases": len(ids), "entities": entity_count, "records": record_count, "source_spans_located": located, "source_spans_invalid": invalid, "median_case_seconds": statistics.median(times), "min_case_seconds": min(times), "max_case_seconds": max(times), "weights_load_seconds": manifest["load_seconds"], "peak_process_working_set_bytes": max(peaks) or None, "case_files": files}
        summary["variants"].append(item)
        options.append('<option value="' + esc(key) + '">' + esc(key) + '</option>')
        rows.append(f'<tr><td>{esc(key)}</td><td>{len(ids)}</td><td>{entity_count}</td><td>{record_count}</td><td>{located}/{located+invalid}</td><td>{statistics.median(times):.3f}</td><td>{max(peaks)/2**30:.2f}</td></tr>')
a.summary.parent.mkdir(parents=True, exist_ok=True)
a.summary.write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding="utf-8")
document = '''<!doctype html><html lang="zh-CN"><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1"><title>GLiNER 购机语义研究对照</title><style>
body{font:15px/1.65 system-ui,"Microsoft YaHei",sans-serif;color:#192c32;background:#f2f5f5;margin:0}main{max-width:1320px;margin:auto;padding:28px}h1{font-size:28px;margin:0 0 12px}h2{font-size:20px}header,article,.overview{background:white;padding:24px;margin:0 0 20px;border-radius:12px}p{margin:8px 0}blockquote{margin:16px 0;padding:14px 18px;background:#edf6f4;border-left:4px solid #007e71}table{border-collapse:collapse;width:100%;margin:12px 0;table-layout:fixed}th,td{border:1px solid #d9e1e2;padding:9px;text-align:left;overflow-wrap:anywhere;vertical-align:top}th{background:#edf2f3;font-weight:600}select{padding:10px;max-width:100%;margin:6px 12px 6px 0;font:inherit}label{display:inline-block}pre{white-space:pre-wrap;overflow-wrap:anywhere;background:#f5f6f7;padding:14px;font:13px/1.5 monospace}summary{cursor:pointer;padding:10px 0}article[hidden]{display:none}.hash{font:12px monospace;overflow-wrap:anywhere}.muted{color:#52666d}@media(max-width:700px){main{padding:12px}header,article,.overview{padding:16px}table{font-size:12px}th,td{padding:5px}}
</style><main><header><h1>GLiNER 购机语义研究对照</h1><p>24 条合成开发输入 × 8 个组合。所有组合进程正常退出，语义质量未获独立人工验收。现有产品默认模型与 Qwen 原始成绩不变。</p><p class="muted">得分是模型原始输出，未校准为正确概率。位置匹配只说明片段来自原文；分类标签不是原文引用。结构化记录与实体属性是不同输出方式，不能按事实数量直接比较准确率。</p><label>组合 <select id="variant">VARIANTS</select></label><label>输入 <select id="case">CASES</select></label></header><details class="overview"><summary>运行统计与口径</summary><p>单句耗时排除加载；首次调用及缓存条件保留，工作集为进程 OS 峰值，不是整机独占内存。模型加载另记于 JSON，未宣称冷启动或相对 8B 的性能收益。</p><table><tr><th>组合</th><th>输入数</th><th>实体数</th><th>记录数</th><th>片段位置匹配</th><th>单句中位秒</th><th>峰值工作集 GiB</th></tr>STATS</table></details>CARDS</main><script>
const variant=document.getElementById('variant'),item=document.getElementById('case');function show(){for(const card of document.querySelectorAll('article'))card.hidden=card.dataset.variant!==variant.value||card.dataset.case!==item.value;}variant.addEventListener('change',show);item.addEventListener('change',show);show();
</script></html>'''
document = document.replace("VARIANTS", "".join(options)).replace("CASES", "".join('<option value="'+esc(i)+'">'+esc(i)+'</option>' for i in ids)).replace("STATS", "".join(rows)).replace("CARDS", "".join(cards))
a.report.parent.mkdir(parents=True, exist_ok=True)
a.report.write_text(document, encoding="utf-8")
print("Published", len(summary["variants"]), "variants,", len(cards), "case outputs; no semantic score assigned.")
