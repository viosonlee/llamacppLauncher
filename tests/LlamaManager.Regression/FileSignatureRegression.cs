using System.IO;
using System.Security.Cryptography;
using LlamaManager.Services;
using static Program;

internal static class FileSignatureRegression
{
    public static void RunPolicies(string root)
    {
        var work = Path.Combine(root, "signature-policy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var reads = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var computer = new FileSignatureComputer(3, path => reads[path] = reads.GetValueOrDefault(path) + 1);
        var paths = Enumerable.Range(0, 4).Select(i => Path.Combine(work, $"model-{i}.gguf")).ToArray();
        for (var i = 0; i < paths.Length; i++) File.WriteAllBytes(paths[i], [(byte)i]);
        var first = computer.Compute(paths[0]);
        Check(first != null, "signature fixture could not be read");
        Equal(first, computer.Compute(paths[0].ToUpperInvariant()));
        Equal(first, computer.Compute(Path.Combine(work, ".", "model-0.gguf")));
        Equal(1, reads[PathIdentity.Normalize(paths[0])]);
        computer.Compute(paths[1]); computer.Compute(paths[2]);
        Equal(3, computer.CacheCount);
        computer.Compute(paths[0]); // promote A; B is now the oldest
        computer.Compute(paths[3]);
        Equal(3, computer.CacheCount);
        Equal(first, computer.Compute(paths[0]));
        Equal(1, reads[PathIdentity.Normalize(paths[0])]);
        computer.Compute(paths[1]);
        Equal(2, reads[PathIdentity.Normalize(paths[1])]);

        var versions = new FileSignatureComputer();
        var time = DateTime.UtcNow;
        string? previous = null;
        for (var version = 0; version < 32; version++)
        {
            File.WriteAllBytes(paths[0], [(byte)version]);
            File.SetLastWriteTimeUtc(paths[0], time.AddSeconds(version));
            var current = versions.Compute(paths[0]);
            Check(current != null && current != previous, "file update reused the previous signature");
            previous = current;
            Equal(1, versions.CacheCount); // previous implementation retained 32 version keys
        }
        for (var path = 0; path < 300; path++)
        {
            var file = Path.Combine(work, $"limit-{path}.bin");
            File.WriteAllBytes(file, [(byte)path]);
            Check(versions.Compute(file) != null, "capacity fixture failed to sample");
            Check(versions.CacheCount <= 256, "signature cache exceeded its production bound");
        }
        Equal(256, versions.CacheCount);
        var empty = Path.Combine(work, "empty.bin");
        File.WriteAllBytes(empty, []);
        Check(versions.Compute(empty) == null && versions.Compute(Path.Combine(work, "missing.bin")) == null, "missing or empty file was assigned a signature");
        Equal(256, versions.CacheCount);
        Throws<ArgumentOutOfRangeException>(() => new FileSignatureComputer(0));

        var attempts = 0;
        var failures = new FileSignatureComputer(beforeRead: _ => { if (++attempts == 1) throw new IOException("temporary read failure"); });
        Check(failures.Compute(paths[0]) == null, "sampling failure was not reported as unknown");
        Equal(0, failures.CacheCount);
        Check(failures.Compute(paths[0]) != null, "failure poisoned the next same-version retry");
        Equal(2, attempts);
        var lockedComputer = new FileSignatureComputer();
        using (var locked = new FileStream(paths[0], FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Check(lockedComputer.Compute(paths[0]) == null, "exclusive lock was ignored");
            Equal(0, lockedComputer.CacheCount);
        }
        Check(lockedComputer.Compute(paths[0]) != null, "released file did not recover");
    }

    public static void RunFormat(string root)
    {
        var work = Path.Combine(root, "signature-format-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var small = Path.Combine(work, "small.bin");
        var bytes = Enumerable.Range(0, 400).Select(i => (byte)i).ToArray();
        File.WriteAllBytes(small, bytes);
        Equal($"{bytes.Length}:{Convert.ToHexString(SHA256.HashData(bytes))}", FileSignature.Compute(small));
        const int chunk = 1024 * 1024;
        var large = Path.Combine(work, "large.bin");
        var data = new byte[3 * chunk + 17];
        new Random(17).NextBytes(data);
        File.WriteAllBytes(large, data);
        var head = Convert.ToHexString(SHA256.HashData(data.AsSpan(0, chunk)));
        var tail = Convert.ToHexString(SHA256.HashData(data.AsSpan(data.Length - chunk, chunk)));
        var expected = $"{data.Length}:{head[..32]}{tail[..32]}";
        Equal(expected, FileSignature.Compute(large));
        // Model rename preserves the sampled identity, so existing preset rebinding stays compatible.
        var copy = Path.Combine(work, "renamed.bin");
        File.Copy(large, copy);
        Equal(expected, FileSignature.Compute(copy));
        // The middle is intentionally NOT sampled, and must not be misrepresented as full integrity.
        data[chunk + 1] ^= 0xFF;
        File.WriteAllBytes(copy, data);
        File.SetLastWriteTimeUtc(copy, DateTime.UtcNow.AddSeconds(3));
        Equal(expected, FileSignature.Compute(copy));
    }

    public static async Task RunConcurrencyAsync(string root)
    {
        var work = Path.Combine(root, "signature-races-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var changing = Path.Combine(work, "changing.bin");
        var unrelated = Path.Combine(work, "unrelated.bin");
        File.WriteAllBytes(changing, [1, 2, 3]);
        File.WriteAllBytes(unrelated, [8, 9]);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var reads = 0;
        var computer = new FileSignatureComputer(4, path =>
        {
            if (PathIdentity.Equals(path, changing) && Interlocked.Increment(ref reads) == 1)
            {
                entered.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("signature test gate was not released");
            }
        });
        var old = Task.Run(() => computer.Compute(changing));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // An expensive read must not hold the global cache lock and block unrelated model reads.
            Check(await Task.Run(() => computer.Compute(unrelated)).WaitAsync(TimeSpan.FromSeconds(5)) != null, "unrelated signature was blocked by a locked cache");
            File.WriteAllBytes(changing, [4, 5, 6]);
            File.SetLastWriteTimeUtc(changing, DateTime.UtcNow.AddSeconds(5));
            var newer = await Task.Run(() => computer.Compute(changing)).WaitAsync(TimeSpan.FromSeconds(5));
            Check(newer != null, "new version failed while old version was still reading");
            release.Set();
            Check(await old.WaitAsync(TimeSpan.FromSeconds(5)) == null, "a changed file was cached under the old observed version");
            Equal(newer, computer.Compute(changing));
            Equal(2, reads); // old completion must not remove/replace the new entry
            Equal(2, computer.CacheCount);
        }
        finally { release.Set(); try { await old.WaitAsync(TimeSpan.FromSeconds(5)); } catch { } }

        var sharedReads = 0;
        var shared = new FileSignatureComputer(beforeRead: _ => Interlocked.Increment(ref sharedReads));
        var values = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => shared.Compute(changing))));
        Check(values[0] != null && values.All(s => s == values[0]), "concurrent callers observed inconsistent identities");
        Equal(1, sharedReads);
        Equal(1, shared.CacheCount);
    }

