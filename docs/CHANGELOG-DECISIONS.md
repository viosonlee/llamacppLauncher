# 变更与决策记录（CHANGELOG-DECISIONS）

按时间倒序记录影响需求或架构的变更。每条引用需求编号（见 docs/REQUIREMENTS.md）。
普通修复不记；**改变行为、需求或取舍的**才记。

## 2026-10-18

- **退役 `strata-upstream` 后端**（REQ-112，废弃 REQ-011/REQ-106）：
  - **背景**：三个引擎并存时，`strata-upstream`（`strata.exe`）与 `polystrata`（`strata-poly.exe`）
    在 qwen35moe 上完全重叠。用户问「它还有没有存在的必要」，答案用三组事实定下来：
    - **能力**：`strata.exe --families` 只有 `qwen4exp qwen35moe`；`strata-poly` 支持
      qwen35moe / glm5-next / gemma4 / deepseek4 / lfm2moe。上游唯一独有的是 `qwen4exp`
      （Qwen3.8-Flash-Next），而它最小的 Coder IQ1_M 主分片 29.6 GB、官方写明「fits a PC with
      32 GB of RAM」，本机 23.4 GB 内存 + 6 GB 显存根本装不下（且 Coder 版砍掉一半专家，
      中文能力退化）。
    - **速度**：同一 Ornith-1.5-35B-A3B，上游 35.1 tok/s、预热 27.4–88.4 s；poly 46.0 tok/s
      （tail 0）/ 49.8（tail 6）、载入 3.1 s。
    - **显存控制**：上游对 `--tail-skip` 只打印 `ignoring`，也没有 `--kv`/`--vram-reserve-mib`；
      poly 三者齐备，专家缓存还按空闲显存动态伸缩（日志 `2162 -> 2069 experts`）。
  - **决策**：删除后端，而不是留着「以后可能用」。理由：真到有 32 GB 以上内存那天，该装的是
    PolyStrata 的完整主引擎（同一份 qwen4exp 代码，版本更新、带 `--kv int8`/`--spec`/
    `--resident-experts`），而不是这个 0.1.44.1 的定制分支。
  - **移除**：`StrataEngineProfile.UpstreamProfile` 与 `VerifiesFamilies` 探测、注册表条目、
    `EngineCatalog.StrataPlan`/`StrataRepo`/`StrataSourceRepo`/`StrataSourceBranch`/`BranchArchive`、
    采集与 bootstrap/doctor/CLI 的 upstream 分支、面板字段 `expertSlots`/`mtpDrafts`/`prefill`/
    `maxNew`、`--expert-slots`/`--mtp`/`--prefill`/`--max-new`/`--pool-workers` 的 runconfig 分支、
    `LogService` 的 `hybrid experts:` 正则，以及对应的 4 项回归断言。
  - **保留**：`StrataService`/`StrataPackService`/`StrataCompatibility`（分词表准备与
    `ProcessPluginBackend` 复用）、`StrataConfig` 旧配置块（polystrata 的回退来源）。
  - **顺带修正**：面板上的 `poolWorkers` 从来没被写进命令行（只有上游分支写 `--pool-workers`，
    poly 分支没写），等于一个假控件。改为 `threads`，真正写 `--threads N`（poly 的等价选项是
    「显存外专家在 CPU 上计算用的线程数」）。
  - **现场**：删掉 `engines/strata-upstream`（806.2 MB）、`venvs/strata-upstream`（180.4 MB）、
    `data/strata-upstream`（19.2 MB），共 1005.8 MB；配置里的 `Engines.strata-upstream` 段一并清除，
    备份在 `%TEMP%\config-before-upstream-removal-*.json`。
  - **验证**：`dotnet build` 0 警告 0 错误；回归 61 项全过（原 62 项减去已失去前提的
    「挂起引擎探测」一项）；`tools/docs-gate.ps1` 通过。

