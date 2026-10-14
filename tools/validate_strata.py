"""Reproducible local Strata/llama.cpp comparison; never stops existing servers.

All commands, raw logs, token IDs and timings are saved under --output.
Run on an idle GPU. Occamy is the main comparison model; Compact supplies MTP.
"""
import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import time
import urllib.request
from statistics import median


def summarize(output):
    manifest = json.loads((output / "results.json").read_text(encoding="utf-8"))
    results = manifest["results"]
    groups = {}
    for result in results:
        group = result["name"].rsplit("-", 1)[0]
        groups.setdefault(group, []).append(result)
    summary = {}
    compact_base = next(r["tokens"] for r in results if r["model"] == "compact" and not r.get("mtp", True))
    occamy_base = next(r["tokens"] for r in results if r["engine"] == "strata" and r["model"] == "occamy")
    for group, runs in groups.items():
        decode = [r["decode"] if r["engine"] == "strata" else r["timings"]["predicted_per_second"] for r in runs]
        first = runs[0]["tokens"]
        reference = compact_base if runs[0]["model"] == "compact" else occamy_base
        prefix = next((i for i, (a, b) in enumerate(zip(first, reference)) if a != b), min(len(first), len(reference)))
        summary[group] = dict(decode_min=min(decode), decode_max=max(decode), decode_median=median(decode),
                              output_tokens=len(first), same_output_on_repeats=all(r["tokens"] == first for r in runs),
                              matches_strata_baseline=first == reference, common_prefix_tokens=prefix,
                              all_runs_match_baseline=all(r["tokens"] == reference for r in runs),
                              acceptance=[r.get("acceptance") for r in runs])
    (output / "summary.json").write_text(json.dumps(summary, indent=2), encoding="utf-8")
    print(json.dumps(summary, indent=2))


