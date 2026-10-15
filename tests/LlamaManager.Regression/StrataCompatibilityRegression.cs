using System.IO;
using LlamaManager.Services;
using static Program;

internal static class StrataCompatibilityRegression
{
    private static GgufMetadata Read(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes);
        var metadata = GgufService.Read(stream);
        Equal(stream.Length, stream.Position); // descriptors only; the fixture has no model weights
        return metadata;
    }

    public static void RunFormats()
    {
        var baseline = Read(Fixtures.StrataGguf());
        Equal(322, baseline.TensorCount); // globals plus all eight required roles in 40 layers
        var mixed = new Dictionary<string, uint>(baseline.TensorFormats);
        for (var layer = 10; layer < 30; layer++)
            foreach (var role in new[] { "gate", "up", "down" }) mixed[$"blk.{layer}.ffn_{role}_exps.weight"] = 18;
        var report = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(tensors: mixed)));
        Check(report.IsPackable, "Occamy-like mixed Q3_K/IQ3_XXS must pass: " + StrataCompatibility.Describe(report));
        foreach (var role in new[] { "gate/up", "down" })
            foreach (uint type in new uint[] { 11, 18 })
                Check(report.Findings.Any(f => f.Role == role && f.TypeId == type && f.Layers == 20 &&
                    f.Decode == StrataCompatibility.Grade.Native && f.Prefill == StrataCompatibility.Grade.Native),
                    $"missing native {role} type {type}");
        Equal(3, report.Findings.Count(f => f.Role.StartsWith("shared ") && f.TypeId == 13 && f.Layers == 40));

        // The native LM head has a DIFFERENT table from iq_mmvq: Q4_0 and Q6_K are native,
        // while the float formats have no fallback in NativeHead::load.
        foreach (uint type in new uint[] { 2, 14 })
        {
            var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(headType: type)));
            Check(r.IsPackable && r.Findings.Any(f => f.Role == "lm_head" && f.TypeId == type &&
                f.Decode == StrataCompatibility.Grade.Native), "native head rejected or misgraded: " + type);
            Equal(0, r.Notes.Count);
        }
        foreach (uint type in new uint[] { 0, 1, 30, 3, 999 })
        {
            var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(headType: type)));
            Check(!r.IsPackable && r.Blockers.Any(b => b.Contains("output.weight") && b.Contains(StrataCompatibility.TypeName(type))),
                "unsupported head accepted: " + type);
        }
        Check(StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(embedType: 30))).IsPackable, "BF16 embedding rejected");
        Check(StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(embedType: 2))).IsPackable, "Q4_0 embedding rejected");
        foreach (uint type in new uint[] { 0, 1 })
        {
            var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(embedType: type)));
            Check(!r.IsPackable && r.Blockers.Any(b => b.Contains("token_embd.weight")), "unsupported embedding accepted");
        }

        foreach (uint type in new uint[] { 7, 14, 20 })
        {
            var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(gateType: type)));
            Check(!r.IsPackable && r.Blockers.Any(b => b.Contains("gate/up") && b.Contains(StrataCompatibility.TypeName(type))),
                "down-only format accepted for gate/up: " + type);
        }
        foreach (uint type in new uint[] { 16, 17, 21, 29 })
        {
            var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(downType: type)));
            Check(!r.IsPackable && r.Blockers.Any(b => b.Contains("down") && b.Contains(StrataCompatibility.TypeName(type))),
                "gate/up-only format accepted for down: " + type);
        }
        foreach (uint type in new uint[] { 6, 29 })
        {
            var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(gateType: type)));
            Check(r.IsPackable && r.Findings.Any(f => f.Role == "gate/up" && f.Decode == StrataCompatibility.Grade.Native &&
                f.Prefill == StrataCompatibility.Grade.Fallback), "native decode with prefill fallback rejected");
            Check(StrataCompatibility.Summary(r).Contains("fp16"), "prefill fallback missing from status");
        }
        // Q4_0/Q4_1 experts are native on both roles (upstream 16bdc707); neither has a prefill MMQ kernel.
        foreach (uint gu in new uint[] { 2, 3 })
            foreach (uint d in new uint[] { 2, 3 })
            {
                var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(gateType: gu, downType: d, sharedType: 8)));
                Check(r.IsPackable, $"Q4_0/Q4_1 expert pair rejected ({gu}/{d}): " + string.Join("; ", r.Blockers));
                Check(r.Findings.Where(f => f.Role is "gate/up" or "down")
                    .All(f => f.Decode == StrataCompatibility.Grade.Native && f.Prefill == StrataCompatibility.Grade.Fallback),
                    $"Q4_0/Q4_1 ({gu}/{d}) must be decode native with prefill fallback");
            }
        var mismatch = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(upType: 18)));
        Check(!mismatch.IsPackable && mismatch.Blockers.Any(b => b.Contains("blk.0") && b.Contains("不一致")),
            "gate/up mismatch lost its layer-specific blocker");

        foreach (var name in new[] { "token_embd.weight", "output.weight", "blk.7.ffn_down_exps.weight",
                     "blk.7.ffn_up_shexp.weight", "blk.7.ffn_gate_inp.weight" })
        {
            var incomplete = new Dictionary<string, uint>(baseline.TensorFormats);
            incomplete.Remove(name);
            var r = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(tensors: incomplete)));
            Check(!r.IsPackable && r.Blockers.Any(b => b.Contains(name.Replace("blk.7.", ""))), "missing tensor accepted: " + name);
        }
        var missingLayer = baseline.TensorFormats.Where(p => !p.Key.StartsWith("blk.15.", StringComparison.Ordinal));
        var missingReport = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(tensors: missingLayer)));
        Check(!missingReport.IsPackable && missingReport.Blockers.Any(b => b.Contains("blk.15")), "missing whole layer accepted");
        Check(!StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(tensors: []))).IsPackable, "empty descriptor table accepted");

        // Shared projections are checked separately; they need not share their routed-expert types.
        var shared = new Dictionary<string, uint>(baseline.TensorFormats) { ["blk.7.ffn_up_shexp.weight"] = 14 };
        Check(StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(tensors: shared))).IsPackable, "native shared Q6_K rejected");
        shared["blk.7.ffn_up_shexp.weight"] = 3;
        var badShared = StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(tensors: shared)));
        Check(!badShared.IsPackable && badShared.Blockers.Any(b => b.Contains("共享专家 up") && b.Contains("Q4_1")),
            "unsupported shared up was hidden by a supported shared gate");

        var mtp = Read(Fixtures.StrataGguf(mtp: true));
        Check(mtp.HasMtp && mtp.MtpLayerCount == 1, "MTP metadata or descriptors lost");
        var mtpReport = StrataCompatibility.Analyze(mtp);
        Check(mtpReport.IsPackable, "valid MTP model rejected: " + StrataCompatibility.Describe(mtpReport));
        Equal(40, mtpReport.Findings.Where(f => f.Role == "gate/up").Sum(f => f.Layers));
        var mtpTypes = new Dictionary<string, uint>(mtp.TensorFormats)
        {
            ["blk.40.ffn_gate_exps.weight"] = 7, ["blk.40.ffn_up_exps.weight"] = 20, ["blk.40.ffn_down_exps.weight"] = 29
        };
        Check(StrataCompatibility.Analyze(mtp with { TensorFormats = mtpTypes }).IsPackable,
            "excluded MTP formats must not be required to match the trunk");
        mtpTypes.Remove("blk.40.nextn.eh_proj.weight");
        Check(!StrataCompatibility.Analyze(mtp with { TensorFormats = mtpTypes }).IsPackable, "41 layers without MTP marker accepted");
        Check(!StrataCompatibility.Analyze(baseline with { LayerCount = 41 }).IsPackable, "bare 41-layer trunk accepted");
        Check(!StrataCompatibility.Analyze(mtp with { LayerCount = 40 }).IsPackable, "MTP block beyond a 40-layer header accepted");
        Check(!StrataCompatibility.Analyze(mtp with { MtpLayerCount = 0 }).IsPackable, "MTP packer count mismatch accepted");
        Check(!StrataCompatibility.Analyze(Read(Fixtures.StrataGguf(prism: true))).IsPackable, "Prism metadata ignored");
        Check(!StrataCompatibility.Analyze(Read(Fixtures.Gguf())).StructureOk, "qwen3 architecture accepted");
        Throws<InvalidDataException>(() => Read(Fixtures.StrataGguf(tensors: baseline.TensorFormats.Concat(baseline.TensorFormats.Take(1)))));
    }

    public static void RunShards(string root)
    {
        var directory = Path.Combine(root, "strata-shards");
        Directory.CreateDirectory(directory);
        var all = Read(Fixtures.StrataGguf(mtp: true)).TensorFormats;
        bool InLast(string name) => name.StartsWith("blk.40.") || name.EndsWith("ffn_down_exps.weight");
        var first = all.Where(p => !InLast(p.Key)).ToArray();
        var last = all.Where(p => InLast(p.Key)).ToArray();
        var total = (uint)all.Count;
        var firstBytes = Fixtures.StrataGguf(mtp: true, tensors: first, split: (2, 0, total));
        byte[] Last(string arch = "qwen35moe", uint number = 1, uint count = 2, uint? declared = null,
            IEnumerable<KeyValuePair<string, uint>>? tensors = null, bool prism = false) =>
            Fixtures.StrataGguf(arch: arch, tensors: tensors ?? last, includeGeometry: false,
                split: (count, number, declared ?? total), prism: prism);
        (string First, string Last) Pair(string stem, byte[] a, byte[] b)
        {
            var p1 = Path.Combine(directory, stem + "-00001-of-00002.gguf");
            var p2 = Path.Combine(directory, stem + "-00002-of-00002.gguf");
            File.WriteAllBytes(p1, a); File.WriteAllBytes(p2, b);
            return (p1, p2);
        }

        var valid = Pair("valid", firstBytes, Last());
        var md = GgufService.ReadShards(valid.First);
        Equal(all.Count, md.TensorCount); Equal(all.Count, md.TensorFormats.Count);
        Check(md.HasMtp && md.MtpLayerCount == 1 && StrataCompatibility.Analyze(md).IsPackable,
            "roles or MTP in the later shard were lost");
        Equal(40, md.TensorFormats.Count(p => p.Key.EndsWith("ffn_down_exps.weight") && !p.Key.StartsWith("blk.40.")));
        var noArch = Pair("no-arch-in-last", firstBytes, Last(arch: ""));
        Check(GgufService.ReadShards(noArch.First).HasMtp, "later shard without architecture rejected");

        Throws<ArgumentException>(() => GgufService.ReadShards(valid.Last));
        Check(GgufService.TryReadShards(valid.Last) == null, "non-first shard treated as a complete model");
        Throws<ArgumentException>(() => GgufService.ShardPaths(Path.Combine(directory, "bad-00001-of-00000.gguf")));
        var missing = Pair("missing", firstBytes, Last());
        File.Delete(missing.Last);
        Throws<FileNotFoundException>(() => GgufService.ReadShards(missing.First));
        Check(GgufService.TryReadShards(missing.First) == null, "missing shard accepted");
        var renamed = Path.Combine(directory, "renamed.gguf");
        File.WriteAllBytes(renamed, firstBytes);
        Throws<InvalidDataException>(() => GgufService.ReadShards(renamed));
        foreach (var (name, bytes) in new[]
        {
            ("wrong-arch", Last(arch: "qwen3")), ("wrong-number", Last(number: 0)),
            ("wrong-count", Last(count: 3)), ("wrong-total", Last(declared: total + 1)),
            ("short-directory", Last(tensors: last.Skip(1)))
        })
        {
            var pair = Pair(name, firstBytes, bytes);
            Throws<InvalidDataException>(() => GgufService.ReadShards(pair.First));
            Check(GgufService.TryReadShards(pair.First) == null, "bad split accepted: " + name);
        }
        var duplicate = Pair("duplicate", Fixtures.StrataGguf(mtp: true, tensors: first, split: (2, 0, total + 1)),
            Last(declared: total + 1, tensors: last.Concat(first.Take(1))));
        Throws<InvalidDataException>(() => GgufService.ReadShards(duplicate.First));
        var prism = Pair("prism-in-last", firstBytes, Last(prism: true));
        Check(GgufService.ReadShards(prism.First).RequiresPrism, "later shard's Hadamard metadata lost");
        var mtpOnlyInLast = Pair("mtp-flag-in-last", Fixtures.StrataGguf(tensors: first, split: (2, 0, total)), Last());
        var flagged = GgufService.ReadShards(mtpOnlyInLast.First);
        Check(flagged.HasMtp && !StrataCompatibility.Analyze(flagged).IsPackable, "MTP descriptor flag was not merged");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Throws<OperationCanceledException>(() => GgufService.ReadShards(valid.First, cancelled.Token));
    }
}
