using System.IO;

namespace LlamaManager.Services;

/// <summary>One downloadable recommendation: a direct GGUF link plus what it needs to run.</summary>
public sealed record ModelDownload(
    string Repo,
    string FileName,
    long Bytes,
    int VramMb,
    string Note)
{
    public string DisplayName => Path.GetFileNameWithoutExtension(FileName);
    /// <summary>The direct download URL on HuggingFace's resolve endpoint.</summary>
    public string Url => $"https://huggingface.co/{Repo}/resolve/main/{FileName}";
    /// <summary>Whether this machine could plausibly run it, judged from the detected VRAM.</summary>
    public bool Fits(int availableVramMb) => availableVramMb <= 0 || VramMb <= availableVramMb;
}

/// <summary>
/// A small curated catalogue for a brand-new machine: every entry is a single-file GGUF on
/// HuggingFace that the bundled engines can run, ordered so the first fit is the first pick.
/// Sizes and VRAM figures describe the file itself; the launcher uses them for ordering and
/// warnings only, never as a hard gate, and a model being absent from this list says nothing.
/// </summary>
public static class ModelCatalog
{
    /// <summary>
    /// Recommendations ordered by VRAM need, smallest first. Every URL was verified reachable
    /// (HTTP 200) on 2026-10-18 and the byte counts were read from the response headers; a local
    /// file whose size differs is re-downloaded rather than trusted.
    /// </summary>
    public static IReadOnlyList<ModelDownload> Recommendations { get; } =
    [
        new("ggml-org/gemma-3-1b-it-GGUF", "gemma-3-1b-it-Q4_K_M.gguf", 806058240, 1200,
            "最小可用档：回答快，显存占用极低，适合先验证环境跑通流程。"),
        new("ggml-org/SmolLM3-3B-GGUF", "SmolLM3-Q4_K_M.gguf", 1915305312, 2600,
            "同 4B 档的更小备选：指令跟随略弱。"),
        new("Qwen/Qwen3-4B-GGUF", "Qwen3-4B-Q4_K_M.gguf", 2497280256, 3200,
            "均衡档：中文与工具调用都不错，6GB 显存全速运行。"),
        new("Qwen/Qwen3-8B-GGUF", "Qwen3-8B-Q4_K_M.gguf", 5027783488, 5800,
            "6GB 显存的实际上限：全速运行显存贴边，8K 上下文内。"),
        new("unsloth/Qwen3-14B-GGUF", "Qwen3-14B-Q4_K_M.gguf", 9001753984, 10240,
            "8GB+ 显存档：显存不足时部分层会留在 CPU，速度下降。"),
    ];
}
