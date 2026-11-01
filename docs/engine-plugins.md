# 引擎插件化架构设计

状态：设计稿 v2（含关键发现后的修订）
范围：把当前「llama.cpp 主路径 + 单独一条 Strata 一次性路径」重构为「一个启动器 + 可插拔引擎后端」，目标统一为「启动一个本地 HTTP 服务，供 pi agent / Claude Code / Codex 等工具接入」。

## 0. 关键发现（决定整体形态）

两个 Strata 分支都**已经自带 HTTP 服务**：`serve/server.py`（Python），提供

- `POST /v1/chat/completions`（OpenAI，流式/非流式）
- `POST /v1/messages`（Anthropic）
- `POST /v1/responses`（Responses API / Codex）
- `GET /v1/models`、`/models`、`/props`、`/slots`、`/health`、`/mcp`、`/`

启动方式：`python -m serve.server --engine strata --config strata-<model>.json --port 8080`。

`runconfig`（`strata-<model>.json`）字段：`exe`、`args[]`、`cwd`、`tokenizer`、`model_name`、`log`、`lib_dirs`、`port`，以及 `reasoning_budget_tokens`、`anthropic_thinking` 等。

PolyStrata 同样在 localhost 提供 OpenAI / Anthropic API。llama.cpp 的 `llama-server` 亦然。

**结论：三种引擎对使用者的接口是同一种——本地 HTTP 服务。** 因此启动器不再需要自己实现「分词→引擎→反分词」的一次性流水线；它只需要：定位服务入口 → 拼启动参数/配置 → 等待 `/health` → 管理生命周期 → 展示状态。这让设计大幅简化，也正好满足「跑起来后接入 pi agent」的需求。

## 1. 背景与目标

### 1.1 现状

- `ServerService`：管理 `llama-server.exe`，HTTP 长驻，有端口、`/health`、会话身份（PID + 创建时间 + 引擎路径）。
- `StrataService`：自己驱动 `strata.exe` 做一次性「文本进 / token 出」，`RunGate` + 文件锁串行——**这条路径绕过了 Strata 自带的 HTTP 服务，导致无法接入 agent**。

两者在 `ConfigService`、`ManagerConfig`、UI 中各自成形；再加 PolyStrata 又要抄一遍。

### 1.2 目标

1. 启动器本体保留 llama.cpp 启动能力，作为内置后端。
2. 通过插件添加另外的加载方式（当前实际需要：Strata `upstream`、PolyStrata）。
3. 三种后端统一为「本地 HTTP 服务」，都能直接被 pi agent 接入。
4. 单点生命周期：GUI / CLI / autostart 共用后端注册表与仲裁（AGENTS.md）。
5. 兼容性前置：任何后端都能对选定 GGUF 报「能不能跑、缺什么、需多少显存/内存」。
6. 故障隔离、语言无关。

### 1.3 非目标

- 不把第三方引擎源码编进启动器程序集。
- 不重写 Strata 的 `serve/server.py`（它已处理 chat 模板、工具调用、reasoning、Responses API 等）。
- 不为每个后端写死 UI；参数由 schema 驱动。

## 2. 决策（已确认）

| 项 | 决定 |
|---|---|
| `strata-cyber` 分支 | **归档**：仅作代码参考，不再修改、不再实际使用。不进入后端注册表。 |
| 使用者接口 | 统一为本地 HTTP 服务（OpenAI/Anthropic 兼容），供 pi agent 等接入。 |
| 一次性（OneShot）后端 | **取消**。原「提示词→回答」面板改为运行中 HTTP 服务的客户端（快捷测试），不再单独管理引擎生命周期。 |
| 配置迁移 | **迁移即删**：读旧字段合成新结构，保存时移除旧字段。 |
| 插件机制 | **外部进程 + 清单（manifest）**：语言无关、崩溃隔离、无需重编译启动器，最稳定且优雅。不采用 `AssemblyLoadContext` 程序集插件。 |
| 范围 | 全部阶段（含动态参数 UI）。 |

