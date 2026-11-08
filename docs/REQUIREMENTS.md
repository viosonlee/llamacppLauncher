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
  - llamacpp：官方 nightly Windows CUDA 包 + 同发布 cudart；按 GPU 与 driver 上限选择完整 CUDA 配对，
    <8.0 仅 CUDA 12.4，其它 GPU 不选择超过 driver 上限的 CUDA 13.x，必要时使用同发布 12.4。
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

- REQ-115 已实现（2026-10-18）：预设生成器对「放不进显存的 MoE」给出可跑的起点。
  旧行为按「层数 × 整个文件大小」砍 `-ngl`，13 GiB 的 A3B 在 6 GiB 卡上会被算成几乎纯 CPU；
  新行为是 `-ngl 999` 满层卸载 + `--n-cpu-moe N`：显存先付计算预留与 KV 缓存，剩下的装专家
  （专家约占文件 91%），装不下的层数就是要放到 CPU 的 N。校准锚点为本机实测：
  13.29 GB A3B / 6144 MiB 卡 / 4K 上下文，能跑的边界 26、最快 30，公式给出 28。
  模型装得下时不加 `--n-cpu-moe`；稠密模型维持原 KV 感知砍层路径。

- REQ-116 已实现（2026-10-18）：全新机器的模型获取闭环。模型列表为空时显示「下载推荐模型…」，
  打开内置目录（5 个单文件 GGUF，按显存需求升序，全部经 HTTP 200 验证并记录精确字节数）。
  下载复用引擎下载机制（流式、.part 临时文件），并新增两项模型专属防护：磁盘空间预检
  （含 2 GiB 余量，不足时报错而不是下到一半失败）与下载后 GGUF 文件头校验（404 页面/登录
  重定向会被立即识别删除）。同名同大小的文件跳过下载；同名不同大小则重新下载覆盖。
  下载目标遵循用户已配置的模型目录（默认回退到启动器 models/），完成后自动扫描并选中。

- REQ-117 已实现（2026-10-18）：参数实测对比（llama.cpp）。参数页新增「实测对比…」：
  每行一组 `标签|参数` 的计划，实测器为每组启动独占端口的 llama-server（系统分配临时端口，
  避开 8080、配置及运行会话端口），等就绪后用与「测速」相同的固定提示测量 TTFT 与解码 tok/s，按组汇总
  （均值/最差/★最快）写入日志页。防护：变体参数禁止携带 -m/--port/--host；端口被占拒绝
  启动；报告记录开始时的显存空闲量（有其它程序占显存时提示数字偏低）；失败变体跳过并
  记录原因；进程树保证清理，取消即中止剩余组。MoE 模型预填 `--n-cpu-moe` 梯度计划。
  另有 `--benchmark <model> <runs> <plan文件>` CLI 诊断入口；已用真实模型完成
  moe 26 vs 30 的两变体实测验证（moe 30 更快，与历史校准方向一致）。

- REQ-118 已实现（2026-10-19）：参数实测对比的过程遥测（REQ-117 扩展）。每组变体在测量期间
  采集引擎自报的 prefill（`timings.prompt_per_second`）与投机草稿接受率（`timings.draft_n` /
  `draft_n_accepted`），并在后台每 250 ms 采样一次显存占用（`nvidia-smi --query-gpu=memory.used`）
  取峰值；三者随均值/最差/TTFT 一起写入报告，供 MoE 调优判断「离显存上限还有多远、草稿是否
  真在起作用」。遥测取不到时静默省略（不写 0、不报错），采样失败不影响测量本身；解析为纯函数
  （`SpeedTestService.ParseTimings`、`GpuMemoryService.ParseUsedMib`），有回归覆盖。
  `--benchmark` 命令行同样输出这些字段。
- REQ-119 已实现（2026-10-24）：三引擎版本升级与 PolyStrata 硬件加速参数集成。
  llamacpp 升级至 b11853（CUDA 13.4）、Prism 升级至 b10787、PolyStrata 升级至 v0.1.45-poly.3。
  修复 DoctorService 对 -poly. 等后缀的版本对比解析；PolyStrata 参数面板新增 Tensor-Core 线性注意力
  （STRATA_GDN_MMA=1）、Flash Attention（STRATA_FATTN=1 / STRATA_FDEC=1）及 Gemma 4 的 int8/int8k
  KV 格式选择。实测在 RTX 3060 上 Prefill 提速 39.0%，Decode 提速 63.6%。

