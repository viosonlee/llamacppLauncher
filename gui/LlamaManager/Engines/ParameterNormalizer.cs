using System.Globalization;
using System.Numerics;

namespace LlamaManager.Engines;

/// <summary>A pure field result. Invalid input is retained verbatim, never replaced with zero.</summary>
public sealed record ParameterNormalizationResult(
    string? NormalizedValue,
    bool Changed,
    string? Message = null,
    string? Error = null)
{
    public bool IsValid => Error is null;
}

/// <summary>An explicit adjustment callers can display, log and choose to persist.</summary>
public sealed record ParameterAdjustment(string Key, string OriginalValue, string NormalizedValue, string Message);

public sealed record ParameterNormalizationError(string Key, string Error);

/// <summary>Values is an independent copy; this result never writes back to the input/configuration.</summary>
public sealed record ParameterValuesNormalizationResult(
    Dictionary<string, string> Values,
    IReadOnlyList<ParameterAdjustment> Adjustments,
    IReadOnlyList<ParameterNormalizationError> Errors)
{
    public bool IsValid => Errors.Count == 0;
}

/// <summary>
/// Normalizes only explicitly declared integer multiples. Unmarked fields are left to their
/// existing validators. Null/empty values stay empty so the caller's default semantics survive.
/// No configuration access, persistence, logging or other side effects occur here.
/// </summary>
public static class ParameterNormalizer
{
    public static ParameterNormalizationResult Normalize(ParameterSpec spec, string? value)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ParameterNormalizationResult Invalid(string error) => new(value, false, Error: error);
        if (spec.MultipleOf is not { } multiple) return new(value, false);
        if (spec.Type != ParamType.Integer || multiple <= 0)
            return Invalid($"{spec.Label} 参数定义错误：MultipleOf 必须是正整数，且只能用于 Integer 参数。");
        if ((spec.Min is { } min && !double.IsFinite(min)) ||
            (spec.Max is { } max && !double.IsFinite(max)) ||
            (spec.Min.HasValue && spec.Max.HasValue && spec.Min.Value > spec.Max.Value))
            return Invalid($"{spec.Label} 参数定义错误：最小值/最大值必须有限且顺序正确。");

        // Work in multiplier space with arbitrary precision, including the input. Neither
        // int/long extremes nor finite double bounds can overflow intermediate arithmetic.
        BigInteger? first = spec.Min is { } minimum
            ? -FloorDivide(-new BigInteger(Math.Ceiling(minimum)), multiple) : null;
        BigInteger? last = spec.Max is { } maximum
            ? FloorDivide(new BigInteger(Math.Floor(maximum)), multiple) : null;
        if (first.HasValue && last.HasValue && first.Value > last.Value)
            return Invalid($"{spec.Label} 参数定义错误：范围内没有 {multiple} 的整数倍数。");
        if (string.IsNullOrEmpty(value)) return new(value, false);
        if (!BigInteger.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
            return Invalid($"{spec.Label} 必须为整数；无效输入已保留，请勿输入小数、NaN 或 Infinity。");

        var lower = FloorDivide(number, multiple);
        var remainder = number - lower * multiple; // Always 0 <= remainder < multiple.
        var nearest = remainder * 2 <= multiple ? lower : lower + 1; // Ties choose the smaller integer.
        if (first.HasValue && nearest < first.Value) nearest = first.Value;
        if (last.HasValue && nearest > last.Value) nearest = last.Value;
        var normalized = nearest * multiple;
        if (normalized == number) return new(value, false);

        var text = normalized.ToString(CultureInfo.InvariantCulture);
        return new(text, true, $"{spec.Label}：{value} → {text}（自动调整为范围内最近的 {multiple} 倍数，等距取较小值）。");
    }

    private static BigInteger FloorDivide(BigInteger value, int divisor)
    {
        var quotient = BigInteger.DivRem(value, divisor, out var remainder);
        return remainder.Sign < 0 ? quotient - 1 : quotient;
    }
}
