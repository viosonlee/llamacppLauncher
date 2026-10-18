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
- REQ-011 已废弃（2026-10-18）：~~strata-upstream 内置后端~~。它唯一独有的 `qwen4exp`
  （Qwen3.8-Flash-Next）最小 29.6 GB 文件、官方要求 32 GB 内存，本机 23.4 GB 跑不动；在共同
  支持的 qwen35moe 上比 polystrata 慢约 30%。由 REQ-112 取代。
- REQ-012 已实现：polystrata 内置后端——PolyStrata (strata-poly) MoE 引擎，
  同经 `serve/server.py`；兼容性矩阵独立于 Strata（支持更多专家格式）。
- REQ-013 已实现：插件机制——`plugins/<id>/plugin.json`（schema:1）声明自带 HTTP
  服务的第三方引擎；独立进程、崩溃隔离、无需重编译。
- REQ-014 已实现：引擎获取——「初始化环境」按后端自动下载并安装：
  - llamacpp：官方 nightly Windows CUDA 包 + cudart；**按 GPU 计算能力选 CUDA 大版本**
    （< 8.0 → CUDA 12.4，否则 13.4；13.4 nightly 不含 Turing/Volta SASS）。
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
- REQ-108 已实现：Strata 家族参数按引擎实测能力收敛（真实模型 + `--serve` 验证）：
  - ~~upstream `strata.exe` 只暴露其真正接受的参数~~：该引擎已退役（REQ-112），
    `prefill`/`maxNew`/`mtpDrafts`/`expertSlots` 等字段随之删除；
  - polystrata 只暴露 `context`、`vramReserveMib`、`tailSkip`、`kv`、`threads` 等其真正读取的参数；
  - `parallel` / `slots` / `batch` / `warm-start` / `fit-max-tokens` 一律不作为面板字段，
    也不会被写进命令行；`--expert-slots` 在 `strata-poly.exe` 上是硬错误。
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
- REQ-110 已实现：引擎更新（不只是首次安装）：
  - `EngineAcquisitionService.UpdateAsync` 读取已安装版本（Strata 家族读 `BUILD.json`），
    与最新可用发布比较（复用 `DoctorService.IsOutdated` 的多版本方案比较），落后才重新下载；
  - 「初始化环境」在引擎已存在时也会检查更新，兑现体检里「运行初始化环境更新」的提示
    （此前 bootstrap 只在引擎缺失时下载，该提示实际不生效）；
  - CLI 新增 `--update [--backend ID] [--all]`，可无人值守更新并输出「已更新 x → y」。
- REQ-111 已实现：polystrata 增加 `kv` 参数（`--kv fp16|q8_0`）：
  - 面板提供「默认（fp16）/ fp16 / q8_0」三选项，空值不写命令行（保持引擎默认）；
  - 帮助文案注明 q8_0 仅 qwen35moe 家族（Qwen3.6 / Ornith / Tiel）支持，其它模型启动会被拒绝；
  - 实测（53,127 token 提示词）：fp16 解码 40.4 tok/s、q8_0 37.6 tok/s，该长度下没有收益，
    收益只在 KV 真正挤压专家缓存的长上下文出现。

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

- REQ-100 已实现：polystrata 初始化时若缺 Python 3.10+，自动安装：
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
  - 引擎自包含与 DLL 补全：在 polystrata 初始化时，自动补充/共享 CUDA 运行时依赖（`cublas64_*.dll`, `cudart64_*.dll`），且子进程环境变量 PATH 补全相关目录，彻底杜绝 Windows 下 `STATUS_DLL_NOT_FOUND` (0xC0000135) 错误。
- REQ-105 已实现：多引擎日志与实时速度监控适配（polystrata）：
  - 日志系统（LogService）支持按当前所选后端动态读取其专属日志（`logs/engine-{backendId}.log`、`.stdout.log`、`.stderr.log`），“打开日志文件”按钮自动定位打开当前引擎的主日志文件。
  - 测速监控（SpeedMonitorService）与状态轮询适配 Strata 服务端：在 `serve/server.py` 的 `/slots` 接口暴露当前实时解码 tokens 数（`n_decoded`），使得启动器在推理生成过程中能即时计算实时解码速度；当请求结束空闲时，自动解析 Strata 引擎日志（`[strata] done: ... (XX tok/s)`）或读取 `/v1/status` 的 `last_timings` 显示已完成请求的生成速度。
  - 显存/专家分布诊断：从 `engine-polystrata.log` 解析 PolyStrata 的显存专家分布（`X/Y 专家在显存 (Z GiB)`、`the expert cache A -> B experts (Z GiB)`）并在顶部状态信息中直观呈现。