## 3. 设计原则

1. **进程即插件**：后端 = 一个可执行文件/命令 + 一份能力清单。启动器只负责拉起进程、探测就绪、管理生命周期。语言无关、崩溃隔离。
2. **统一后端形态 = `HttpServer`**：所有后端都有端口与就绪探测（`/health` 或端口监听）。不再区分 OneShot。
3. **内置驱动 + 清单驱动并存**：llama.cpp 与 Strata 用富内置驱动（会话恢复、Prism 引擎选择、Strata runconfig 生成）；其余后端用清单驱动的通用进程驱动。
4. **配置迁移即删**：旧字段只读一次，合成 `EngineSettings` + `ActiveSession`，保存后删除。
5. **不阻塞 UI**：探测、兼容性、就绪等待全部异步、缓存有界。
6. **共享显存互斥**：同一时刻只允许一个 GPU 重负载后端运行，集中在 `EngineArbiter`。

## 4. 架构总览

```
+---------------------------------------------------------------+
|  UI (MainWindow.*) | CLI (CommandLineService) | Autostart       |
+---------------------------------------------------------------+
                  | 统一调用
                  v
+---------------------------------------------------------------+
|  EngineArbiter   资源仲裁：GPU 互斥、启动/停止串行、取消         |
+---------------------------------------------------------------+
                  |
                  v
+---------------------------------------------------------------+
|  BackendRegistry  内置后端 + 插件清单发现                       |
|   IEngineBackend:  Descriptor / Parameters / Probe / Inspect    |
|   IServerBackend:  BuildLaunch / Start / Stop / Ready / Port    |
+---------------------------------------------------------------+
   |                    |                    |
   v                    v                    v
  LlamaCppBackend   StrataBackend       StrataBackend
  (内置)            (内置, upstream)     (内置, polystrata)
    |                    |                    |
    v                    v                    v
  llama-server.exe   python -m           python -m
                     serve.server        serve.server
                     --engine strata     --engine strata
                     --config <json>     --config <json>
```

> 注：PolyStrata 最初以 `plugin.json` 清单形式接入，后来发现其 `strata-poly.exe` 与 Strata 一样只说**行协议**（`--serve`），HTTP API 由 `serve/server.py` 提供。因此它与 Strata 是同一形态，改为**内置 `StrataBackend` 的一个 profile**（`polystrata`），而非清单插件。清单机制仍保留给真正「自带 HTTP 服务」的第三方引擎。

## 5. 核心抽象

新增目录 `gui/LlamaManager/Engines/`。

### 5.1 描述与状态

```csharp
public sealed record BackendDescriptor(
    string Id, string DisplayName, string Description,
    bool BuiltIn, bool GpuHeavy);

public sealed record BackendStatus(
    bool Available, string? ExecutablePath, string? Version, string? Problem);

public sealed record ResourceEstimate(long VramBytes, long HostRamBytes, string Note);

public sealed record CompatibilityReport(
    bool Runnable,
    IReadOnlyList<string> Blockers,
    IReadOnlyList<string> Notes,
    string Summary,
    ResourceEstimate? Estimate);
```

### 5.2 参数 schema（驱动动态 UI）

```csharp
public enum ParamType { Integer, Decimal, Text, Boolean, Choice, FilePath, FolderPath, Multiline }

public sealed record ParamChoice(
    string Value,        // 真正写进配置/命令行的值
    string Label,        // 展示文案
    string? Note,        // 下拉里的一行说明
    bool Recommended);   // 加「推荐」后缀

public sealed record ParameterSpec(
    string Key, string Label, string Group, ParamType Type,
    string? Default, string? ToolTip,
    double? Min, double? Max, ParamChoice[]? Choices,
    string? VisibleWhen,   // 小型布尔表达式，见 §9
    string? Help,          // 「?」按钮正文；为空回退 ToolTip
    string? Unit,          // tokens / MiB / 线程
    bool Editable,         // 下拉可直接输入
    bool Advanced,         // 默认折叠进「高级参数」
    string? RangeHint);    // 如 "256–1,048,576，推荐 4096"

```

