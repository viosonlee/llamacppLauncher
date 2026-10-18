using System.IO;
using LlamaManager.Engines;
using LlamaManager.Models;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Unit tests for the small, pure helpers every other feature builds on: the schema records,
/// path identity, model display/size/category, the llama.cpp argument projection, and the
/// Strata-family path and diagnostic helpers. These are the pieces that fail silently —
/// a wrong range hint, a lost hand-written flag, a mis-split log line — so each one is
/// pinned down here instead of being exercised only through the UI.
/// </summary>
internal static class CoreUtilitiesRegression
{
    public static void RunParameterSpec()
    {
        // ToolTip falls back to the first line of the long help; the "?" body is the full help.
        var withHelp = new ParameterSpec("k", "标签", "基本", ParamType.Integer, Help: "第一行\n第二行\n第三行");
        Equal("第一行", withHelp.EffectiveToolTip);
        Equal("第一行\n第二行\n第三行", withHelp.EffectiveHelp);
        Check(withHelp.EffectiveRangeHint == null, "a spec without Min/Max or RangeHint must not invent one");

        // An explicit short tooltip wins over the help text, and the help body stays the help.
        var both = new ParameterSpec("k", "标签", "基本", ParamType.Integer, ToolTip: "短提示", Help: "长帮助");
        Equal("短提示", both.EffectiveToolTip);
        Equal("长帮助", both.EffectiveHelp);

        // Help alone still gives a "?" body when no tooltip exists.
        Equal("只有帮助", new ParameterSpec("k", "标签", "基本", ParamType.Text, Help: "只有帮助").EffectiveHelp);
        Check(new ParameterSpec("k", "标签", "基本", ParamType.Text).EffectiveHelp == null, "no help and no tooltip must stay null");

        // Range hints: explicit wins, otherwise Min–Max, otherwise nothing.
        Equal("0–64", new ParameterSpec("k", "标签", "基本", ParamType.Integer, Min: 0, Max: 64).EffectiveRangeHint);
        Equal("推荐 6", new ParameterSpec("k", "标签", "基本", ParamType.Integer, Min: 0, Max: 64, RangeHint: "推荐 6").EffectiveRangeHint);
        Check(new ParameterSpec("k", "标签", "基本", ParamType.Integer, Min: 0).EffectiveRangeHint == null,
            "a half-open range must not print a bogus hint");

        // Choices drive the combo box; an empty array is the same as none.
        Check(!new ParameterSpec("k", "标签", "基本", ParamType.Choice).HasChoices, "null choices reported as a combo");
        Check(!new ParameterSpec("k", "标签", "基本", ParamType.Choice, Choices: []).HasChoices, "empty choices reported as a combo");
        Check(new ParameterSpec("k", "标签", "基本", ParamType.Choice, Choices: [ParamChoice.Of("a")]).HasChoices, "one choice is a combo");

        // ParamChoice.Of keeps value and label identical; the recommended flag defaults to false.
        var bare = ParamChoice.Of("q8_0");
        Equal("q8_0", bare.Value);
        Equal("q8_0", bare.Label);
        Check(!bare.Recommended && bare.Note == null, "ParamChoice.Of must not set a note or the recommended flag");
    }

    public static void RunPathIdentity()
    {
        // Normalize resolves "..", unifies separators and lowercases, so two spellings of the
        // same file collapse to one preset key.
        var a = PathIdentity.Normalize(@"C:\Models\Sub\..\a.gguf");
        Equal(@"c:\models\a.gguf", a);
        Check(PathIdentity.Equals(@"C:\Models\a.gguf", @"c:/models/A.GGUF"), "case and separator variants must be equal");
        Check(!PathIdentity.Equals(@"C:\Models\a.gguf", @"C:\Models\b.gguf"), "different files compared equal");
        Check(!PathIdentity.Equals(null, @"C:\Models\a.gguf"), "null must never equal a path");
        Check(!PathIdentity.Equals("", ""), "two empty strings must not be equal");
        Check(!PathIdentity.Equals("   ", @"C:\Models\a.gguf"), "whitespace must not equal a path");
    }