- REQ-120 已实现（2026-10-24）：视觉多模态投影（mmproj）与草稿模型（MTP/draft）UI 接入与智能匹配。
  llama.cpp、Prism 与 PolyStrata 引擎均增加「视觉 / 多模态」参数配置与 mmproj 路径选择器：
  llama.cpp 传递 --mmproj，PolyStrata 自动编排伴生 strata-vision.exe；
  ModelPairingService 自动探测主模型同目录下的 mmproj-*.gguf（优先匹配 Q8_0 / BF16）与 mtp-*.gguf，
  在模型信息栏高亮显示配对状态并自动装配；主模型列表自动过滤排除独立的 mmproj 与 mtp 辅助权重文件；
  参数文件浏览器对 GGUF 模型类型自适应过滤 *.gguf 文件。

- REQ-121 已实现（2026-10-24）：非 llama.cpp 引擎（PolyStrata 等）支持按模型隔离并持久化独立参数预设。
  在 EngineSettings 中引入 ModelPresets（按规范化模型绝对路径键入参数字典），与 llama.cpp 的 ModelPresets 逻辑对齐。
  ManagerConfig.Normalize() 自动迁移现有全局参数并为当前活跃模型建立首份预设。
  UI 参数编辑（OnParameterEdited）与保存（SaveEngineParameters）实时更新当前模型的专属预设字典。
  模型切换（SelectModelAsync）自动提交旧模型修改，并载入新模型已保存的专属预设；针对首次选中的新模型，在保留 engine 级公共路径（packageRoot, serveRoot, pythonPath, servePort, host 等）的同时触发智能配对（mmproj 与 draftModel）。
  保证每个模型的上下文长度、KV缓存量化、跳层配置、视觉投影与草稿模型相互独立，切换时无缝恢复。

- REQ-122 已实现（2026-10-31）：PolyStrata 参数面板接入 v0.1.45-poly.6 新增的显存专家路由偏置与对话驻留缓存。
  `StrataBackend` 新增 `cacheBias`（映射 `--cache-bias`，ParamType.Decimal，仅 qwen35moe 家族有效，默认 0 不传）
  与 `conversationCache`（映射 `--conversation-cache-mib`，Choice：关闭 / auto / 2048 / 4096 / 8192，默认关闭不传）。
  两个选项均为引擎 opt-in，未设置时启动命令行与旧版保持一致；十进制经 InvariantCulture 格式化。
  小数输入接受点号/逗号，逐项与整面板保存统一为点号；NaN/Infinity 与超范围输入校验不通过。
  同批把本机 llamacpp 手动更新至 b12020、polystrata 自动更新至 v0.1.45-poly.6。

- REQ-123 已实现（2026-11-01）：主服务默认端口统一为 8080，启动绑定预检与实测端口隔离。
  llama.cpp（含 Prism 路由）和 PolyStrata 默认 8080；已有显式端口设置不覆盖，冲突时报告原因而不自动换端口。
  启动预检按监听 IP 试探绑定，区分占用与权限/Windows 保留范围；不在预检中阻塞 DNS/WMI 查询。
  实测器使用系统分配临时端口，排除 8080、配置端口、原始参数中的端口与运行会话端口；
  分配有界、可取消，变体启动前再次预检，失败/取消清理仅针对变体进程，保留正式服务身份与配置。
  新增端口分类/分配与失败/取消回归；既有 benchmark 遥测用例不再依赖固定 8095。
- REQ-124 已实现（2026-11-07，协议纠正）：KVMem 参数组仅在独立 `kvmem` 后端显示，保留动态折叠与模型预设隔离。
  原 c3e3644 仅实现 UI/假协议测试，未部署 KVMem 内核；原版 llama.cpp/PolyStrata 不具备该能力。
  原生 PolyStrata 以正数 `--kvmem-budget` 启用，不接受裸 `--kvmem`；原版面板不再暴露这些选项，陈旧启用参数明确拒绝。
  默认 B=8192、R=512、H=1024 MiB、SSD=0、保留历史 S=2；B/R 32 对齐、B+R<=context、prefill<=R，非法输入不静默改写。
  关闭时不发送任何 `--kvmem-*`；SSD>0 必须指定目录。历史数量不是并发数，R 不是总输出上限，H/D 不包含模型与其它进程开销。

