using System.Globalization;
using LlamaManager.Services;

namespace LlamaManager.Engines;

/// <summary>
/// What <see cref="ParameterVisibility.Evaluate"/> may look at: which backend profile is
/// selected, the metadata of the chosen model (if any), and the parameters' current values.
/// </summary>
public sealed record ParameterContext(
    string ProfileId,
    GgufMetadata? Model,
    IReadOnlyDictionary<string, string> Values)
{
    public static ParameterContext For(string profileId) =>
        new(profileId, null, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));
}

/// <summary>
/// Evaluates the tiny boolean expressions that <see cref="ParameterSpec.VisibleWhen"/> carries.
/// </summary>
/// <remarks>
/// The grammar is deliberately small — no scripting engine, no reflection:
/// <list type="bullet">
/// <item><c>true</c> / <c>false</c> / empty</item>
/// <item>variables: <c>profile</c>, <c>model.moe</c>, <c>model.mtp</c>, <c>param.&lt;key&gt;</c></item>
/// <item>comparisons: <c>==</c> <c>!=</c> <c>&gt;</c> <c>&gt;=</c> <c>&lt;</c> <c>&lt;=</c></item>
/// <item>combining: <c>&amp;&amp;</c>, <c>||</c>, <c>!</c>, and parentheses</item>
/// </list>
/// An unknown variable or a malformed expression hides the parameter (false) and records one
/// log line, because a broken spec must never take the settings panel — or the UI thread — down.
/// </remarks>
public static class ParameterVisibility
{
    private static readonly object Gate = new();
    private static readonly HashSet<string> ReportedUnknown = new(StringComparer.Ordinal);

    /// <summary>True when the parameter should be shown. Never throws.</summary>
    public static bool Evaluate(string? expr, ParameterContext context)
    {
        if (string.IsNullOrWhiteSpace(expr)) return true;
        var text = expr.Trim();
        if (text.Equals("true", StringComparison.OrdinalIgnoreCase)) return true;
        if (text.Equals("false", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            var index = 0;
            var value = ParseOr(text, ref index, context);
            SkipSpace(text, ref index);
            if (index != text.Length) throw new FormatException($"unexpected '{text[index..]}'");
            return value;
        }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            Report($"表达式 \"{expr}\" 无法求值（{ex.Message}），该参数按隐藏处理");
            return false;
        }
    }

    private static bool ParseOr(string text, ref int index, ParameterContext context)
    {
        var value = ParseAnd(text, ref index, context);
        while (true)
        {
            SkipSpace(text, ref index);
            if (!Peek(text, index, "||")) return value;
            index += 2;
            // Both sides are always evaluated: these expressions are cheap and side-effect free,
            // and short-circuiting would hide unknown-variable reports from the user's log.
            var right = ParseAnd(text, ref index, context);
            value = value || right;
        }
    }

    private static bool ParseAnd(string text, ref int index, ParameterContext context)
    {
        var value = ParseUnary(text, ref index, context);
        while (true)
        {
            SkipSpace(text, ref index);
            if (!Peek(text, index, "&&")) return value;
            index += 2;
            var right = ParseUnary(text, ref index, context);
            value = value && right;
        }
    }

    private static bool ParseUnary(string text, ref int index, ParameterContext context)
    {
        SkipSpace(text, ref index);
        if (Peek(text, index, "!"))
        {
            index++;
            return !ParseUnary(text, ref index, context);
        }
        return ParsePrimary(text, ref index, context);
    }

    private static bool ParsePrimary(string text, ref int index, ParameterContext context)
    {
        SkipSpace(text, ref index);
        if (index < text.Length && text[index] == '(')
        {
            index++;
            var inner = ParseOr(text, ref index, context);
            SkipSpace(text, ref index);
            Expect(text, ref index, ')');
            return inner;
        }

        var left = ReadOperand(text, ref index, context, allowLiteral: false)
            ?? throw new FormatException("缺少操作数");

        SkipSpace(text, ref index);
        var op = ReadOperator(text, ref index);
        if (op == null) return Truthy(left.Value);

        // On the right of a comparison a bare word is a literal, so "profile==upstream"
        // compares against the text "upstream" instead of looking for a variable of that name.
        var right = ReadOperand(text, ref index, context, allowLiteral: true)
            ?? throw new FormatException($"运算符 \"{op}\" 右侧缺少操作数");
        return Compare(left, right, op);
    }

    private sealed record Operand(string Value, bool IsNumeric, double Number);

