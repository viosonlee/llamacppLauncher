# llama.cpp v1.0 adaptation and cross-engine compatibility

## Scope and evidence status

**Snapshot: 2026-11-14. Source investigation followed by launcher integration tests; not a new-engine/model acceptance report.**

This review read `AGENTS.md`, the architecture/update/known-defect documents, current C# services and adapters, saved official release responses, and upstream source pinned to `v1.0.0`. The checkout already contained other agents' changes; observations below describe the files at inspection time, not a clean or independently validated release.

- The documentation subtask was read-only except for this file; the coordinator integrated the separate test, parameter and repository tasks.
- Coordinator verification: Windows Release build 0 warnings/errors, **174 regression groups passed, 0 failed**. This does not establish new upstream binary/model acceptance.
- Local b12182/native version and driver **621.24 / CUDA UMD13.5** were checked read-only by the coordinator; no model was loaded, no real service stopped and no user configuration changed. No commit/push was made.
- Integrated: opt-in MoE/FP8 schema, actual resolved-EXE help capability gate, canonical Poly repository with legacy metadata preservation, pinned-release/telemetry regressions and stricter same-release asset URL checks. Real b12359/Poly.9 installation and GPU inference remain unperformed.
- Upstream requests were limited to individual tagged text files and the `b12359` release metadata, with timeouts and per-response size caps; no repository clone, release archive or model download. Saved `/tmp/llamacpp-1-release.md` and `*-releases.json` supplied the release snapshot. Temporary source excerpts are not durable acceptance artifacts.

## 1. Decision summary

1. **Keep the EXE + HTTP architecture.** v1.0's breaking C API does not require a C# binding rewrite: this launcher does not link to `llama.h`/`llama.dll`.
2. **Separate stable identity from nightly identity.** The official `v1.0.0` release points to binary release **`b12359`**. **`b12375`** is the latest nightly in the checked snapshot, not the same pinned stable artifact. Current acquisition selects the newest compatible nightly, not a stable channel. [S1–S3]
3. **Preserve security defaults and existing presets.** Local text-only agents do not need server-side tools, MCP proxying, private media fetches, or blanket CORS exceptions. Authentication, browser origin handling and private media need distinct compatibility checks.
4. **Treat MoE cache and FP8 as opt-in capabilities.** Do not replace established `--n-cpu-moe`, KV types or explicit memory settings automatically. `--fit on` is not an OOM guarantee.
5. **Keep all engines isolated.** Versioned sibling installations are appropriate; parallel installation/adaptation does not mean simultaneous GPU-heavy services. Preserve the shared GPU arbitration, independent sessions, per-model settings and rollback paths.

## 2. Pinned release / local-state matrix

| Component | Local baseline / checked upstream | Adaptation decision |
|---|---|---|
| Official llama.cpp | Local **b12182 / 0.9 development**, configured at `engines/llamacpp/b12182-cuda-12.4/llama-server.exe`; receipt confirms b12182/CUDA12.4. Stable **v1.0.0 → b12359**; checked latest nightly **b12375**. | Pin b12359 for reproducible v1.0 acceptance. Evaluate b12375 separately if choosing the nightly channel. Neither was installed or run here. |
| NVIDIA environment | User reports **driver 621.24, CUDA UMD 13.5**, replacing the older 617.42/13.4 historical state; RTX 3060 Laptop / sm86 baseline. | CUDA13.5 is now an eligible package candidate under the existing selection policy, not proof of successful runtime loading/inference. Preserve CUDA12.4 rollback. |
| PolyStrata | Local **v0.1.45-poly.7**, versioned CUDA13.0 package; current canonical upstream **`simplexai-labs/PolyStrata`**, checked release **v0.1.45-poly.9**. | .8/.9 are candidates, not Windows-validated upgrades: both release notes explicitly say their Windows package was built but **not run on Windows for that release**. [S12, S13] |
| KVMem | Local/applicable NVIDIA30 unified package **v0.19.1** remains unchanged. New **v0.19.rad1** is Radiance for AMD RX 9000 (`gfx1200`/`gfx1201`), not the NVIDIA unified package. | Retain v0.19.1 NVIDIA30; do not upgrade by release recency alone or substitute the AMD package. [S14, S15] |
| Prism | Installed **prism-b12128-d7b2896**; checked latest public release is the same. | **Prism does have public releases**, including Windows x64 CUDA12.4/13.3 binaries and runtimes. It remains a separate, manually managed variant in this launcher, not an official llama.cpp update target. [S16] |

