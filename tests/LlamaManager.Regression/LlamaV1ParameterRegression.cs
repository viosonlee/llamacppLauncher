using System.Globalization;
using LlamaManager.Engines;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Pure schema/argument regressions: no config writes, engine probes, downloads or GPU processes.
/// Presence in the schema is not evidence of runtime support; the shared launch gate owns that check.
/// </summary>
internal static class LlamaV1ParameterRegression
{
    private static readonly (string Key, string Flag)[] CacheParameters =
    [
        ("moeCacheMib", "--moe-cache-mib"),
        ("moeCacheCpuShare", "--moe-cache-cpu-share"),
    ];

    private static readonly (string Key, string Flag)[] QuantParameters =
    [
        ("contextQuantK", "-ctk"),
        ("contextQuantV", "-ctv"),
    ];

    public static void RunDefaults()
    {
        foreach (var source in new string?[]
        {
            null, "", "  ",
            "  --prompt \"keep  spaces and --moe-cache-mib 512\" -ctk q8_0 -ctv q8_0 --custom=keep  ",
            "--cpu-moe --n-cpu-moe 12 -ngl all -fa on --split-mode layer --tensor-split 3,1",
        })
        {
            var original = source ?? "";
            var values = LlamaCppParameters.ReadFromArgs(source);
            var result = original;
            foreach (var (key, flag) in CacheParameters)
            {
                Equal("", Spec(key).Default);
                Equal("", values[key]);
                foreach (var unset in new string?[] { null, "", " \t " })
                    Equal(original, LlamaCppParameters.Apply(source, key, unset));
                result = LlamaCppParameters.Apply(result, key, Spec(key).Default);
                Check(!new LauncherArguments(result).Has(flag), "default emitted " + flag);
            }
            Equal(original, result);
            Equal(values["contextQuantK"], LlamaCppParameters.ReadFromArgs(result)["contextQuantK"]);
            Equal(values["contextQuantV"], LlamaCppParameters.ReadFromArgs(result)["contextQuantV"]);
        }

        foreach (var (key, flag) in QuantParameters)
        {
            Equal("", Spec(key).Default);
            Equal("", LlamaCppParameters.ReadFromArgs(null)[key]);
            Equal("", LlamaCppParameters.Apply(null, key, Spec(key).Default));
            var choices = Spec(key).Choices!;
            Equal("", choices.Single(choice => choice.Recommended).Value);
            Check(choices.Any(choice => choice.Value == "q8_0"), "FP8 replaced q8_0");
            Check(!choices.Single(choice => choice.Value == "f8_e4m3").Recommended, "FP8 became a default recommendation");
            Equal("q8_0", new LauncherArguments(LlamaCppParameters.Apply("", key, "q8_0")).Get(flag));
        }

        const string oldArgs = "-ctk q8_0 -ctv q8_0 --custom \"quoted  value\"";
        var changed = LlamaCppParameters.Apply(oldArgs, "moeCacheMib", "512");
        Equal(oldArgs + " --moe-cache-mib 512", changed);
        Equal("q8_0", LlamaCppParameters.ReadFromArgs(changed)["contextQuantK"]);
        Equal("q8_0", LlamaCppParameters.ReadFromArgs(changed)["contextQuantV"]);
        Check(!new LauncherArguments(changed).Has("--moe-cache-cpu-share"), "cache size implicitly emitted a CPU share");
        changed = LlamaCppParameters.Apply(oldArgs, "moeCacheCpuShare", "0.5");
        Equal(oldArgs + " --moe-cache-cpu-share 0.5", changed);
        Check(!new LauncherArguments(changed).Has("--moe-cache-mib"), "CPU share implicitly enabled a cache");
    }

