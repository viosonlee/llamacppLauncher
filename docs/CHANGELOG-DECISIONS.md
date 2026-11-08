# 变更与决策记录（CHANGELOG-DECISIONS）

按时间倒序记录影响需求或架构的变更。每条引用需求编号（见 docs/REQUIREMENTS.md）。
普通修复不记；**改变行为、需求或取舍的**才记。

## 2026-11-08

- **预检不得臆断显存下限，说明须可见**（REQ-131 / DEF-26）：删除“6GB 低于 8GB 最低要求”的无条件断言（该检查只读 GGUF 元数据，且与本机 6GB 实机运行矛盾），改为事实性专家缓存说明；插件驱动复用同一判定，删除分叉副本；兼容性提示框显示摘要、说明与阻断项。

- **浏览器入口只打开真实可用的聊天页面**（REQ-130 / DEF-25）：KVMem unified 包未附带 `serve/web/index.html`，根路径在上游前端会抛 `FileNotFoundError`；此前按钮还错误指向 `/props` JSON。现以安装包是否含该页面为准，无页面时不打开浏览器并提示改用 `/v1`，GUI 与 CLI 共用同一判定，不伪造页面或引入代理。

- **按模型配置KVMem默认回复上限**（REQ-129 / DEF-24）：修复前端默认1024将长回复按length截断的问题。新增基础参数maxOutputTokens默认8192、整数边界及context关系校验；思考与答案共享预算，客户端显式值优先，不修改B/R。每轮验证和提交/推送状态见 [软件迭代记录](ITERATION-LOG.md)。

- **用户明确纠正范围：倍数对齐、模型自动保存与硬件一键保守配置**（REQ-127 / REQ-128）：
  - 撤除误加的多轮性能测试界面、命令行入口、测试服务及实测推荐缓存；用户要求的是基础硬件配置，不是实验性调优产品。既有 llama.cpp 参数实测对比（REQ-117 / REQ-118）不在撤除范围。
  - 保留 schema `MultipleOf` 最近合法倍数归一化：失焦/回车/保存时调整，等距取较小，KVMem B/R=32 对齐，明确显示调整原因。
  - 保留按引擎+规范化模型路径隔离的参数自动保存与恢复，不覆盖其它模型参数、独立手工路径或运行会话身份。
  - 硬件一键配置仅由后台 `KvmemHardwareConfigurationService` 生成“未实测”的保守策略，不启动测试模型、不读写实测推荐缓存，不宣称实测最优或整个进程RAM安全保证。
  - 应用使用准备好的同一份完整策略，核验引擎/模型路径/模型签名，并以参数快照CAS拒绝覆盖并发编辑；使用边界见 [KVMem硬件配置指南](KVMEM-RECOMMENDATION.md)。本条不作为本轮构建或回归已通过的证据。

## 2026-11-07

- **规格、用例与提交整理**（REQ-124 / REQ-125 / REQ-126）：新增 [变更规格](KVMEM-CHANGE-SPEC.md) 与 [测试用例矩阵](KVMEM-TEST-CASES.md)，区分108组自动回归、显式GPU验收和本机忽略的运行证据。仅提交必要源码、测试、工具源码与文档；不提交模型、引擎、用户配置、日志或构建产物。

- **全部内置引擎与匹配运行库一键更新、限制旧版数量**（REQ-126）：
  - GUI 全局「一键更新」与 CLI `--update --all` 共用 `EngineAcquisitionService.UpdateAllAsync`，包含 llama.cpp、PolyStrata、KVMem；失败逐项报告，可取消，不停止模型服务。
  - llama.cpp 读取真实 build（包括新 semver/build 输出），按 driver 的 CUDA/UMD 能力选择同一 release 的 binary+cudart 完整配对；不再每次返回未知版本而全量重下，也不硬编码一个过时 CUDA minor。
  - PolyStrata 对应 tag 的原生包、服务源码、exact NVIDIA wheel pins 和私有 Python 运行环境一起验证；不混拷其它引擎 CUDA DLL，不伪写上游 BUILD 版本。
  - 新目录先校验后提交；引擎级公共安装路径优先于模型预设陈旧路径，仅同步仍引用同一旧公共路径的预设，不改模型调优参数或独立手工路径。
  - 启动器 ownership manifest 与下载 receipt 驱动清理；每个 backend/架构保留当前版+最近旧版。运行/手工/用户数据保护优先，无法安全清理只报告，不回滚已提交安装。
  - 随发行包提供的历史 `.log` 仅在安装时有独立 SHA/长度记录且未改动时可随旧包清理；新建或改动的用户日志依然保护。
  - 更新后的本机验证与最终通过数见 `docs/ENGINE-UPDATE-GUIDE.md`；之前 KVMem 32K/64K 证据仍保留，不把更新回归等同于所有模型性能验证。