The outer folder name containing `b9822`/`cuda-13.3` is not the installed engine version. Configuration and validated native/package identity take precedence.

The `b12359` release API reports target commit `b1b770d90d94cad9e9ea5b993bdbc383a522fb28`. Its Windows x64 packages include both:

- `llama-b12359-bin-win-cuda-13.5-x64.zip` + `cudart-llama-bin-win-cuda-13.5-x64.zip`;
- `llama-b12359-bin-win-cuda-12.4-x64.zip` + `cudart-llama-bin-win-cuda-12.4-x64.zip`.

Use the selected release's SHA-256 metadata and a complete **same-release, same-CUDA-minor** pair. Do not mix stable EXEs with nightly GGML/runtime DLLs, or official binaries with Prism/Poly/KVMem libraries. Driver-reported CUDA support is not an installed CUDA Toolkit requirement or a reason to overwrite existing packages. [S2]

## 3. Current integration boundaries and gaps

Paths below are relative to `gui/LlamaManager/` unless stated otherwise.

| Area | Current source evidence | Adaptation implication |
|---|---|---|
| Shared architecture | `Engines/BackendRegistry.cs`, `IEngineBackend`/`IServerBackend`, `EngineArbiter.cs`; GUI, CLI and autostart delegate to shared C# services. | No new standalone PowerShell lifecycle or duplicate v1 backend is needed. Retain process-identity and GPU-start protections. |
| Official EXE/HTTP | `Engines/LlamaCppBackend.cs` delegates to `Services/ServerService.cs`; `DetachedProcess.cs` creates the external EXE. P/Invokes found in launcher code target Windows APIs, not llama's C API. | Native ABI breakage is inside the engine package, not this adapter's API. Package consistency still matters. |
| Health / status / benchmark | `ServerService.TestApiReadyAsync` uses loopback `/health`; `SpeedTestService.cs` uses `/v1/models` and streaming `/v1/chat/completions`; `SpeedMonitorService.cs` consumes `/slots`, engine-specific status and logs. | Verify HTTP bodies, SSE completion/error/cancellation and timing fields, not just TCP connectivity or HTTP 200. |
| Authentication | These llama HTTP clients do not attach an API key. `ServerService` logs the launch argument string. | Authenticated inference/telemetry can fail with 401 while public health succeeds. Add scoped credential support and redaction before claiming authenticated-server support; do not log raw `--api-key` values. |
| Model/variant routing | `ConfigService.GetLlamaServerPathFor` honors committed official paths and routes Prism models to the separate Prism EXE. `LlamaCppBackend.InspectAsync` primarily checks GGUF metadata. | A metadata-level `Runnable` report is not proof a new architecture, quantization or projector loads. Probe capabilities for the **resolved EXE/model route**, not only the default official engine. |
| Arguments / schema | `LlamaCppParameters` now exposes unset-by-default MoE cache MiB/CPU share and optional FP8 choices. `LlamaCppFeatureGate` probes the resolved EXE only when these are requested, caches at most32 executable-version entries, bounds probes at5s/256Ki characters per stream. | Existing q8_0 and raw options remain. GUI validates before stopping an old service; shared native start and benchmark also gate before creation. Help capability is not proof that every model/kernel supports the requested combination. |
| Presets / fit | `PresetService.Plan` still emits legacy explicit preferences; its comment and GPU-layer UI help no longer promise fit will shrink explicit choices. | Automatic-mode policy redesign remains separate; existing measured presets were not rewritten. See §5. |
| Acquisition / doctor | `BuiltInInstallation.ParseLlamaVersion` already normalizes modern semver + build and older numeric output. `EngineCatalog.ManagedLlamaCppPlan` pairs matching assets in immutable version roots. `EngineAcquisitionService.LatestPlanAsync` scans at most 30 releases and chooses the highest compatible build; `DoctorService` shares that selection. | Keep these protections. A plain `v1.0.0` tag is not a comparable `bNNNNN` install plan, and no explicit stable→nightly mapping/channel is present. Expose the selected channel and both identities, rather than presenting any newer nightly as the pinned stable release. |
| Driver / variants | `ParseDriverCudaVersion` handles both `CUDA Version` and `CUDA UMD Version`; planner chooses a complete pair within its driver/GPU policy. Prism is reported separately by doctor and excluded from official overwrite. | Old hard-coded CUDA13.4 legacy-plan comments are not the managed updater's selection logic. Test the managed path with driver13.5 fixtures, unknown-driver fallback and pre-Ampere constraints. |
| Poly / KVMem | `Engines/StrataBackend.cs`, `Services/StrataService.cs` and tokenizer/runconfig services launch matching Python HTTP services/native workers. KVMem has its own bundle and configuration contract. | Do not copy official v1 flags into Poly or KVMem, or replace their bundled workers with official llama.cpp. |