面板按 `Group` 的声明顺序生成控件，`Advanced=true` 的字段收进折叠区，`VisibleWhen` 为假的字段隐藏但保留已填的值（见 §9）。

### 5.3 后端契约

```csharp
public interface IEngineBackend
{
    BackendDescriptor Descriptor { get; }
    IReadOnlyList<ParameterSpec> Parameters { get; }

    Task<BackendStatus> ProbeAsync(CancellationToken ct);
    Task<CompatibilityReport> InspectAsync(string modelPath, CancellationToken ct);
    Task<EngineStatus> StatusAsync(EngineSession? session, CancellationToken ct);
}

public interface IServerBackend : IEngineBackend
{
    LaunchPlan BuildLaunch(EngineStartRequest request);          // 命令行 + 环境 + 工作目录 + 配置内容
    Task<EngineSession> StartAsync(EngineStartRequest request, ILogSink log, CancellationToken ct);
    Task StopAsync(EngineSession session, ILogSink log);
    Task<bool> ReadyAsync(EngineSession session, CancellationToken ct);
    int ResolvePort(EngineStartRequest request);
    string HealthUrl(EngineSession session);
    void OpenWebUi(EngineSession session);
}

public sealed record LaunchPlan(
    string FileName, IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyDictionary<string, string> GeneratedFiles);   // 例如 Strata runconfig JSON
```

### 5.4 会话身份

```csharp
public sealed record EngineSession
{
    public string BackendId { get; init; } = "";
    public int ProcessId { get; init; }
    public long StartTimeUtcTicks { get; init; }
    public string ExecutablePath { get; init; } = "";
    public string ModelPath { get; init; } = "";
    public string Arguments { get; init; } = "";
    public int Port { get; init; }
    public Dictionary<string, string> Extra { get; init; } = new();
}
```

会话身份校验沿用 `ServerService.ValidateProcess`：PID + 创建时间 + 可执行路径三者一致。

## 6. 内置后端

### 6.1 LlamaCppBackend（`llamacpp`）

- 定位 `ConfigService.GetLlamaServerPath` / `GetPrismServerPath`（保留 Prism 选择）。
- 参数：与其它引擎一样由 schema 生成（`Engines/LlamaCppParameters.cs` 声明 16 个字段）。
  与别的后端不同，llama.cpp 的值保存在原始参数串 `Args` 里，所以 `LlamaCppParameters`
  负责双向投射：`ReadFromArgs` 把 flag 投影成面板值，`Apply` 只改写自己拥有的那个 flag，
  手写 flag 原样保留。「高级」页的原始参数框仍是任意 flag 的兜底，输入后即时回填面板。
- 启动/停止/端口恢复/会话身份：内部调用现有 `ServerService` 逻辑，不复制。
- 就绪：`GET /health`。

### 6.2 StrataBackend（`polystrata`）

- **不再自己跑一次性推理**，而是启动 PolyStrata 自带 HTTP 服务：
  `python -m serve.server --engine strata --config <runconfig.json> --port <port>`。
- 定位：
  - 包根目录（含 `engine-poly/strata-poly.exe`）。
  - `serve/server.py` 所在目录（PolyStrata 源码根）。
  - Python 解释器（留空时用 `venvs/polystrata` 专属虚拟环境）。
- 生成 runconfig（`LaunchPlan.GeneratedFiles`）：`--model <gguf>`、`--max-context`、`--vram-reserve-mib`、`--tail-skip`、`--kv`、`--threads`。只写引擎真正接受的 flag——`strata-poly` 对未知参数直接 `unknown option` 退出，不像上游那样只打印 `ignoring`。
- 探测：引擎 exe、`serve/server.py`、Python 3.10+ 三项齐备。
- 就绪：`GET /health`。
- 兼容性：`PolyStrataCompatibility.Analyze`（专家格式白名单 + MTP 草稿块提示）。
- 参数：端口、上下文、显存预留、尾部跳过、KV 格式、CPU 线程、`reasoning_budget_tokens`、`anthropic_thinking`。

