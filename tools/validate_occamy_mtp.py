"""Measure Occamy with a Compact donor MTP runtime on an otherwise idle GPU.

This is a cross-model draft experiment, not a claim that the donor matches Occamy.
Never stops existing servers. Saves commands, raw logs, token IDs and per-run results.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
from statistics import median
import subprocess

from validate_strata import run


def sha256(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def summarize(manifest):
    groups = {}
    for result in manifest["results"]:
        groups.setdefault(result["config"], []).append(result)
    baseline = [r["tokens"] for r in manifest["results"] if r.get("ok") and not r["mtp"]]
    summary = {}
    for name, runs in groups.items():
        good = [r for r in runs if r.get("ok")]
        speeds = [r["decode"] for r in good]
        summary[name] = dict(
            completed=len(good), failed=len(runs) - len(good),
            decode_rates=speeds, decode_median=median(speeds) if speeds else None,
            acceptance=[r.get("acceptance") for r in good],
            peak_gpu_used_mib=[r.get("peak_gpu_used_mib") for r in good],
            tail_gpu_used_mib=[r.get("tail_gpu_used_mib") for r in good],
            cache_slots=[r.get("cache_slots") for r in good],
            cache_hits=[r.get("cache_hit_ratio") for r in good],
            generated_tokens=[len(r["tokens"]) for r in good],
            same_output_on_repeats=bool(good) and all(r["tokens"] == good[0]["tokens"] for r in good),
            matches_any_baseline=[any(r["tokens"] == b for b in baseline) for r in good],
            common_prefix_with_first_baseline=[
                next((i for i, (a, b) in enumerate(zip(r["tokens"], baseline[0])) if a != b),
                     min(len(r["tokens"]), len(baseline[0]))) for r in good] if baseline else [],
        )
    return summary


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--llama-root", type=Path, required=True)
    parser.add_argument("--strata-root", type=Path, required=True)
    parser.add_argument("--tokens", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=2)
    parser.add_argument("--summary-only", action="store_true")
    parser.add_argument("--reserves", type=int, nargs="+", default=[800])
    args = parser.parse_args()
    out = args.output.resolve()
    if args.summary_only:
        print(json.dumps(summarize(json.loads((out / "results.json").read_text(encoding="utf-8"))), indent=2))
        return
    if args.repeats < 1:
        parser.error("--repeats must be positive")
    for image in ("llama-server.exe", "strata.exe"):
        active = subprocess.check_output(["tasklist", "/FI", f"IMAGENAME eq {image}", "/FO", "CSV"], text=True)
        if f'"{image}"' in active.lower():
            raise RuntimeError(f"{image} is running. Leave it untouched and run later.")
    root, builds = args.llama_root.resolve(), args.strata_root.resolve()
    exe = builds / "release-pkg/engine/strata.exe"
    model = root / "models/Occamy-1.0.APEX-I-MiniPlus-V2.1-Abliterated.gguf"
    donor = builds / "mtp-apex-compact"
    profile = builds / "profile-occamy.bin"
    pack = builds / "pack-occamy"
    for file in (exe, model, profile, pack / "index.txt", pack / "dense.bin",
                 donor / "dense.bin", donor / "dense.txt", donor / "experts.bin"):
        if not file.is_file():
            raise FileNotFoundError(file)
    ids = [int(n) for n in args.tokens.read_text(encoding="utf-8-sig").replace(",", " ").split()]
    identity = dict(engine_sha256=sha256(exe), profile_sha256=sha256(profile),
                    donor_sha256={f: sha256(donor / f) for f in ("dense.bin", "dense.txt", "experts.bin")},
                    model=str(model), model_size=model.stat().st_size, model_mtime_ns=model.stat().st_mtime_ns,
                    input_tokens=ids, repeats=args.repeats)
    if args.reserves != [800]:
        identity["reserves"] = args.reserves
    out.mkdir(parents=True, exist_ok=True)
    results_path = out / "results.json"
    manifest = dict(identity=identity, results=[])
    if results_path.exists():
        manifest = json.loads(results_path.read_text(encoding="utf-8"))
        if manifest["identity"] != identity:
            raise RuntimeError("Input or binaries changed; use a new output directory")
    token_file = out / "prompt.tokens.txt"
    token_file.write_text(" ".join(map(str, ids)), encoding="utf-8")
    vocab = {}
    for line in (builds / "release-pkg/engine/vocab_bytes.tsv").read_text(encoding="utf-8").splitlines():
        parts = line.split("\t", 1)
        if len(parts) == 2 and parts[0].isdigit():
            vocab[int(parts[0])] = bytes.fromhex(parts[1].strip())
    base = [str(exe), "--pack", str(pack), "--native", str(model), "--no-ple",
            "--tokens-file", str(token_file), "--expert-profile", str(profile), "--expert-cache", "auto",
            "--vram-reserve-mib", "800", "--prefill", "2048", "--max-context", "4096", "--max-new", "128",
            "--kv", "int8", "--kv-resident", "0", "--suffix-draft", "0", "--pool-workers", "6",
            "--pool-affinity", "all", "--stats"]
    env = dict(os.environ, STRATA_MTP_PLAIN="1")
    for repeat in range(args.repeats):
        # Repeat the baseline alongside the donor runs rather than reusing old timings.
        configs = [(name, spec, enabled, reserve) for reserve in args.reserves
                   for name, spec, enabled in (("no-mtp", 2, False), ("donor-spec2", 2, True), ("donor-spec4", 4, True))]
        for name, spec, enabled, reserve in configs:
            if args.reserves != [800]:
                name += f"-r{reserve}"
            label = f"{name}-{repeat}"
            if any(r["name"] == label for r in manifest["results"]):
                continue
            command = base + ["--spec", str(spec)]
            command[command.index("--vram-reserve-mib") + 1] = str(reserve)
            if enabled:
                command += ["--mtp", str(donor)]
            result = dict(name=label, config=name, spec=spec, mtp=enabled, reserve_mib=reserve)
            try:
                memory_file = out / f"{label}.gpu.csv"
                with memory_file.open("w", encoding="utf-8") as memory_log:
                    monitor = subprocess.Popen(["nvidia-smi", "-i", "0", "--query-gpu=memory.total,memory.used,memory.free,utilization.gpu",
                                                "--format=csv,noheader,nounits", "-lms", "500"],
                                               stdout=memory_log, stderr=subprocess.DEVNULL)
                    try:
                        text = run(command, out, label, env)
                    finally:
                        monitor.terminate()
                        monitor.wait(timeout=10)
                samples = []
                for row in memory_file.read_text(encoding="utf-8").splitlines():
                    try:
                        samples.append([float(n.strip()) for n in row.split(",")])
                    except ValueError:
                        continue
                if samples:
                    occupied = [row[1] for row in samples if row[1] > row[0] * 0.6]
                    result.update(peak_gpu_used_mib=max(row[1] for row in samples),
                                  tail_gpu_used_mib=median(occupied[-6:]) if occupied else None)
                stderr = (out / f"{label}.stderr.log").read_text(encoding="utf-8")
                slots = re.search(r"expert cache (\d+) slots, ([\d.]+) GiB", stderr)
                if slots:
                    result["cache_slots"] = int(slots[1])
                tokens = [int(n) for n in re.search(r"^output\s*:\s*(.*)$", text, re.M)[1].split()]
                decode = float(re.search(r"^decode\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+)", text, re.M)[1])
                acceptance = re.search(r"drafts accepted (\d+) of (\d+) \(([\d.]+)\)", text)
                result.update(ok=True, tokens=tokens, decode=decode,
                              acceptance=float(acceptance[3]) if acceptance and enabled else None,
                              accepted=int(acceptance[1]) if acceptance and enabled else None,
                              proposed=int(acceptance[2]) if acceptance and enabled else None)
                hits = re.search(r"R4 expert-cache hits\s+\d+ of \d+ = ([\d.]+)", text)
                result["cache_hit_ratio"] = float(hits[1]) if hits else None
                answer = b"".join(vocab.get(t, b"") for t in tokens).decode("utf-8", errors="replace")
                (out / f"{label}.answer.txt").write_text(answer, encoding="utf-8")
                print(label, "decode", decode, "acceptance", result["acceptance"], flush=True)
            except (RuntimeError, subprocess.TimeoutExpired) as error:
                result.update(ok=False, error=str(error))
                print(label, "FAILED", str(error), flush=True)
            manifest["results"].append(result)
            results_path.write_text(json.dumps(manifest, indent=2), encoding="utf-8")
            (out / "summary.json").write_text(json.dumps(summarize(manifest), indent=2), encoding="utf-8")
    print(json.dumps(summarize(manifest), indent=2), flush=True)


if __name__ == "__main__":
    main()
