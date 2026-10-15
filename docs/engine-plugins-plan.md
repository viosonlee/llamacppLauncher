# 引擎插件化实施清单

对应设计：[engine-plugins.md](engine-plugins.md)（v2）。完成条件：代码、自动测试、构建与文档齐备；不主动提交上游 PR。

约定：
- 每阶段结束 `dotnet build LlamaManager.sln -c Release` 0 警告 0 错误，并跑一次回归（修复后只跑一次，不重复刷）。
- 回归使用隔离 `LLAMA_MANAGER_ROOT` 与本地假服务器，不下载 GPU/模型，不停止用户真实服务。
- 所有生命周期实现留在共享 C# 服务；GUI/CLI/autostart 共用，不复制到 PowerShell。
- 阶段 1 结束时行为必须与现状**逐位一致**（llama.cpp 路径零回归），这是地基。

## 已确认决策（开工基线）

1. `strata-cyber` 分支：**归档**，仅作代码参考，不进注册表、不再修改。
2. 使用者接口：统一本地 HTTP 服务（OpenAI/Anthropic 兼容），供 pi agent 接入。
3. 一次性（OneShot）后端：**取消**；原提示词面板改为运行中服务的快捷测试客户端。
4. 配置迁移：**迁移即删**。
5. 插件机制：**外部进程 + 清单**（最稳定/优雅，语言无关、崩溃隔离）。
6. 范围：全部阶段（含动态参数 UI）。

---

## 阶段 0：基线冻结（准备）

- [x] 确认基线：`main` 分支、回归通过、`dotnet build` 干净；记录当前 commit（`a1ef5e6`，回归 36/36）。
- [x] 建立 `gui/LlamaManager/Engines/` 目录与命名空间 `LlamaManager.Engines`。
- [x] 行为基线快照：llama.cpp 启动/停止/端口恢复/取消/会话身份（复用现有回归用例）。
- [x] 明确 `Engines` 与 `Services` 边界：`Services` 保留 IO/配置/日志工具，`Engines` 只放后端与注册表。

## 阶段 1：抽象与 llama.cpp 收编（行为不变）

- [x] 核心类型：`BackendDescriptor`、`BackendStatus`、`EngineSession`、`EngineStartRequest`、`LaunchPlan`、`CompatibilityReport`、`ResourceEstimate`、`ILogSink`、`EngineStatus`。
- [x] 接口：`IEngineBackend`、`IServerBackend`（**无 OneShot**）。
- [x] `LlamaCppBackend : IServerBackend`：探测、Inspect、BuildLaunch/Start/Stop/Ready/ResolvePort/OpenWebUi 全部委托 `ServerService`，未复制逻辑。
- [x] `EngineSession` 与 `ServerSession` 双向映射。
- [x] `BackendRegistry` 骨架（阶段 2 完善）。
- [ ] 主面板启动/停止改经 `IServerBackend`；UI 行为不变（延后到阶段 2，与仲裁一起改）。
- [x] 回归：新增 2 项后端适配测试；合计 **38/38 通过**，llama.cpp 路径行为不变。

## 阶段 2：注册表、配置迁移（即删）、仲裁

- [x] `BackendRegistry`：注册内置后端，`All()`/`ById()`/`Default`/`ProbeAllAsync()`。
- [x] `EngineArbiter`：GPU 重负载互斥（`ActiveGpuBackend`/`ConflictFor`），主面板启动/停止与嵌入服务改经后端门面。
- [x] `ManagerConfig` 新字段：`Running`（槽位会话）、`ActiveBackendId`、`Engines`。
- [x] 迁移（**即删**）：`RunningServer`/`RunningEmbedding` → `Running` 槽位；保存后旧键消失。
- [x] 回归：迁移往返、迁移即删、空配置不臆造会话、并发保存、注册表/仲裁（**41/41**）。

## 阶段 3：Strata 后端（改为启动 HTTP 服务）

- [x] `StrataBackend : IServerBackend`（id `strata-upstream`）。
- [x] 定位三要素：包根（`engine/strata.exe`）、源码根（`serve/server.py`）、Python 3.10+。
- [x] `BuildLaunch` 生成 runconfig JSON（`GeneratedFiles`）：`exe`/`args`（`--native`/`--max-context`/`--prefill`/`--max-new`/`--pool-workers`）/`cwd`/`tokenizer`/`model_name`/`arch`/`log`/`port`/`host`/`reasoning_budget_tokens`/`anthropic_thinking`。
- [x] 命令行：`python -m serve.server --engine strata --config <json> --port <port>`。
- [x] `ProbeAsync`：`--families` 含 `qwen35moe` + `serve/server.py` + Python 可用性。
- [x] `ReadyAsync`：`GET /health`；`StartAsync` 先准备 tokenizer 目录（`EnsureTokenizerDirAsync`）。
- [x] `InspectAsync`：复用 `StrataCompatibility.Analyze` + 资源估算。
- [x] 回归：假 `serve/server.py`、runconfig 生成、解析器诚实报错（**48/48**）。
- [x] UI：删除旧 Strata pack 页与其代码隐藏（`MainWindow.Strata.cs`、`StrataParameterHelp.cs`）；Strata 的包目录/源码根/Python 改为引擎页的参数（`packageRoot`/`serveRoot`/`pythonPath`，带浏览按钮）。
- [x] 修复：引擎页参数此前写入 `EngineSettings` 但 `StrataBackend` 只读 `StrataConfig`（UI 值被忽略）；现改为后端优先读自身设置，`StrataConfig` 仅作旧安装回退。
- [x] 修复：`strata.exe --families` 探测只读 stdout、不读 stderr 且超时不 kill，日志填满管道会死锁 UI（"选择 strata 卡死"）；改为异步排空双流 + 超时强杀，并把探测移出 UI 线程。新增复现该卡死的回归测试。