    public static void RunValuesAndValidation()
    {
        foreach (var value in new[] { "0", "1", "512", "2048", "2147483647" })
            ExpectRoundTrip("moeCacheMib", "--moe-cache-mib", value);
        foreach (var value in new[] { "0", "0.0", "0.25", "0.5", "1", "1.0", "2.5e-1" })
            ExpectRoundTrip("moeCacheCpuShare", "--moe-cache-cpu-share", value);
        foreach (var (key, flag) in QuantParameters)
            ExpectRoundTrip(key, flag, "f8_e4m3");

        var args = LlamaCppParameters.Apply("--cpu-moe --n-cpu-moe 4 -ngl all", "moeCacheMib", " 1024 ");
        args = LlamaCppParameters.Apply(args, "moeCacheCpuShare", " 0 ");
        args = LlamaCppParameters.Apply(args, "contextQuantK", "f8_e4m3");
        args = LlamaCppParameters.Apply(args, "contextQuantV", "f8_e4m3");
        var values = LlamaCppParameters.ReadFromArgs(args);
        Equal("1024", values["moeCacheMib"]);
        Equal("0", values["moeCacheCpuShare"]);
        Equal("1", values["cpuMoe"]);
        Equal("4", values["moeOffload"]);
        Equal("all", values["offloadLayers"]);
        Equal("f8_e4m3", values["contextQuantK"]);
        Equal("f8_e4m3", values["contextQuantV"]);
        foreach (var (key, _) in CacheParameters.Concat(QuantParameters))
        {
            args = LlamaCppParameters.Apply(args, key, "");
            Equal("", LlamaCppParameters.ReadFromArgs(args)[key]);
        }
        Check(args.StartsWith("--cpu-moe --n-cpu-moe 4 -ngl all", StringComparison.Ordinal), "clearing new values lost MoE placement flags");

        foreach (var invalid in new[] { "-1", "1.5", "1e3", "1,024", "2147483648", "99999999999999999999999999", "NaN", "Infinity", "auto", "512MiB", "--custom" })
            ExpectRejected("moeCacheMib", invalid);
        foreach (var invalid in new[] { "-0.01", "1.01", "1.0000001", "NaN", "Infinity", "-Infinity", "1e999", "0,1,2", "auto", "0.5foo", "--custom" })
            ExpectRejected("moeCacheCpuShare", invalid);

        // Explicit decimal edits follow the existing UI convention; reads of raw args do not.
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            foreach (var culture in new[] { "zh-CN", "de-DE", "en-US" })
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
                Equal("--moe-cache-cpu-share 0.5", LlamaCppParameters.Apply("", "moeCacheCpuShare", " 0,5 "));
                ExpectRoundTrip("moeCacheCpuShare", "--moe-cache-cpu-share", "0.25");
                ExpectRejected("moeCacheCpuShare", "1,01");
            }
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }

        // FlagOf's key lookup remains case-insensitive, including validation of the new keys.
        Equal("--moe-cache-mib 0", LlamaCppParameters.Apply("", "MOECACHEMIB", "0"));
        Equal("0.5", LlamaCppParameters.ReadFromArgs("--moe-cache-cpu-share=0.5")["MOECACHECPUSHARE"]);
        ExpectRejected("MOECACHECPUSHARE", "2");
    }

    public static void RunRawArguments()
    {
        // Preserve the source spelling of every unowned token, not just its parsed value.
        const string prefix = "--prompt \"--moe-cache-mib 999 and \\\"quoted\\\"  text\"  --custom \"C:\\模型 路径\\\\\" --threads-batch=3 --threads 6";
        const string suffix = "--cpu-moe -ncmoe 8 --tensor-split 3,1 --split-mode layer --future=keep";
        foreach (var (key, flag) in CacheParameters.Concat(QuantParameters))
        {
            var value = key switch
            {
                "moeCacheMib" => "2048",
                "moeCacheCpuShare" => "0.25",
                _ => "f8_e4m3",
            };
            var initial = key switch
            {
                "moeCacheMib" => "512",
                "moeCacheCpuShare" => "0",
                _ => "q8_0",
            };
            foreach (var rawOption in new[] { $"{flag} {initial}", $"{flag}={initial}", $"{flag} \"{initial}\"", $"{flag}=\"{initial}\"" })
            {
                var original = prefix + "  " + rawOption + "  " + suffix;
                Equal(initial, LlamaCppParameters.ReadFromArgs(original)[key]);
                var changed = LlamaCppParameters.Apply(original, key, value);
                Equal(prefix + "  " + flag + " " + value + "  " + suffix, changed);
                Equal(value, LlamaCppParameters.ReadFromArgs(changed)[key]);
                Equal(prefix + "    " + suffix, LlamaCppParameters.Apply(original, key, null));
                Equal(original, LlamaCppParameters.Apply(original, "unknown-schema-key", "x"));
            }
        }

        // Long K/V aliases and duplicate forms: last value reads, one canonical option writes.
        const string duplicate = "--cache-type-k=q8_0 -ctk \"f8_e4m3\" --cache-type-v=f8_e4m3 " +
            "--moe-cache-mib=512 --moe-cache-mib \"0\" --moe-cache-cpu-share=0.75 --moe-cache-cpu-share 0";
        var read = LlamaCppParameters.ReadFromArgs(duplicate);
        Equal("f8_e4m3", read["contextQuantK"]);
        Equal("f8_e4m3", read["contextQuantV"]);
        Equal("0", read["moeCacheMib"]);
        Equal("0", read["moeCacheCpuShare"]);
        var replaced = duplicate;
        foreach (var (key, flag) in CacheParameters.Concat(QuantParameters))
        {
            var value = key.StartsWith("contextQuant", StringComparison.Ordinal) ? "f8_e4m3" : "0";
            replaced = LlamaCppParameters.Apply(replaced, key, value);
            var parsed = new LauncherArguments(replaced);
            Equal(value, parsed.Get(flag));
            Equal(1, parsed.Values.Count(token => LauncherArguments.Canonical(token.Split('=')[0]) == flag));
        }
        foreach (var (key, flag) in CacheParameters.Concat(QuantParameters))
        {
            replaced = LlamaCppParameters.Apply(replaced, key, null);
            Check(!new LauncherArguments(replaced).Has(flag), "clearing left duplicate " + flag);
        }
        Equal("", replaced);

        const string raw = "--moe-cache-mib=000512 --moe-cache-cpu-share=0,50 -ctk q8_0 --custom \"x  y\"";
        read = LlamaCppParameters.ReadFromArgs(raw);
        Equal("000512", read["moeCacheMib"]);
        Equal("0,50", read["moeCacheCpuShare"]);
        Equal(raw + " -ctv f8_e4m3", LlamaCppParameters.Apply(raw, "contextQuantV", "f8_e4m3"));
        const string invalidRaw = "--moe-cache-mib=-1 --moe-cache-cpu-share=NaN --custom \"leave  me\"";
        read = LlamaCppParameters.ReadFromArgs(invalidRaw);
        Equal("-1", read["moeCacheMib"]);
        Equal("NaN", read["moeCacheCpuShare"]);
        Equal(invalidRaw + " -t 8", LlamaCppParameters.Apply(invalidRaw, "cpuCores", "8"));
        Equal(invalidRaw, LlamaCppParameters.Apply(invalidRaw, "contextQuantV", ""));

        // A flag-looking prompt is data, not a cache setting or a reason to enable an option.
        const string literal = "--prompt \"--moe-cache-mib\" --system-prompt \"--moe-cache-cpu-share=0.5\" --custom \"-ctk f8_e4m3\"";
        foreach (var (key, _) in CacheParameters.Concat(QuantParameters))
        {
            Equal("", LlamaCppParameters.ReadFromArgs(literal)[key]);
            Equal(literal, LlamaCppParameters.Apply(literal, key, ""));
        }
    }

    public static void RunSchema()
    {
        Equal(LlamaCppParameters.Specs.Count, LlamaCppParameters.Specs.Select(spec => spec.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var mib = Spec("moeCacheMib");
        var share = Spec("moeCacheCpuShare");
        Equal(ParamType.Integer, mib.Type);
        Equal<double?>(0, mib.Min);
        Equal<double?>(int.MaxValue, mib.Max);
        Equal("MiB", mib.Unit);
        Equal(ParamType.Decimal, share.Type);
        Equal<double?>(0, share.Min);
        Equal<double?>(1, share.Max);
        foreach (var spec in new[] { mib, share })
        {
            Equal("MoE", spec.Group);
            Equal("", spec.Default);
            Equal<int?>(null, spec.MultipleOf); // No silent rounding/clamping or budget alignment.
            Equal("model.moe", spec.VisibleWhen);
            Check(!ParameterVisibility.Evaluate(spec.VisibleWhen, ParameterContext.For("llamacpp")), "cache field visible without a model");
            Check(!ParameterVisibility.Evaluate(spec.VisibleWhen, Context(new GgufMetadata())), "cache field visible for a dense model");
            Check(ParameterVisibility.Evaluate(spec.VisibleWhen, Context(new GgufMetadata { IsMoE = true })), "cache field hidden for a MoE model");
            ContainsAll(spec.EffectiveHelp, spec.Key, "CPU", "GPU", "显存", "MoE", "--n-cpu-moe", "--cpu-moe",
                "不兼容", "--split-mode tensor", "KVMem", "SSD", "RAM", "不是", "不保证提速", "留空不传", "默认 0",
                "v1", "Prism", "不保证支持", "--help");
            ContainsAll(spec.EffectiveToolTip, spec.Key + " tooltip", "v1", "Prism", "--help");
            ContainsAll(spec.EffectiveRangeHint, spec.Key + " range", "0", "留空不传");
        }
        ContainsAll(mib.Help, mib.Key, "专家张量", "层已分配给 GPU", "额外占用", "0", "关闭", "不是 KVMem / KV 缓存分页", "--tensor-split");
        ContainsAll(share.Help, share.Key, "缓存已满", "未命中", "不上传到 GPU", "不会自动启用缓存", "0–1");

        foreach (var (key, _) in QuantParameters)
        {
            var spec = Spec(key);
            Equal(ParamType.Choice, spec.Type);
            Check(ParameterVisibility.Evaluate(spec.VisibleWhen, ParameterContext.For("llamacpp")), "K/V choices unexpectedly require MoE");
            foreach (var oldChoice in new[] { "", "q8_0", "q4_0", "q4_1", "f16" })
                Equal(1, spec.Choices!.Count(choice => choice.Value == oldChoice));
            var fp8 = spec.Choices!.Single(choice => choice.Value == "f8_e4m3");
            ContainsAll(fp8.Label, key + " label", "FP8", "支持");
            foreach (var help in new[] { spec.EffectiveHelp, fp8.Note })
                ContainsAll(help, key + " FP8 help", "f8_e4m3", "q8_0", "不会自动替换", "模型架构", "内核", "不保证", "v1", "Prism", "--help",
                    "不统一要求 RTX 40 系列及以上");
        }
    }

    private static ParameterSpec Spec(string key) => LlamaCppParameters.Specs.Single(spec => spec.Key == key);

    private static ParameterContext Context(GgufMetadata model) => new("llamacpp", model, LlamaCppParameters.ReadFromArgs(""));

    private static void ExpectRoundTrip(string key, string flag, string value)
    {
        var args = LlamaCppParameters.Apply(null, key, value);
        Equal(flag + " " + value, args);
        Equal(value, LlamaCppParameters.ReadFromArgs(args)[key]);
        Equal(value, new LauncherArguments(args).Get(flag));
        Equal(args, LlamaCppParameters.Apply(args, key, LlamaCppParameters.ReadFromArgs(args)[key]));
        foreach (var unset in new string?[] { null, "", " \t " })
            Equal("", LlamaCppParameters.Apply(args, key, unset));
    }

    private static void ExpectRejected(string key, string value)
    {
        const string original = "--moe-cache-mib 512 --moe-cache-cpu-share 0.5 --custom \"keep  me\" -ctk q8_0";
        Throws<ArgumentException>(() => LlamaCppParameters.Apply(original, key, value));
        Equal("512", LlamaCppParameters.ReadFromArgs(original)["moeCacheMib"]);
        Equal("0.5", LlamaCppParameters.ReadFromArgs(original)["moeCacheCpuShare"]);
    }

    private static void ContainsAll(string? text, string field, params string[] expected)
    {
        foreach (var fragment in expected)
            Check(text?.Contains(fragment, StringComparison.Ordinal) == true, field + " lacks: " + fragment);
    }
}
