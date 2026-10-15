using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

internal static class CompatibilityRegression
{
    /// <summary>
    /// Phase 5: each backend reports a CompatibilityReport with an optional resource estimate,
    /// and the PolyStrata matrix flags unsupported expert formats without crashing.
    /// </summary>
    public static async Task RunAsync(string root)
    {
        var model = Path.Combine(root, "apex compat.gguf");
        File.WriteAllBytes(model, Fixtures.StrataGguf());

        // llama.cpp: always runnable, with an estimate.
        var llamacpp = BackendRegistry.ServerById("llamacpp")!;
        var lc = await llamacpp.InspectAsync(model, CancellationToken.None);
        Check(lc.Runnable, "llamacpp rejected the fixture");
        Check(lc.Estimate != null && lc.Estimate.HostRamBytes > 0, "llamacpp produced no estimate");

        // Strata: the patched matrix accepts Q3_K/IQ2_S/Q6_K experts.
        var strata = BackendRegistry.ServerById("strata-upstream")!;
        var st = await strata.InspectAsync(model, CancellationToken.None);
        Check(st.Runnable, "strata rejected the APEX fixture: " + string.Join("; ", st.Blockers));

        // PolyStrata plugin: its matrix runs on the same fixture.
        var poly = BackendRegistry.ById("polystrata") as IServerBackend;
        if (poly != null)
        {
            var py = await poly.InspectAsync(model, CancellationToken.None);
            Check(py.Runnable, "polystrata rejected the APEX fixture: " + string.Join("; ", py.Blockers));
            Check(py.Notes.Any(n => n.Contains("MTP") || n.Contains("lookup")), "polystrata notes missing");
        }

        // Missing model: every backend reports not-runnable rather than throwing.
        var missing = Path.Combine(root, "nope.gguf");
        foreach (var backend in BackendRegistry.All())
        {
            var report = await backend.InspectAsync(missing, CancellationToken.None);
            Check(!report.Runnable, backend.Descriptor.Id + " reported a missing model runnable");
        }

        // The estimator scales with file size and never returns zero.
        var estimate = ResourceEstimator.ForGguf(model, 40);
        Check(estimate.VramBytes > 0 && estimate.HostRamBytes > 0, "estimator returned a zero estimate");
    }
}
