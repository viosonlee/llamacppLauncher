using System.IO;

namespace LlamaManager.Services;

public static class PathIdentity
{
    public static string Normalize(string path) => Path.GetFullPath(path).ToLowerInvariant();
    public static bool Equals(string? left, string? right) => !string.IsNullOrWhiteSpace(left) &&
        !string.IsNullOrWhiteSpace(right) && string.Equals(Normalize(left), Normalize(right), StringComparison.Ordinal);
}
