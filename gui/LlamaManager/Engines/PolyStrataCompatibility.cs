using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// PolyStrata (strata-poly) reads the GGUF in place and runs the MoE families on its own engine.
/// Its expert-format support is close to the patched Strata engine's, plus Q4_0/Q4_1 and the MTP
/// draft block. This is a coarse check: unknown roles are noted, not blocked.
/// </summary>
public static class PolyStrataCompatibility
{
    private static readonly HashSet<uint> Supported =
        [2, 3, 6, 7, 8, 11, 12, 13, 14, 16, 17, 18, 20, 21, 22, 23, 29, 42];

    public static CompatibilityReport Analyze(GgufMetadata md)
    {
        var notes = new List<string>();
        var blockers = new List<string>();
        foreach (var (name, type) in md.TensorFormats)
        {
            if (!name.Contains("_exps.weight")) continue;
            if (!Supported.Contains(type))
                blockers.Add($"{name} 使用 {StrataCompatibility.TypeName(type)}：strata-poly 可能不支持该专家格式。");
        }
        if (md.HasMtp) notes.Add("含内置 MTP 草稿块：strata-poly 会启用草稿解码。");
        else notes.Add("无 MTP 草稿块：strata-poly 仅用重复/lookup 猜测。");
        notes.Add("strata-poly 使用专家缓存；6GB 卡低于其 8GB 最低要求，速度可能受限。");
        var summary = blockers.Count == 0
            ? $"PolyStrata 预检通过：{md.Architecture} / {md.LayerCount} 层。"
            : $"PolyStrata 预检发现问题（{blockers.Count} 项）。";
        return new CompatibilityReport(blockers.Count == 0, blockers, notes, summary, null);
    }
}
