# 需求文档（REQUIREMENTS）

本文件是 LlamaManager（本地大模型助手）的需求基线。每个需求有稳定编号（REQ-xxx），
供变更文档（CHANGELOG-DECISIONS.md）与文档门禁引用。**改动行为前先来这里登记或修订需求。**

需求状态：`已实现` / `部分实现` / `规划中` / `已废弃`（废弃的需求保留编号与原因）。

## 1. 定位

- REQ-001 已实现：产品名「本地大模型助手」（界面标题），仓库/程序集名保持 LlamaManager。
- REQ-002 已实现：Windows x64 单机工具，.NET 8 WPF，自包含发布，双击即用。
- REQ-003 已实现：所有引擎统一抽象为「本地 HTTP 服务」（OpenAI/Anthropic 兼容），
  供 pi / Claude Code / Codex 等 agent 接入。

## 2. 引擎后端

- REQ-010 已实现：llamacpp 内置后端——任意 GGUF，OpenAI 兼容；Prism 模型（三元量化，
  `prism.hadamard.*`）自动路由到 Prism 定制引擎。
- REQ-011 已实现：strata-upstream 内置后端——Strata 原生 qwen35moe 路径，
  经 `serve/server.py` 提供 HTTP 服务；引擎须支持 `--families qwen35moe`。
- REQ-012 已实现：polystrata 内置后端——PolyStrata (strata-poly) MoE 引擎，
  同经 `serve/server.py`；兼容性矩阵独立于 Strata（支持更多专家格式）。
- REQ-013 已实现：插件机制——`plugins/<id>/plugin.json`（schema:1）声明自带 HTTP
  服务的第三方引擎；独立进程、崩溃隔离、无需重编译。
- REQ-014 已实现：引擎获取——「初始化环境」按后端自动下载并安装：
  - llamacpp：官方 nightly Windows CUDA 包 + cudart；**按 GPU 计算能力选 CUDA 大版本**
    （< 8.0 → CUDA 12.4，否则 13.4；13.4 nightly 不含 Turing/Volta SASS）。
  - strata-upstream：APEX 补丁构建（viosonlee/apex-on-strata release）+ upstream 分支源码
    归档（serve/）。
  - polystrata：VecSzn/PolyStrata release 引擎 + 源码归档。
  - 下载须校验 SHA-256（GitHub digest），解压防 zip-slip。
- REQ-015 已实现：Strata 家族自动建 Python venv 并按引擎 `requirements.txt` 安装依赖；
  源码缺失时回退内置依赖列表。
- REQ-016 已实现：分词表准备对工具变体鲁棒——`vocab.json` 落在 `--out/` 或
  `--out/tokenizer/` 均可接受，runconfig 指向实际目录。

## 3. 生命周期与并发

- REQ-020 已实现：GUI / CLI / 自启动共用同一后端注册表与生命周期逻辑，禁止旁路复制。
- REQ-021 已实现：会话身份 = PID + 创建时间 + 引擎路径；防 PID 重用误杀。
- REQ-022 已实现：会话槽位按后端隔离（llamacpp → "main"，其它 → 后端 id）；
  `--status`/`--stop`/`--open` 必须读对应槽位。
- REQ-023 已实现：GPU 重负载后端互斥（EngineArbiter），启动时统一确认。
- REQ-024 已实现：服务以分离进程运行（stdout/stderr 直写文件），GUI/CLI 退出不影响服务。
- REQ-025 已实现：引擎子进程的工作目录显式指定（`python -m` 模块启动必须用源码根）。

## 4. 界面

- REQ-030 已实现：顶部统一控制条——引擎下拉 + 一套启动/停止/打开页面/测速按钮。
- REQ-031 已实现：标签页按功能分：模型 / 参数 / 高级 / 嵌入 / 日志。
- REQ-032 已实现：所有引擎（含 llama.cpp）共用同一套 schema 生成的参数面板；llama.cpp 专属的
  原始参数框、嵌入服务与「自动打开页面」仅在选择 llama.cpp 时显示。
- REQ-107 已实现：参数面板 schema v2（`docs/parameter-ui-design.md`）：
  - `ParameterSpec` 增加 `Help` / `Unit` / `Editable` / `Advanced` / `RangeHint`，`Choices` 由
    `string[]` 升级为 `ParamChoice[]`（值 / 展示文案 / 说明 / 推荐）；
  - `VisibleWhen` 由 `Engines/ParameterVisibility.cs` 求值（`profile==…`、`model.moe`、`model.mtp`、
    `param.<key>` 比较，支持 `&&` / `||` / `!` / 括号），未知变量按隐藏处理并记日志，绝不抛异常；
  - 面板按声明顺序分组生成，`Advanced` 字段折叠进「高级参数」，每个字段显示默认值、单位与范围，
    失焦与启动前校验 `Min/Max`，越界红框加内联错误并阻止启动（不静默改写用户输入）；
  - 提供分组与全局「恢复默认」、按标签/Key/分组过滤；只有用户改动过的值才落盘；
  - 插件清单 `schema:1` 兼容扩展：`help` / `unit` / `rangeHint` / `editable` / `advanced` /
    `visibleWhen`，`choices` 既可写字符串数组也可写对象数组。