> **已退役：`strata-upstream`（`strata.exe`）。** 它唯一独有的 `qwen4exp` 家族（Qwen3.8-Flash-Next）最小 29.6 GB 文件、官方要求 32 GB 内存，本机 23.4 GB 跑不动；在共同支持的 qwen35moe 上比 `polystrata` 慢约 30%，且没有 `--tail-skip`/`--kv`/`--vram-reserve-mib`。注册、下载计划、面板字段与 CLI 分支均已移除。`StrataService`/`StrataPackService`/`StrataCompatibility` 保留：分词表准备和 `ProcessPluginBackend` 仍在复用它们。详见 `docs/CHANGELOG-DECISIONS.md` 2026-10-18 条。

> 说明：`strata-cyber`（pack 路径）已归档，不进入注册表。

## 7. 插件机制（外部进程 + 清单）

### 7.1 发现

扫描：
- 用户级：`ConfigService.GetManagerRoot()/plugins/<id>/plugin.json`
- 随安装：`LlamaCppRoot/engines/<id>/plugin.json`

内置后端不写清单，由 `BackendRegistry` 直接注册。

### 7.2 清单 schema（`schema:1`）

```json
{
  "schema": 1,
  "id": "polystrata",
  "displayName": "PolyStrata (strata-poly)",
  "description": "小显存下的 MoE 专家缓存引擎",
  "gpuHeavy": true,
  "executable": { "windows": "strata-poly.exe", "linux": "strata-poly" },
  "detect": { "versionArg": ["--version"], "versionRegex": "([0-9]+\\.[0-9]+\\.[0-9]+)" },
  "launch": {
    "workingDirectory": "{installDir}",
    "arguments": ["--model", "{model}", "--port", "{port}", "--max-context", "{context}", "--max-new", "{maxNew}"],
    "environment": {}
  },
  "ready": { "type": "http", "path": "/health", "timeoutSeconds": 180 },
  "parameters": [
    { "key": "port",    "label": "端口",   "group": "基本", "type": "Integer", "default": "8080" },
    { "key": "context", "label": "上下文", "group": "基本", "type": "Integer", "default": "4096", "min": 256, "max": 1048576,
      "unit": "tokens", "rangeHint": "256–1,048,576，推荐 4096", "help": "【上下文】\n上下文越大，KV 缓存占用显存越多。" },
    { "key": "maxNew",  "label": "生成长度","group": "基本", "type": "Integer", "default": "256",  "min": 1,   "max": 65536 },
    { "key": "quant",   "label": "量化",   "group": "基本", "type": "Choice",  "default": "q8_0",
      "choices": [
        { "value": "q8_0", "label": "q8_0 (8-bit)", "note": "画质优先", "recommended": true },
        { "value": "q4_0", "label": "q4_0 (4-bit)" }
      ] },
    { "key": "vramReserve", "label": "显存预留", "group": "基本", "type": "Integer", "default": "256",
      "visibleWhen": "profile==polystrata" },
    { "key": "debug", "label": "调试日志", "group": "高级", "type": "Boolean", "default": "0", "advanced": true }
  ],
  "logs": { "perfRegex": { "prefill": "prefill .* ([0-9.]+) tok/s", "decode": "decode .* ([0-9.]+) tok/s" } },
  "compatibility": { "driver": "polystrata-matrix" }
}
```

- `arguments` 里的 `{model}` `{port}` `{context}` 等由参数值替换。
- `ready.type`：`http`（GET `path` 200）或 `tcp`（端口监听）。
- `compatibility.driver`：`none` / `strata-matrix` / `polystrata-matrix`（启动器内置的已知矩阵，见 §8）。
- 参数项的 v2 可选字段（向后兼容，`schema` 仍为 `1`）：
  - `help`、`unit`、`rangeHint`：面板上的说明、单位与范围提示；
  - `editable`：`Choice` 下拉允许直接输入；
  - `advanced`：默认折叠进「高级参数」；
  - `visibleWhen`：布尔表达式，语法见 §9；
  - `choices` 既可用字符串数组（旧写法：`["q8_0", "q4_0"]`），也可用对象数组（带 `label` / `note` / `recommended`）。