- REQ-125 已实现（2026-11-07，PolyStrata 范围）：真实 KVMem 统一包获取、独立安全部署与运行验证。
  `kvmem/kvmem` 完整 unified release 按精确 CUDA 架构选包（本机 sm86 → RTX30），未知架构拒绝猜测；SHA-256 校验后完整解压至新版本目录。
  原版 PolyStrata/llama.cpp 不覆盖，不硬回滚代码，不改现有模型预设/活动后端/运行身份；取消或失败不切换安装路径。
  使用包内 Python、CUDA、对应 serve 与同 GGUF 五件套分词表；不为 tokenizer 联网安装 gguf/venv。
  当前接入仅 `qwen35moe` GGUF、单 GPU、fp16/q8_0、普通解码，自动禁用外部草稿与旧会话缓存；CPU 视觉需匹配 mmproj。
  体检核验布局、版本、架构、原生 capabilities/worker hash；运行就绪核验 loaded、模型与有效预算，HTTP 200 不等于载入成功。
  提供显式隔离 GPU smoke（不进入普通回归），需实测实际 32K/64K prompt、有效分层及非空输出；端到端证据与限制记录在 docs/KVMEM-INTEGRATION.md。

- REQ-126 已实现（2026-11-07）：所有内置引擎及匹配运行库的一键更新，并只保留最近一版旧版本。
  GUI 全局「一键更新」与 CLI `--update --all` 共用服务，覆盖注册的 llamacpp/polystrata/kvmem；逐引擎报告成功或错误，取消可中止未完成任务。
  llama.cpp 必须读取真实 build 并与 Windows CUDA 资产比较，同发布 binary+cudart 配套且不超过驱动能力；PolyStrata 原生、对应 tag 服务源码及 Python/CUDA 依赖配套更新；KVMem 更新完整统一包。
  新版本在独立目录校验完成后才原子提交路径，不覆盖正在运行或手工安装的引擎，不修改模型调优参数、活动后端与会话身份。
  引擎级共享安装路径可同步到引用同一旧路径的模型预设；用户另行指定的手工路径保留，模型切换不能把新版公共路径覆盖回旧版。
  清理只处理带启动器有效 ownership 记录的版本和匹配 SHA/长度的下载缓存；按 backend/架构保留当前版与最近一版旧版。
  被运行会话、手工指定或用户数据保护的版本可额外保留并报告原因；未知目录、模型、配置、日志绝不为了数量上限删除。
  清理失败仅警告，不回滚已成功安装；需通过安装/取消/保留策略回归及本机真实更新后推理验证。

- REQ-127 已实现（2026-11-08）：对明确声明倍数约束的整数参数，输入完成后自动取最近合法倍数。
  ParameterSpec 声明 MultipleOf；KVMem B/R 为32对齐，其它无此要求的参数不臆加限制。只在失焦、回车或保存前调整，输入过程中不跳数；等距离取较小值，越界整数取合法范围内最近倍数。
  明确显示调整前后与原因并记录日志；空值保留默认语义，非法文本/非整数/非有限值仍报错。GUI与native参数组装共用纯函数，CLI输出有效调整，不偷偷修改其它关联参数。

- REQ-128 已实现（2026-11-08，用户范围纠正）：KVMem 读取本机CPU/内存/GPU，为当前引擎的当前模型一键生成并应用保守配置；不是实测最优配置，也不是内置性能测试产品。
  `KvmemHardwareConfigurationService` 在后台任务中进行异步、有界的硬件探测与静态策略生成；未知数据明确标注，结合当前模型大小/架构与目标盘余量，不把当前空闲显存当硬件总容量。
  不启动候选模型、不执行多轮性能测试，不读取或写入实测推荐缓存；仅提供明确标注“未实测”的硬件保守起点，不承诺性能最优或消除OOM。
  应用必须使用后台准备好的同一份完整策略，并核验当前引擎、规范化模型路径及模型签名，通过参数快照CAS（比较后提交）拒绝覆盖并发编辑；不得在应用时重新生成另一份策略。
  逐项编辑和一键应用均按引擎/模型自动保存，切换后恢复该模型预设，不串用其它模型参数；安装公共路径与独立手工路径遵循既有隔离规则，运行会话身份不变。
  KV的GPU/RAM/SSD分层只管理KV缓存，模型权重RAM另计；Host/SSD配额不是整个进程或整机内存上限，硬件配置不能保证整体RAM不超限。

- REQ-137 已实现（2026-11-08）：常驻状态轮询不得做与可见性无关的重复日志读取，也不得在无会话时读引擎日志。
  日志面板仅在自身被选中时刷新，轮询中不得重复调用；切回日志页时强制刷新一次以显示隐藏期间的内容。
  无会话（服务未运行）时跳过 GPU 摘要读取，避免无谓文件 IO 与日志写锁争用。
  状态、速度、显存等信息仍按原频率刷新；须通过真实界面“隐藏不刷新、切换即刷新”验证。

