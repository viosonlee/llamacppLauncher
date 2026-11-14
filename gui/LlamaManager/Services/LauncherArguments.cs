using System.Globalization;
using System.Text;

namespace LlamaManager.Services;

/// <summary>Windows command-line tokens with source spans, so editing one option preserves all others.</summary>
public sealed class LauncherArguments
{
    private sealed record Token(string Value, int Start, int End, bool Quoted);
    private static readonly string[][] Aliases =
    [
        ["-m", "--model"], ["--alias", "-a"], ["--port"], ["--host"],
        ["-c", "--ctx-size"], ["-ngl", "--gpu-layers", "--n-gpu-layers"],
        ["-t", "--threads"], ["-tb", "--threads-batch"],
        ["-b", "--batch-size"], ["-ub", "--ubatch-size"],
        ["-ctk", "--cache-type-k"], ["-ctv", "--cache-type-v"],
        ["-fa", "--flash-attn"], ["--reasoning"], ["--reasoning-budget"],
        ["--reasoning-format"], ["--n-cpu-moe", "-ncmoe"], ["--cpu-moe", "-cmoe"],
        ["-v", "--verbose", "--log-verbose"], ["--fit", "-fit"],
        ["--fit-target", "-fitt"], ["--embedding", "--embeddings"],
        ["-p", "--prompt"], ["-f", "--file"], ["--system-prompt"],
        ["--log-file"], ["--device", "-dev"], ["-np", "--parallel"],
        ["-md", "--model-draft", "--spec-draft-model"], ["--spec-type"],
        ["--mmproj", "-mm"], ["--split-mode", "-sm"]
    ];
    private static readonly HashSet<string> Flags = ["--cpu-moe", "-v", "--embedding"];
    private readonly string _source;
    private readonly List<Token> _tokens;

    public LauncherArguments(string? source)
    {
        _source = source ?? "";
        _tokens = Tokenize(_source);
    }

    public static string Canonical(string key) => Aliases.FirstOrDefault(a => a.Contains(key))?[0] ?? key;
    public IReadOnlyList<string> Values => _tokens.Select(t => t.Value).ToArray();
    public bool Has(string key) => Find(key).Count > 0;

    public string? Get(string key)
    {
        var matches = Find(key);
        if (matches.Count == 0) return null;
        var (index, end) = matches[^1];
        var token = _tokens[index].Value;
        var equals = token.IndexOf('=');
        return equals >= 0 ? token[(equals + 1)..] : end > index ? _tokens[end].Value : null;
    }

    public int GetPort(int fallback = 8080)
    {
        var value = Get("--port");
        if (value == null && !Has("--port")) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ArgumentException("端口必须为 1–65535 的整数。");
        return port;
    }

    public string Set(string key, string? value) => Edit(key, value == null ? null : $"{Canonical(key)} {Quote(value, value.StartsWith('-'))}");
    public string SetFlag(string key, bool enabled) => Edit(key, enabled ? Canonical(key) : null);

    private string Edit(string key, string? replacement)
    {
        var matches = Find(key);
        if (matches.Count == 0) return replacement == null ? _source : (_source.TrimEnd() + " " + replacement).TrimStart();
        var result = _source;
        for (var n = matches.Count - 1; n >= 0; n--)
        {
            var (first, last) = matches[n];
            result = result.Remove(_tokens[first].Start, _tokens[last].End - _tokens[first].Start)
                .Insert(_tokens[first].Start, n == 0 ? replacement ?? "" : "");
        }
        return result.Trim();
    }

    private List<(int First, int Last)> Find(string key)
    {
        var wanted = Canonical(key);
        List<(int, int)> matches = [];
        for (var i = 0; i < _tokens.Count; i++)
        {
            var token = _tokens[i];
            if (!token.Value.StartsWith('-')) continue;
            var equals = token.Value.IndexOf('=');
            var option = Canonical(equals < 0 ? token.Value : token.Value[..equals]);
            var last = i;
            if (equals < 0 && !Flags.Contains(option) && i + 1 < _tokens.Count)
            {
                var next = _tokens[i + 1];
                if (next.Quoted || !next.Value.StartsWith('-') || double.TryParse(next.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                    last = i + 1;
            }
            if (option == wanted) matches.Add((i, last));
            i = last;
        }
        return matches;
    }

    public static string Quote(string value) => Quote(value, false);

    private static string Quote(string value, bool force)
    {
        if (!force && value.Length > 0 && !value.Any(c => char.IsWhiteSpace(c) || c == '"')) return value;
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            result.Append(c);
            slashes = 0;
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    private static List<Token> Tokenize(string text)
    {
        List<Token> tokens = [];
        for (var i = 0; i < text.Length;)
        {
            if (char.IsWhiteSpace(text[i])) { i++; continue; }
            var start = i;
            var quoted = text[i] == '"';
            var inside = false;
            var value = new StringBuilder();
            while (i < text.Length && (inside || !char.IsWhiteSpace(text[i])))
            {
                var slashes = 0;
                while (i < text.Length && text[i] == '\\') { slashes++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    value.Append('\\', slashes / 2);
                    if (slashes % 2 != 0) value.Append('"');
                    else if (inside && i + 1 < text.Length && text[i + 1] == '"') { value.Append('"'); i++; }
                    else inside = !inside;
                    i++;
                }
                else
                {
                    value.Append('\\', slashes);
                    if (i < text.Length && (inside || !char.IsWhiteSpace(text[i]))) value.Append(text[i++]);
                }
            }
            if (inside) throw new ArgumentException("启动参数中存在未闭合的双引号。");
            tokens.Add(new Token(value.ToString(), start, i, quoted));
        }
        return tokens;
    }
}
