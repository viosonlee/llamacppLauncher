# 变更与决策记录（CHANGELOG-DECISIONS）

按时间倒序记录影响需求或架构的变更。每条引用需求编号（见 docs/REQUIREMENTS.md）。
普通修复不记；**改变行为、需求或取舍的**才记。

## 2026-10-17

- **strata-upstream 投机加速与显存槽位性能修复**（REQ-106）：
  - **根本原因排查**：在 RTX 3060 Laptop (6GB) 上，此前 `strata-upstream` 测速仅有 27 tok/s，而同一模型在 `polystrata` 下可达 50+ tok/s。经深度反查源码与运行日志，定位到两个性能杀手：
    1. `strata.exe` 并不支持 `--vram-reserve-mib`（日志显式输出 `ignoring --vram-reserve-mib ...`），且默认 `expert_slots` 为 0。引擎内部计算 `budget = free - 768 MiB` 后被双重缓冲的 MMQ 提示分块（`staging = 1.59 GiB`）完全吞噬，导致分配给显存的专家槽位 `S` 坍缩为 0（`0 of 256 per layer in VRAM (0.00 GiB)`），100% 的 MoE 专家全部在 CPU 线程池计算。
    2. 模型自带的 MTP（Multi-Token Prediction）多 Token 投机块已被加载进显存（`MTP block loaded`），但由于 `StrataBackend` 从未传递 `--mtp 2` 参数，`strata.exe` 内部默认 `mtp_d = 0`，完全未启用投机预测，每次只能按单 Token 逐步解码。
  - **修复措施与参数优化**：
    1. 为 `strata-upstream` 引入专属参数 `expertSlots`（默认 16，占用约 0.75 GiB 显存），启动时向 `strata.exe` 传递 `--expert-slots 16`，使 GPU 专家命中率提升至 40%~50%，未命中专家与动态置换无缝并行。
    2. 传递 `--mtp 2`（`mtpDrafts`，默认 2），激活 MTP 投机采样加速，实测草稿接受率达到 70%~95%。
    3. `LogService` 扩展诊断正则，支持解析 `hybrid experts: X of Y per layer in VRAM` 并在界面顶部状态栏直观显示。
  - **实测性能**：在同一 RTX 3060 笔记本上，`strata-upstream` 实测生成速度从 27 tok/s 跃升至 **50.3 ~ 72.3 tok/s**（提升至原先的 1.9x ~ 2.7x）。

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