## 4. What v1.0 breaks—and what it does not

### C API / ABI versus this application

The official release removes `llama_set_warmup`, 35 deprecated functions and `defrag_thold`; promotes NextN/speculative declarations from `llama-ext.h`; renames `llama_set_embd_state_src` to `llama_set_ctx_state_src`; and adds `moe_cache_size`/`moe_cache_cpu_share` to `llama_context_params`. It also changes mtmd interfaces and removes `clip.h`. Direct native bindings/plugins must update declarations and rebuild; unrelated native DLLs are not interchangeable. **None of those functions/structs is directly consumed by this launcher.** [S1, S11]

One removal *does* cross our command-line boundary: **`--defrag-thold` was removed**. No generated use/reference was found in launcher/test source. Old user-supplied raw presets may still contain it; report that specific unsupported option with an actionable message rather than deleting arbitrary unknown options.

### Existing CLI and HTTP remain the starting point

Pinned `common/arg.cpp` retains the launcher's current option families: `--host`, `--port`, `-m`, `-t`/`-tb`, `-c`, `-np`, `-b`/`-ub`, `-ngl` (number/`auto`/`all`), `--fit`, `-fa`, `-ctk`/`-ctv`, `--cpu-moe`, `--n-cpu-moe`, `--reasoning`, `--reasoning-budget`, `--jinja`, `--spec-type`, `-md`, `--mmproj` and `--embedding`. **There is no evidence that the existing generated flag vocabulary requires wholesale replacement.** Recognition does not prove the chosen model/GPU supports every value or combination. [S4, S9]

The pinned server still documents `/health`, `/v1/models`, `/v1/chat/completions`, `/slots`, embeddings and the agent-facing Responses/Anthropic-compatible routes. Additions such as `/v1/audio/speech` are optional new capability, not a migration requirement. Structured-output/tool-argument parsing fixes and new model templates can change output behavior; exercise actual client contracts and do not assume all engines implement identical extensions. [S1, S9]

## 5. Memory parameters: precise semantics, conservative defaults

| Option / feature | Pinned semantics | Launcher policy |
|---|---|---|
| `--moe-cache-mib N` | **GPU cache budget** in MiB for MoE experts whose backing weights are kept on the CPU; nonnegative integer; **0 disables**. Multi-GPU allocation follows the layer/`--tensor-split` distribution. | This is not a RAM budget, a KV cache or SSD paging. Show its VRAM cost alongside KV, compute, projector and resident weights. Do not choose a fixed “safe” value for every 6GB card. |
| `--moe-cache-cpu-share F` | Finite fraction **0–1** to request: share of cache **misses** computed on CPU rather than uploaded to GPU once the cache is full; upstream default **0.0**. | Not the fraction of all experts kept in RAM, CPU thread count, or a percent from 0–100. 0 uploads misses; 1 computes those misses on CPU. No meaningful caching benefit should be promised with cache disabled. |
| `--cpu-moe` / `--n-cpu-moe` | Existing controls for keeping expert weights on CPU remain supported. | A GPU cache complements CPU-backed expert placement; it is not a spelling change or automatic replacement. Preserve explicit placement and benchmark interactions. |
| `-ctk f8_e4m3` / `-ctv f8_e4m3` | New OCP FP8 KV type; release notes also describe optional static per-tensor scales. Existing f16/q8_0/etc. remain available. | Optional selectable capability, **not the new default**. Preserve current q8_0/f16 presets; compare quality, memory and speed on the actual backend/model. CUDA13.5 availability alone does not establish FP8 kernel support or a speedup. Do not conflate FP8 KV with NVFP4 model weights. |
| `--fit on` | Adjusts **unset** parameters. Pinned fit code leaves a user-set context unchanged; allocation adjustment can abort when GPU layers, tensor split or buffer overrides are already set. | Explicit `-ngl all`/`999`, `-c 4096`, `--n-cpu-moe`, batch and ubatch settings must not be described as guaranteed to shrink. A failed fit attempt is not proof that the subsequent model load will fit. |
| `--fit-target` / `--fit-ctx` | Target free-memory margin per device / minimum context for automatic fitting; not hard overall memory caps. | Consider advanced controls or help text; keep estimated versus observed allocations distinct. If designing an automatic preset, use upstream automatic/unset semantics deliberately, with tests—not silent changes to saved tuning. |
| `--ple` / `--shard` | Additional external PLE/engram GGUF loading. | Optional future scope. Review explicit auxiliary-path persistence/validation; ordinary split-GGUF or mmproj pairing does not by itself certify external PLE support. |