    public static async Task RunNativeAsync(string root, string model)
    {
        // Read only the model's head/tail; never change the user's GGUF or running service.
        model = Path.GetFullPath(model);
        var reads = 0;
        var computer = new FileSignatureComputer(8, _ => Interlocked.Increment(ref reads));
        var signatures = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => computer.Compute(model))));
        Check(signatures[0] != null && signatures.All(s => s == signatures[0]), "real model sampling failed");
        Equal(1, reads);
        Equal(1, computer.CacheCount);
        Equal(signatures[0], FileSignature.Compute(model));
        var work = Path.Combine(root, "signature-native-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var scratch = Path.Combine(work, "mutable-fixture.bin");
        var stamp = DateTime.UtcNow;
        for (var version = 0; version < 32; version++)
        {
            File.WriteAllBytes(scratch, [(byte)version]);
            File.SetLastWriteTimeUtc(scratch, stamp.AddSeconds(version));
            Check(computer.Compute(scratch) != null, "real disk version test failed");
            Equal(2, computer.CacheCount);
        }
        Console.WriteLine($"PASS real file signatures: model={Path.GetFileName(model)}; length={new FileInfo(model).Length}; 20 callers=1 sampled read; mutable file versions=32; retained entries=2; format compatible; no model/service modified.");
    }
}