- **引擎更新能力、polystrata 的 kv 选项，以及上下文/端口的实测结论**（REQ-110、REQ-111）：
  - **体检报「有更新」却无法执行**：`BootstrapService` 只在引擎或 `serve/server.py` 缺失时调用
    `AcquireAsync`，已安装的引擎永远走不到下载分支，所以体检里的「运行初始化环境更新」是一句空话。
    新增 `EngineAcquisitionService.UpdateAsync`：读 `BUILD.json` 得到本机版本，用
    `DoctorService.IsOutdated` 与最新可用发布比较，落后才重新下载；`--bootstrap` 在引擎已存在时
    也走这条路径，并新增 CLI `--update`。实测把 polystrata 从 0.1.44.1 更新到 v0.1.45-poly.1。
  - **polystrata 的 `--kv` 无法从界面设置**：`BuildRunConfig` 从未写这个参数。面板新增 `kv`
    （默认 fp16 / q8_0），空值不写命令行。注意它只对 qwen35moe 家族有效，其它模型启动会被拒绝。
    实测 53,127 token 提示词：fp16 解码 40.4 tok/s、q8_0 37.6 tok/s —— 这个长度没有收益，
    省下的 KV 显存不足以让专家缓存多留一层；收益只在 KV 真正挤压缓存时出现。
  - **上下文可以放大**：polystrata 的 KV 按需增长（`--kv-whole` 默认关），所以 `--max-context`
    配大本身不占显存。实测 13,819 / 28,879 / 93,340 token 的提示词：填充 1129 / 970 / 756 tok/s，
    解码 50.5 / 51.3 / 32.2 tok/s，显存始终约 5.0 GB。40K 很轻松，100K 可用但解码降到约 32。
  - **三个引擎统一端口**：`servePort` 与 llama.cpp 的 `Port` 都设为 8080。实测三个引擎都支持
    `/v1/chat/completions`、`/v1/messages`（Anthropic）与 `/v1/responses`，agent 工具配一次即可；
    只有 `/v1/status` 是 strata 家族独有。
  - **修正 `tailSkip` 的说明**：原面板文案写成「末尾若干层改用另一条执行路径」，与引擎实际语义
    不符。正确语义是「显存外、且在一个窗口内每个 token 都排在第 N+1 位之后的专家被跳过」，
    跳过的是命中率最低的专家，更快但答案会略有变化；项目自带安装脚本给 Qwen3.6 家族设 6。

## 2026-10-17

- **llama.cpp 参数面板迁到 schema，删除手写控件块**（REQ-109）：
  - **背景**：P1 让 schema 能表达完整参数交互后，llama.cpp 仍保留 385 行手写 XAML 与一套
    `SyncSelection` / `SyncArgsFromControls` 双向同步代码，同一个参数要在两处维护，且它的
    MoE/MTP 分区靠代码手动显隐，与其它引擎的 `VisibleWhen` 规则不一致。
  - **决策**：llama.cpp 与其它引擎共用同一套 schema 面板，但保留「原始参数串」作为唯一数据源。
    `Engines/LlamaCppParameters.cs` 声明 16 个字段并负责双向投射：`ReadFromArgs` 把 flag 投影成
    面板值，`Apply` 只改写该字段自己的 flag，用户手写的其它 flag 原样保留；「高级」页的原始
    参数框仍是任意 flag 的兜底，输入后即时回填面板。
  - **删除**：`MainWindow.xaml` 的手写控件块、逐控件同步处理器、`GetEngineParamDetailedHelp`
    与 `InfoButton_Click` 的 switch（帮助文案已随 P1 迁入后端）、`MoeSection`/`MtpSection` 的
    手动显隐、以及构造函数里手工填充 CPU 核心下拉的代码。
  - **顺带修掉的两个真 bug**：可编辑下拉的 `SelectionChanged` 里 `Text` 尚未更新，会把旧值写回
    参数串；`ComboBoxItem.Content` 带「（推荐）」后缀，使默认值被当作非法数字而标红。
  - **验证**：离屏渲染 `logs/ui-regression-1.png` 确认面板无报错、默认值/开关/搜索/恢复默认完整；
    回归改为按 `Param_<key>` 取控件，覆盖「面板编辑 → 原始参数串」「原始参数串 → 面板」两个方向、
    可编辑下拉的自由输入与预设切换、以及切引擎后旧控件不残留。