Sources: pinned option parser/defaults and fit implementation [S4, S5, S10]; FP8 and external-shard changes [S1]. The inspected local main args combine explicit layers/context/batches and CPU-MoE placement with fit, so the fit caveat applies to a real saved configuration pattern, not merely a theoretical example.

## 6. Security defaults and local-agent impact

**First distinguish two different uses of “tools”.** An agent sending tool schemas in a chat request and executing returned calls itself is not asking llama-server to expose its own filesystem/shell tools. pi/Claude Code/Codex-style local HTTP clients generally need the former, not `--agent`, `--tools`, or server MCP hosting. Do not enable those features merely because the client is an agent.

| Boundary | v1.0 behavior / likely impact | Safe adaptation |
|---|---|---|
| Ordinary loopback inference | Existing `127.0.0.1` clients without browser-origin headers and without server tools do not acquire a new mandatory API-key requirement. | Preserve loopback defaults. Separately verify protocol, templates and streaming; do not add opt-out security flags to normal presets. |
| Server-side tools, MCP hosting or UI MCP proxy | Enabling these on a **non-loopback bind without an API key** makes startup fail, unless `--allow-unauthenticated-tools` is explicitly set. Includes wildcard binds even if the caller is local. Default opt-in flag is false. [S6] | Prefer loopback or authenticated access. Treat `--allow-unauthenticated-tools` as an exceptional, explicitly consented trust decision—not an automatic retry after failure. It is not needed for client-side tool calls. |
| CORS / cross-site request protection | Plain inference still defaults to **`--cors-origins *`**. Tools/MCP/proxy automatically change the default to **`localhost`** unless explicitly configured. Here `localhost` means **same-origin**, not every port/app on localhost. The implementation rejects disallowed requests, not merely hides response headers. [S4, S5, S7] | A browser frontend on another port, or `localhost` versus `127.0.0.1`, may need its exact trusted origin configured. Prefer a single exact origin or same-origin proxy; test actual response/preflight headers for any multi-origin setup. Never solve compatibility by blanket wildcard/credential relaxation. |
| DNS rebinding / Host | With restrictive CORS configured, loopback requests also validate Host, including allowed authority/port. A custom DNS name resolving to loopback can be rejected even without Origin. **The pinned code short-circuits these origin/Host checks for `cors_origins == "*"`; do not claim unconditional protection.** [S7] | Keep numeric loopback URLs for launcher probes. For intentionally named frontends/proxies, validate narrowly scoped Host/origin settings. CORS is not authentication or protection from arbitrary local native software. |
| Private media URLs / SSRF | `image_url` and other HTTP(S) media fetches use public-address resolution by default. Loopback, LAN and link-local destinations are denied unless **`--media-allow-private-urls`** is enabled; this default is false. [S5, S8] | An agent can connect to local llama-server successfully but fail when its attachment URL points to another local service. Prefer an inline supported base64/data URI, or carefully scoped `--media-path` for intentional file input. If private fetching is indispensable, isolate/authenticate the service and assess SSRF exposure before explicit opt-in. This flag is not a per-host allowlist. |
| UI MCP CORS proxy | Off by default; intended for **public** MCP endpoints without CORS headers. Local/private proxy destinations are refused. This is distinct from server-side MCP child configuration and from media URL opt-in. [S4, S9] | Do not use `--media-allow-private-urls` as a proxy “fix”. Where appropriate, connect the frontend directly to a trusted local MCP endpoint with that endpoint's own auth/CORS policy, or use explicitly configured trusted server-side MCP. Do not auto-enable either route. |
| Router child instances | Browser-like requests directly to router children are rejected with 403; browser traffic must pass through the router. [S7] | Existing single-model direct-EXE launches are not router deployments. Any future router integration must publish the router URL, not a child port. |
| API-key authentication | `/health` is public; protected requests accept `Authorization: Bearer ...` or `X-Api-Key`. [S7, S9] | Fix the launcher's missing credential propagation before declaring authenticated telemetry/benchmarking supported. Key-file references avoid putting the key itself in launch logs; headers must be per session/backend, redacted and never forwarded across redirects/origins. A public healthy response is not authenticated inference readiness. |
| MCP cancellation / HF token | Release notes report cancellation/notification of server MCP calls on disconnect/timeout and restriction of `HF_TOKEN` to Hugging Face hosts. [S17, S18] | Test aborts and error propagation; do not turn cancellation into success or restart a healthy server. If a non-HF mirror relied on inherited HF credentials, handle its authentication explicitly rather than broad token forwarding. |