- REQ-136 已实现（2026-11-08）：共享配置集合只允许在 UI 线程修改，后台任务只做只读计算。
  模型扫描可在线程池枚举文件与预热 GGUF 元数据，但预设重绑定（增删 `ModelPresets`、改写 `ModelPath`、保存配置）
  必须在 UI 线程执行，避免枚举期间被修改。
  不得为规避竞态而把昂贵的文件签名采样搬回调度器常驻路径；仅在确实存在孤立预设时才进行。
  须通过既有预设恢复/签名回归，以及真实模型目录界面扫描无异常验证。

- REQ-135 已实现（2026-11-08）：下载必须使用互不冲突的临时文件，失败清理只影响本次任务。
  每次下载使用带随机后缀的独立临时文件；成功才原子移动到目标，校验失败/取消只删除自己的临时文件。
  并发下载（如“一键更新”同时下载多个引擎）不得因共用临时路径而互相失败或互删；
  校验、原子替换与失败清理语义不变。须通过并发下载与“失败不删除他人临时文件”回归。

- REQ-134 已实现（2026-11-08）：辅助权重文件不得作为可启动主模型列出，配对规则与列表过滤必须一致。
  统一判定 `mmproj*` / `mtp-*` / `draft-*`（仅 `.gguf`），列表过滤与自动配对共用同一实现，不得各写一份。
  多个同类型候选时选择必须确定（按路径序），MTP 块优先于普通草稿；不得因过滤而隐藏真正的主模型。
  须通过纯函数回归（大小写、非 gguf、多候选稳定性）与真实模型目录界面列表验证。

- REQ-133 已实现（2026-11-08）：GPU 互斥对 GUI、CLI 与自启动一致生效，并以进程存活为准。
  只有完整身份（PID+创建时刻+可执行路径）匹配的存活会话才占用显卡；崩溃/重启留下的过期记录不得阻塞任何引擎启动。
  启动任一占显存引擎前统一判定：另一引擎正在运行时拒绝并指名，同引擎重复启动也拒绝；不得加载第二个占显存实例。
  GUI 的“停止冲突引擎并继续”仍使用“自身不算冲突”的判定；被拒绝的启动不得创建进程、不得改写会话记录。
  须通过过期/存活记录回归、后端启动拒绝回归，以及本机“过期记录下可启动 + 运行时拒绝第二引擎”实测。

- REQ-132 已实现（2026-11-08）：KVMem 回复上限留空时必须随逻辑上下文收缩，不得阻止启动。
  未填写/空白时取 `min(8192, context)`；用户显式填写时仍须为整数、1–131072 且不超过 context，越界明确报错。
  区分“未填写默认”与“显式越界”，不得用隐藏默认值阻止合法的小上下文配置，也不得静默改写用户显式输入。
  须通过留空/显式两向回归，以及本机小上下文真实服务启动与 `/props` 默认值验证。

- REQ-131 已实现（2026-11-08）：兼容性预检说明不臆断显存下限，且说明必须对用户可见。
  只读 GGUF 元数据的检查不得断言 VRAM 最小值；显存相关结论须来自实际观测，不能与本机已验证行为矛盾。
  专家缓存的真实限制应以事实描述给出（显存外专家在 CPU 计算、受内存带宽限制、显存越小越需限制上下文）。
  同一兼容性判定只保留一份实现，插件驱动与内置引擎复用，不得各自维护已分叉文案。
  界面须显示摘要、说明与阻断项，说明不得被静默丢弃；须通过纯函数回归与本机真实模型界面验证。

- REQ-130 已实现（2026-11-08）：「打开页面」只打开引擎实际能提供的聊天页面，不打开 JSON 接口或必然报错的地址。
  serve 前端根路径为 `/`；`/props` 是原始 JSON 能力接口，不得作为浏览器入口。
  是否可打开以实际安装包存在 `serve/web/index.html` 为准，各 engine profile 按自己的 serve 根解析，不跨包误用。
  无网页时不启动浏览器，明确说明缺失并以 `http://127.0.0.1:PORT/v1` 作为 OpenAI 兼容入口；GUI 与 CLI `--open` 共用此判定。
  不修改或伪造引擎包内容，不引入代理/第二进程；须通过有/无 `index.html` 两向回归及本机真实包验证。

- REQ-129 已实现（2026-11-08）：KVMem默认输出长度可按模型配置，不以固定1024截断长回复。
  基础参数maxOutputTokens默认8192，整数1–131072且不超过逻辑上下文；思考和答案共用总预算，客户端显式输出限制优先。
  参数保存仅作用当前KVMem模型，不改变B/R、原版PolyStrata或运行会话；非法值明确拒绝，不静默回落。必须通过默认/自定义/非法/context关系回归与本机超过1024tokens的真实输出验证。