- **参数面板 schema v2 与 Strata 参数按实测收敛**（REQ-107、REQ-108）：
  - **背景**：三个引擎里只有 `llamacpp` 是 llama.cpp；`strata-upstream`（`strata.exe`）与
    `polystrata`（`strata-poly.exe`）是独立引擎，能接受的命令行标志并不相同。此前面板
    `VisibleWhen` 已声明但从未被消费，`Choice`/`Boolean` 的编辑值也不会落盘（控件注册只覆盖
    `TextBox`），且 Strata 面板缺少单位、范围、说明与推荐值。
  - **决策**：不把 llama.cpp 的面板照搬到其它引擎，而是把 schema 升级到能表达同样的交互
    （`Help`/`Unit`/`Editable`/`Advanced`/`RangeHint`、`Choices` 带说明与推荐、`VisibleWhen` 求值），
    再不支持的引擎上只显示它真正认的参数。
  - **标志实测**（真实模型 + `--serve`，不是占位路径）：upstream `strata.exe` 接受
    `--native`、`--max-context`、`--prefill`、`--max-new`、`--mtp`、`--expert-slots`、
    `--pool-workers`；`--parallel`、`--batch`、`--slots`、`--warm-start`、`--fit-max-tokens`、
    `--tail-skip`、`--adapt-async`、`--expert-cache`、`--peer-slots`、
    `--conversation-cache-slots`、`--vram-reserve-mib` 会被日志标记为 `ignoring …`
    （`not an option of this engine`），因此它们**不是**面板字段，也不会被写进命令行。
    poly `strata-poly.exe` 确实接受 `--tail-skip` 与 `--vram-reserve-mib`，但 `--expert-slots`
    是硬错误（`unknown option`），所以它只在 upstream 出现。
  - **后果**：`prefill`/`maxNew` 之前只从旧 `StrataConfig` 块读取，现在改由面板字段驱动
    （`ParamInt` 查找，旧值仍作回退）；poly 新增 `tailSkip` 字段。批量并发若将来要做，
    应由 `serve/server.py` 的 `parallel` 转 `--batch`/`--slots`（上限 8），而不是伪造 CLI 参数。
  - **校验**：越界值红框加内联错误并阻止启动，绝不静默改写；只有用户真正改动过的值才落盘，
    未改动的字段仍由引擎或旧配置块决定。
  - **回归**：新增 `VisibleWhen` 语法测试、插件清单 v2 兼容测试，以及“只发引擎认识的标志”的
    runconfig 断言（覆盖 `--prefill`/`--max-new`/`--pool-workers` 生效与 `--tail-skip` 等不出现）。

- **strata-upstream 投机加速与显存槽位性能修复**（REQ-106）：
  - **根本原因排查**：在 RTX 3060 Laptop (6GB) 上，此前 `strata-upstream` 测速仅有 27 tok/s，而同一模型在 `polystrata` 下可达 50+ tok/s。经深度反查源码与运行日志，定位到两个性能杀手：
    1. `strata.exe` 并不支持 `--vram-reserve-mib`（日志显式输出 `ignoring --vram-reserve-mib ...`），且默认 `expert_slots` 为 0。引擎内部计算 `budget = free - 768 MiB` 后被双重缓冲的 MMQ 提示分块（`staging = 1.59 GiB`）完全吞噬，导致分配给显存的专家槽位 `S` 坍缩为 0（`0 of 256 per layer in VRAM (0.00 GiB)`），100% 的 MoE 专家全部在 CPU 线程池计算。
    2. 模型自带的 MTP（Multi-Token Prediction）多 Token 投机块已被加载进显存（`MTP block loaded`），但由于 `StrataBackend` 从未传递 `--mtp 2` 参数，`strata.exe` 内部默认 `mtp_d = 0`，完全未启用投机预测，每次只能按单 Token 逐步解码。
  - **修复措施与参数优化**：
    1. 为 `strata-upstream` 引入专属参数 `expertSlots`（默认 16，占用约 0.75 GiB 显存），启动时向 `strata.exe` 传递 `--expert-slots 16`，使部分 MoE 专家在 GPU 上计算；设为 0 时仍交给引擎自动计算。
    2. 传递 `--mtp 2`（`mtpDrafts`，默认 2），激活模型自带 MTP 块的投机采样加速。
    3. `LogService` 扩展诊断正则，支持解析 `hybrid experts: X of Y per layer in VRAM` 并在界面顶部状态栏直观显示。
    4. 测速优先采用服务端 `timings.predicted_per_second`，避免 MTP 将多个 Token 合并在一个 SSE 事件时低估速度。`--mtp 2` 实测接受约 70%（212 个草稿接受 149 个），因此上游路径通常仍低于带额外 lookup 加速的 PolyStrata。
    5. 专家槽位默认按上下文调整：不超过 8192 时使用 16 槽，大于 8192 时交给引擎自动计算；只有用户显式填写槽位数才强制覆盖。6GB 卡在 40960 上下文下启动时已显示 `0.00 GiB of VRAM free`，再强制 16 槽会通过 WDDM 换页，重新掉到约 27 tok/s。
    6. CLI 的 `--port` 现在也会覆盖 Strata runconfig 与 `serve.server` 监听端口；此前它只改动主配置，实际服务仍使用保存的 `servePort`。
  - **实测性能**：同一 RTX 3060 Laptop (6GB)、4096 上下文、256 Token 请求，`strata-upstream` 从约 28.0 tok/s 提升到 39.5 tok/s（16 槽）；24 槽实测 41.3 tok/s，但显存只剩约 8 MiB，存在驱动换页风险，因此默认仍保留 16 槽。不同模型量化、提示词与 GPU 配置会有明显差异。