- **真实 KVMem 独立后端与协议纠正**（REQ-124 / REQ-125）：
  - 保留原提交和原版引擎，新增 `kvmem` profile。上游 v0.19.1 PolyStrata 只支持 qwen35moe，不能覆盖原版后再声称支持其它 GGUF 家族。
  - 本机 RTX 3060 sm86 完整包已按官方 SHA-256 校验后部署；保留 workers/backends/runtime/provenance 全布局。
  - 去掉原版引擎上的误导参数组；新后端以 `--kvmem-budget` 正数启用，禁止裸 `--kvmem`，同时设置 window=1/no-draft/lookup=0 与 history-reuse。
  - RAM 配额默认从错误的 16 GiB 下调为 1 GiB（本机总 RAM 约 24 GiB，模型另占十余 GiB）；KV B/R、预填充和专家搬运工作区均有保守默认。
  - Python/CUDA 使用完整包内运行时，native 环境通过 serve runconfig 的 `env/lib_dirs` 真正传入，不依赖未传到 DetachedProcess 的 LaunchPlan 环境。
  - tokenizer 使用上游 assets.py 的同模型、导出器哈希及五件套缓存，不为纯导出安装 gguf/venv；CPU 视觉、MTP 不默认启用。
  - 就绪检查要求模型已加载与 `/props` 的真实 KV 能力/预算；启动失败/取消仅清理本次完整身份匹配的进程，不把 health HTTP 200 当推理成功。
  - 验证明细与不承诺消除所有 OOM 的限制见 docs/KVMEM-INTEGRATION.md。旧参数测试通过不构成 GPU 端到端证据。

- **原始参数层尝试（历史记录，已由上述纠正）**（REQ-124）：
  - 架构决策（方案 A）：保留现有 `llamacpp` 与 `polystrata` 引擎生态，通过统一参数模型与动态联动 UI 接入 KVMem 超长上下文特性。
  - 参数面板与命令行映射：
    - 在 `LlamaCppParameters` 与 `StrataBackend` 中新增「超长上下文 (KVMem)」参数组；
    - 主开关 `kvmem`（映射 `--kvmem`）；
    - 联动二级参数：`kvmemBudget`（`--kvmem-budget`，默认 32768）、`kvmemGenReserve`（`--kvmem-gen-reserve`，默认 2048）、`kvmemHostMib`（`--kvmem-host-mib`，默认 16384）、`kvmemDiskMib`（`--kvmem-disk-mib`，默认 0）、`kvmemDiskPath`（`--kvmem-disk-path`，路径）、`kvmemSessions`（`--kvmem-sessions`，默认 4）；
    - 动态折叠联动：二级参数配置 `VisibleWhen: "param.kvmem"`，在主开关关闭时不占用视觉空间且不向后端注入命令行或 runconfig 选项；当 `kvmemDiskMib > 0` 时额外展开磁盘持久化路径选择器（`param.kvmem && param.kvmemDiskMib > 0`）；
    - 隔离与独立性：开关关闭时不产生多余启动参数，各模型间通过 `ModelPresets` 独立记忆 KVMem 配置。
  - 验证：
    - `CoreUtilitiesRegression` 覆盖 `--kvmem` 启闭往返投影与 CLI 参数构建；
    - `ParameterVisibilityRegression` 覆盖 `param.kvmem` 布尔与数值复合条件动态可见性计算；
    - `StrataBackendRegression` 覆盖 `BuildRunConfig` 在 KVMem 启闭下的参数组注入；
    - 回归测试 **80 项全部通过**；文档门禁验证通过（63 条需求）。

