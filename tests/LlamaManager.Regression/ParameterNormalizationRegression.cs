using System.Globalization;
using System.Numerics;
using System.Text.Json;
using LlamaManager.Engines;
using LlamaManager.Models;
using static Program;

/// <summary>Pure, GPU/filesystem-free normalization and native-argument checks.</summary>
internal static class ParameterNormalizationRegression
{
    public static void Run()
    {
        RunRoundingAndBounds();
        RunInvalidAndSchema();
        RunDefaultsAndUnmarked();
        RunCopyAndNativeArguments();
    }

    private static ParameterSpec Integer(double? min = null, double? max = null, int multiple = 32) =>
        new("fixture", "测试整数", "测试", ParamType.Integer, Min: min, Max: max, MultipleOf: multiple);

    private static ParameterSpec Budget => KvmemParameters.Specs.Single(spec => spec.Key == "kvmemBudget");
    private static ParameterSpec Reserve => KvmemParameters.Specs.Single(spec => spec.Key == "kvmemGenReserve");

    private static void RunRoundingAndBounds()
    {
        foreach (var (input, expected) in new[]
        {
            ("8193", "8192"), ("8208", "8192"), ("8210", "8224"), ("8192", "8192"),
            ("2047", "2048"), ("2049", "2048"), ("131073", "131072"), ("0", "2048"),
            ("-999999999999999999999999999999999999", "2048"),
            ("999999999999999999999999999999999999", "131072"),
            ("2147483647", "131072"), ("-2147483648", "2048"),
            ("9223372036854775807", "131072"), ("-9223372036854775808", "2048"),
        }) Expect(Budget, input, expected);
        foreach (var (input, expected) in new[]
        {
            ("511", "512"), ("33", "32"), ("31", "32"), ("48", "32"),
            ("49", "64"), ("16385", "16384"), ("-1", "32"),
        }) Expect(Reserve, input, expected);
        foreach (var (input, expected) in new[]
        {
            ("-33", "-32"), ("-48", "-64"), ("-49", "-64"), ("-16", "-32"),
            ("-15", "0"), ("16", "0"), ("17", "32"), ("0", "0"),
        }) Expect(Integer(-128, 128), input, expected);

        // Rounding must stay inside the range even when its endpoints are not multiples.
        Expect(Integer(33, 95), "33", "64");
        Expect(Integer(33, 95), "95", "64");
        Expect(Integer(-95, -33), "-95", "-64");
        Expect(Integer(-95, -33), "-33", "-64");
        Expect(Integer(31.5, 32.5), "33", "32");
        Expect(Integer(-32.5, -31.5), "-33", "-32");
        Expect(Integer(32, 32), "0", "32");
        Expect(Integer(min: 33), "0", "64");
        Expect(Integer(max: -33), "0", "-64");
        Expect(Integer(-20, 20, 3), "-5", "-6");
        Expect(Integer(-20, 20, 3), "5", "6");
        Expect(Integer(-20, 20, 1), "99", "20");

        Expect(Integer(int.MinValue, int.MaxValue), "2147483647", "2147483616");
        Expect(Integer(int.MinValue, int.MaxValue), "-2147483648", "-2147483648");
        Expect(Integer(), "9223372036854775807", "9223372036854775808");
        Expect(Integer(multiple: 3), "-9223372036854775808", "-9223372036854775809");
        Expect(Integer(int.MinValue, int.MaxValue, int.MaxValue), "-2147483648", "-2147483647");
        Expect(Integer(multiple: int.MaxValue), "1073741824", "2147483647");
        var huge = new BigInteger(double.MaxValue).ToString(CultureInfo.InvariantCulture);
        Expect(Integer(-double.MaxValue, double.MaxValue), huge, huge);
        Expect(Integer(-double.MaxValue, double.MaxValue), "-" + huge, "-" + huge);

        // Independent exhaustive oracle: minimizes distance, then chooses the smaller value.
        for (var multiple = 1; multiple <= 9; multiple++)
        for (var min = -12; min <= 12; min += 4)
        for (var max = min; max <= 12; max += 3)
        {
            var legal = Enumerable.Range(min, max - min + 1).Where(value => value % multiple == 0).ToArray();
            if (legal.Length == 0) continue;
            for (var input = -20; input <= 20; input++)
            {
                var expected = legal.OrderBy(value => Math.Abs(value - input)).ThenBy(value => value).First();
                Expect(Integer(min, max, multiple), input.ToString(CultureInfo.InvariantCulture),
                    expected.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static void RunInvalidAndSchema()
    {
        foreach (var input in new[] { "bad", "1.5", "8192.0", "NaN", "Infinity", "-Infinity", "1e3", "8,192", "0x20", "--1", " ", "\t" })
            ExpectError(Budget, input);
        foreach (var spec in new[]
        {
            Integer(multiple: 0), Integer(multiple: -32), Integer(multiple: int.MinValue),
            Integer() with { Type = ParamType.Decimal }, Integer() with { Type = ParamType.Text },
            Integer(100, 32), Integer(33, 63), Integer(-63, -33), Integer(0.1, 0.9),
            Integer(double.NaN, 100), Integer(0, double.NaN),
            Integer(double.NegativeInfinity, 100), Integer(0, double.PositiveInfinity),
        })
        {
            ExpectError(spec, "8193");
            ExpectError(spec, ""); // Bad schema must not be concealed by an empty/default field.
        }
        var originalCulture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
            Expect(Budget, "8210", "8224");
            ExpectError(Budget, "8192,5");
        }
        finally { CultureInfo.CurrentCulture = originalCulture; }
    }

    private static void RunDefaultsAndUnmarked()
    {
        Expect(Budget, null, null);
        Expect(Budget, "", "");
        Expect(Budget, "+8192", "+8192");
        Expect(Budget, " 8192 ", " 8192 ");
        Expect(Budget, "008192", "008192");
        foreach (var input in new string?[] { null, "", "bad", "NaN", "1.5", "-1", "8193", "999999999999999999999" })
            Expect(Integer(32, 64) with { MultipleOf = null }, input, input);
        Expect(new ParameterSpec("text", "文本", "测试", ParamType.Text), "anything", "anything");
        Equal(2, KvmemParameters.Specs.Count(spec => spec.MultipleOf.HasValue));
        Equal<int?>(32, Budget.MultipleOf);
        Equal<int?>(32, Reserve.MultipleOf);
        foreach (var key in new[] { "promptBatch", "stageMib" })
        {
            var spec = KvmemParameters.Specs.Single(spec => spec.Key == key);
            Equal<int?>(null, spec.MultipleOf);
            Expect(spec, "129", "129");
            Expect(spec, "bad", "bad"); // The original backend validator, not the normalizer, rejects it.
        }
        var missing = KvmemParameters.NormalizeValues(null);
        Check(missing.IsValid && missing.Values.Count == 0 && missing.Adjustments.Count == 0, "missing defaults were materialized");
        var empty = KvmemParameters.NormalizeValues(new Dictionary<string, string>
            { ["kvmem"] = "", ["kvmemBudget"] = "", ["kvmemGenReserve"] = "" });
        Check(empty.IsValid && empty.Adjustments.Count == 0, "empty default values were normalized");
        Equal("", empty.Values["kvmemBudget"]);
        Equal("", empty.Values["kvmemGenReserve"]);
        var config = new ManagerConfig();
        var before = JsonSerializer.Serialize(config);
        var args = Append(config);
        Equal("8192", Argument(args, "--kvmem-budget"));
        Equal("512", Argument(args, "--kvmem-gen-reserve"));
        Equal(before, JsonSerializer.Serialize(config));
        config.Engines["kvmem"] = new EngineSettings { Parameters = empty.Values };
        args = Append(config);
        Equal("8192", Argument(args, "--kvmem-budget"));
        Equal("512", Argument(args, "--kvmem-gen-reserve"));
        Equal("", config.Engines["kvmem"].Parameters["kvmemBudget"]);
    }

    private static void RunCopyAndNativeArguments()
    {
        var input = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["kvmemBudget"] = "8210", ["kvmemGenReserve"] = "511", ["promptBatch"] = "129",
            ["stageMib"] = "131", ["context"] = "65536", ["unrelated"] = "keep me",
        };
        var original = JsonSerializer.Serialize(input);
        var result = KvmemParameters.NormalizeValues(input);
        Check(result.IsValid && !ReferenceEquals(input, result.Values), "normalization must return a separate copy");
        Equal(2, result.Adjustments.Count);
        Equal("8224", result.Values["kvmemBudget"]);
        Equal("512", result.Values["kvmemGenReserve"]);
        Equal("129", result.Values["promptBatch"]);
        Equal("131", result.Values["stageMib"]);
        Equal("65536", result.Values["context"]);
        Equal("keep me", result.Values["unrelated"]);
        Equal("8224", result.Values["KVMEMBUDGET"]); // Keep a Dictionary caller's comparer.
        foreach (var adjustment in result.Adjustments)
        {
            Equal(input[adjustment.Key], adjustment.OriginalValue);
            Equal(result.Values[adjustment.Key], adjustment.NormalizedValue);
            Check(!string.IsNullOrWhiteSpace(adjustment.Message), "adjustment has no display/log message");
        }
        Equal(original, JsonSerializer.Serialize(input));
        result.Values["unrelated"] = "modified copy";
        Equal("keep me", input["unrelated"]);
        var again = KvmemParameters.NormalizeValues(result.Values);
        Check(again.IsValid && again.Adjustments.Count == 0, "normalization is not idempotent");

        var config = new ManagerConfig();
        config.Engines["kvmem"] = new EngineSettings { Parameters = input };
        var before = JsonSerializer.Serialize(config);
        var native = Append(config);
        Equal("8224", Argument(native, "--kvmem-budget"));
        Equal("512", Argument(native, "--kvmem-gen-reserve"));
        Equal("129", Argument(native, "--prompt-batch"));
        Equal("131", Argument(native, "--stage-mib"));
        Equal(before, JsonSerializer.Serialize(config));

        input["kvmemBudget"] = "8193";
        input["kvmemGenReserve"] = "33";
        before = JsonSerializer.Serialize(config);
        Throws<ArgumentException>(() => Append(config)); // R -> 32, but prefill stays 129.
        Equal(before, JsonSerializer.Serialize(config));
        input["promptBatch"] = "32";
        Equal("32", Argument(Append(config), "--kvmem-gen-reserve"));
        before = JsonSerializer.Serialize(config);
        Throws<ArgumentException>(() => Append(config, 8192 + 32 - 1));
        Equal(before, JsonSerializer.Serialize(config));
        Equal("8192", Argument(Append(config, 8192 + 32), "--kvmem-budget"));

        input["kvmemBudget"] = "NaN";
        input["kvmemGenReserve"] = "1.5";
        result = KvmemParameters.NormalizeValues(input);
        Check(!result.IsValid && result.Errors.Count == 2 && result.Adjustments.Count == 0, "invalid values were silently replaced");
        Equal("NaN", result.Values["kvmemBudget"]);
        Equal("1.5", result.Values["kvmemGenReserve"]);
        Check(result.Errors.All(error => !string.IsNullOrWhiteSpace(error.Key) && !string.IsNullOrWhiteSpace(error.Error)), "errors lack field identity/message");
        before = JsonSerializer.Serialize(config);
        Throws<ArgumentException>(() => Append(config));
        Equal(before, JsonSerializer.Serialize(config));

        input["kvmemGenReserve"] = "511";
        result = KvmemParameters.NormalizeValues(input);
        Check(!result.IsValid && result.Errors.Count == 1 && result.Adjustments.Count == 1, "mixed results were lost");
        Equal("NaN", result.Values["kvmemBudget"]);
        Equal("512", result.Values["kvmemGenReserve"]);
        Equal("511", input["kvmemGenReserve"]);
        input["kvmem"] = "0";
        before = JsonSerializer.Serialize(config);
        result = KvmemParameters.NormalizeValues(input);
        Check(result.IsValid && result.Errors.Count == 0 && result.Adjustments.Count == 0, "disabled KVMem normalized/validated stale values");
        Equal(JsonSerializer.Serialize(input), JsonSerializer.Serialize(result.Values));
        Check(!ReferenceEquals(input, result.Values), "disabled normalization returned the original dictionary");
        native = Append(config);
        Check(!native.Any(arg => arg.StartsWith("--kvmem", StringComparison.Ordinal)), "disabled KVMem emitted native paging flags");
        Equal(before, JsonSerializer.Serialize(config));

        // Unmarked fields still use the old integer/range validator; they are never clamped.
        foreach (var (key, value) in new[] { ("promptBatch", "0"), ("promptBatch", "bad"), ("stageMib", "8193") })
        {
            var old = input[key];
            input[key] = value;
            Throws<ArgumentException>(() => Append(config));
            Equal(value, input[key]);
            input[key] = old;
        }
    }

    private static void Expect(ParameterSpec spec, string? input, string? expected)
    {
        var result = ParameterNormalizer.Normalize(spec, input);
        Check(result.IsValid && result.Error is null, $"normalization failed for {input}: {result.Error}");
        Equal(expected, result.NormalizedValue);
        Equal(input != expected, result.Changed);
        if (result.Changed)
        {
            Check(!string.IsNullOrWhiteSpace(result.Message), "changed input has no user-facing explanation");
            var again = ParameterNormalizer.Normalize(spec, result.NormalizedValue);
            Check(again.IsValid && !again.Changed && again.NormalizedValue == result.NormalizedValue, "field normalization is not idempotent");
        }
        else Check(result.Message is null, "unchanged input has a spurious adjustment message");
    }

    private static void ExpectError(ParameterSpec spec, string input)
    {
        var result = ParameterNormalizer.Normalize(spec, input);
        Check(!result.IsValid && !string.IsNullOrWhiteSpace(result.Error), "invalid input/schema lacks an error: " + input);
        Equal(input, result.NormalizedValue);
        Check(!result.Changed && result.Message is null, "invalid input was changed or reported as adjusted");
    }

    private static List<string> Append(ManagerConfig config, int context = 65536)
    {
        var args = new List<string>();
        KvmemParameters.Append(config, "kvmem", context, args);
        return args;
    }

    private static string Argument(List<string> args, string flag)
    {
        Equal(1, args.Count(value => value == flag));
        return args[args.IndexOf(flag) + 1];
    }
}
