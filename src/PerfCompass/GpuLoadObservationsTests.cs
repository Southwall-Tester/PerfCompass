namespace PerfCompass;

public static partial class SelfTests
{
    private static void RunGpuLoadObservationsTests(Action<string, Action> check, Action<bool, string> assert)
    {
        check("Idle GPU core does not hide busy compute, video, copy or 3D engines", () =>
        {
            var observations = GpuLoadObservations.Normalize([
                ("GPU Core", 0d), ("D3D Compute_0", 96d), ("GPU Video Engine", 84d),
                ("D3D Video Encode", 91d), ("D3D Video Decode", 83d),
                ("D3D Video Processing", 76d), ("D3D Copy", 85d), ("GPU D3D 3D", 72d)]);
            assert(observations.Count == 8 && observations.Single(o => o.Kind == "core").Load == 0,
                "independent engine categories collapsed or zero core was lost");
            assert(observations.Single(o => o.Kind == "compute").Load == 96
                && observations.Single(o => o.Kind == "encode").Load == 91
                && observations.Single(o => o.Kind == "video").Load == 84
                && observations.Single(o => o.Kind == "3d").Load == 72
                && observations.All(o => o.Complete), "a valid zero masked another engine");
        });
        check("Parallel nodes use their maximum while retaining each raw source name", () =>
        {
            var observations = GpuLoadObservations.Normalize([
                ("D3D Compute_0", 65d), ("D3D Compute_1", 55d), ("D3D Compute_1", 75d),
                ("D3D Video Decode", 20d), ("D3D JPEG Decode 0", 80d)]);
            var compute = observations.Single(o => o.Kind == "compute");
            assert(compute.Load == 75 && compute.Sources.Length == 3
                && compute.Sources.Count(s => s == "D3D Compute_1") == 2,
                "nodes were added/averaged or duplicate source labels were discarded");
            assert(observations.Single(o => o.Kind == "decode").Load == 80,
                "known JPEG decode node was lost");
        });
        check("Memory, controller, bus, power and ambiguous names are excluded from GPU engine load", () =>
        {
            var observations = GpuLoadObservations.Normalize([
                ("GPU Memory", 100d), ("GPU Memory Controller", 100d), ("GPU Bus", 100d),
                ("GPU Power", 100d), ("GPU Core Power", 100d), ("D3D Dedicated Memory Used", 100d),
                ("D3D Compute Memory", 100d), ("D3D Copy Bandwidth", 100d), ("D3D Unknown", 100d),
                ("D3D Optical Flow Accelerator 0", 100d), ("D3D Compute_", 100d), ("D3D Compute0", 100d),
                ("", 100d), (null!, 100d), ("GPU Core", 0d)]);
            assert(observations.Count == 1 && observations[0].Kind == "core" && observations[0].Load == 0,
                "unrelated Load sensor manufactured GPU activity");
        });
        check("Missing and invalid node values keep category incomplete without erasing known busy work", () =>
        {
            foreach (double? invalid in new double?[] { null, -1, 101, double.NaN, double.PositiveInfinity, double.NegativeInfinity })
            {
                var observation = GpuLoadObservations.Normalize([
                    ("D3D Compute_0", 92d), ("D3D Compute_1", invalid)]).Single();
                assert(observation.Load == 92 && !observation.Complete && observation.Sources.Length == 2,
                    "missing node became zero or known positive work disappeared");
                var missing = GpuLoadObservations.Normalize([("D3D Video Encode", invalid)]).Single();
                assert(missing.Load == null && !missing.Complete, "invalid engine value was clamped to valid percent");
            }
        });
        check("No exposed engine is unknown and endpoint percentages remain valid", () =>
        {
            assert(GpuLoadObservations.Normalize([]).Count == 0
                && GpuLoadObservations.Normalize([("GPU Memory Controller", 0d)]).Count == 0,
                "absence manufactured supported engine coverage");
            var result = GpuLoadObservations.Normalize([("  gpu core  ", 0d), ("d3d compute 12", 100d)]);
            assert(result.Count == 2 && result.All(r => r.Complete)
                && result.Single(r => r.Kind == "core").Sources.Single() == "  gpu core  "
                && result.Single(r => r.Kind == "compute").Load == 100,
                "valid boundary, case alias or original source label was lost");
        });
        check("A second snapshot cannot mutate the detached first observation", () =>
        {
            var input = new List<(string Name, double? Value)> { ("D3D Video Decode", 90d) };
            var first = GpuLoadObservations.Normalize(input); input[0] = ("D3D Video Decode", null);
            var second = GpuLoadObservations.Normalize(input);
            assert(first.Single().Load == 90 && first.Single().Complete
                && second.Single().Load == null && !second.Single().Complete,
                "normalization retained mutable input or stale category state");
        });
    }
}