## 2026-11-01

- **固定 agent 接入 8080 与测速端口隔离**（REQ-123）：PolyStrata schema 默认与 StrataConfig 回退默认
  对齐到 8080，与 llama.cpp/Prism 共用主服务地址。已有自定义端口保留，启动冲突不自动换端口。
  `PortUnavailableReason` 按绑定地址检查可用性，权限拒绝提示可能来自 Windows 保留范围，不把
  “无活动监听器”误当“可绑定”。`TestPortInUse` 保留活动监听语义用于状态和 TCP 就绪检查。
  实测器不再固定 8095，使用系统分配临时端口，并排除 8080、配置、原始参数和运行会话端口；
  分配支持取消且有界，子进程交接前再次预检。变体失败/取消会等待进程清理，不影响正式服务。
  早退时即使 .NET 无法读取附加进程的退出码，也保留 stderr 诊断。
- **小数输入与保存口径一致**（REQ-122）：点号/逗号归一化下沉到读取控件值，避免整面板保存
  把逐项保存后的 `0.5` 覆盖回 `0,5`；校验拒绝 NaN、Infinity 与越界值。
- **验证与缺陷记录**（REQ-123）：新增占用/权限错误分类、动态端口避让、变体早退/取消不触碰现有
  fake 服务测试；WPF 回归覆盖 zh-CN/de-DE/en-US 下逐项及整面板小数保存。
  进程退出后的 TCP 表检查采用最多 5 秒的有界等待，仍会对残留监听报错，不重试启停操作。
  回归连续两次 **80 通过 / 0 失败**；第二次实测 TCP 表延迟移除约 242 ms，验证有界等待的必要性。
  构建 0 警告 0 错误；文档门禁通过（62 条需求）。
  本机 Windows x64 自包含单文件已先发布到临时目录，再通过原子替换部署到正式 `dist`，
  旧版 EXE/PDB 保留备份；暂存版与正式安装文件的 EXE/PowerShell 隔离冒烟测试均通过，
  用户配置哈希与运行会话身份不变，两个主服务配置仍为 8080。
  `docs/KNOWN-DEFECTS.md` 保留审查证据并标注 DEF-01/08/21 本轮修复，其余审读项仍需逐项复现。

## 2026-10-31

- **引擎更新（llamacpp b12020 / polystrata v0.1.45-poly.6）与 PolyStrata 新增路由偏置、对话驻留缓存参数**（REQ-122）：
  - 引擎更新：llamacpp 手动替换发行包至 b12020（启动器对 llamacpp 仅做存在性检查，不自动更新）；
    polystrata 由 `--update` 自动升级至 v0.1.45-poly.6（含 Windows 冷启动双线程预热、Qwen3.6 家族
    Flash-Attention 与 Tensor-Core 解码内核默认开启）。
  - `StrataBackend` 参数面板新增 `cacheBias`（`--cache-bias`，十进制，仅 qwen35moe 家族有效，默认 0 不传）
    与 `conversationCache`（`--conversation-cache-mib`，可选 auto / 固定 MiB，默认关闭不传）：
    未设置时命令行与旧版逐字节一致；十进制值用 InvariantCulture 输出，避免 zh-CN 区域设置发出 "0,5"。
  - 测试：`StrataBackendRegression` 扩展断言（未设置不传、auto 逐字传递、固定 MiB 传递、偏置 0 不传），
    回归 78 项，77 通过；「benchmark: per-variant telemetry」一项为预先存在的环境性失败（与本改动无关，
    改动前后失败一致）。

## 2026-10-24

