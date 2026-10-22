using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace PerfCompass;

public partial class MainWindow
{
    private async Task RunSmokeTest()
    {
        try
        {
            await Task.Delay(15000);
            using var process = Process.GetCurrentProcess(); process.Refresh();
            var startCpu = process.TotalProcessorTime; int startHandles = process.HandleCount;
            long startMemory = process.WorkingSet64; var watch = Stopwatch.StartNew();
            await Task.Delay(60000); process.Refresh();
            var result = new
            {
                DurationSeconds = watch.Elapsed.TotalSeconds,
                CpuMachinePercent = (process.TotalProcessorTime - startCpu).TotalSeconds / watch.Elapsed.TotalSeconds / Environment.ProcessorCount * 100,
                WorkingSetStartMb = startMemory / 1048576d, WorkingSetEndMb = process.WorkingSet64 / 1048576d,
                HandleStart = startHandles, HandleEnd = process.HandleCount,
                SavedSamples = store.Read(7, collector!.HardwareId).Count(),
                LiveCpuAvailable = live?.Sample.Cpu.HasValue, LiveMemoryAvailable = live?.Sample.MemoryTotalGb > 0,
                LiveGpuAvailable = live?.Sample.Gpu.HasValue, MainWindowResponsive = IsLoaded,
                ErrorLogExists = File.Exists(Path.Combine(store.Root, "errors.log"))
            };
            Directory.CreateDirectory("artifacts");
            File.WriteAllText("artifacts/smoke-test.json", JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
            Environment.ExitCode = result.SavedSamples >= 5 && result.LiveCpuAvailable == true && result.LiveMemoryAvailable ? 0 : 1;
        }
        catch (Exception ex) { store.Log(ex); Environment.ExitCode = 1; }
        finally { Exit(); }
    }
}