Audit both raw args and inherited environment (`LLAMA_ARG_*`, `LLAMA_API_KEY`, etc.) when diagnosing a change. An apparently safe argument string does not establish the effective policy. Changes to reverse-proxy auth, browser headers or attachment transport belong in focused acceptance cases; **no blanket disabling is recommended**.

## 7. Other-engine compatibility work

### PolyStrata .7 → .8/.9

- The checked releases resolve to **`simplexai-labs/PolyStrata`**. The catalog now uses this canonical repository. Legacy `VecSzn/PolyStrata` release metadata and download receipts remain unchanged/readable; regression verifies old poly.7 hash-backed version detection and same-version reuse without downloads. No artificial ownership migration was needed. [S12, S13]
- Keep native ZIP, matching tag's `serve/`/tools, BUILD metadata, CUDA wheel pins and private Python environment together. Repository redirects or a successful package-layout check do not validate inference.
- .8 changes expert-RAM behavior: when off-VRAM experts do not fit available RAM, an automatic RAM budget/cache plus SSD reads replaces the old all-mapped behavior; `--ram-experts-mib -1` requests the old behavior. It also adds opt-in `--lock-experts`, changes default thread allocation, and extends selected model/KV behavior. Audit existing explicit threads and memory settings; do not silently force locking or revert the new default. [S13]
- .9 adds **opt-in `--batch-mtp`**, NVFP4 GGUF support and vision-prompt prefill changes. Batch MTP requires actual parallelism and suitable draft weights; this launcher must not claim it is enabled because the engine updated. NVFP4 prefill's RTX50-specific experimental option is not a recommendation for this RTX3060. [S12]
- Recheck `StrataBackend` runconfig keys, tokenizer preparation, emitted flags, explicit environment overrides and `PolyMetricsService` parsing against the matching package. Keep unsupported/model-specific KV values out of generic promises.
- Most importantly, **.8 and .9 explicitly lack release-specific Windows execution validation**. Linux/Metal results quoted by upstream and old local .7 results cannot close that gap. Keep .7 available until a separately authorized Windows real-model comparison passes.

### KVMem and Prism

- KVMem v0.19.1's unified release includes NInfer and a PolyStrata backend based on **poly.6**. The launcher's existing KVMem integration is the dedicated PolyStrata/GGUF route; upstream NInfer features do not imply this app supports `.ninfer`, and the Radiance release does not imply `.rad` support. Do not independently replace the embedded Poly worker with .9. [S14, S15]
- Exact GPU-family asset matching and complete-bundle validation must continue rejecting AMD/unknown/wrong-family packages. The new release's appearance is a useful regression fixture, not a reason to relax tag/asset matching.
- Prism's public release supports its fork-specific Q1_0 one-bit quantization. Retain actual GGUF/variant routing and its separate libraries. “Manually managed” means this launcher lacks a Prism acquisition adapter, **not** that upstream has no public binaries. No newer Prism update is indicated by the checked snapshot; do not graft official v1.0 flags or C API assumptions onto b12128 without probing/testing. [S16]
- Keep generic process plugins working through their declared executable, readiness path and schema. Adding v1 capabilities must not leak llama-only flags into other backends or reset their per-model settings.