- **多引擎日志与实时速度监控适配**（REQ-105）：
  - **日志查看器按引擎动态路由**：原 `LogService.ReadRecent()` 仅读取 llama.cpp 的日志文件，导致在 polystrata / strata-upstream 后端下日志页完全空白。「打开日志文件」同样硬编码为 `GetMainStderrPath()`。现支持按选定后端读取 `logs/engine-{backendId}.log` 以及 stdout/stderr，并正确定位打开日志。
  - **Strata 服务端实时测速采样**：Strata 的 `serve/server.py` 在单请求模式（`--batch 0`）下未在 `/slots` 返回当前生成的 `n_decoded`，导致测速监控服务误判并丢弃采样。现已在 `server.py` 的 `/slots` 暴露 `n_decoded` 与 `n_prompt_tokens`。
  - **空闲历史生成速度展示**：原速度监控只解析 llama.cpp 的 stderr 耗时行。现新增对 Strata 引擎日志格式（`[strata] done: X tokens in Y s (Z tok/s)`）及 `/v1/status` 的 `last_timings` 接口的解析与轮询，确保在请求完成后在顶部常驻显示最新速度。
  - **MoE 专家显存分布展示**：针对 Strata 引擎在日志中输出的 `strata-poly: loaded in ...; X of Y routed experts in VRAM (Z GiB)`，提取显存内专家分布并显示在顶部状态条。

- **全新电脑一键配置并启动**（REQ-104）：
  - **启动拦截与一键配置启动一体化**：在新电脑上点击「启动服务」时，若当前引擎未就绪，不再直接抛出 FileNotFoundException / 空路径异常，而是弹出友好确认提示「是否立即一键自动下载并配置环境，并在完成后自动启动服务？」，确认后自动进入日志页实时呈现下载解压与环境初始化进度，完成后无缝拉起服务。
  - **顶部控制条常驻「配置环境」按钮**：在顶部控制条「体检」旁新增「配置环境」按钮，一键初始化当前所选引擎的二进制文件及 Python 依赖环境。
  - **CLI 与脚本全面升级**：CLI `--start` 增加 `--auto-bootstrap` / `--bootstrap` 无缝前置初始化；`--bootstrap --all` 支持一次性配置全部内置引擎；`llama-manager.ps1` 与 `llama-manager.cmd` 支持 `-Bootstrap`、`-Doctor`、`-All`、`-AutoBootstrap`、`-Backend`。
  - **修复 Windows 下 Strata 引擎 STATUS_DLL_NOT_FOUND (0xC0000135)**：`strata.exe` 动态依赖 `cublas64_13.dll`，在没有全局安装 CUDA Toolkit 的新电脑上运行时会直接报 0xC0000135 退出并导致 doctor 误判为缺失 qwen35 路径。现已在初始化时自动将 CUDA 运行时库补全到引擎目录，并在进程启动时补充 PATH 环境变量。
  - **修复 StrataBackend 未配置时抛出 ArgumentNullException**：未下载引擎源码时，`Path.GetDirectoryName(ResolveServer(...))` 曾抛出 `Value cannot be null. (Parameter 'path')`，现已增加明确的文件检查与指引异常。

## 2026-10-16

- **Python 自动安装**（REQ-100）：Strata 家族初始化时缺 Python 3.10+ 自动装——winget 优先
  （`Python.Python.3.12`、用户级、静默、免管理员），回退 python.org 3.12.10 amd64
  （`/quiet InstallAllUsers=0 PrependPath=1`）。装完探测标准目录（PATH 未刷新也能找到）再建 venv。
  失败不静默：日志给出人工步骤，doctor 把该项标 missing 并指向自动路径。发现接缝可替换，回归不触网。