- REQ-108 已实现：Strata 家族参数按引擎实测能力收敛（真实模型 + `--serve` 验证）：  - upstream `strata.exe` 只暴露其真正接受的参数：`context`、`prefill`、`maxNew`、`mtpDrafts`
    （`VisibleWhen=model.mtp`）、`expertSlots`（`VisibleWhen=model.moe`）、`poolWorkers`、
    `reasoningBudget`、`anthropicThinking`；
  - polystrata 只暴露 `context`、`vramReserveMib`、`tailSkip` 等其真正读取的参数；
  - `parallel` / `slots` / `batch` / `warm-start` / `fit-max-tokens` / `tail-skip` 在 `strata.exe`
    上会被日志标记为 ignoring，`--expert-slots` 在 `strata-poly.exe` 上是硬错误，均不作为面板字段，
    也不会被写进命令行。
- REQ-033 已实现：嵌入（Embedding）服务独立成「嵌入」页——模型选择、端口、启停；
  供资料量化 / RAG 使用，与主服务相互独立。
- REQ-109 已实现：llama.cpp 参数面板迁到 schema，消除双维护：
  - `Engines/LlamaCppParameters.cs` 声明 16 个字段（上下文、K/V 缓存量化、GPU 卸载层数、
    CPU 线程、思考模式与预算、Flash Attention、批大小与物理批大小、MoE 卸载层数与全部走 CPU、
    投机解码类型与草稿模型、内置 MTP 开关、详细日志）；
  - 面板与原始参数串双向投射：`ReadFromArgs` 投影，`Apply` 只改写该字段自己的 flag，
    手写 flag 原样保留；「高级」页原始参数框仍是任意 flag 的兜底，输入后即时回填面板；
  - `MainWindow.xaml` 中 385 行手写控件块与其同步/帮助 switch 已删除，MoE/MTP 分区改为
    `VisibleWhen="model.moe"` / `"model.mtp"`，随模型元数据自动显隐。
- REQ-034 已实现：模型列表按引擎感知——切换引擎时选择其自有的模型路径。

## 5. 体检与维护

- REQ-040 已实现：doctor 一键体检——每个引擎报告：缺失 / 可用 / 有更新 / 需手动；
  联网查询发布版本（`--offline` 或无网时降级为本地检查）。
- REQ-041 已实现：CLI `--doctor`（退出码：健康 0，有问题 1）；GUI「一键体检」按钮输出到运行输出。
- REQ-042 已实现：Prism 引擎（无公开下载源）在 doctor 中报状态与本机版本，更新提示为手动。
- REQ-043 部分实现：版本比较支持 `b11671`（llama.cpp）、`0.1.44.1-apex.1`（Strata fork）、
  `v0.1.44.1-poly.5`（PolyStrata）三种命名；不可比较的命名不误报更新。

## 6. 配置与数据

- REQ-050 已实现：配置原子写（临时文件 + 替换），损坏时从 `.bak` 恢复，坏原稿存 `.corrupt`。
- REQ-051 已实现：迁移即删——旧字段读时折叠进新结构，保存后删除。
- REQ-052 已实现：设置保存不得覆盖运行会话身份；运行时更新不得覆盖预设。
- REQ-053 已实现：预设按规范化绝对路径 + 文件签名；孤儿预设自动重绑定。
- REQ-054 已实现：引擎安装/下载/分词表等运行时数据位于 ManagerRoot 下的
  `engines/`、`downloads/`、`data/`、`venvs/`、`logs/`，均不入库（.gitignore）。

## 7. 安全与约束

- REQ-060 已实现：服务默认绑定 127.0.0.1；不默认开放 0.0.0.0。
- REQ-061 已实现：不阻塞 UI dispatcher；探测/就绪等待异步且缓存有界。
- REQ-062 已实现：回归测试使用隔离 LLAMA_MANAGER_ROOT 与假服务器；不停用户真实服务。
- REQ-063 已实现：不 stage 用户配置、日志、下载的引擎、模型、构建产物。

## 8. 验收

- REQ-070 已实现：回归套件（console runner）覆盖以上行为，含失败/取消路径；
  退出码非 0 即失败。
- REQ-071 部分实现：真实端到端冒烟（下载→启动→对话→停止）覆盖三个内置后端；
  Prism 引擎路径因本机缺引擎未覆盖启动，仅覆盖探测与 doctor 报告。

## 9. 已废弃

- REQ-090 已废弃：Strata cyber pack 一次性推理路径（2026-10 归档；统一走 HTTP 服务）。
- REQ-091 已废弃：polystrata 以 plugin.json 清单接入（strata-poly 说行协议而非 HTTP，
  清单无法表达 `serve/server.py` 包装，改为内置后端）。