## 8. Prioritized adaptation checklist

Unchecked items are recommendations / acceptance gates, not completed implementation.

### P0 — before describing a release as safely adapted

- [ ] **Version/channel identity:** explicitly represent `v1.0.0 ↔ b12359`; distinguish b12375 nightly. Make doctor/update/UI agree, preserve immutable package roots and old-session identities, and never auto-overwrite Prism or user-managed installs.
- [ ] **Driver/package correctness:** fixture-check 621.24 / CUDA UMD13.5 selection, complete matching pairs, hash verification, CUDA12.4 fallback, unknown/pre-Ampere hardware, cancellation and rollback. A current driver does not validate a binary.
- [ ] **Security-aware HTTP:** retain loopback/default-disabled dangerous features; document clear 401/403/startup diagnostics, exact-origin frontend behavior and private-attachment alternatives. Add scoped credential handling/redaction where authenticated access is supported.
- [ ] **Preset truthfulness:** remove the guaranteed-fit/fallback claim and verify generated automatic-mode semantics. Preserve explicit saved values, unknown args, model associations and running sessions. Flag removed `--defrag-thold` specifically.
- [ ] **Cross-engine acquisition:** preserve KVMem NVIDIA30 v0.19.1, isolate Prism, resolve Poly's canonical repository without damaging provenance, and prominently retain the .8/.9 Windows validation caveat.

### P1 — capability and regression coverage

- [x] **Parameters:** MoE GPU-cache MiB/CPU-share, optional FP8, range/finite checks, raw-argument preservation, corrected fit help and actual-help capability gating integrated; no new defaults injected. Engine-specific model/kernel validation remains upstream.
- [x] **Launcher regression:** pinned release/build/driver fixtures, same-release URL pairing, schema/raw arguments, telemetry shapes, old/Prism isolation and capability cache/refusal tests integrated; 174 groups passed. Authenticated-server security integration is NOT included in this checked item.
- [x] **Repository routing:** canonical Poly source/query URL and legacy metadata/receipt preservation tested. No installed directory or user path was renamed; real Poly.9 inference acceptance remains open.
- [ ] **Protocol contracts:** OpenAI chat streaming/non-streaming, tool-call arguments, reasoning fields, structured output and client-specific Responses/Anthropic use. Check `/slots`, timings, missing usage, errors and cancellation separately; SSE events are not token counts.
- [ ] **Poly .9 compatibility:** tagged service/native/runtime coherence, memory-default changes, optional batching/MTP, supported model/KV combinations and `/metrics` compatibility. Keep KVMem's frozen worker independent.

### P2 — optional expansion after the baseline is accepted

- [ ] External PLE/shards, new architectures/projectors and speech endpoints, each with explicit metadata/file/HTTP coverage.
- [ ] Per-model MoE-cache/CPU-share/FP8 performance and quality tuning; no hardware-independent “best” defaults.
- [ ] A dedicated Prism release adapter only if separately scoped, with variant-native identity and runtime verification; not an official-llama update shortcut.

## 9. Validation gates: do not merge their meanings

| Gate | What it can establish | What it cannot establish |
|---|---|---|
| **This read-only review** | Tagged upstream option/security semantics, package availability, current adapter boundaries and likely gaps. | That any changed code compiles, a Windows EXE runs, or a model generates correctly. |
| **Pure/fixture regression** | Release mapping and filtering; driver parsing; flag/default persistence; preserved configs/provenance; deliberate reject cases. Candidate locations include `BuiltInUpdateRegression`, `DoctorRegression`, `KvmemAcquisitionRegression`, parameter/UI and engine-isolation tests. | Runtime DLL resolution, upstream enforcement or GPU/model correctness. |
| **Isolated fake-process/HTTP regression** | Launcher lifecycle/identity/timeout/cancellation behavior; 401/403/503 diagnostics, malformed/error/truncated SSE, missing usage and guarded cleanup. Existing process/stream/readiness tests are useful seams, not v1 proof. | Real upstream CORS/SSRF enforcement, native tool behavior or model/template output. |
| **Authorized tagged-server security integration** | Test actual allowed/denied origins and Host/port, ordinary no-Origin local client, auth, non-loopback tools refusal, private media rejection, public→private redirect/DNS cases, proxy rejection and disconnect cancellation. Use controlled endpoints and harmless tool fixtures, never real metadata/internal targets. | Model quality or long-context GPU capacity. This gate was not run here. |
| **Separately authorized Windows real-model validation** | Matching runtime load, actual offload/VRAM/RAM, cold/warm startup, inference, model/projector compatibility, quality/performance and rollback. | Universal compatibility with every quantization/architecture or future nightly. |

