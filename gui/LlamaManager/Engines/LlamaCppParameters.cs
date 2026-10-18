using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// The llama.cpp parameter panel, expressed as a schema instead of hand-written XAML.
/// </summary>
/// <remarks>
/// Unlike the other backends, llama.cpp keeps its settings in the raw <c>Args</c> string that the
/// user can also edit by hand, so this type translates between that string and the schema panel:
/// <see cref="ReadFromArgs"/> projects the flags onto values, and <see cref="Apply"/> writes one
/// edited value back while leaving every flag it does not own untouched.
/// </remarks>
public static class LlamaCppParameters
{
    private static readonly Lazy<IReadOnlyList<ParameterSpec>> LazySpecs = new(Build);

    /// <summary>The flags this panel owns, keyed by spec key.</summary>
    private static readonly Dictionary<string, string> FlagOf = new(StringComparer.OrdinalIgnoreCase)
    {
        ["context"] = "-c",
        ["contextQuantK"] = "-ctk",
        ["contextQuantV"] = "-ctv",
        ["offloadLayers"] = "-ngl",
        ["cpuCores"] = "-t",
        ["reasoning"] = "--reasoning",
        ["reasoningBudget"] = "--reasoning-budget",
        ["flashAttention"] = "-fa",
        ["batchSize"] = "-b",
        ["ubatchSize"] = "-ub",
        ["moeOffload"] = "--n-cpu-moe",
        ["cpuMoe"] = "--cpu-moe",
        ["specType"] = "--spec-type",
        ["draftModel"] = "-md",
        ["verbose"] = "-v",
    };

    public static IReadOnlyList<ParameterSpec> Specs => LazySpecs.Value;