### 7.3 隔离与版本协商

- 插件是独立进程，启动器不加载其二进制。
- `detect.versionArg` + 正则得到版本；`schema` 不匹配时**禁用插件并提示**，而非崩溃。
- 清单缺字段：禁用该插件并在状态里说明。

## 8. 兼容性检查插件化

现状：`StrataCompatibility` 是只服务 Strata 的静态矩阵。目标：

- 提升为 `ICompatibilityCheck`，按 `driver` 选择：
  - `none`（llamacpp）：恒 `Runnable`，附带量化与层数说明（保留 Prism/TQ1 例外）。
  - `strata-matrix`：现 `StrataCompatibility`（格式矩阵 + 结构校验）。
  - `polystrata-matrix`：其矩阵（Q2_K/Q3_K/Q4_K/IQ2_S/IQ3_XXS/Q4_0/Q4_1/Q5_0/Q5_1/Q6_K/Q8_0/MXFP4
    + MTP 草稿块 + `--tail-skip` 说明）。
- 新增 `ResourceEstimate`：按层数、专家格式、上下文估算显存与主机内存；6GB 卡给「会打页/可能 OOM」警告（不阻断）。
- 统一为 `CompatibilityReport`，UI 用同一控件展示（摘要行 + 详细 ToolTip）。

## 9. 参数 schema 与动态 UI

- 引擎标签页顶部：后端下拉框（`BackendRegistry` 枚举可用后端）。
- 选中后端：参数区按 `ParameterSpec.Group` 生成控件，按声明顺序排列；路径类挂选择按钮；`Choice` 生成下拉；`Boolean` 生成开关；`Advanced=true` 收进「高级参数」折叠区。
- 每个字段显示默认值、单位与范围提示；失焦与启动前校验 `Min/Max`，越界时红框加内联错误并**阻止启动**，绝不静默改写用户输入。
- 分组标题右侧有「恢复默认」，面板顶部有「全部恢复默认」和过滤框（按标签/Key/分组过滤）。
- 参数值存 `EngineSettings.Parameters`；只有用户真正改动过的字段才落盘，未改动的仍由引擎或旧配置块决定。
- 状态区显示：后端可用性、模型兼容性、运行状态（端口/就绪）、最近 tok/s、可复制给 agent 的 base URL。

### 9.1 VisibleWhen 表达式

`VisibleWhen` 是一个刻意做小的布尔表达式，由 `Engines/ParameterVisibility.cs` 求值，不引入脚本引擎：

| 表达式 | 求值依据 |
|---|---|
| `profile==upstream` / `profile==poly` | 后端 profile id |
| `model.moe` / `model.mtp` | 所选 GGUF 的元数据（`GgufService.ReadShards`） |
| `param.expertSlots>0` | 面板内其他参数的当前值 |
| `true` / 空 | 恒显示 |

支持 `==` `!=` `>` `>=` `<` `<=`、`&&`、`||`、`!` 与括号。未知变量或语法错误一律按「隐藏」处理并记一条日志，绝不把异常抛到 UI 线程。

## 10. 配置与持久化（迁移即删）

```csharp
public class ManagerConfig
{
    public string? ActiveBackendId { get; set; }
    public Dictionary<string, EngineSettings> Engines { get; set; } = new();
    public EngineSession? ActiveSession { get; set; }
    // 旧字段（RunningServer/RunningEmbedding/Strata）仅在读取时用于迁移，保存后移除
}

public class EngineSettings
{
    public string ModelPath { get; set; } = "";
    public string ExecutablePath { get; set; } = "";
    public int Port { get; set; } = 8080;
    public Dictionary<string, string> Parameters { get; set; } = new();
    public string? LastStats { get; set; }
}
```

