using LlamaManager.Engines;
using LlamaManager.Services;
using static Program;

/// <summary>
/// Covers the <see cref="ParameterVisibility"/> grammar that <see cref="ParameterSpec.VisibleWhen"/>
/// uses. The evaluator must never throw into the UI thread: a malformed or unknown condition hides
/// the parameter and moves on.
/// </summary>
internal static class ParameterVisibilityRegression
{
    public static void Run()
    {
        var empty = ParameterContext.For("poly");

        // Absent and literal conditions are always visible; this keeps schema:1 plugins working.
        Check(ParameterVisibility.Evaluate(null, empty), "null condition must be visible");
        Check(ParameterVisibility.Evaluate("", empty), "empty condition must be visible");
        Check(ParameterVisibility.Evaluate("   ", empty), "whitespace condition must be visible");
        Check(ParameterVisibility.Evaluate("true", empty), "'true' must be visible");
        Check(!ParameterVisibility.Evaluate("false", empty), "'false' must be hidden");

        // Profile identity drives the per-engine split.
        Check(ParameterVisibility.Evaluate("profile==poly", empty), "profile==poly on poly");
        Check(!ParameterVisibility.Evaluate("profile==poly", ParameterContext.For("llamacpp")),
            "profile==poly must be false for llama.cpp");
        Check(ParameterVisibility.Evaluate("profile==llamacpp", ParameterContext.For("llamacpp")), "profile==llamacpp on llama.cpp");
        Check(ParameterVisibility.Evaluate("profile != poly", ParameterContext.For("llamacpp")),
            "whitespace around the operator is optional");
        Check(ParameterVisibility.Evaluate("profile = poly", ParameterContext.For("poly")),
            "a single '=' is accepted as equality");

        // Model metadata comes from the GGUF header, so a missing model is simply "not MoE/MTP".
        var moe = new GgufMetadata { IsMoE = true, HasMtp = true };
        Check(ParameterVisibility.Evaluate("model.moe", new ParameterContext("llamacpp", moe, Values())),
            "model.moe is true for a MoE model");
        Check(!ParameterVisibility.Evaluate("model.moe", empty), "model.moe is false without a model");
        Check(ParameterVisibility.Evaluate("model.mtp", new ParameterContext("llamacpp", moe, Values())),
            "model.mtp is true for a model with an MTP head");
        Check(!ParameterVisibility.Evaluate("model.mtp", new ParameterContext("llamacpp", new GgufMetadata(), Values())),
            "model.mtp is false for a model without an MTP head");

        // Parameter values are compared numerically whenever both sides look like numbers.
        var slots = Values(("tailSkip", "6"));
        Check(ParameterVisibility.Evaluate("param.tailSkip>0", new ParameterContext("poly", null, slots)),
            "a positive tail-skip is visible");
        Check(!ParameterVisibility.Evaluate("param.tailSkip>0", new ParameterContext("poly", null, Values(("tailSkip", "0")))),
            "zero tail-skip hides the dependent field");
        Check(!ParameterVisibility.Evaluate("param.tailSkip>0", new ParameterContext("poly", null, Values())),
            "an unset parameter behaves like zero");
        Check(ParameterVisibility.Evaluate("param.context>=4096", new ParameterContext("poly", null, Values(("context", "8192")))),
            "numeric comparison must not compare as strings");
        Check(!ParameterVisibility.Evaluate("param.context>=4096", new ParameterContext("poly", null, Values(("context", "512")))),
            "512 is below 4096");

        // Boolean structure.
        Check(ParameterVisibility.Evaluate("profile==poly && param.tailSkip>0",
            new ParameterContext("poly", null, slots)), "&& combines two true terms");
        Check(!ParameterVisibility.Evaluate("profile==llamacpp && param.tailSkip>0",
            new ParameterContext("poly", null, slots)), "&& is false when one term is false");
        Check(ParameterVisibility.Evaluate("profile==poly || param.tailSkip>0",
            new ParameterContext("poly", null, slots)), "|| is true when one term is true");
        Check(ParameterVisibility.Evaluate("!(profile==poly)", ParameterContext.For("llamacpp")), "! negates");
        Check(ParameterVisibility.Evaluate("(profile==poly || profile==llamacpp) && true", empty),
            "parentheses group terms");

        // A broken spec must hide the field, never throw.
        Check(!ParameterVisibility.Evaluate("profile==", empty), "a trailing operator must not throw");
        Check(!ParameterVisibility.Evaluate("(profile==poly", empty), "an unbalanced parenthesis must not throw");
        Check(!ParameterVisibility.Evaluate("unknown.variable", empty), "an unknown variable must not throw");
        Check(!ParameterVisibility.Evaluate("!!", empty), "a bare negation must not throw");
    }

    private static Dictionary<string, string> Values(params (string Key, string Value)[] pairs)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in pairs) values[key] = value;
        return values;
    }
}