    private static IReadOnlyList<ParameterSpec> Build()
    {
        var quantChoices = new[]
        {
            new ParamChoice("", "默认（未量化 / f16）", "不传该参数，由引擎决定。", Recommended: true),
            new ParamChoice("q8_0", "q8_0（8-bit）"),
            new ParamChoice("q4_0", "q4_0（4-bit）"),
            new ParamChoice("q4_1", "q4_1（4-bit）"),
            new ParamChoice("f16", "f16"),
        };
        var onOffAuto = new[]
        {
            new ParamChoice("", "默认", "不传该参数，由引擎决定。", Recommended: true),
            new ParamChoice("off", "关闭"),
            new ParamChoice("on", "开启"),
            new ParamChoice("auto", "自动"),
        };
        var coreChoices = new List<ParamChoice> { new("", "默认", "由引擎自动决定。", Recommended: true) };
        for (var i = 1; i <= Math.Max(1, Environment.ProcessorCount); i++)
            coreChoices.Add(ParamChoice.Of(i.ToString()));

        return
        [
            new("context", "上下文", "基本", ParamType.Integer, "",
                "上下文窗口大小（-c）。留空由模型决定。", 1, 1048576,
                Help: "【上下文】\n• 作用：单次推理可用的上下文 Token 数（-c）。\n• 显存：越大 KV 缓存占用越多，6GB 卡建议 4096–8192。\n• 留空：使用模型原生上下文长度。",
                Unit: "tokens", RangeHint: "1–1,048,576，留空用模型原生值"),

            new("contextQuantK", "K 缓存量化", "基本", ParamType.Choice, "", "K 缓存量化类型（-ctk）。",
                Choices: quantChoices,
                Help: "【K 缓存量化】\n• 作用：压缩注意力 Key 缓存（-ctk），降低显存占用。\n• 默认：不量化。量化会带来轻微精度损失，但能显著省显存。"),

            new("contextQuantV", "V 缓存量化", "基本", ParamType.Choice, "", "V 缓存量化类型（-ctv）。",
                Choices: quantChoices,
                Help: "【V 缓存量化】\n• 作用：压缩注意力 Value 缓存（-ctv）。\n• 注意：V 缓存的量化对输出质量影响通常比 K 更明显，建议先试 K。"),

            new("offloadLayers", "GPU 卸载层数", "基本", ParamType.Choice, "", "卸载到 GPU 的层数（-ngl）。",
                Choices:
                [
                    new ParamChoice("", "默认", "由引擎决定。", Recommended: true),
                    new ParamChoice("all", "全部（all）"),
                    new ParamChoice("auto", "自动（auto）"),
                    new ParamChoice("0", "0（纯 CPU）"),
                ],
                Help: "【GPU 卸载层数】\n• 作用：把多少层放到 GPU（-ngl）。\n• all：全部卸载；auto：由引擎估算；0：纯 CPU。\n• 也可直接输入层数，例如 35。\n• 显存不足时引擎会回退部分层到 CPU，实际以加载日志为准。",
                Unit: "层", Editable: true),

            new("cpuCores", "CPU 线程", "基本", ParamType.Choice, "", "CPU 线程数（-t）。",
                Choices: coreChoices.ToArray(),
                Help: "【CPU 线程】\n• 作用：CPU 侧计算线程数（-t）。\n• 建议：留空由引擎按物理核心决定；通常设为物理核心数较优。",
                Editable: true),

            new("reasoning", "思考模式", "基本", ParamType.Choice, "", "思考模型是否输出推理过程（--reasoning）。",
                Choices: onOffAuto,
                Help: "【思考模式】\n• 作用：控制带思维链的模型是否输出思考内容（--reasoning）。\n• off：直接作答，响应更快；on：输出完整思考；auto：由引擎判断。"),

            new("reasoningBudget", "思考预算", "基本", ParamType.Choice, "", "思考 Token 上限（--reasoning-budget）。",
                Choices:
                [
                    new ParamChoice("", "默认", "不传该参数。", Recommended: true),
                    new ParamChoice("-1", "-1（无限制）"),
                    new ParamChoice("0", "0（零预算）"),
                    new ParamChoice("1024", "1024"),
                    new ParamChoice("2048", "2048"),
                    new ParamChoice("4096", "4096"),
                    new ParamChoice("8192", "8192"),
                ],
                Help: "【思考预算】\n• 作用：限制思考过程的最大 Token 数（--reasoning-budget）。\n• -1：不限制；0：不允许思考；正数：思考深度上限。\n• 也可直接输入其他数字。",
                Unit: "tokens", Editable: true, Min: -1, Max: 65536),

            new("flashAttention", "Flash Attention", "基本", ParamType.Choice, "", "Flash Attention 开关（-fa）。",
                Choices: onOffAuto,
                Help: "【Flash Attention】\n• 作用：启用显存高效的注意力实现（-fa）。\n• auto：由引擎按显卡能力决定；老卡可试 off。"),

            new("batchSize", "批大小", "基本", ParamType.Choice, "", "提示处理的批大小（-b）。",
                Choices:
                [
                    new ParamChoice("", "默认", "不传该参数。", Recommended: true),
                    new ParamChoice("2048", "2048"),
                    new ParamChoice("1024", "1024"),
                    new ParamChoice("512", "512"),
                ],
                Help: "【批大小】\n• 作用：处理提示时每批的 Token 数（-b）。\n• 越大吞吐越高，峰值显存也越高；显存紧张时降到 512。",
                Editable: true, Min: 1, Max: 65536),

            new("ubatchSize", "物理批大小", "基本", ParamType.Choice, "", "底层计算批大小（-ub）。",
                Choices:
                [
                    new ParamChoice("", "默认", "不传该参数。", Recommended: true),
                    new ParamChoice("512", "512"),
                    new ParamChoice("256", "256"),
                    new ParamChoice("128", "128"),
                ],
                Help: "【物理批大小】\n• 作用：子批（微批）的 Token 数（-ub），影响显存峰值。\n• 出现显存不足时，先降 -ub 再降 -b。",
                Editable: true, Min: 1, Max: 65536),

            new("moeOffload", "MoE 卸载层数", "MoE", ParamType.Choice, "", "把前 N 层的 MoE 权重放到 CPU（--n-cpu-moe）。",
                Choices:
                [
                    new ParamChoice("", "默认", "不传该参数。", Recommended: true),
                    new ParamChoice("0", "0（不强制）"),
                    new ParamChoice("1", "1"), new ParamChoice("2", "2"), new ParamChoice("4", "4"),
                    new ParamChoice("8", "8"), new ParamChoice("16", "16"), new ParamChoice("32", "32"),
                ],
                Help: "【MoE 卸载层数】\n• 作用：把前 N 层的 MoE 权重留在 CPU（--n-cpu-moe），为 KV 缓存腾出显存。\n• 建议：显存吃紧时从小到大试，配合实测速度决定。",
                VisibleWhen: "model.moe", Editable: true, Min: 0, Max: 999),

            new("cpuMoe", "MoE 全部走 CPU", "MoE", ParamType.Boolean, "0", "所有 MoE 权重留在 CPU（--cpu-moe）。",
                Help: "【MoE 全部走 CPU】\n• 作用：所有 MoE 专家都在 CPU 计算（--cpu-moe）。\n• 适合显存极小但内存充足的机器；速度会明显下降。",
                VisibleWhen: "model.moe"),

            new("specType", "投机解码类型", "投机解码", ParamType.Choice, "", "投机解码方式（--spec-type）。",
                Choices:
                [
                    new ParamChoice("", "默认（不传）", "不启用投机解码。", Recommended: true),
                    new ParamChoice("draft-mtp", "draft-mtp（内置 MTP 头）"),
                    new ParamChoice("draft-simple", "draft-simple"),
                    new ParamChoice("draft-eagle3", "draft-eagle3"),
                    new ParamChoice("ngram-mod", "ngram-mod"),
                    new ParamChoice("none", "none"),
                ],
                Help: "【投机解码类型】\n• 作用：选择投机解码方式（--spec-type）。\n• draft-mtp：使用模型内置 MTP 头，带 MTP 的模型可显著加速。\n• draft-simple / draft-eagle3：需要额外草稿模型（-md）。\n• ngram-mod：不依赖草稿模型的 n-gram 投机。"),

            new("draftModel", "草稿模型", "投机解码", ParamType.FilePath, "", "草稿模型路径（-md）。",
                Help: "【草稿模型】\n• 作用：投机解码所用的小模型路径（-md）。\n• 仅 draft-simple / draft-eagle3 需要；draft-mtp 用模型自带的 MTP 头，不需要单独文件。",
                VisibleWhen: "model.mtp"),

            new("mtp", "启用内置 MTP", "投机解码", ParamType.Boolean, "0",
                "把 --spec-type 设为 draft-mtp（仅当模型带 MTP 头）。",
                Help: "【启用内置 MTP】\n• 作用：把投机解码切换为模型自带的 MTP 头（等价于 --spec-type draft-mtp）。\n• 关闭时只移除 draft-mtp，不会覆盖你手填的其他投机类型。",
                VisibleWhen: "model.mtp"),

            new("verbose", "详细日志", "基本", ParamType.Boolean, "0", "输出详细运行日志（-v）。",
                Help: "【详细日志】\n• 作用：打开引擎的详细日志（-v），排查问题时有帮助，日常可关闭。"),
        ];
    }