For later real-model acceptance, first record exact executable/build/hash, runtime pair, driver/GPU, model/shard/projector identities and effective args/environment. Compare the retained b12182 baseline with pinned b12359 using a small dense model, a representative existing MoE/MTP model and an applicable vision/embedding case; test long-context memory and tool/structured-output behavior only within the machine's budget. Compare MoE cache/CPU-share/FP8 one change at a time, recording TTFT, prefill/decode, peak memory and correctness rather than only startup success. Evaluate Poly .9 separately against .7; use Prism-specific weights only with the Prism path and preserve KVMem's own acceptance boundary.

Use a separate version directory and isolated configuration/ports; never stop a user's service for ordinary regression. Real GPU/model testing needs explicit scheduling/authorization and remains subject to GPU arbitration. Historical GPU successes in other documents apply only to their recorded versions/settings. The 174-group result above is launcher regression only; no new upstream-engine readiness or real-model result is asserted.

## Official sources

Source tags are deliberately pinned; “latest” above refers only to the checked release snapshot.

- **S1 — v1.0.0 release notes and binary mapping:** https://github.com/ggml-org/llama.cpp/releases/tag/v1.0.0
- **S2 — pinned binary release/assets:** https://github.com/ggml-org/llama.cpp/releases/tag/b12359 ; metadata: https://api.github.com/repos/ggml-org/llama.cpp/releases/tags/b12359
- **S3 — separately checked nightly:** https://github.com/ggml-org/llama.cpp/releases/tag/b12375
- **S4 — pinned option parsing, help and tools/CORS defaults:** https://github.com/ggml-org/llama.cpp/blob/v1.0.0/common/arg.cpp
- **S5 — pinned parameter defaults:** https://github.com/ggml-org/llama.cpp/blob/v1.0.0/common/common.h
- **S6 — non-loopback tools authentication startup guard:** https://github.com/ggml-org/llama.cpp/blob/v1.0.0/tools/server/server.cpp#L262-L271
- **S7 — origin/Host, auth and router-child enforcement:** https://github.com/ggml-org/llama.cpp/blob/v1.0.0/tools/server/server-http.cpp#L107-L168 ; https://github.com/ggml-org/llama.cpp/blob/v1.0.0/tools/server/server-http.cpp#L347-L477
- **S8 — public-address resolution and media handling:** https://github.com/ggml-org/llama.cpp/blob/v1.0.0/tools/server/server-common.cpp#L1250-L1328 ; security change: https://github.com/ggml-org/llama.cpp/pull/32115
- **S9 — pinned server options, protocols and MCP/CORS documentation:** https://github.com/ggml-org/llama.cpp/blob/v1.0.0/tools/server/README.md
- **S10 — fitting respects explicit settings:** https://github.com/ggml-org/llama.cpp/blob/v1.0.0/common/fit.cpp#L480-L573
- **S11 — public API pruning:** https://github.com/ggml-org/llama.cpp/pull/30565
- **S12 — canonical PolyStrata .9 release:** https://github.com/simplexai-labs/PolyStrata/releases/tag/v0.1.45-poly.9
- **S13 — PolyStrata .8 release:** https://github.com/simplexai-labs/PolyStrata/releases/tag/v0.1.45-poly.8
- **S14 — KVMem NVIDIA unified release:** https://github.com/kvmem/kvmem/releases/tag/v0.19.1
- **S15 — distinct AMD Radiance release:** https://github.com/kvmem/kvmem/releases/tag/v0.19.rad1
- **S16 — public Prism release/assets:** https://github.com/PrismML-Eng/llama.cpp/releases/tag/prism-b12128-d7b2896
- **S17 — HF token host restriction:** https://github.com/ggml-org/llama.cpp/pull/31730
- **S18 — MCP cancellation:** https://github.com/ggml-org/llama.cpp/pull/32025
