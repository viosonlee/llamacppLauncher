using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace LlamaManager.Services;

public sealed class IncrementalLogReader
{
    private long _position;
    private DateTime _modified;
    private byte[] _prefix = [];
    private readonly Queue<string> _lines = new();
    private Decoder _decoder = Encoding.UTF8.GetDecoder();
    private string _partial = "";
    public long BytesRead { get; private set; }

    public IReadOnlyList<string> Read(string path, int capacity = 500)
    {
        if (!File.Exists(path)) { Reset(); return []; }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var modified = File.GetLastWriteTimeUtc(path);
        var prefix = new byte[Math.Min(64, stream.Length)];
        stream.ReadExactly(prefix);
        var changedPrefix = _prefix.Length > 0 && !prefix.Take(_prefix.Length).SequenceEqual(_prefix);
        if (stream.Length < _position || changedPrefix || (stream.Length == _position && modified != _modified)) Reset();
        _prefix = prefix;
        _modified = modified;
        if (stream.Length == _position) return _lines.ToArray();
        const int maxBytes = 1024 * 1024;
        var skipped = stream.Length - _position > maxBytes;
        if (skipped)
        {
            Reset();
            _position = stream.Length - maxBytes;
            _prefix = prefix;
            _modified = modified;
        }
        stream.Position = _position;
        var data = new byte[(int)Math.Min(maxBytes, stream.Length - _position)];
        var length = stream.Read(data);
        _position += length;
        BytesRead += length;
        var chars = new char[Encoding.UTF8.GetMaxCharCount(length)];
        var count = _decoder.GetChars(data, 0, length, chars, 0, false);
        var text = _partial + new string(chars, 0, count);
        var pieces = text.Split('\n');
        _partial = pieces[^1];
        if (_partial.Length > 65536) _partial = "[超长日志行已截断] " + _partial[^65536..];
        for (var i = skipped ? 1 : 0; i < pieces.Length - 1; i++)
        {
            var line = Regex.Replace(pieces[i].TrimEnd('\r'), @"\x1b\[[0-9;?]*[A-Za-z]", "");
            if (line.Length > 0) _lines.Enqueue(line);
        }
        while (_lines.Count > capacity) _lines.Dequeue();
        return _lines.ToArray();
    }

    private void Reset()
    {
        _position = 0; _partial = ""; _prefix = []; _lines.Clear(); _decoder = Encoding.UTF8.GetDecoder();
    }
}