    /// <summary>Projects the raw argument string onto one value per spec key.</summary>
    public static Dictionary<string, string> ReadFromArgs(string? args)
    {
        var parsed = new LauncherArguments(args ?? "");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var spec in Specs)
        {
            values[spec.Key] = spec.Key switch
            {
                // A boolean flag exists or it does not.
                "cpuMoe" => parsed.Has("--cpu-moe") ? "1" : "0",
                "verbose" => parsed.Has("-v") ? "1" : "0",
                // MTP is expressed through --spec-type, so only that exact value counts.
                "mtp" => parsed.Get("--spec-type") == "draft-mtp" ? "1" : "0",
                _ when FlagOf.TryGetValue(spec.Key, out var flag) => parsed.Get(flag) ?? "",
                _ => "",
            };
        }
        return values;
    }

    /// <summary>
    /// Writes one edited value back into the argument string. Only flags this panel owns are touched,
    /// so hand-written flags in the raw box survive a panel edit.
    /// </summary>
    public static string Apply(string? args, string key, string? value)
    {
        var original = args ?? "";
        var parsed = new LauncherArguments(original);
        var text = string.IsNullOrWhiteSpace(value) ? null : value!.Trim();
        switch (key)
        {
            case "cpuMoe": return parsed.SetFlag("--cpu-moe", text == "1");
            case "verbose": return parsed.SetFlag("-v", text == "1");
            case "mtp":
                if (text == "1") return parsed.Set("--spec-type", "draft-mtp");
                // Turning MTP off must not discard a different speculative type the user chose.
                return parsed.Get("--spec-type") == "draft-mtp" ? parsed.Set("--spec-type", null) : original;
        }
        if (!FlagOf.TryGetValue(key, out var known)) return original;
        return parsed.Set(known, text);
    }
}