def request(port, route, body=None):
    data = None if body is None else json.dumps(body).encode()
    req = urllib.request.Request(f"http://127.0.0.1:{port}/{route}", data=data,
                                 headers={"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=180) as response:
        return json.load(response)


def run(command, output, name, env=None):
    (output / f"{name}.command.json").write_text(json.dumps(command, indent=2), encoding="utf-8")
    with (output / f"{name}.stdout.log").open("w", encoding="utf-8") as stdout, \
            (output / f"{name}.stderr.log").open("w", encoding="utf-8") as stderr:
        result = subprocess.run(command, stdout=stdout, stderr=stderr, env=env, timeout=240)
    text = (output / f"{name}.stdout.log").read_text(encoding="utf-8")
    if result.returncode:
        raise RuntimeError(f"{name} exited {result.returncode}; see saved logs")
    return text


def calibrate_occamy(output, builds, tokenizer, model, repeats):
    command = json.loads((output / "occamy-spec2-mtp0-0.command.json").read_text(encoding="utf-8"))
    questions = ["Write an iterative Python Fibonacci function and handle negative inputs.",
                 "Explain the difference between UTF-8 bytes and Unicode characters, with examples.",
                 "Summarize how a database index speeds up queries and discuss the write overhead."]
    traces = []
    for i, question in enumerate(questions):
        prompt = output / f"calibration-{i}.prompt.txt"
        prompt.write_text(f"<|im_start|>user\n{question}<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", encoding="utf-8")
        text = run([str(tokenizer), "-m", str(model), "-f", str(prompt), "--ids"], output, f"calibration-tokenize-{i}")
        ids = json.loads(re.search(r"^\[.*\]$", text, re.M).group())
        token_file = output / f"calibration-{i}.tokens.txt"
        token_file.write_text(" ".join(map(str, ids)), encoding="utf-8")
        trace = output / f"calibration-{i}.routing.txt"
        args = command.copy()
        args[args.index("--tokens-file") + 1] = str(token_file)
        run(args + ["--dump-routing", str(trace)], output, f"calibration-trace-{i}")
        traces.append(str(trace))
        print("Collected calibration trace", i, flush=True)
    import sys
    profile = output / "occamy-calibrated.bin"
    run([sys.executable, str(builds / "apex-on-strata/tools/make_profile.py"), *traces,
         "--no-base", "--n-layer", "40", "--n-expert", "256", "--out", str(profile)], output, "calibration-profile")
    manifest = json.loads((output / "results.json").read_text(encoding="utf-8"))
    for repeat in range(repeats):
        for tuned in (False, True):
            name = f"occamy-{'calibrated' if tuned else 'control'}-{repeat}"
            args = command.copy()
            if tuned:
                args[args.index("--expert-profile") + 1] = str(profile)
            text = run(args, output, name)
            ids = [int(n) for n in re.search(r"^output\s*:\s*(.*)$", text, re.M)[1].split()]
            decode = float(re.search(r"^decode\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+)", text, re.M)[1])
            hits = re.search(r"R4 expert-cache hits\s+\d+ of \d+ = ([\d.]+)", text)
            manifest["results"].append(dict(name=name, engine="strata", model="occamy", tokens=ids,
                                            decode=decode, profile=str(profile) if tuned else args[args.index("--expert-profile") + 1],
                                            cache_hit_ratio=float(hits[1]) if hits else None))
            (output / "results.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
            print(name, decode, "tok/s", flush=True)
    summarize(output)


def tune_occamy(output, repeats):
    command = json.loads((output / "occamy-spec2-mtp0-0.command.json").read_text(encoding="utf-8"))
    manifest = json.loads((output / "results.json").read_text(encoding="utf-8"))
    for repeat in range(repeats):
        for workers, reserve in [(6, 800), (6, 500), (7, 500), (4, 500)]:
            name = f"occamy-w{workers}-r{reserve}-{repeat}"
            args = command.copy()
            args[args.index("--pool-workers") + 1] = str(workers)
            args[args.index("--vram-reserve-mib") + 1] = str(reserve)
            text = run(args, output, name)
            ids = [int(n) for n in re.search(r"^output\s*:\s*(.*)$", text, re.M)[1].split()]
            decode = float(re.search(r"^decode\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+)", text, re.M)[1])
            hits = re.search(r"R4 expert-cache hits\s+\d+ of \d+ = ([\d.]+)", text)
            manifest["results"].append(dict(name=name, engine="strata", model="occamy", tokens=ids,
                                            decode=decode, workers=workers, reserve=reserve,
                                            cache_hit_ratio=float(hits[1]) if hits else None))
            (output / "results.json").write_text(json.dumps(manifest, indent=2), encoding="utf-8")
            print(name, decode, "tok/s", flush=True)
    summarize(output)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--llama-root", type=Path, required=True)
    parser.add_argument("--strata-root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--repeats", type=int, default=2)
    parser.add_argument("--summary-only", action="store_true")
    parser.add_argument("--calibrate-occamy", action="store_true")
    parser.add_argument("--tune-occamy", action="store_true")
    args = parser.parse_args()
    out, root, builds = args.output, args.llama_root, args.strata_root
    if args.summary_only:
        summarize(out)
        return
    out.mkdir(parents=True, exist_ok=True)
    active = subprocess.check_output(["tasklist", "/FI", "IMAGENAME eq llama-server.exe", "/FO", "CSV"], text=True)
    if '"llama-server.exe"' in active.lower():
        raise RuntimeError("A llama-server is already running. Leave it untouched and run later.")
    exe = builds / "release-pkg/engine/strata.exe"
    tok = builds / "release-pkg/engine/tokenize/llama-tokenize.exe"
    models = {
        "occamy": root / "models/Occamy-1.0.APEX-I-MiniPlus-V2.1-Abliterated.gguf",
        "compact": root / "models/Qwen3.6-35B-A3B-APEX-MTP-Compact.gguf",
    }
    if args.calibrate_occamy:
        calibrate_occamy(out, builds, tok, models["occamy"], args.repeats)
        return
    if args.tune_occamy:
        tune_occamy(out, args.repeats)
        return
    prompt = ("<|im_start|>user\nWrite a Python function that removes duplicates from a list "
              "while preserving order. Explain its time complexity.<|im_end|>\n"
              "<|im_start|>assistant\n<think>\n\n</think>\n\n")
    prompt_path = out / "prompt.txt"
    prompt_path.write_text(prompt, encoding="utf-8")
    tokens = {}
    for model, path in models.items():
        text = run([str(tok), "-m", str(path), "-f", str(prompt_path), "--ids"], out, f"tokenize-{model}")
        tokens[model] = json.loads(re.search(r"^\[.*\]$", text, re.M).group())
        (out / f"{model}.tokens.txt").write_text(" ".join(map(str, tokens[model])), encoding="utf-8")
    manifest = {"prompt_sha256": hashlib.sha256(prompt.encode()).hexdigest(),
                "engine_sha256": hashlib.sha256(exe.read_bytes()).hexdigest(),
                "prompt_tokens": {k: len(v) for k, v in tokens.items()}, "results": []}
    # Python text output uses platform newlines; retain both the logical text hash
    # and the actual tokenizer input bytes, plus the exact token IDs in *.tokens.txt.
    manifest["tokenizer_input_sha256"] = hashlib.sha256(prompt_path.read_bytes()).hexdigest()
    if (out / "results.json").exists():
        previous = json.loads((out / "results.json").read_text(encoding="utf-8"))
        for key in ("prompt_sha256", "engine_sha256", "prompt_tokens"):
            if previous[key] != manifest[key]:
                raise RuntimeError("Cannot resume a run with changed inputs or engine")
        manifest = previous
        manifest["tokenizer_input_sha256"] = hashlib.sha256(prompt_path.read_bytes()).hexdigest()

    def save():
        (out / "results.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2), encoding="utf-8")

    for model, spec, mtp in [("occamy", 2, False), ("compact", 2, False),
                             ("compact", 2, True), ("compact", 3, True), ("compact", 4, True)]:
        pack = "pack-occamy" if model == "occamy" else "pack-apex-compact"
        profile = "profile-occamy.bin" if model == "occamy" else "profile-apex-compact.bin"
        command = [str(exe), "--pack", str(builds / pack), "--native", str(models[model]),
                   "--no-ple", "--tokens-file", str(out / f"{model}.tokens.txt"),
                   "--expert-profile", str(builds / profile), "--expert-cache", "auto",
                   "--vram-reserve-mib", "800", "--prefill", "2048", "--max-context", "4096",
                   "--max-new", "128", "--kv", "int8", "--kv-resident", "0", "--spec", str(spec),
                   "--suffix-draft", "0", "--pool-workers", "6", "--pool-affinity", "all", "--stats"]
        if mtp:
            command += ["--mtp", str(builds / "mtp-apex-compact")]
        env = dict(os.environ, STRATA_MTP_PLAIN="1")
        for repeat in range(args.repeats):
            name = f"{model}-spec{spec}-mtp{int(mtp)}-{repeat}"
            if any(r["name"] == name for r in manifest["results"]):
                continue
            text = run(command, out, name, env)
            output_ids = [int(n) for n in re.search(r"^output\s*:\s*(.*)$", text, re.M)[1].split()]
            rates = {key: float(re.search(rf"^{key}\s+\d+ tokens in [\d.]+ ms\s+->\s+([\d.]+)", text, re.M)[1])
                     for key in ("decode", "prefill")}
            acceptance = re.search(r"drafts accepted (\d+) of (\d+) \(([\d.]+)\)", text)
            manifest["results"].append(dict(name=name, engine="strata", model=model, spec=spec, mtp=mtp,
                                            tokens=output_ids, **rates,
                                            acceptance=float(acceptance[3]) if acceptance else None))
            save()
            print(name, rates, flush=True)

    server_cmd = [str(root / "llama-server.exe"), "-m", str(models["occamy"]), "--host", "127.0.0.1",
                  "--port", "18089", "-t", "6", "-tb", "6", "-c", "4096", "-np", "1", "-ngl", "all",
                  "--n-cpu-moe", "32", "--fit", "off", "-fa", "on", "-ctk", "q8_0", "-ctv", "q8_0"]
    (out / "llama-server.command.json").write_text(json.dumps(server_cmd, indent=2), encoding="utf-8")
    with (out / "llama-server.log").open("w", encoding="utf-8") as log:
        server = subprocess.Popen(server_cmd, stdout=log, stderr=log)
        try:
            deadline = time.monotonic() + 180
            while True:
                if server.poll() is not None:
                    raise RuntimeError("Comparison server failed to load; see llama-server.log")
                try:
                    request(18089, "health")
                    break
                except (OSError, ValueError):
                    if time.monotonic() > deadline:
                        raise TimeoutError("Comparison server readiness timeout")
                    time.sleep(1)
            for repeat in range(args.repeats):
                if any(r["name"] == f"llama-occamy-{repeat}" for r in manifest["results"]):
                    continue
                body = dict(prompt=tokens["occamy"], n_predict=128, temperature=0, seed=7,
                            repeat_penalty=1, cache_prompt=False, return_tokens=True, ignore_eos=True)
                response = request(18089, "completion", body)
                (out / f"llama-occamy-{repeat}.json").write_text(json.dumps(response, ensure_ascii=False, indent=2), encoding="utf-8")
                manifest["results"].append(dict(name=f"llama-occamy-{repeat}", engine="llama.cpp", model="occamy",
                                                tokens=response.get("tokens", []), timings=response.get("timings"),
                                                content=response.get("content")))
                save()
                print(f"llama-occamy-{repeat}", response.get("timings"), flush=True)
        finally:
            server.terminate()
            try:
                server.wait(timeout=15)
            except subprocess.TimeoutExpired:
                server.kill()
                server.wait()
    print("Saved:", out, flush=True)
    summarize(out)


if __name__ == "__main__":
    main()