- **体检按钮改为全局**（REQ-101）：一键体检检查**所有**引擎，原先放在「高级」页（内容随所选引擎
  变化）语义错误。现移到顶部控制条，与「刷新」并列且**相互独立**（刷新=本地快速探测，体检=联网深度
  检查）；结果写入全局「日志」页并自动切页，切引擎不丢失。
- **新增 `docs/AGENT-GUIDE.md`**（REQ-102）：面向自动化 agent 的全新电脑部署指南（前置条件、
  路径布局、CLI 三步、Python 自动安装、引擎/模型矩阵、排障表、agent 约束、验收清单），
  纳入文档门禁必填与交叉引用检查。
- **修复 CLI 无值开关**（REQ-103）：`--doctor --offline` 曾因「每个选项都要求带值」抛
  「命令选项缺少值」。现支持无值开关 `--offline`，带值选项缺值时报错并**指明选项名**。
- **产品更名**「本地大模型助手」（REQ-001）。界面标题变更；程序集/仓库名不变，
  因 ConfigService 依赖安装布局定位 ManagerRoot。
- **新增 doctor 一键体检**（REQ-040~043）：CLI `--doctor` + GUI「一键体检」。
  检查 llamacpp / prism / strata-upstream / polystrata / python 的缺失与更新；
  Prism 无公开下载源，报手动状态。
- **llama.cpp 下载按 GPU 选 CUDA 大版本**（REQ-014）：实测发现 13.4 nightly 在 RTX 2060
  （sm_75）报 `PTX was compiled with an unsupported toolchain`——官方 13.4 构建不再携带
  Turing/Volta SASS。现在计算能力 < 8.0 选 CUDA 12.4，并已在本机验证 12.4 构建可启动推理。
- **嵌入服务独立成 TAB**（REQ-033）：从「高级」页底栏升级为完整「嵌入」页
  （模型选择 + 端口 + 启停）。定位为资料量化 / RAG 用向量化服务。
- **修复 CLI 状态/停止读错槽位**（REQ-022）：`--status`/`--stop`/`--open` 此前硬读
  `main` 槽，任何非 llamacpp 后端都误报"未运行"；现按后端槽位解析。
  实测：strata-upstream 启动 → status 报 ready → stop 干净退出。

## 2026-10-15

- **PolyStrata 由插件改为内置后端**（REQ-012；废弃 REQ-091）：`strata-poly.exe` 与
  `strata.exe` 一样只说行协议（`--serve`），HTTP API 来自 `serve/server.py`；原 plugin.json
  的 `--port`/`/health` 启动方式永远无法就绪。两者合并为 `StrataBackend` 的两个 profile。
- **修复分词表工具变体不匹配**（REQ-016）：PolyStrata 的 `strata_tokenizer.py` 把
  `vocab.json` 写进 `--out/tokenizer/` 子目录，upstream 版直写 `--out/`；改为探测实际产物。
- **修复 DetachedProcess 忽略工作目录**（REQ-025）：`python -m serve.server` 此前在
  Python 安装目录启动，报 `No module named 'serve'`。工作目录改为显式参数。
- **引擎一键下载落地**（REQ-014/015）：EngineDownloadService（下载+SHA-256+zip-slip 防护）、
  EngineCatalog（发布 JSON → 安装计划，纯函数可测）、EngineAcquisitionService（查询→下载→
  解压→写配置）。llama.cpp 二进制在 prerelease，扫描发布列表取首个含 Windows CUDA 的版本。
- **Strata 引擎发布源切换到 fork**（REQ-014）：官方 Niko1221/Strata 拒绝 Q3_K 专家，
  APEX 模型必须用补丁构建；发布 `v0.1.44.1-apex.1`（sm_75/86/89/120 + PTX）到
  viosonlee/apex-on-strata，下载含 SHA-256 校验。

## 2026-10-15（早）

- **UI 重设计**（REQ-030/031/032/034）：顶部统一控制条 + 功能标签页；删除两套重复的
  启动/停止按钮。
- **UI 修复**：schema 面板路径输入框误入标签列（三个窄框）；区块分隔线画成竖线；
  CPU MoE 开关文字被截断。

## 2026-10-14 及以前

- **可插拔引擎后端架构**（REQ-003/010~013/020~023）：BackendRegistry + IEngineBackend/
  IServerBackend + EngineArbiter；配置迁移即删（REQ-051）。
- **Strata cyber pack 路径归档**（废弃 REQ-090）：统一走引擎自带 HTTP 服务。