迁移规则：

1. `RunningServer`/`RunningEmbedding` → `llamacpp` 的 `EngineSettings` + `ActiveSession`。
2. `StrataConfig` → `polystrata` 的 `EngineSettings`（模型、包根、Python、端口、上下文等；旧块仅作回退）。
3. 保存时写新结构并**删除旧字段**；沿用 `ConfigStore` 原子替换与损坏恢复。

## 11. 生命周期、并发与仲裁

- `EngineArbiter`：
  - 每个后端一把生命周期锁（沿用 `logs/*.lock` 命名）。
  - GPU 重负载后端互斥：GUI 用 `ConflictFor` 弹统一确认；`IServerBackend.StartAsync` 首行调用
    `EnsureCanStart`，因此 CLI 的 `--start`/`--service`（登录自启动走的正是这条）遇到冲突直接报错退出，
    不会悄悄和另一个引擎同抢显存。
  - 占用判定以“会话进程仍存活”为准：崩溃/手动结束后残留的记录不再触发冲突确认。
  - 启动取消 / 停止 / 状态轮询的令牌与版本控制集中。
- `StatusAsync`：PID + 创建时间 + 引擎路径 + 就绪探测。
- 日志：`DetachedProcess` 标准句柄输出；`IncrementalLogReader`/`LogService` 读取；`ParsePerf` 按后端正则提取 tok/s。
- 快捷测试面板：作为运行中 HTTP 服务的客户端（POST `/v1/chat/completions`），不再管理引擎生命周期。

## 12. 安全与约束（对齐 AGENTS.md）

- 生命周期只在共享 C# 服务中实现；GUI/CLI/autostart 共用注册表与仲裁。
- 不阻塞 UI dispatcher；探测与就绪等待异步且缓存有界。
- 会话身份校验（PID + 创建时间 + 引擎路径）不变，避免 PID 重用误杀。
- 不 stage 用户配置、日志、下载的引擎、模型、构建产物。
- 不为普通回归测试停止用户真实模型服务；测试用隔离 `LLAMA_MANAGER_ROOT` 与假服务器。
- 服务默认绑定 `127.0.0.1`；不默认开放 `0.0.0.0`。

## 13. 分阶段实施

见 [engine-plugins-plan.md](engine-plugins-plan.md)。顺序：

1. 抽象 + llama.cpp 收编（行为不变）。
2. 注册表 + 配置迁移（即删）+ 仲裁。
3. Strata 后端（改为启动 `serve/server.py` HTTP 服务）。
4. 通用进程插件 + PolyStrata。
5. 兼容性插件化 + 资源估算。
6. 动态参数 UI + 引擎标签页。
7. CLI / autostart 走注册表。
8. 测试、发布、文档。

## 14. 资源打包与一键初始化

原则：**小而稳定、许可兼容的资源随启动器打包；大而随版本变化或需 GPU/网络下载的资源由一键初始化脚本获取。** 二者都不让用户手工摆放。

### 15.1 可打包（随启动器分发）

| 资源 | 体积 | 说明 |
|---|---|---|
| 插件清单 `plugin.json` | < 1 KB/个 | PolyStrata 等后端的预置清单 |
| Strata `serve/` Python 源码 + `tools/strata_tokenizer.py` | 数百 KB | MIT；**版本跟随引擎**，见下 |
| 初始化脚本（`bootstrap.ps1` / 内置向导） | 小 | 探测 + 下载 + 校验 + 写配置 |
| 兼容性矩阵 | 代码内 | 已在 `StrataCompatibility` |

注意：`serve/server.py` 约 425 KB 且随 Strata 版本变化。**不硬编码打包其内容**；采用「随引擎版本从对应源码/发布包取用」或「用户指向已安装的 Strata 源码根」，避免版本错配。若确需打包，需带版本标记，引擎探测不匹配时改用下载。

