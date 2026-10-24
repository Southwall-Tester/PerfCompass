using System.Globalization;
using System.IO;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunSpecificationImportTests(Action<string, Action> check, Action<bool, string> assert)
    {
        const string raw = "Model: SYNTH-ONE\nProcessor: 合成处理器\nGraphics: 合成 GPU 8GB GDDR7\nMemory: 2x 16GB DDR5\nMax Memory: Up to 96GB\nStorage: 1TB SSD";
        SpecificationDraft Draft(string text = raw)
        {
            var p = SpecificationImport.Parse(text);
            string N(double? value) => value?.ToString("0.#########", CultureInfo.InvariantCulture) ?? "";
            var d = new SpecificationDraft { Source = "合成规格来源，非商品", Text = text, Name = p.Name, Cpu = p.Cpu, Gpu = p.Gpu,
                Memory = N(p.MemoryGb), Vram = N(p.VramGb), Storage = N(p.StorageGb), RecordedAt = DateTimeOffset.Now };
            d.ParsedStamp = SpecificationImport.Stamp(d.Source, d.Text); return d;
        }
        AnalysisReport Report() => new(DateTimeOffset.Now, 7, 0, 0, 0, false, [], [], [], "");
        check("Specification import supports structured bilingual rows and tabular or paired lines", () =>
        {
            foreach (string text in new[] { raw, "型号\nSYNTH-ONE\n处理器\n合成处理器\n显卡\n合成 GPU 8GB GDDR7\n内存\n2x 16GB DDR5\n硬盘\n1TB SSD",
                "Storage\t1TB SSD\nMemory\t2x 16GB DDR5\nProcessor\t合成处理器\nGraphics\t合成 GPU 8GB GDDR7\nModel\tSYNTH-ONE" })
            {
                var p = SpecificationImport.Parse(text);
                assert(p.Name == "SYNTH-ONE" && p.Cpu == "合成处理器" && p.MemoryGb == 32 && p.StorageGb == 1000 && p.VramGb == 8,
                    "structured field association, count or order lost");
            }
        });
        check("Specification import converts binary units and explicit module counts without marketing rounding", () =>
        {
            foreach (var pair in new[] { ("2 × 16GB DDR5", 32d), ("16GB x 2", 32d), ("1x 16GB SODIMM DDR5-4800", 16d), ("32GBDDR5-5600", 32d), ("8GBGDDR7", 8d), ("512GBSSD", 512d), ("32GB, bandwidth 88GB/s", 32d), ("1.5 TB SSD", 1500d), ("16 GiB", 17.179869184), ("1 TiB", 1099.511627776) })
                assert(Math.Abs(SpecificationImport.ParseCapacity(pair.Item1)!.Value - pair.Item2) < 1e-8, "quantity mismatch: " + pair.Item1);
            assert(SpecificationImport.ParseCapacity("512GB SSD M.2 2242 PCIe 4.0x4") == 512, "interface numbers multiplied storage capacity");
        });
        check("Specification import keeps maximum options multiple capacities and malformed numbers unknown", () =>
        {
            foreach (string value in new[] { "Up to 64GB", "32GB or 64GB", "1TB + 512GB", "Shared 32GB", "1,5TB", "-16GB", "1e3GB", "32G", "2x16GBx2", "0x16GB", "٣٢ GB", "16Gb", "32GB/s", "1GBps" })
                assert(SpecificationImport.ParseCapacity(value) == null, "ambiguous capacity was invented: " + value);
            var p = SpecificationImport.Parse(raw + "\nMemory: 64GB");
            assert(p.MemoryGb == null && p.Questions.Length > 0, "conflicting installed values became the largest option");
            assert(SpecificationImport.Parse(raw + "\nMemory: 2x 16GB DDR5").MemoryGb == 32, "byte-identical repeated rows became conflicting models");
        });
        check("Specification import does not confuse shared memory or optional GPU models with dedicated VRAM", () =>
        {
            var p = SpecificationImport.Parse("Model: SYNTH-TWO\nGraphics: Integrated shared 32GB memory\nMemory: 32GB\nMax Memory: 128GB");
            assert(p.MemoryGb == 32 && p.VramGb == null, "system or maximum memory became dedicated graphics memory");
            assert(SpecificationImport.Parse("GPU: 合成显卡甲 or 合成显卡乙").Gpu == "", "optional GPU list became one exact model");
        });
        check("Specification import keeps original and user-corrected fields while edits invalidate the preview", () =>
        {
            var d = Draft(); d.Memory = "64";
            var c = SpecificationImport.Candidate(d);
            assert(c.MemoryGb == 64 && c.SpecificationSource.Contains(raw) && c.SpecificationSource.Contains("内存 64 GB") && c.PriceCny == null && c.Source == "",
                "source or user correction was discarded or became a quotation");
            d.Source += " changed";
            bool rejected = false; try { SpecificationImport.Candidate(d); } catch (ArgumentException) { rejected = true; }
            assert(rejected && !SpecificationImport.Current(d), "changed source retained its preview permission");
            d = Draft(); d.Text += "\nchanged";
            assert(!SpecificationImport.Current(d), "changed raw text retained its preview permission");
        });
        check("Independent specification sources satisfy capacities without qualifying any quotation", () =>
        {
            var r = Report(); var c = SpecificationImport.Candidate(Draft()); c.PriceCny = 8000;
            var w = new PurchaseWorkspace { ContextStamp = CandidateComparison.Stamp(r), CoverageConfirmed = true, Candidates = [c],
                Criteria = [new() { Name = "内存目标", Metric = "memory", Target = 32, Unit = "GB", Reviewed = true }] };
            var result = CandidateComparison.Evaluate(w, r);
            assert(result.Candidates[0].State == "pass" && result.Candidates[0].Checks[0].Evidence.Contains("合成规格来源")
                && result.Candidates[0].Checks[0].EvidenceLabel == "独立规格资料"
                && !result.Candidates[0].Checks[0].Evidence.Contains(raw)
                && result.Shortlists[0].Options.All(o => o.CandidateId.Length == 0) && result.Summary.Contains("待补报价来源"),
                "manufacturer specifications were used as a price source");
            w.Criteria.Add(new() { Name = "预算", Metric = "price", Direction = "max", Target = 10000, Unit = "元", Reviewed = true });
            result = CandidateComparison.Evaluate(w, r);
            assert(result.Candidates[0].Checks[0].State == "pass" && result.Candidates[0].Checks[1].State == "unknown", "independent price and capacity evidence were mixed");
            c.Source = "合成店铺报价，非真实价格";
            assert(CandidateComparison.Evaluate(w, r).Candidates[0].State == "pass", "separate quote evidence failed to complete the price goal");
        });
        check("Comparison 1.4 preserves incomplete specification drafts and legacy combined source behavior", () =>
        {
            string root = Path.Combine(Path.GetTempPath(), "PerfCompass-specification-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
            string path = Path.Combine(root, "comparison.json");
            var d = Draft(); d.Memory = "not yet corrected";
            var w = new PurchaseWorkspace { SpecificationDraft = d, Candidates = [new() { Source = "旧合并资料", PriceCny = 6000, MemoryGb = 32 }] };
            CandidateComparison.SaveFile(path, w); var reopened = CandidateComparison.LoadFile(path);
            assert(reopened.Version == "comparison-1.4" && reopened.SpecificationDraft.Memory == "not yet corrected"
                && reopened.SpecificationDraft.Text == raw && SpecificationImport.Current(reopened.SpecificationDraft)
                && reopened.Candidates[0].Source == "旧合并资料", "draft input or legacy source lost on round trip");
            string legacyPath = Path.Combine(root, "legacy.json");
            File.WriteAllText(legacyPath, "{\"Version\":\"comparison-1.3\",\"Candidates\":[{\"Name\":\"旧候选\",\"Source\":\"旧合并资料\"}]}");
            assert(CandidateComparison.LoadFile(legacyPath).Candidates[0].SpecificationSource == "", "legacy combined data was fabricated as a separate source");
        });
        check("Specification import HTML escapes the complete excerpt and presents its separate provenance", () =>
        {
            var r = Report(); var c = SpecificationImport.Candidate(Draft(raw + "\n<script>example</script>"));
            var w = new PurchaseWorkspace { Candidates = [c] }; var result = CandidateComparison.Evaluate(w, r);
            string html = CandidateComparison.Html(w, result);
            assert(html.Contains("独立规格资料与摘录原文") && html.Contains("&lt;script&gt;example&lt;/script&gt;") && !html.Contains("<script>example</script>"), "excerpt was lost or executed as HTML");
        });
    }
}
