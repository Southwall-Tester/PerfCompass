using System.IO;
using System.Text.Json;

namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunStorageCapacityTests(Action<string,Action> check,Action<bool,string> assert)
    {
        var now=DateTimeOffset.Now;const string hardware="synthetic-storage-capacity";
        Sample Row(int age=10)=>new(){Time=now.AddMinutes(-age),HardwareId=hardware,Seconds=10,Active=true,Cpu=5,CoreMax=10,
            MemoryTotalGb=24,MemoryUsedGb=8,CommitGb=10,DiskUsedGb=200,DiskTotalGb=500,DiskCapacityFieldsPresent=true};
        AnalysisReport Analyze(IEnumerable<Sample> rows,string? id=hardware)=>AnalysisEngine.Analyze(rows,new(),7,null,now,id);
        check("Storage shopping converts decimal bytes to GiB before comparing occupied space",()=>
        {
            var small=StorageCapacityPreview.Compare("128 GB",128_000_000_000,200);var larger=StorageCapacityPreview.Compare("256 GB",256_000_000_000,200);
            assert(small.DifferenceGiB<0 && larger.DifferenceGiB>0 && Math.Abs(larger.NominalGiB-238.4185791015625)<1e-9,"Decimal and binary capacity confused");
            assert(StorageCapacityPreview.Compare("1 TiB",1L<<40,200).NominalGiB==1024
                && StorageCapacityPreview.Compare("1 TB",1_000_000_000_000,200).NominalGiB<932,"TB silently treated as TiB");
        });
        check("Storage comparison uses the latest recorded occupancy rather than an older deleted-data peak",()=>
        {
            var old=Row(60) with {DiskUsedGb=450};var current=Row(5) with {Active=false};var report=Analyze([current,old]);
            var preview=StorageCapacityPreview.From(report)!;
            assert(preview.Evidence.OccupiedGiB==200 && preview.Evidence.RecordedAt==current.Time
                && preview.Period.Contains(current.Time.LocalDateTime.ToString("yyyy-MM-dd")),"Old or active-only occupancy replaced latest snapshot");
            assert(StorageCapacityPreview.From(Analyze([old,current with {DiskCapacityFieldsPresent=false}]))==null,"Missing latest data silently carried an earlier reading");
        });
        check("Storage comparison preserves zero known occupancy while rejecting absent and inconsistent fields",()=>
        {
            assert(StorageCapacityPreview.From(Analyze([Row() with {DiskUsedGb=0}]))!=null,"Known empty space became unknown");
            assert(StorageCapacityPreview.From(Analyze([Row() with {DiskCapacityFieldsPresent=null}]))!=null,"Explicit nonzero legacy API values lost compatibility");
            foreach(var row in new[]{Row() with {DiskCapacityFieldsPresent=false},Row() with {DiskCapacityFieldsPresent=null,DiskUsedGb=0},Row() with {DiskTotalGb=0},Row() with {DiskUsedGb=600},Row() with {DiskUsedGb=double.NaN}})
                assert(StorageCapacityPreview.From(Analyze([row]))==null,"Absent or invalid capacity became a fit");
            foreach(double occupied in new[]{-1d,double.NaN,double.PositiveInfinity})
            {bool rejected=false;try{StorageCapacityPreview.Compare("custom",123456,occupied);}catch(ArgumentException){rejected=true;}assert(rejected,"Invalid occupancy accepted");}
        });
        check("Legacy missing disk occupancy is unknown without disabling unrelated CPU and RAM observations",()=>
        {
            string root=Path.Combine(Path.GetTempPath(),"PerfCompass-storage-fields-"+Guid.NewGuid().ToString("N"));var store=new LocalStore(root);
            var input=Enumerable.Range(0,30).Select(i=>Row() with {Time=now.AddSeconds(-600+i*10)}).ToArray();
            string file=Path.Combine(root,$"samples-{input[0].Time.LocalDateTime:yyyy-MM-dd}.jsonl");
            File.WriteAllLines(file,input.Select(s=>{var node=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(s))!.AsObject();node.Remove("DiskUsedGb");return node.ToJsonString();}));
            var report=HistoryAnalysis.Analyze(store,new(),7,asOf:now,hardwareId:hardware);
            assert(StorageCapacityPreview.From(report)==null && report.Cards.Single(c=>c.Key=="memory").Observation!=null && !report.HistoryQuality.HasIssues,"Missing optional disk field became zero, corrupted history, or lost independent observations");
        });
        check("Storage table keeps hardware and history integrity boundaries",()=>
        {
            assert(StorageCapacityPreview.From(Analyze([Row(),Row() with {HardwareId="other"}],null))==null,"Mixed hardware created one storage budget");
            var report=Analyze([Row()]);using var d=new HistoryDiagnostics(7);d.Add("synthetic.jsonl",1,"invalid");
            assert(StorageCapacityPreview.From(report with {HistoryQuality=d.Build()})==null,"Damaged history created an occupancy budget");
        });
        check("Storage report remains a conditional occupancy comparison, not a task or SSD performance verdict",()=>
        {
            var report=Analyze([Row()]);var preview=StorageCapacityPreview.From(report)!;string html=ReportExporter.Html(report,null);
            assert(html.Contains("标称容量与已测卷占用") && html.Contains("512 GB") && html.Contains("不是增长量预测") && StorageCapacityPreview.Notice.Contains("重复挂载"),"Scope, units or assumption lost");
            var changed=report with {LatestStorageCapacity=report.LatestStorageCapacity! with {OccupiedGiB=201}};
            assert(CandidateComparison.Stamp(report)==CandidateComparison.Stamp(changed) && preview.Rows.All(r=>!r.Meaning.Contains("达标")),"Display arithmetic changed review scope or certified a candidate");
        });
    }
}