- **非 llama.cpp 引擎（PolyStrata 等）按模型隔离并保存独立参数预设**（REQ-121）
  - 现象与痛点：先前除 llama.cpp 具备 \ModelPresets\ 机制外，其它后端（如 PolyStrata）所有模型共用单一 \EngineSettings.Parameters\ 字典。切换模型时，上一个模型的上下文、KV、tailSkip、mmproj、草稿模型等参数被直接沿用并覆盖，无法对不同模型（如文本模型 vs 视觉模型，轻量模型 vs 大显存模型）做独立调优与记忆。
  - 架构决策：
    1. 在 \EngineSettings\ 中扩展 \ModelPresets: Dictionary<string, Dictionary<string, string>>\，使用规范化绝对路径索引各模型专属参数。
    2. \ManagerConfig.Normalize()\ 自动进行数据结构平滑迁移，保证旧版配置文件在加载时自动为活跃模型建立首份预设。
    3. 参数变更事件（\OnParameterEdited\）与保存（\SaveEngineParameters\）实时双向同步至当前模型的 \ModelPresets\。
    4. 模型切换流程（\SelectModelAsync\）：切换前先保存前序模型的配置，切换到目标模型时，若已有预设则恢复参数，若为首次选中的新模型则生成新预设，保留引擎级安装与通信路径（packageRoot, serveRoot, pythonPath, servePort, host 等）并触发自动智能配对。
  - 测试：新增 \StrataBackendRegression.RunModelPresets\ 单元回归测试，全套回归用例由 77 项提升至 **78 项** 并全数通过。

- **视觉多模态投影（mmproj）与投机草稿模型（MTP）UI 接入与同目录智能配对**（REQ-120）：
  为了让包含 `mmproj-Q8_0.gguf`、`mmproj-BF16.gguf` 或 `mtp-*.gguf` 的多模态/投机模型能够开箱即用，
  在启动器参数面板新增视觉与草稿权重配置，并实现智能联动：
  1. `LlamaCppParameters` 新增「视觉 / 多模态」分组与 `mmproj` 参数（映射 `--mmproj` / `-mm`）；
  2. `StrataBackend` 新增 `mmproj` 参数，自动拉起伴生 `strata-vision.exe` 并生成 runconfig 的 `vision` 配置；新增 `draftModel`（`--draft-model`）；
  3. 新增 `ModelPairingService`：用户选择模型时，自动探测所在目录是否包含 `mmproj-*.gguf`（优先 Q8_0、BF16）与 `mtp-*.gguf`，并在模型信息面板提示已匹配；
  4. 主模型列表智能过滤：排除 `mmproj*.gguf` 与 `mtp-*.gguf` 独立辅助文件，防止用户误启动裸视觉头或适配器；
  5. 浏览按钮自适应：模型/投影/草稿类文件自动采用 `*.gguf` 过滤器。
  回归测试 76 增至 **77 项**，全部通过。

- **三引擎升级（llamacpp b11853 / prism 10787 / polystrata v0.1.45-poly.3）与 PolyStrata 加速面板**（REQ-119）：
  三引擎均升级至最新上游 release 并清理旧版归档。PolyStrata 参数面板扩充 Tensor-Core 线性注意力（`STRATA_GDN_MMA=1`）
  与长文本 Flash Attention（`STRATA_FATTN=1`、`STRATA_FDEC=1`）以及 Gemma 4 `int8`/`int8k` KV 选项。
  修复 `DoctorService` 跨变体版本解析；A/B 基准实测 RTX 3060 Laptop 上 Qwen3.6-35B Prefill 提速 39.0%，Decode 提速 63.6%。

## 2026-10-19

- **参数实测对比接入过程遥测**（REQ-118，REQ-117 扩展）：此前每组只报完成后的 tok/s。
  现在同时抓引擎自报的 prefill 与投机草稿接受率（llama.cpp 的 `timings.prompt_per_second` /
  `draft_n` / `draft_n_accepted`——先在本机 `llama-server-impl.dll` 里确认了字段名，避免写成
  永远取不到的死代码），并在后台采样 `nvidia-smi` 的显存占用取峰值。三者写进同一行结果，
  让 MoE 调参能直接看到「离显存上限多远、草稿有没有真的被接受」——这两点正是 REQ-113 记录的
  方法论中最有用的变量，而 tok/s 单项无法体现。遥测缺失时静默省略，采样异常不影响测量；
  解析为纯函数并有回归覆盖。