    public static void RunModelInfo()
    {
        // DisplayName prefers the relative path (subfolder + file), falling back to the file name.
        Equal("Sub/a.gguf", new ModelInfo { FileName = "a.gguf", RelativePath = "Sub/a.gguf" }.DisplayName);
        Equal("a.gguf", new ModelInfo { FileName = "a.gguf" }.DisplayName);

        // Sizes: GB keeps two decimals, MB one, bytes none. The culture-dependent separators are
        // not asserted, only the unit and the magnitude.
        var gb = new ModelInfo { SizeBytes = 2L * 1024 * 1024 * 1024 }.SizeFormatted;
        Check(gb.EndsWith("GB") && gb.StartsWith("2"), "2 GiB did not format as 2.xx GB: " + gb);
        var mb = new ModelInfo { SizeBytes = 5L * 1024 * 1024 }.SizeFormatted;
        Check(mb.EndsWith("MB") && mb.StartsWith("5"), "5 MiB did not format as 5.x MB: " + mb);
        Check(new ModelInfo { SizeBytes = 900 }.SizeFormatted.EndsWith("B"), "900 bytes lost its unit");

        // Category thresholds: 2 GB, 6 GB, 12 GB.
        Equal("Small (<2GB)", new ModelInfo { SizeBytes = (1L << 31) - 1 }.Category);
        Equal("Medium (2-6GB)", new ModelInfo { SizeBytes = 1L << 31 }.Category);
        Equal("Large (6-12GB)", new ModelInfo { SizeBytes = 3L << 31 }.Category);
        Equal("XL (>12GB)", new ModelInfo { SizeBytes = 12L << 30 }.Category);

        // MoE detection by name (the file does not exist, so only the heuristic runs).
        Check(ModelInfo.DetectIsMoE("Qwen3.6-35B-A3B-APEX-MTP-I-Mini.gguf"), "A3B name not detected as MoE");
        Check(ModelInfo.DetectIsMoE("Mixtral-8x7B-v0.1.gguf"), "8x7B name not detected as MoE");
        Check(ModelInfo.DetectIsMoE("some-moe-model.gguf"), "the moe token was not detected");
        Check(!ModelInfo.DetectIsMoE("llama-3-8b-instruct.gguf"), "a dense name was reported as MoE");
        Check(!ModelInfo.DetectIsMoE(""), "an empty name was reported as MoE");
        Check(!ModelInfo.DetectIsMoE(null), "a null name was reported as MoE");
        Check(new ModelInfo { FileName = "Qwen3.6-35B-A3B.gguf" }.ModelTypeDisplay == "MoE", "ModelTypeDisplay did not follow IsMoE");
    }