## 阶段 3b：引擎环境一键初始化（bootstrap）

- [x] `BootstrapService.RunAsync(backendId)` + CLI `--bootstrap` + GUI「初始化环境」按钮。
- [x] 幂等：已存在且校验通过则跳过；失败只标记该后端不可用，不阻断其它后端。
- [x] **strata-upstream**：探测引擎/源码/Python；创建 `<ManagerRoot>/venvs/strata`；`pip install` 依赖（**优先读引擎 `requirements.txt`，缺失回退内置列表**）。
- [x] **llamacpp**：定位现有 `llama-server.exe`。
- [x] **polystrata**：探测插件可执行文件。
- [x] 不下载模型（体积大）：模型由用户选择本地 `.gguf`。
- [x] 回归：`requirements.txt` 优先 + 回退、CLI 路由（**47/47**）。
## 阶段 4：通用进程插件 + PolyStrata

- [x] `plugin.json`（`schema:1`）解析 + 严格校验（schema/id/displayName/executable/arguments），缺字段禁用并说明。
- [x] `ProcessPluginBackend`：发现（`AppContext.BaseDirectory/plugins` + `ManagerRoot/plugins` + `LlamaCppRoot/engines`）、`{model}`/`{port}`/参数模板替换、`ready.type` = `http`/`tcp`、`detect.versionArg` 探测、崩溃隔离（独立进程）。
- [x] 首个插件 `polystrata`：定位 `strata-poly(.exe)`、`/health` 就绪、参数（port/context/maxNew）、兼容性 `driver: polystrata-matrix`。
- [x] `PluginLoader.RegisterAll()` 在启动时注册；清单随构建复制到输出。
- [x] 回归：清单解析/校验/模板替换、内置 id 冲突拒绝（**45/45**）。

## 阶段 5：兼容性插件化 + 资源估算

- [x] 各后端实现 `InspectAsync` 返回 `CompatibilityReport`；`driver` = `none`/`strata-matrix`/`polystrata-matrix`。
- [x] `StrataCompatibility` 复用为实现之一，保持矩阵与文案。
- [x] `ResourceEstimator.ForGguf`：按文件大小与层数估算 VRAM/RAM；6GB 卡给提示（不阻断）。
- [x] 回归：各后端对同一 APEX fixture 与缺失模型的判定 + 估算（**46/46**）。

## 阶段 6：动态参数 UI + 引擎标签页

- [x] 引擎标签页（第 6 个导航页）：后端下拉、模型选择、参数按 `ParameterSpec.Group` 动态生成。
- [x] 参数值存 `EngineSettings.Parameters`；`ConfigService.SaveEngineSettings` 持久化。
- [x] 状态区：可用性、兼容性、运行状态、**可复制的 base URL**（供 pi/Claude Code/Codex）。
- [x] 启动/停止经 `IServerBackend` + `EngineArbiter` GPU 互斥（统一冲突确认）。
- [x] 初始化按钮（`BootstrapService`）。
- [x] UI 回归：5 个导航页、引擎控件存在、离屏渲染 5 张 PNG（**48/48**）。

## 阶段 7：CLI / autostart 走注册表

- [x] `CommandLineService` 经 `BackendRegistry` 解析后端，支持 `--backend`；未知 id 报错。
- [x] 新增 `--bootstrap`。
- [x] autostart 记录 `--service --backend <active>`。
- [x] 回归：后端路由、help、bootstrap 依赖读取（引擎 `requirements.txt` 优先，缺失回退内置）（**47/47**）。

## 阶段 8：测试、发布、文档

- [x] 回归覆盖阶段 1–7 全部新增路径 + 现有用例：**47/47 通过**，构建 0 警告 0 错误。
- [x] `dotnet publish ... -o logs\publish-plugins`（不与在用 `dist/` 冲突）；发布 EXE 烟雾测试通过；`plugins\polystrata\plugin.json` 随包分发。
- [x] 文档：`architecture.md` 增补引擎抽象；`validation.md` 记录本轮结果（并保留上轮历史）；新增 `THIRD-PARTY-NOTICES.md`。
- [ ] Git：审查 diff，提交（用户明确要求时才提交）。

---

## 建议的最小可用切片

阶段 0 → 1 → 2 → 3 → 5（Strata 兼容性）→ 7。
先让「一个启动器 + llama.cpp + Strata（HTTP 服务）」稳定可用、可被 pi agent 接入；再把 PolyStrata 作为阶段 4 首个插件接入，验证抽象是否成立。

## 待确认（不影响开工）

1. Strata 服务的默认端口与 `--host`（建议 `127.0.0.1`）。
2. 是否需要在启动器内直接生成/维护 `strata-<model>.json`，还是允许用户指定现成 runconfig。
3. PolyStrata 插件是否随启动器分发预置清单，还是由用户放入 `plugins/`。