## 10. 全新电脑部署

- REQ-100 已实现：strata-upstream / polystrata 初始化时若缺 Python 3.10+，自动安装：
  winget 优先（`Python.Python.3.12`、用户级、静默、免管理员），回退 python.org
  3.12.10 amd64 安装包（`/quiet InstallAllUsers=0 PrependPath=1`）；装完建 venv 并按引擎
  `requirements.txt` 装依赖。两条路都失败时明确提示手动安装，不静默。安装逻辑具备可替换的
  探测/执行/下载接缝，回归测试不触网、不改本机。
- REQ-101 已实现：一键体检是**全局**功能，放在顶部控制条（与「刷新」并列且相互独立：刷新=本地
  快速探测，体检=联网深度检查）；结果写入全局「日志」页并自动切到该页，不随引擎选择消失。
- REQ-102 已实现：提供面向 agent 的全新电脑配置指南 `docs/AGENT-GUIDE.md`，纳入文档门禁
  必填清单与交叉引用检查。
- REQ-103 已实现：CLI 支持无值开关（如 `--doctor --offline` 的 `--offline`）；带值选项缺值时
  报错并指明选项名。
- REQ-104 已实现：全新电脑一键配置并启动：
  - GUI: 当所选引擎未就绪（缺少引擎二进制文件、服务源码或 Python 依赖）时，点击「启动服务」自动检测到缺失，向用户弹出友好提示并确认一键自动下载并配置环境，配置完成后无缝自动启动服务；同时在顶部控制条提供直观的「配置环境」按钮，并在未就绪时状态栏清晰提示一键配置；支持配置进度实时呈现（全局日志联动）。
  - CLI: `--start` 支持在缺少环境时通过 `--auto-bootstrap`（或 `--bootstrap`）开关一键全自动配置环境并拉起服务；`--bootstrap --all` 支持一条命令配置所有内置引擎环境；`llama-manager.ps1` 和 `llama-manager.cmd` 完整支持 `-Bootstrap`、`-Doctor`、`-All`、`-Offline`、`-AutoBootstrap`、`-Backend` 等参数。
  - 引擎自包含与 DLL 补全：在 Strata 家族（strata-upstream, polystrata）初始化时，自动补充/共享 CUDA 运行时依赖（`cublas64_*.dll`, `cudart64_*.dll`），且子进程环境变量 PATH 补全相关目录，彻底杜绝 Windows 下 `STATUS_DLL_NOT_FOUND` (0xC0000135) 错误。
- REQ-105 已实现：多引擎日志与实时速度监控适配（polystrata 与 strata-upstream）：
  - 日志系统（LogService）支持按当前所选后端动态读取其专属日志（`logs/engine-{backendId}.log`、`.stdout.log`、`.stderr.log`），“打开日志文件”按钮自动定位打开当前引擎的主日志文件。
  - 测速监控（SpeedMonitorService）与状态轮询适配 Strata 服务端：在 `serve/server.py` 的 `/slots` 接口暴露当前实时解码 tokens 数（`n_decoded`），使得启动器在推理生成过程中能即时计算实时解码速度；当请求结束空闲时，自动解析 Strata 引擎日志（`[strata] done: ... (XX tok/s)`）或读取 `/v1/status` 的 `last_timings` 显示已完成请求的生成速度。
  - 显存/专家分布诊断：从 `engine-polystrata.log` 及 `engine-strata-upstream.log` 解析 Strata 引擎专有的显存专家分布（`X/Y 专家在显存 (Z GiB)`、`X/Y 专家/层在显存 (Z GiB)`）并在顶部状态信息中直观呈现。
- REQ-106 已实现：strata-upstream 投机加速与专家槽位性能修复（解决 27 TPS 瓶颈）：
  - 修正参数传递：`strata.exe` 不支持 `--vram-reserve-mib`（日志提示 ignoring），且默认 `expert_slots` 为 0，在 6GB 显卡上可能让 routed experts 全部回退到 CPU 线程池。现为 `strata-upstream` 配置专属的 `expertSlots`（默认 16，占用约 0.75 GiB 显存）。
  - 激活 MTP 多 Token 投机加速：配置中显式传递 `--mtp 2`（`mtpDrafts`，默认 2），利用模型自带的 MTP 模块进行投机解码。
  - 测速优先采用服务端 `timings.predicted_per_second`，避免 SSE 合并多个 Token 时低估 MTP 吞吐。
  - 专家槽位按上下文自适应：不超过 8192 时默认 16 槽，大于 8192 时默认交给引擎计算；显式填写的槽位数仍优先。
  - 同一 RTX 3060 Laptop (6GB) 的 256 Token 实测为 28.0 -> 39.5 tok/s；24 槽为 41.3 tok/s，但仅余约 8 MiB 显存，默认采用更安全的 16 槽。