### 15.2 一键初始化（`bootstrap`）

启动器提供「初始化引擎环境」入口（GUI 按钮 + CLI `--bootstrap --backend <id>`），按后端执行：

- **llamacpp**：定位 `llama-server.exe`；缺失则从 `ggml-org/llama.cpp` 发布下载 Windows CUDA 包 + cudart（`llama-<build>-bin-win-cuda-13.4-x64.zip`；CUDA 13.4 优先，12.4 回退）。注意 llama.cpp 的 Windows 二进制位于**预发布（nightly）**，因此取「发布列表中第一个含可用 Windows CUDA 资产的版本」。已安装时执行 `llama-server --version` 提取 build 并与该版本比较；版本不可读则跳过自动更新，不覆盖手工安装。
- **polystrata**：
  1. 定位 `engine-poly/strata-poly.exe` 与 `serve/server.py`；缺失则从 `VecSzn/PolyStrata` 下载 `strata-poly-windows-x64-<hash>.zip`（引擎）与该 tag 的源码归档（`serve/` + `requirements.txt`），校验 SHA-256 后解压。
  2. 探测 Python ≥ 3.10（`py -3` / `python` / 配置项）；缺失则自动安装。
  3. 创建本地 venv（`<ManagerRoot>/venvs/polystrata`）。
  4. `pip install` 依赖：优先读取引擎源码根的 `requirements.txt`（版本已固定）；源码缺失时回退内置列表。
  5. 已安装时顺带检查更新：`BUILD.json` 版本落后于最新发布就安全升级
     （`EngineAcquisitionService.UpdateAsync`，`--update` 是命令行入口）；服务运行时拒绝替换引擎文件。

依赖清单来源（已确认）：优先读取引擎源码根的 `requirements.txt`（版本已固定）；仅在源码缺失时回退到启动器内置的兼容版本表（带版本标记）。启动器**不复制** `requirements.txt` 内容。

下载实现：`EngineDownloadService`（HTTP 流式下载 + SHA-256 校验 + zip-slip 防护解压 +
暂存目录事务发布/失败回滚）、`EngineCatalog`（把 GitHub 发布 JSON 映射为安装计划，纯函数、可测）、
`EngineAcquisitionService`（查询发布 → 下载全部归档 → 解压暂存 → 校验运行状态 → 发布 → 写回配置路径）。

### 15.3 校验与幂等

- 每步幂等：已存在且校验通过则跳过（对齐 Strata `setup.py` 的「已完成的步骤不重做」）。
- 下载校验 SHA-256；失败给出镜像建议（如 HuggingFace 镜像）。
- 网络/权限失败不阻断其它后端：该后端标记「不可用 + 原因」。
- 不下载模型（体积过大）：模型由用户选择本地 `.gguf`；可选提供「打开下载页」链接。

### 15.4 许可

- Strata（Niko1221）与 PolyStrata 均为 MIT；打包其源码需保留版权与许可文本（`THIRD-PARTY-NOTICES`）。
- 仅打包许可允许再分发的内容；引擎二进制与模型按各自发布条款处理，默认走下载而非打包。

## 15. 风险与取舍

| 风险 | 取舍 |
|---|---|
| Strata 走 Python 服务引入 Python 依赖 | 用户环境已有 Python 3.13；缺失时后端报「不可用」并给出原因，不阻断其它后端 |
| runconfig 字段随 Strata 版本变化 | 只写必需键；其余交给引擎默认；探测失败即报告 |
| 抽象过早 | 先只抽 llama.cpp 与 Strata 两个真实后端所需公共面，PolyStrata 作为首个插件验证清单够用 |
| 插件清单表达力不足 | 保留内置驱动兜底；清单无法表达时降级为内置 |
| 6GB 卡估算不准 | 估算只作警告不作阻断；以引擎加载日志与实测为准 |
| 迁移即删不可逆 | 迁移前自动备份旧配置（`ConfigStore` 已有备份）；迁移失败回退旧字段 |