    public static void RunLlamaCppRoundTrip()
    {
        const string args = "--host 127.0.0.1 -c 8192 -t 8 --spec-type draft-mtp -v --n-cpu-moe 32 --hand-written foo";

        var read = LlamaCppParameters.ReadFromArgs(args);
        Equal("8192", read["context"]);
        Equal("8", read["cpuCores"]);
        Equal("1", read["mtp"]);
        Equal("draft-mtp", read["specType"]);
        Equal("1", read["verbose"]);
        Equal("32", read["moeOffload"]);
        Equal("", read["offloadLayers"]);

        // Range validation reads Min/Max off the spec itself, so the fields the panel promises to
        // check must still declare a range. -ngl is the exception: it also offers all/auto, which
        // ValidateValue exempts by value, so it carries presets instead of a range.
        var contextSpec = LlamaCppParameters.Specs.Single(s => s.Key == "context");
        Check(contextSpec.Min == 1 && contextSpec.Max == 1048576, "context lost its 1–1,048,576 range");
        var offloadSpec = LlamaCppParameters.Specs.Single(s => s.Key == "offloadLayers");
        Check(offloadSpec.Choices!.Any(c => c.Value == "all") && offloadSpec.Choices!.Any(c => c.Value == "auto"),
            "-ngl lost the all/auto presets that exempt it from range checking");

        // Editing one value must not disturb the other flags, including hand-written ones.
        var edited = LlamaCppParameters.Apply(args, "context", "4096");
        Check(edited.Contains("-c 4096"), "context was not rewritten: " + edited);
        Check(edited.Contains("--hand-written foo"), "a hand-written flag was lost: " + edited);
        Check(edited.Contains("--n-cpu-moe 32"), "an unrelated flag was lost: " + edited);
        Equal("4096", LlamaCppParameters.ReadFromArgs(edited)["context"]);

        // Clearing a value removes its flag entirely.
        var cleared = LlamaCppParameters.Apply(edited, "context", "");
        Check(!cleared.Contains("-c "), "clearing context left the flag behind: " + cleared);

        // MTP is expressed through --spec-type: turning it on writes draft-mtp, turning it off
        // removes only that value and never clobbers a different speculative type.
        Check(LlamaCppParameters.Apply("-c 4096", "mtp", "1").Contains("--spec-type draft-mtp"), "mtp on did not write draft-mtp");
        Check(!LlamaCppParameters.Apply(args, "mtp", "0").Contains("--spec-type"), "mtp off left draft-mtp behind");
        var ngram = LlamaCppParameters.Apply("--spec-type ngram-mod", "mtp", "0");
        Check(ngram.Contains("ngram-mod"), "mtp off discarded a different speculative type: " + ngram);

        // Boolean flags are presence-only.
        var withCpuMoe = LlamaCppParameters.Apply("-c 4096", "cpuMoe", "1");
        Check(withCpuMoe.Contains("--cpu-moe"), "--cpu-moe was not added");
        Check(!LlamaCppParameters.Apply(withCpuMoe, "cpuMoe", "0").Contains("--cpu-moe"), "--cpu-moe was not removed");
        Check(LlamaCppParameters.Apply("-c 4096", "verbose", "1").Contains("-v"), "-v was not added");
        Check(!LlamaCppParameters.Apply("-v -c 4096", "verbose", "0").Contains("-v"), "-v was not removed");

        // An unknown key leaves the string byte-identical.
        Equal(args, LlamaCppParameters.Apply(args, "notAKey", "1"));

        // Both cache types keep their own flag; they must not overwrite each other.
        var kv = LlamaCppParameters.Apply(LlamaCppParameters.Apply("-c 4096", "contextQuantK", "q8_0"), "contextQuantV", "q4_0");
        var kvRead = LlamaCppParameters.ReadFromArgs(kv);
        Equal("q8_0", kvRead["contextQuantK"]);
        Equal("q4_0", kvRead["contextQuantV"]);
    }

    public static void RunStrataDiagnostics()
    {
        // The loaded line and the later "cache shrank" line are two different formats.
        var loaded = LogService.ParseStrataGpuSummary(
            ["strata-poly: loaded in 5.8 s; 2162 of 10496 routed experts in VRAM (2.5 GiB), 8 CPU threads"]);
        Check(loaded.Contains("2162/10496") && loaded.Contains("2.5 GiB"), "loaded line not parsed: " + loaded);

        var shrunk = LogService.ParseStrataGpuSummary(
            ["strata-poly: the expert cache 2162 -> 2069 experts (2.25 GiB) for the VRAM free now, 288 MiB left"]);
        Check(shrunk.Contains("2069") && shrunk.Contains("2.25 GiB"), "shrunk-cache line not parsed: " + shrunk);

        Equal("", LogService.ParseStrataGpuSummary(["strata-poly: tail skip 6: an expert outside VRAM ..."]));

        // PolyStrata compatibility: a supported expert type passes, an unknown one blocks and
        // names the tensor, and the MTP note appears only when the header has an MTP block.
        var supported = PolyStrataCompatibility.Analyze(new GgufMetadata
        {
            Architecture = "qwen35moe",
            LayerCount = 40,
            TensorFormats = new Dictionary<string, uint> { ["blk.0.ffn_gate_exps.weight"] = 2 },
        });
        Check(supported.Runnable, "Q4_0 experts rejected: " + string.Join("; ", supported.Blockers));

        var unsupported = PolyStrataCompatibility.Analyze(new GgufMetadata
        {
            Architecture = "qwen35moe",
            TensorFormats = new Dictionary<string, uint> { ["blk.0.ffn_gate_exps.weight"] = 40 },
        });
        Check(!unsupported.Runnable, "an unsupported expert type was accepted");
        Check(unsupported.Blockers.Any(b => b.Contains("blk.0.ffn_gate_exps.weight")), "the blocker does not name the tensor");

        var withMtp = PolyStrataCompatibility.Analyze(new GgufMetadata { Architecture = "qwen35moe", HasMtp = true });
        Check(withMtp.Notes.Any(n => n.Contains("MTP")), "an MTP model got no draft note");
        var withoutMtp = PolyStrataCompatibility.Analyze(new GgufMetadata { Architecture = "qwen35moe" });
        Check(withoutMtp.Notes.Any(n => n.Contains("lookup")), "a non-MTP model got no lookup note");
    }