    private static Operand? ReadOperand(string text, ref int index, ParameterContext context, bool allowLiteral)
    {
        SkipSpace(text, ref index);
        if (index >= text.Length) return null;

        if (text[index] == '"' || text[index] == '\'')
        {
            var quote = text[index++];
            var start = index;
            while (index < text.Length && text[index] != quote) index++;
            if (index >= text.Length) throw new FormatException("字符串缺少结尾引号");
            var literal = text[start..index];
            index++;
            return MakeOperand(literal);
        }

        var begin = index;
        while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] is '.' or '_' or '-'))
            index++;
        if (begin == index) return null;

        var name = text[begin..index];
        if (name.Equals("true", StringComparison.OrdinalIgnoreCase)) return MakeOperand("1");
        if (name.Equals("false", StringComparison.OrdinalIgnoreCase)) return MakeOperand("0");
        // A bare number is a literal, not a variable name.
        if (double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            return MakeOperand(name);

        if (TryResolve(name, context, out var known)) return MakeOperand(known);
        // "profile==upstream" must compare against the word "upstream" itself; only the
        // left-hand side of a comparison is required to be a known variable.
        if (allowLiteral) return MakeOperand(name);
        Report($"未知的可见性变量 \"{name}\"，该参数按隐藏处理");
        return null;
    }

    /// <summary>
    /// Resolves one variable from the fixed vocabulary. Reports nothing: a word that is merely
    /// an unknown variable name and a word that is a comparison literal are the same string here.
    /// </summary>
    private static bool TryResolve(string name, ParameterContext context, out string value)
    {
        if (name.Equals("profile", StringComparison.OrdinalIgnoreCase)) { value = context.ProfileId; return true; }
        if (name.Equals("model.moe", StringComparison.OrdinalIgnoreCase)) { value = context.Model?.IsMoE == true ? "1" : "0"; return true; }
        if (name.Equals("model.mtp", StringComparison.OrdinalIgnoreCase)) { value = context.Model?.HasMtp == true ? "1" : "0"; return true; }
        if (name.StartsWith("param.", StringComparison.OrdinalIgnoreCase))
        {
            var key = name[6..];
            // A parameter the panel has not filled in yet behaves like an unset number, so
            // "param.expertSlots>0" stays false instead of comparing against an empty string.
            value = context.Values.TryGetValue(key, out var current) ? current ?? "" : "0";
            return true;
        }

        value = "";
        return false;
    }

    private static Operand MakeOperand(string value)
    {
        var numeric = double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var number);
        return new Operand(value, numeric, numeric ? number : 0);
    }

    private static string? ReadOperator(string text, ref int index)
    {
        foreach (var op in new[] { ">=", "<=", "==", "!=", ">", "<" })
        {
            if (!Peek(text, index, op)) continue;
            index += op.Length;
            return op;
        }
        // A single '=' is a common slip when writing a spec; accept it as equality.
        if (Peek(text, index, "=")) { index++; return "=="; }
        return null;
    }

    private static bool Compare(Operand left, Operand right, string op)
    {
        int order;
        if (left.IsNumeric && right.IsNumeric)
            order = left.Number.CompareTo(right.Number);
        else
            order = string.Compare(left.Value, right.Value, StringComparison.OrdinalIgnoreCase);

        return op switch
        {
            "==" => order == 0,
            "!=" => order != 0,
            ">" => order > 0,
            ">=" => order >= 0,
            "<" => order < 0,
            "<=" => order <= 0,
            _ => false,
        };
    }

    private static bool Truthy(string value) =>
        !string.IsNullOrWhiteSpace(value)
        && !value.Equals("0", StringComparison.OrdinalIgnoreCase)
        && !value.Equals("false", StringComparison.OrdinalIgnoreCase);

    private static bool Peek(string text, int index, string token) =>
        index + token.Length <= text.Length
        && string.CompareOrdinal(text, index, token, 0, token.Length) == 0;

    private static void Expect(string text, ref int index, char expected)
    {
        if (index >= text.Length || text[index] != expected)
            throw new FormatException($"缺少 '{expected}'");
        index++;
    }

    private static void SkipSpace(string text, ref int index)
    {
        while (index < text.Length && char.IsWhiteSpace(text[index])) index++;
    }

    private static void Report(string message)
    {
        lock (Gate)
        {
            if (!ReportedUnknown.Add(message)) return;
        }
        LogService.Write($"[参数可见性] {message}");
    }
}
