using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using LlamaManager.Services;
using static Program;

internal static class LogCacheRegression
{
    private static readonly object Gate = typeof(LogService).GetField("Gate", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static IDictionary Cache => (IDictionary)typeof(LogService).GetField("Readers", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
    private static void Clear() { lock (Gate) Cache.Clear(); } // Isolated runner process only; no production hooks.
    private static Dictionary<string, object> Snapshot()
    {
        lock (Gate)
        {
            var result = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var iterator = Cache.GetEnumerator();
            while (iterator.MoveNext()) result[(string)iterator.Key] = iterator.Value!;
            return result;
        }
    }
    private static IncrementalLogReader Reader(string path)
    {
        var entry = Snapshot()[Path.GetFullPath(path)];
        return (IncrementalLogReader)entry.GetType().GetProperty("Reader")!.GetValue(entry)!;
    }
    private static string Work(string root, string name)
    {
        var work = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work); return work;
    }
    private static string[] Lines(int count) => Enumerable.Range(1, count).Select(n => $"line-{n:D4}").ToArray();
    private static void Write(string path, IEnumerable<string> lines) => File.WriteAllText(path, string.Join('\n', lines) + "\n", new UTF8Encoding(false));
    private static void EqualLines(IEnumerable<string> expected, IEnumerable<string> actual, string operation)
    {
        var a = expected.ToArray(); var b = actual.ToArray();
        Check(a.SequenceEqual(b), $"{operation}: expected {a.Length} correct lines, got {b.Length}");
    }

    public static void RunCounts(string root)
    {
        Clear();
        var work = Work(root, "log-counts");
        var path = Path.Combine(work, "counts.log");
        var lines = Lines(650); Write(path, lines);
        EqualLines(lines.TakeLast(600), LogService.ReadFileTail(path, 600), "request >500");
        EqualLines(lines.TakeLast(8), LogService.ReadFileTail(path, 8), "small consumer");
        EqualLines(lines, LogService.ReadFileTail(path, 650), "capacity growth on unchanged file");
        Clear();
        foreach (var count in new[] { 12, 100, 500, 600, 8, 650 })
            EqualLines(lines.TakeLast(count), LogService.ReadFileTail(path, count), $"mixed count {count}");
        // Small UI/error consumers share history with later 500-line GPU diagnostics.
        Clear(); Write(path, Lines(500));
        foreach (var count in new[] { 100, 500, 12, 500, 125, 500 })
            EqualLines(Lines(500).TakeLast(count), LogService.ReadFileTail(path, count), $"shared consumer {count}");
        var retained = Snapshot()[Path.GetFullPath(path)];
        var large = Path.Combine(work, "large-request.log"); Write(large, Lines(7000));
        EqualLines(Lines(7000).TakeLast(6000), LogService.ReadFileTail(large, 6000), "large uncached request must not clamp result count");
        Equal(1, Snapshot().Count);
        Check(ReferenceEquals(retained, Snapshot()[Path.GetFullPath(path)]), "large uncached request changed another reader");
        EqualLines(Lines(500), LogService.ReadFileTail(path), "large request damaged shared history");
    }

    public static void RunCachePolicy(string root)
    {
        Clear();
        var work = Work(root, "log-cache-policy");
        var alias = Path.Combine(work, "alias.log"); Write(alias, ["same reader"]);
        var forms = new[] { alias, Path.GetRelativePath(Environment.CurrentDirectory, alias), Path.Combine(work, ".", "alias.log"),
            Path.Combine(work, "unused", "..", "alias.log"), alias.Replace('\\', '/'), alias.ToUpperInvariant() };
        foreach (var form in forms) EqualLines(new[] { "same reader" }, LogService.ReadFileTail(form), "path alias");
        Equal(1, Snapshot().Count);
        var firstReader = Reader(alias);
        foreach (var form in forms)
        {
            LogService.ReadFileTail(form);
            Check(ReferenceEquals(firstReader, Reader(alias)), "alias created a second incremental reader");
        }
        Clear();
        var paths = Enumerable.Range(0, 65).Select(i => Path.Combine(work, $"entry-{i}.log")).ToArray();
        for (var i = 0; i < paths.Length; i++) Write(paths[i], [$"entry-{i}"]);
        for (var i = 0; i < 64; i++) LogService.ReadFileTail(paths[i], 12);
        Equal(64, Snapshot().Count);
        var evicted = Snapshot()[paths[1]];
        LogService.ReadFileTail(paths[0]); // promote oldest
        LogService.ReadFileTail(paths[64]);
        var snapshot = Snapshot();
        Equal(64, snapshot.Count);
        Check(snapshot.ContainsKey(paths[0]) && !snapshot.ContainsKey(paths[1]), "LRU eviction ignored recent access");
        EqualLines(new[] { "entry-1" }, LogService.ReadFileTail(paths[1]), "evicted file rehydration");
        Check(!ReferenceEquals(evicted, Snapshot()[paths[1]]), "evicted reader was retained");
        Equal(64, Snapshot().Count);

        Clear();
        foreach (var invalid in new[] { "", "\0-invalid", Path.Combine(work, "missing.log") })
            Equal(0, LogService.ReadFileTail(invalid, 12).Count);
        Equal(0, LogService.ReadFileTail(null!, 12).Count);
        Equal(0, LogService.ReadFileTail(alias, 0).Count);
        Equal(0, LogService.ReadFileTail(alias, -1).Count);
        Equal(0, Snapshot().Count);
        LogService.ReadFileTail(alias);
        File.Delete(alias); // Unique fixture path, never a user log.
        Equal(0, LogService.ReadFileTail(alias).Count); Equal(0, Snapshot().Count);
        Write(alias, ["replacement"]);
        EqualLines(new[] { "replacement" }, LogService.ReadFileTail(alias), "delete/recreate");
        using (var locked = new FileStream(alias, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Equal(0, LogService.ReadFileTail(alias).Count); Equal(0, Snapshot().Count);
        }
        EqualLines(new[] { "replacement" }, LogService.ReadFileTail(alias), "locked file retry");
    }

    public static void RunIncremental(string root)
    {
        Clear(); var work = Work(root, "log-cache-streaming"); var path = Path.Combine(work, "unicode.log");
        Write(path, ["first"]);
        EqualLines(new[] { "first" }, LogService.ReadFileTail(path, 12), "initial log");
        var reader = Reader(path); var before = reader.BytesRead;
        LogService.ReadFileTail(path, 100); Equal(before, reader.BytesRead);
        var chinese = Encoding.UTF8.GetBytes("中文\n");
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(chinese[..2]);
        EqualLines(new[] { "first" }, LogService.ReadFileTail(path, 12), "partial UTF8");
        var afterPartial = reader.BytesRead;
        LogService.ReadFileTail(path, 100); Equal(afterPartial, reader.BytesRead);
        EqualLines(new[] { "first" }, LogService.ReadFileTail(path, 600), "promote while UTF8 incomplete");
        Check(!ReferenceEquals(reader, Reader(path)), "capacity growth did not backfill");
        using (var stream = new FileStream(path, FileMode.Append)) stream.Write(chinese[2..]);
        EqualLines(new[] { "first", "中文" }, LogService.ReadFileTail(path, 12), "UTF8 resumes after backfill");
        File.AppendAllText(path, "part", new UTF8Encoding(false));
        EqualLines(new[] { "first", "中文" }, LogService.ReadFileTail(path, 12), "incomplete line hidden");
        File.AppendAllText(path, "ial\r\n\x1b[32mcolored\x1b[0m\n", new UTF8Encoding(false));
        EqualLines(new[] { "first", "中文", "partial", "colored" }, LogService.ReadFileTail(path, 12), "partial CRLF and ANSI");
        File.WriteAllText(path, "short\n", new UTF8Encoding(false));
        EqualLines(new[] { "short" }, LogService.ReadFileTail(path), "truncation");

        var stamp = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        Write(path, ["AAAA"]); File.SetLastWriteTimeUtc(path, stamp); LogService.ReadFileTail(path);
        Write(path, ["BBBB"]); File.SetLastWriteTimeUtc(path, stamp);
        EqualLines(new[] { "BBBB" }, LogService.ReadFileTail(path), "same timestamp/length changed prefix");
        var prefix = new string('p', 64);
        Write(path, [prefix, "old"]); File.SetLastWriteTimeUtc(path, stamp); LogService.ReadFileTail(path);
        Write(path, [prefix, "new"]); File.SetLastWriteTimeUtc(path, stamp.AddSeconds(2));
        EqualLines(new[] { prefix, "new" }, LogService.ReadFileTail(path), "same prefix/length changed timestamp");
    }

    public static void RunByteBudget(string root)
    {
        Clear(); var work = Work(root, "log-cache-budget"); var path = Path.Combine(work, "large.log");
        Write(path, Enumerable.Range(0, 80000).Select(n => $"中文-long-line-{n:D6}").Concat(new[] { "END-A", "END-B" }));
        Check(new FileInfo(path).Length > 1024 * 1024, "large fixture was too short");
        var reader = new IncrementalLogReader();
        var lines = reader.Read(path, 600);
        Check(reader.BytesRead <= 1024 * 1024, "cold reader exceeded byte budget");
        EqualLines(new[] { "END-A", "END-B" }, lines.TakeLast(2), "bounded read tail");
        Check(lines.All(l => !l.Contains('\uFFFD')), "cold read exposed a cut UTF8 fragment");
        EqualLines(lines, LogService.ReadFileTail(path, 600), "service changed bounded reader semantics");
        var previous = Reader(path).BytesRead;
        LogService.ReadFileTail(path, 600); Equal(previous, Reader(path).BytesRead);
        var partial = Path.Combine(work, "long-partial.log");
        File.WriteAllText(partial, "first\n" + new string('x', 100000), new UTF8Encoding(false));
        EqualLines(new[] { "first" }, LogService.ReadFileTail(partial), "huge incomplete line");
        File.AppendAllText(partial, "\nend\n", new UTF8Encoding(false));
        var result = LogService.ReadFileTail(partial);
        Check(result.Count == 3 && result[1].StartsWith("[超长日志行已截断]") && result[1].Length < 66000, "partial-line bound was lost");
        Equal("end", result[^1]);
    }

    public static void RunNative(string path)
    {
        path = Path.GetFullPath(path); var original = new FileInfo(path);
        var length = original.Length; var modified = original.LastWriteTimeUtc;
        var referenceReader = new IncrementalLogReader(); var reference = referenceReader.Read(path, 4096).ToArray();
        foreach (var count in new[] { 12, 600, 650, 100, 500 })
            EqualLines(reference.TakeLast(count), LogService.ReadFileTail(path, count), $"real log requested {count}");
        LogService.ReadFileTail(Path.Combine(Path.GetDirectoryName(path)!, ".", Path.GetFileName(path)), 650);
        Equal(1, Snapshot().Count);
        var after = new FileInfo(path);
        Check(after.Length == length && after.LastWriteTimeUtc == modified, "real log changed during verification; comparison is not a stable snapshot");
        Check(referenceReader.BytesRead <= 1024 * 1024, "real log probe exceeded content read budget");
        Console.WriteLine($"PASS real log cache: file={Path.GetFileName(path)}; size={length}; available complete lines in bounded tail={reference.Length}; requested 12->600->650->100->500; aliases share1 reader; cold content bytes={referenceReader.BytesRead}; user log/service unchanged.");
    }
}