    public static void RunBackendTypesAndDiagnostics()
    {
        // The factory helpers are what every probe path returns, so their shape is pinned here.
        var missing = BackendStatus.Unavailable("未找到引擎");
        Check(!missing.Available && missing.Problem == "未找到引擎" && missing.ExecutablePath == null,
            "Unavailable() must not claim an executable");
        var unknown = CompatibilityReport.Unknown("未检查");
        Check(unknown.Runnable && unknown.Blockers.Count == 0 && unknown.Notes.Count == 0 && unknown.Estimate == null,
            "Unknown() must be runnable with no blockers");

        // The estimator floors VRAM at 4 GiB and mirrors the file size into host RAM.
        var file = Path.Combine(Path.GetTempPath(), "estimator-" + Guid.NewGuid().ToString("N") + ".gguf");
        File.WriteAllBytes(file, new byte[8 * 1024 * 1024]);
        try
        {
            var estimate = ResourceEstimator.ForGguf(file, 40);
            Check(estimate.VramBytes == 4L * 1024 * 1024 * 1024, "the 4 GiB VRAM floor was not applied");
            Equal(8L * 1024 * 1024, estimate.HostRamBytes);
            Check(estimate.Note.Contains("40 层"), "the estimate does not name the layer count: " + estimate.Note);
        }
        finally { File.Delete(file); }

        // Crash logging appends and never throws; a null exception is a no-op.
        var crash = Path.Combine(ConfigService.GetManagerRoot(), "logs", "crash.log");
        if (File.Exists(crash)) File.Delete(crash);
        Diagnostics.WriteCrash(null);
        Check(!File.Exists(crash), "a null exception created a crash log");
        Diagnostics.WriteCrash(new InvalidOperationException("boom"));
        var text = File.ReadAllText(crash);
        Check(text.Contains("InvalidOperationException") && text.Contains("boom"), "the crash log lost the exception: " + text);
        var length = text.Length;
        Diagnostics.WriteCrash(null);
        Equal(length, File.ReadAllText(crash).Length);
    }

    public static void RunStrataPaths()
    {
        var config = new ManagerConfig();
        const string model = @"C:\AIModel\Ornith-1.5-35B-A3B-APEX-MTP-I-Mini.gguf";

        // The tokenizer directory is data/<backend>/<16 hex>/tokenizer and stable per model path.
        var first = StrataBackend.TokenizerDir(config, "polystrata", model);
        var second = StrataBackend.TokenizerDir(config, "polystrata", model);
        Equal(first, second);
        Check(first.EndsWith(Path.Combine("tokenizer")), "tokenizer dir does not end in tokenizer: " + first);
        Check(first.Contains(Path.Combine("data", "polystrata")), "tokenizer dir is not under data/polystrata: " + first);
        var segment = first.Split(Path.DirectorySeparatorChar)[^2];
        Equal(16, segment.Length);
        Check(segment.All(Uri.IsHexDigit), "the model hash is not hexadecimal: " + segment);
        Check(StrataBackend.TokenizerDir(config, "polystrata", model.Replace("Ornith", "ornith")) == first,
            "the hash must ignore case in the model path");

        // Port precedence: an explicit request wins, then the panel value, then the legacy block.
        var backend = new StrataBackend(StrataBackend.PolyProfile);
        config.Engines["polystrata"] = new EngineSettings
        {
            Parameters = new Dictionary<string, string> { ["servePort"] = "8091" },
        };
        config.Strata.ServePort = 8095;
        Equal(9000, backend.ResolvePort(new EngineStartRequest { Config = config, Port = 9000 }));
        Equal(8091, backend.ResolvePort(new EngineStartRequest { Config = config }));
        config.Engines["polystrata"].Parameters.Remove("servePort");
        Equal(8095, backend.ResolvePort(new EngineStartRequest { Config = config }));
    }
}