## 2026-10-18

- **参数实测对比入口（REQ-117）**：把 REQ-113 的方法论（同提示、同轮次、独占、按变量分组）
  做成一键操作。计划格式 `标签|参数`（每行一组，# 注释），实测器统一分配端口（8095 起）
  与 `-m/--host`；结果（均值/最差/★最快 + 开始时显存空闲）写入日志页。报告带上显存快照是
  本次实测验证中发现的真问题：poly 占着 5 GB 显存时跑对比，数字比干净环境低一半且不可比——
  现在报告会明说。真实验证：13.29 GB A3B 上 moe 26（11.76）vs moe 30（15.79），方向与历史
  校准一致（绝对值受显存共享影响，这正是要记录快照的原因）。回归 74 → **75 项**。

- **全新机器的模型获取闭环**（REQ-116）：模型列表为空时提供「下载推荐模型…」。内置 5 个
  单文件 GGUF 目录，**每个链接都经 HTTP 200 实测、字节数取自 CDN 响应头**——第一版目录里
  `SmolLM3-3B-Q4_K_M.gguf` 就是 404（真实文件名没有 `-3B` 前缀），实测抓住了它。
  下载防护：磁盘空间预检（含 2 GiB 余量）、下载后 GGUF 文件头校验（HTML 错误页/重定向立即
  识别删除）、同名同大小跳过/不同大小重下。目标目录遵循用户配置的 ModelsDirectory。
  回归 73 → **74 项**（含目录排序、URL 形状、字节数未取整、防护与目标目录回退）。

- **MoE 预设从「砍层」改为「专家上 CPU」**（REQ-115）：旧 `PresetService.Plan` 把整个文件当作
  显存需求线性砍 `-ngl`，13 GiB 的 A3B 在 6 GiB 卡上得到接近纯 CPU 的起点——而这个模型的正确
  答案（实测）是 `-ngl 999 --n-cpu-moe 30`。新公式：显存减去计算预留与 KV 缓存后装专家，
  专家按文件 91% 计，装不下的层数给 `--n-cpu-moe`；对校准机器给出 28（实测边界 26 / 最快 30）。
  同时修复遥测的一个边界：`state="generating"` 且 `generated=0`（reading 刚切换、第一个 token
  还没出）此前被当成 idle，现在按 generating 显示「0 tokens」。
  回归 71 → **72 项**。

- **修复“界面看不到速度”与“poly 下日志不显示不更新”**（REQ-114）：
  - **布局是第一根因**：顶部控制条把状态文本（MaxWidth 300）和速度文本（MaxWidth 200）塞进同一
    水平 StackPanel，WPF 会裁掉溢出而不是压缩——速度实际被渲染到窗口外，llama.cpp 也一样看不见。
    改成三行（引擎 / 按钮 / 状态），速度徽章独占 Grid 列；UI 回归断言徽章右缘不超出卡片。
  - **数据源是第二根因**：polystrata 的 `/slots` 没有 token 计数，采样路径把整个生成期当成
    “提示词处理中”。新增 `PolyMetricsService`：`/metrics` 的 live 状态机（reading → generating →
    idle）自带窗口化 tok/s 与草稿统计，是引擎口径，不再标“约”；不可用时回退旧采样。
  - **poly 没有逐请求日志可看**：引擎与服务都不输出。启动器在每个新完成请求首次出现时写
    “请求完成”一行进管理器日志（按 `requests[].time` 去重，启动前已有的只记录不补写），
    日志页从此有实时活动；poly 启停消息也改为同时写持久日志。
  - **顺手加固**：日志读取失败不再拖垮整个状态轮询、日志页在轮询开头刷新、“清空显示”后能
    重新填充、已消失的进程不再显示为运行中（API 可达时以 API 为准）。新增
    `ServerService.IsSessionAlive` 与 `--read-live-speed <backend>` / `--probe-logs <backend>`
    两个诊断入口。回归 68 → **71 项**。