- REQ-106 已废弃（2026-10-18）：~~strata-upstream 投机加速与专家槽位性能修复~~。该引擎已退役，
  `expertSlots`/`mtpDrafts` 参数、`--expert-slots`/`--mtp`/`--prefill`/`--max-new` 的 runconfig
  分支一并移除；同类能力改由 polystrata 的内置 MTP 草稿与 `--tail-skip`/`--kv`/`--threads` 承担。
- REQ-112 已实现（2026-10-18）：退役 `strata-upstream` 后端。判定依据：
  - 能力重叠：`strata-poly` 支持 qwen35moe / glm5-next / gemma4 / deepseek4 / lfm2moe，
    覆盖 `strata.exe` 的 qwen35moe；后者唯一独有的 `qwen4exp`（Qwen3.8-Flash-Next）最小
    Coder IQ1_M 主分片 29.6 GB、官方写明需 32 GB 内存，本机 23.4 GB 无法运行。
  - 实测：同一 Ornith-1.5-35B-A3B 模型，`strata.exe` 35.1 tok/s、预热 27–88 s；
    `strata-poly` 46.0 tok/s（tail 0）/ 49.8 tok/s（tail 6）、载入 3.1 s。
  - 显存控制：`strata.exe` 无 `--tail-skip`/`--kv`/`--vram-reserve-mib`（日志对前者打印
    `ignoring`），`strata-poly` 三者齐备且专家缓存按空闲显存动态伸缩。
  - 移除范围：`StrataEngineProfile.UpstreamProfile`、注册表条目、`EngineCatalog.StrataPlan`
    /`StrataRepo`/`StrataSourceBranch`、采集与 bootstrap/doctor/CLI 分支、面板字段与帮助。
  - 保留范围：`StrataService`/`StrataPackService`/`StrataCompatibility`（分词表准备与
    `ProcessPluginBackend` 仍在用）、`StrataConfig` 旧配置块（作为 polystrata 的回退）。
  - 顺带修正：面板里 `poolWorkers` 从未被发出（只有上游分支写 `--pool-workers`），
    改名为 `threads` 并真正写 `--threads N`。
  - 回归：61 项全部通过；`docs-gate` 通过。
- REQ-113 已实现（2026-10-18）：引擎与模型的选型、参数默认值必须有实测依据，并记录在
  `docs/engine-benchmarks.md`（硬件、方法、矩阵、复现命令）。测量以引擎自己回报的
  `timings` 为准，不用客户端估算；MTP、线程数这类变量必须按上下文分组报告，不能给单一结论。
  - 已记录：poly 对 llama.cpp 的 1.8–2.6 倍优势、MTP 在 4K（+13%）与 40K（更慢）的分野、
    poly `threads` 最优 8、Gemma 4 选 QAT、APEX 与 Q3 的取舍。
  - 二创生态调研落在 `docs/qwen36-35b-a3b-ecosystem.md`，标明哪些有实测、哪些只是宣传。
  - 回归覆盖这些纯函数：schema 回退、路径归一化、模型元信息、llama.cpp 参数投影、
    Strata 日志解析、分词表路径与端口优先级、后端状态工厂与资源估算（共 68 项）。

- REQ-114 已实现（2026-10-18）：修复“界面看不到速度”与“poly 下日志不显示不更新”。
  根因各有两个，均已修复并加回归：
  - 布局：顶部控制条只有一行，状态文本（最大 300px）之后才是速度文本（最大 200px），
    StackPanel 会裁掉溢出——速度被挤出窗口外，任何引擎都看不见。现改为三行（引擎 / 按钮 / 状态），
    速度徽章独占 Grid 最后一列，长模型名只截断自己；UI 回归断言徽章始终落在卡片右缘之内。
  - 数据源：polystrata 的 `/slots` 没有 token 计数，采样会把整个生成期显示成“提示词处理中”。
    新增 `PolyMetricsService` 解析服务自报的 `/metrics`（live 状态机 + 窗口化 tok/s +
    完成请求环），`/metrics` 不可用时回退旧采样。live 数值是引擎口径，不再标“约”。
  - 日志：polystrata 引擎与服务都不写逐请求日志行，`/metrics` 之外没有活动可显示。
    启动器现在在每个新完成请求首次出现时写一行“请求完成”（token、tok/s、耗时、草稿接受率），
    聚合页从此有实时反馈；poly 启停消息也改为同时写入持久日志，不再只进高级页的输出框。
  - 健壮性：日志读取失败（截断/轮转/权限）不再让整次状态轮询退出；日志页在轮询开头刷新；
    “清空显示”后下一次轮询会重新填充；已记录会话的进程消失时不再显示为“服务运行中”
    （API 仍可达时以 API 为准）。