- **引擎/模型实测基准，以及 MTP 与线程数的结论**（REQ-108、REQ-113）：
  - **新增 `docs/engine-benchmarks.md`**：7 个真实模型 × 2 个引擎的横向对比、MTP 开关、
    线程数扫描，全部用引擎自己回报的 `timings`，附复现命令。
  - **poly 在共同支持的模型上快 1.8–2.6 倍**：原版 Qwen3.6 APEX-I-Mini 70.2 vs 30.4、
    Ornith 64.5 vs 28.4、heretic 64.7 vs 24.5、LFM2.5 51.3 vs 28.6、Gemma 4 QAT 44.8 vs 19.1。
    poly 显存稳定在 4973–5159 MB；llama.cpp 在 4.4–6.0 GB 之间大幅波动（取决于
    `--n-cpu-moe` 与是否开 MTP），贴着 6144 时速度会断崖下跌。
  - **MTP 的结论要按上下文分开**（这条推翻了我当天早些时候「MTP 几乎没用」的判断）：
    - 4K 上下文、`--n-cpu-moe 30`：**开 MTP 34.9 tok/s，关 31.0**，+13%。
    - 40K 上下文、同一模型：开 28.4、关 31.2，MTP 反而更慢；`--n-cpu-moe 30` + MTP
      更是掉到 19.4（显存 5969 撞墙）。
    - 机制：**MTP 固定多吃约 1.2 GB 显存**（5337 − 4161）。4K 时 KV 只占几十 MB，
      付得起；40K 时 KV 与其它缓冲把余量吃掉，MTP 就把模型推过悬崖。
    - 用户实测的「35 tok/s」是对的（4K + moe 30 + MTP = 34.9）；当天早些时候我把它
      归因成「MTP 无用」是错的，真实原因是那次跑在 40K。
    - 接受率 66–67%（llama.cpp）/ 85–93%（poly），说明这些 APEX-MTP 文件的 MTP 头是
      训练过的，不是坏头。
  - **poly 的 `threads` 实测最优为 8**（66.0 tok/s），12 为 63.6，自动（16 线程）约 55，
    与 llama.cpp 的 `-t 8` 一致：超线程会与显存搬运争内存带宽。代码默认仍是 0（自动），
    本机配置写 8。
  - **Gemma 4 选 QAT 而不是后训练量化**：改用 Google 官方 `gemma-4-26B_q4_0-it.gguf`
    （14.44 GB，QAT 训练时就让权重适应 4-bit），实测 poly 44.8 / llama.cpp 19.1 tok/s。
    APEX 与 Q3 的关系同理：**同体积选 APEX，同位数基本打平**，不存在「APEX 一定强」。
  - **新增二创生态调研 `docs/qwen36-35b-a3b-ecosystem.md`**：Ornith 不是 Qwen3.6 的微调
    （同架构不同权重），真正改权重的只有蒸馏与去审查两类，APEX/GSQ/Tiel/Nail 本质是
    「量化 + 模板」；中文能力没有任何本地实测支撑，别为「中文优化」标签买单。
  - **工具修正**：`tools/docs-gate.ps1` 第 5 项仍按 `llamacpp / strata-upstream / polystrata`
    校验 README，而注册表只剩两个引擎；改为 `llamacpp / polystrata`。
  - **补测与清理**：新增 `tests/LlamaManager.Regression/CoreUtilitiesRegression.cs`，覆盖
    参数 schema 的回退规则、路径归一化、模型显示/大小/分类/MoE 判定、llama.cpp 参数投影的
    往返与「手写 flag 不丢」、Strata 日志解析与 PolyStrata 兼容性判定、分词表路径与端口优先级、
    后端状态工厂 / 资源估算下限 / 崩溃日志。回归从 61 项增至 **68 项**。
    顺带删掉 `LlamaCppParameters.IsNumericKey` 与 `NumericKeys`：面板改成按 `ParameterSpec.Min/Max`
    校验后它们已无调用点，留着会变成第二个「数字键」真相来源。

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
