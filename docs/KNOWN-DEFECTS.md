# 已知缺陷登记（KNOWN-DEFECTS）

本文件记录一次初步缺陷审查（2026-10-31，代码基线 `d57ad11`），以及后续修复状态。
这是缺陷与风险清单，不代表已找出所有 bug；“代码审读”条目需核对上游实现并复现后才能定论。
下文位置默认指审查基线，修复后的行号可能变化。
每条缺陷标注**严重度**、**状态**、**位置**、**现象与证据**、**根因**、**影响**、**修复建议**与**回归建议**。

状态含义：
- **已实测确认**：本次审查在本机复现或直接观测到。
- **代码审读**：通过阅读代码路径推导，未在本机触发（多为需要特定配置/并发时序）。
- **已修复**：代码与回归验证完成，保留原始证据；不等同于已经发布或提交。

严重度：**P1** 影响正常使用或造成数据/带宽浪费；**P2** 正确性或一致性缺陷；**P3** 健壮性、性能与可维护性。

---

## 汇总

| 编号 | 严重度 | 状态 | 主题 |
|---|---|---|---|
| DEF-01 | P1 | 已修复（2026-11-01） | 启动绑定预检 + 测速临时端口隔离，正式服务仍使用 8080 |
| DEF-02 | P1 | 已修复（2026-11-01） | `--update` 在活动引擎为 llama.cpp 时每次全量重新下载 |
| DEF-03 | P1 | 已修复（2026-11-01） | 体检不为 llama.cpp 检查更新，但文档声称会检查 |
| DEF-04 | P2 | 已修复（2026-11-01） | 外部普通草稿与外部 MTP 权重的自动选择未区分 |
| DEF-05 | P2 | 已修复（2026-11-01） | 模型列表过滤与配对规则不一致（`draft-*.gguf`） |
| DEF-06 | P2 | 已修复（2026-11-01） | PolyStrata Q2_K 预检白名单疑似过时；其它类型须按模型家族验证 |
| DEF-07 | P2 | 已修复（2026-11-01） | PolyStrata 预检硬编码"6GB 低于 8GB 最低要求"（且重复实现） |
| DEF-08 | P2 | 已修复（2026-11-01） | 小数点/逗号输入、逐项与整面板保存口径一致 |
| DEF-09 | P2 | 已修复（2026-11-01） | GPU 互斥只在 GUI 生效，CLI/自启动可双开吃显存引擎 |
| DEF-10 | P2 | 已修复（2026-11-01） | GPU 互斥只看配置记录，不看进程是否存活 |
| DEF-11 | P3 | 已实测确认 → 已修复（2026-11-01） | 下载临时文件名固定 `.part`，并发下载互相踩踏 |
| DEF-12 | P3 | 已修复（2026-11-01） | `FileSignature` 缓存无上限（键含 mtime） |
| DEF-13 | P3 | 已修复（2026-11-01） | 多个 mmproj/draft 候选时选择不确定 |
| DEF-14 | P3 | 已修复（2026-11-01） | 后台线程直接修改共享 `_config.ModelPresets` |
| DEF-15 | P3 | 已修复（2026-11-01） | 状态轮询每 2 秒读两遍日志（服务未运行时也读） |
| DEF-16 | P3 | 已修复（2026-11-01） | `IncrementalLogReader` 实例与日志路径无界增长；容量早于 count 截断 |
| DEF-17 | P3 | 已修复（2026-11-01） | CUDA 设备缓存无 TTL，只有显式刷新 |
| DEF-18 | P3 | 已修复（2026-11-01） | `CudaService.Parse` 用 `int.Parse` 无溢出保护 |
| DEF-19 | P3 | 已修复（2026-11-01） | 归档 Strata 路径空 `PackageRoot` 抛 `ArgumentException` |
| DEF-20 | P3 | 已修复（2026-11-01） | 文档/注释与实现不一致（多处，含本次新增偏差） |
| DEF-21 | P3 | 已修复（2026-11-01） | benchmark 脱离保留端口段，当前回归 81 通过 / 0 失败 |
| DEF-22 | P2 | 已修复（2026-11-01） | 记录丢失后接管的会话，停止时被 WMI 恢复节流误判为无服务 |
| DEF-23 | P2 | 已实测确认 → 已修复（2026-11-01） | `TestPortInUse` 漏报刚绑定的监听，接管/停止误判“无服务” |
| DEF-24 | P2 | 已修复（2026-11-01） | 引擎更新可能覆盖运行中文件，失败会留下半新半旧安装 |
| DEF-25 | P3 | 已修复（2026-11-01） | 端口探测的监听表兜底偶发拖慢数秒，改为 IPv4/IPv6 双绑定 |

---

## DEF-01 端口占用检测漏掉 Windows 保留端口段

**严重度 P1 · 已实测确认 → 已修复（2026-11-01）**

- 修复：新增 `PortUnavailableReason(port, host)` 按实际监听 IP 试探绑定，区分活动占用和
  权限/保留范围；`AccessDenied` 不武断等同于保留段。`TestPortInUse` 保持活动监听语义，
  不拿不可绑定端口当作插件已就绪。PolyStrata 主服务默认与回退默认对齐为 8080，已有端口不覆盖。
  实测器由系统分配临时端口，排除 8080、配置、原始参数与运行会话端口；分配/清理有界，
  启动前再次预检。不会替正式服务选择临时端口，也不会停止正式服务。
- 验证：新端口分类/分配回归、变体早退/取消保护服务回归及原 benchmark 遥测用例均通过。
  保留端口分类通过纯错误映射测试，不修改 Windows 端口保留配置。
- 限制：预检与子进程实际绑定之间仍有竞争窗口，失败时保留子进程 stderr；不宣称完全消除竞争。

以下保留修复前的证据与建议：

- 位置：`gui/LlamaManager/Services/ServerService.cs:241`（`TestPortInUse`）、`Services/ParameterBenchmarkService.cs:83`（`BasePort = 8095`）、`Models/ManagerConfig.cs`（`StrataConfig.ServePort` 默认 8091）。
- 现象：`TestPortInUse(8095)` 返回 `false`，但同一端口绑定直接失败。
- 证据（本机 PowerShell）：

  ```text
  TestPortInUse(8095) -> False
  bind 8095/loop -> FAIL AccessDenied
  bind 8091/loop -> FAIL AccessDenied
  netsh int ipv4 show excludedportrange protocol=tcp
      8081  8180      ← 8091 / 8095 / 8100 全在保留段内
  ```

  回归用例 `benchmark: per-variant telemetry is collected end-to-end and rendered` 的失败原因即为此：
  变体子进程 stderr 为 `SocketException: DoBind → TcpListener.Start` 失败，随后
  `ParameterBenchmarkService.WaitReadyAsync` 把 `HasExited` 抛出成
  `Process was not started by this object`，两个变体均计为 0 次成功。
- 根因：`TestPortInUse` 只枚举 `GetActiveTcpListeners()`——端口被系统**保留但未监听**时看不到占用；
  而 `AccessDenied (WSAEACCES 10013)` 只在实际绑定那一刻才出现。
- 影响：
  1. 参数实测（`--benchmark` 与 GUI「参数实测对比」）在本机整体不可用；
  2. polystrata 面板默认端口 8091 在本机无法启动，错误只能从子进程 stderr 间接暴露；
  3. 回归套件长期带 1 项失败（见 DEF-21）。
- 修复建议：
  - 预检改为**试探绑定**：`new TcpListener(IPAddress.Loopback, port).Start()`，
    捕获 `SocketException` 时区分 `AddressAlreadyInUse` / `AccessDenied`，两者都判定"不可用"并给出不同文案
    （"被其它程序占用" vs "被 Windows 保留端口段占用，请换端口"）；
  - 保留段信息可用于文案：`netsh int ipv4 show excludedportrange protocol=tcp`（只读探测，注意缓存与超时）；
  - 实测器不再使用固定基址：先由系统分配一个空闲端口（`port 0` 绑定后读取），或从排除段之外取段。
- 回归建议：新增纯函数级用例（注入绑定委托）覆盖"已监听 / 保留段 / 空闲"三种结果；
  基准用例改为动态端口，避免环境耦合。

## DEF-02 `--update` 在活动引擎为 llama.cpp 时每次全量重新下载

**严重度 P1 · 已修复（2026-11-01）**

- 修复：`DoctorService.ReadLlamaCppVersionAsync` 执行 `llama-server --version`，从
  `version: 0.8.0-dev (build 12020, …)` 提取可与 nightly tag 比较的 `b12020`；兼容旧式
  `bNNNN` 与纯数字版本行。
- `EngineAcquisitionService.UpdateAsync` 现在对 llama.cpp 走同一套“最新可用 release → build 比对”流程。
  引擎缺失才进入安装修复；已安装但版本不可读时返回带 `SkippedReason` 的“不更新”，不会再全量覆盖。
- `--update --all` 不再刻意绕开 llama.cpp；初始化环境在 llama.cpp 已存在时也会检查更新，离线失败只记录提示，
  不影响现有可执行文件。
- 验证：夹具版本为 b12020，注入远端 b12020 时断言不下载；带 `no-build` 标记时断言连发布列表都不请求，
  并给出跳过原因。错误实现会尝试不存在的下载 URL，因此该用例能捕获旧的“无条件重装”。
- 性能补充：发布列表选择现只探测一次 GPU 计算能力；更新判定落后后复用已选计划，不再二次查询
  GitHub。回归加入“无资产 release → 可用 release”的序列并断言能力探测次数为 1。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Engines/EngineAcquisitionService.cs:170`、`:204-206`。
- 现象：`InstalledVersionAsync` 对非 `polystrata` 一律返回 `null`，`UpdateAsync` 把 `null`
  当作"无可比对 → 安装"，直接走 `AcquireAsync` 全量下载。
- 证据：该方法的 XML 注释写着 "Engines whose version cannot be read (llama.cpp nightlies) are
  left to the caller"，与实现相反；`Services/CommandLineService.cs:122` 的 `--all` 分支用
  `new[] { "polystrata" }` 刻意绕开 llama.cpp，说明这个坑早已被绕而不是被修。
- 影响：`llama-manager.ps1 -Update`（活动引擎为 llamacpp 的默认安装）每次都下载并重装约 600 MB，
  限流/离线时失败，且会覆盖用户手工放置的发行包。
- 修复建议：为 llama.cpp 读取 `llama-server --version` 的 build 号（`DoctorService.ReadStrataVersionAsync`
  已是同类实现），与最新含 Windows CUDA 资产的发布 tag 比较；无法比对时返回 "不支持自动更新"
  而不是下载。
- 回归建议：用注入的 `ReleasesFetcher` + 下载桩，断言"已最新 → 不调用下载"。

## DEF-03 体检不为 llama.cpp 检查更新，但文档声称会检查

**严重度 P1 · 已修复（2026-11-01）**

- 修复：体检在本地存在 `llama-server.exe` 后读取 build 号；联网模式用最新含本机 CUDA 资产的 release
  比较并输出 `outdated`，网络失败降级为 `manual`，版本不可读时明确说明未检查更新。
- 文档统一：`ENGINE-UPDATE-GUIDE` 说明自动更新优先、手工替换仅作离线/自编译后备；历史 changelog 保留当时状态，
  并注明后续由 REQ-133 修复。
- 验证：回归断言真实格式解析、b12020 本机 vs b12020 远端为最新、b12021 远端为有更新；既有离线降级用例继续通过。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Services/DoctorService.cs:54-61`；文档 `docs/CHANGELOG-DECISIONS.md:266`。
- 现象：体检对 llama.cpp 仅做 `File.Exists` 判断，恒为 `[ok]`；文档写"检查 llamacpp / prism / … 的缺失与**更新**"。
- 证据：升级前（b11853，落后 167 个构建）执行 `--doctor` 输出 `[ok] llamacpp: …\llama-server.exe`。
- 影响：用户无法通过体检发现 llama.cpp 落后；文档承诺与实现不符。
- 修复建议：与 DEF-02 共用一套版本比对；或把文档改为"仅检查存在性"，并在体检输出里说明。

## DEF-04 外部草稿模型被自动配上 `--spec-type draft-mtp`

**严重度 P2 · 已修复（2026-11-01）**

- 修复：自动投机类型移入 `ModelPairingService.ApplySpeculativeDefaults`，按实际 GGUF 元数据判断：
  主模型自带 MTP → `draft-mtp`；外部权重有 nextn/MTP 张量 → `draft-mtp + -md`；
  普通外部草稿 → `draft-simple + -md`；辅助文件无法解析 → 不猜测、不写投机参数。
- 用户显式设置的 `--spec-type` 或 `-md` 优先，不再被自动配对覆盖。
- 证据：本机 `mtp-Cyber-Tiel-Coder-35B-A3B.gguf` 含 4 个 `blk.40.nextn.*` 张量，而对应主模型不含 nextn；
  因此仅凭文件名统一标成 `draft-mtp` 的证据不足。
- 验证：回归覆盖内置 MTP、普通外部草稿、外部 MTP 头、未知文件、显式 `--spec-type`。

以下保留修复前风险：

- 位置：`gui/LlamaManager/MainWindow.Parameters.cs:48-60`（`EnsureBundledMtp`）。
- 现象：模型无内置 MTP 头、但目录里有 `draft-*.gguf`/`mtp-*.gguf` 时，代码写入
  `--spec-type draft-mtp` 并追加 `-md <草稿文件>`。
- 风险：普通外部草稿与外部 MTP 头不能仅凭文件名前缀认定为同一类型。原审查把全部外部
  权重归为 `draft-simple` 的结论证据不足；需先核对已安装 llama.cpp 的模型家族、MTP 头
  加载能力和对应 `--spec-type` 语义，不能统一替换。
- 影响待复现：普通草稿被自动设为 `draft-mtp` 时可能不生效或启动失败，但外部 MTP 头
  的正确组合必须另行验证。
- 修复建议：按实际草稿架构区分普通 draft 与 MTP 权重；无法确定时不自动选择投机类型。
- 回归建议：覆盖内置 MTP、普通外部草稿、外部 MTP 头、未知类型与显式用户设置。

## DEF-05 模型列表过滤与配对规则不一致

**严重度 P2 · 已修复（2026-11-01）**

- 修复：新增 `ModelPairingService.IsAuxiliaryModel`，`mmproj*` / `mtp-*` / `draft-*` 由扫描过滤与
  配对逻辑共用；模型页搜索/排序基于扫描结果做 `ICollectionView` 过滤，不会重新引入辅助权重。
- 验证：通用回归断言三类辅助名均被排除，UI 回归在含辅助权重的目录中只列出两个主模型。

以下保留修复前证据：

- 位置：`gui/LlamaManager/MainWindow.Models.cs:25` 对比 `Services/ModelPairingService.cs:44-48`。
- 现象：模型列表只排除 `mmproj*` 与 `mtp-*`，但配对逻辑还接受 `draft-*`。
- 影响：`draft-*.gguf` 既作为可选主模型出现（可被误启动），又被当作配对草稿；用户可选中裸草稿权重。
- 修复建议：抽出统一的"辅助权重文件"判定（`mmproj*` / `mtp-*` / `draft-*`），列表过滤与配对共用。

## DEF-06 PolyStrata 专家格式白名单缺 Q2_K/Q8_K/BF16/F16/F32

**严重度 P2 · 已修复（2026-11-01）**

- 修复：以本机安装的当前 PolyStrata 源码 `native_mmvq_supported` 为准，补入 `10 (Q2_K)` 与
  `39 (MXFP4)`；仍不把 Q8_K/F16/F32/BF16 当作已知的路由专家 GPU kernel。
- 唯一实现：删除 `ProcessPluginBackend.PolyStrataMatrixAsync` 中的重复白名单，改为调用
  `PolyStrataCompatibility.Analyze`，避免两处再次分叉。
- 验证：Q2_K 与 MXFP4 用例通过，未知 type 40 仍明确阻断并指出张量名。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Engines/PolyStrataCompatibility.cs:12`，同一白名单在
  `gui/LlamaManager/Engines/ProcessPluginBackend.cs`（`PolyStrataMatrixAsync`）重复一份。
- 现象：白名单为 `2,3,6,7,8,11,12,13,14,16,17,18,20,21,22,23,29,42`，缺 `10 (Q2_K)`、
  `15 (Q8_K)`、`30 (BF16)`、`1 (F16)`、`0 (F32)`；命中即写入 blocker。
- 证据：上游 v0.1.45-poly.5 发布说明明确 Kolibri 1 的 **Q2_K** 文件可在 `strata-poly` 上运行；
  尚需核对其具体专家张量是否为 Q2_K。Q8_K/BF16/F16/F32 仅因是 GGML 类型不能认定引擎
  所有家族的专家 kernel 都支持，原审查“应全部放行”的建议证据不足。
- 影响待复现：若模型的专家实际为 Q2_K 且当前引擎支持，该白名单会误报不可运行；
  同类预检重复两处，维护时可能分叉。
- 修复建议：按模型家族与当前引擎 kernel 能力验证后再调整白名单，合并重复实现；
  不应未经验证就把所有未知格式改成允许启动。

## DEF-07 PolyStrata 预检硬编码 6GB 结论（并重复实现）

**严重度 P2 · 已修复（2026-11-01）**

- 修复：删除固定的“6GB 低于 8GB 最低要求”，改为说明实际速度取决于可用显存、内存带宽与专家命中率；
  重复预检已合并到 `PolyStrataCompatibility`，不再由插件后端另写一份。
- 验证：回归断言预检说明不再包含固定 6GB/8GB 结论。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Engines/PolyStrataCompatibility.cs:29`。
- 现象：每次预检都追加固定说明"6GB 卡低于其 8GB 最低要求，速度可能受限"，与实际显卡无关。
- 影响：本机 6GB 已稳定运行该引擎，提示自相矛盾；也无法反映真实推荐值。
- 修复建议：按实际检测到的显存生成条件化提示（`CudaService` 已有数据），或删除该静态说明。

## DEF-08 数值框允许逗号小数，校验用 InvariantCulture 拒绝

**严重度 P2 · 代码审读 → 已修复（2026-11-01）**

- 修复：读取 Decimal 控件值时把逗号归一化成点号，逐项编辑和整面板保存共用该口径；
  校验允许这两种小数分隔符，但拒绝非有限值、无效字符串和越界值。
- 验证：WPF 回归在 zh-CN、de-DE、en-US 下验证 `0,5` / `0.3` 的逐项与整面板保存；
  额外覆盖 NaN、Infinity、auto、2.5 和多个分隔符。
- 说明：逗号小数是本面板显式支持的输入，不是中文区域的默认小数格式。

以下保留修复前的证据与建议：

- 位置：`gui/LlamaManager/MainWindow.Engine.cs:601`（输入过滤允许 `.` 与 `,`）与 `:643-652`（`ValidateValue` 用 `NumberStyles.Float` + `InvariantCulture`）。
- 现象：中文区域用户输入 `0,5` 能通过按键过滤，但校验报"需要一个数字"，且不提示正确写法。
- 影响：本次新增的 `cacheBias`（Decimal）直接受影响；用户看到的是"输入合法却启动被阻止"。
- 修复建议：校验前按当前区域把 `,` 归一化为 `.`（或过滤只允许 `.` 并在 RangeHint 写明）；
  启动路径（`StrataBackend.BuildRunConfig` 用 InvariantCulture 输出）保持不变。

## DEF-09 GPU 互斥只在 GUI 生效

**严重度 P2 · 代码审读 → 已修复（2026-11-01）**

- 位置：`Engines/EngineArbiter.cs`（实现）与唯一调用点 `MainWindow.Runtime.cs:139`。
- 现象：`CommandLineService` 的 `--start` / `--service` 与 `AutostartService` 直接调用
  `backend.StartAsync`，从不检查 `EngineArbiter.ConflictFor`。
- 影响：登录自启动拉起 polystrata 后，再用 CLI/GUI 启动 llamacpp → 两个引擎同时抢 6GB 显存。
- 修复：新增 `EngineArbiter.EnsureCanStart(backendId)`，并在三个 `IServerBackend` 实现
  （`LlamaCppBackend`、`ProcessPluginBackend`、`StrataBackend`）的 `StartAsync` 首行调用。
  互斥因此落在共享启动入口上：GUI 仍先用 `ConflictFor` 弹确认，CLI 的 `--start`/`--service`
  （登录自启动走的就是这条命令）碰到冲突会抛 `InvalidOperationException` 并以非零码退出，
  日志里说明是哪个引擎占着显卡，而不是静默双开。
- 边界：同一后端的不同槽位（llama.cpp 主服务 + 嵌入服务）互不冲突，与既有行为一致。
- 验证：回归新增 `engines: GPU exclusion cannot be bypassed by CLI or autostart`，直接断言后端
  `StartAsync` 与 `CommandLineService --start` 都会被拒；完整回归 82/0 连续两次通过。

## DEF-10 GPU 互斥不校验进程存活

**严重度 P2 · 代码审读 → 已修复（2026-11-01）**

- 位置：`Engines/EngineArbiter.cs`（`ActiveGpuBackend` 仅判断 `config.GetRunning(slot) != null`）。
- 影响：崩溃/断电残留的 session 记录会让"启动另一个引擎"弹出不必要的显存冲突对话框；
  确认后 `StopAsync` 发现进程不存在只清理记录并返回 `false`，交互体验混乱。
- 修复：`ActiveGpuBackend` 改为只统计**进程仍然存活**的会话：`ProcessId > 0` 时按 PID 实际存在与否
  判定，进程已不存在则视为未占用；记录带 `StartTimeUtcTicks` 时同时核对进程创建时间，避免
  PID 被复用后错误占卡。刻意不复用 `ValidateProcess` 的引擎路径比较——用户换过引擎目录后会把
  仍在运行的旧进程判成“已退出”，那才是真正危险的方向。
  没有 PID 的异常记录仍按占用处理（保守），不会因为身份缺失就放第二个引擎进来。
- 验证：回归同时覆盖活会话（当前进程 PID → 判定占用、与 polystrata 冲突）、PID 复用
  （当前 PID + 不匹配创建时间 → 不占用）和死会话（不存在的 PID → 不占用、
  `EnsureCanStart` 不抛异常）。

## DEF-11 下载临时文件名固定 `.part`，并发下载互相踩踏

**严重度 P3 · 已实测确认 → 已修复（2026-11-01）**

- 位置：`gui/LlamaManager/Services/EngineDownloadService.cs:30`。
- 影响：GUI 与 CLI 同时 bootstrap（或双开 GUI）下载同一资产时使用同一 `.part`。
  `FileShare.None` 阻止同时写入，所以不能断言会交错写数据；已能从代码确认并发任务会
  争用同一个临时路径，失败分支的清理可能与另一任务交接阶段冲突，需并发回归复现。
- 实测：按缺陷建议加了并发回归后发现**两个**问题。① 共用一个 `.part` 时第二个任务
  `FileShare.None` 直接失败；② 即便临时名分开，两个任务同时 `File.Move(overwrite: true)`
  发布同一目标时，Windows 的“替换式移动”会给失败的一方 `UnauthorizedAccessException`，
  偶发失败（本地连跑 5 次全失败，之后又时好时坏）。
- 修复：临时文件名改为 `<目标>.<pid>.<guid>.part`，各任务互不干扰；最终发布改走
  `PublishAsync`，对 `IOException`/`UnauthorizedAccessException` 做 8 次有界退避重试——
  两边的载荷都通过了校验，后写覆盖先写是正确的，重试即可收敛，不必为一个良性竞态报下载失败。
- 验证：新增“同一资产并发下载两次”用例（本地 HTTP fixture 每连接一个任务，真实并发），
  断言两次都成功、目标内容完整、目录下无 `.part` 残留；该用例连续 10 次通过，完整回归 83/0。

## DEF-12 `FileSignature` 缓存无上限

**严重度 P3 · 已修复（2026-11-01）**

- 位置：`gui/LlamaManager/Services/FileSignature.cs:17-28`。
- 现象：缓存键为 `path|length|mtime`，模型每次改动都会新增一条且永不淘汰。
- 影响：长期运行（尤其反复下载/替换模型）内存缓慢增长。
- 修复：缓存改为以完整路径为键的 `Entry(长度, mtime, 签名)`，文件变化时**替换**同一键而不是新增；
  条目数超过 512 时淘汰任意一条（条目只有路径 + 两个长整数 + 64 字符，上限约百 KB）。
- 验证：回归断言同一文件两次计算命中缓存、内容变化后签名随之变化、缺失文件返回 null。

## DEF-13 多个 mmproj/draft 候选时选择不确定

**严重度 P3 · 已修复（2026-11-01）**

- 修复：投影与草稿候选都先按文件名（忽略大小写）排序，再应用 Q8_0/BF16/F16 偏好或取首项；
  同一目录的名称顺序不再影响结果。
- 验证：回归先在目录中写入 `draft-b.gguf` 再写入 `draft-a.gguf`，仍稳定选择 `draft-a.gguf`。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Services/ModelPairingService.cs:22-27`、`:48`。
- 现象：`Directory.EnumerateFiles` 的顺序不作保证；多个候选且都不含 Q8_0/BF16/F16 时落到
  `candidates[0]`，draft 更是直接 `FirstOrDefault()`。
- 影响：同一目录下多个投影/草稿文件时，自动装配结果可能在不同次启动不同，难以复现问题。
- 修复建议：按文件名排序后取第一个，或候选不唯一时给出"检测到多个候选，请在面板手选"的提示。

## DEF-14 后台线程直接修改共享 `_config.ModelPresets`

**严重度 P3 · 已修复（2026-11-01）**

- 修复：模型扫描前在 UI 线程复制一份轻量预设快照；后台线程只对快照做孤儿判定、签名采样和评分，
  返回不含共享对象引用的重绑定计划。回到 UI 线程后，计划通过 `ConfigStore.Update` 一次事务落地，
  并只写回预设与 `ModelPath`，不覆盖其它活动设置或运行会话。
- 安全合并：落地方案会再次检查源预设仍与计划快照一致、目标键仍未占用；扫描期间新增的目标预设优先，
  用户已编辑的源预设不会被旧计划覆盖。其它窗口在扫描期间保存的无关预设也会保留。
- 验证：回归断言后台计算不修改活动字典、并发新增与用户编辑都能保留、保存事务与另一窗口写入可合并；
  原有“同名但签名不同必须拒绝”用例继续通过。

以下保留修复前证据：

- 位置：`gui/LlamaManager/MainWindow.Models.cs:33`（`Task.Run` 内调用 `ConfigService.RebindOrphanPresets(_config, …)`），
  实现见 `Services/ConfigService.cs:266-320`（增删 `config.ModelPresets`、写 `config.ModelPath`）。
- 影响：`Dictionary` 非线程安全，UI 线程同时读写时可能抛 `InvalidOperationException`（枚举时修改）或出现数据错乱；
  与 AGENTS.md "不阻塞 UI 调度器"的约束方向一致，实现方式不安全。
- 修复建议：后台只计算（文件签名、孤儿判定），结果回到 UI 线程再落地；或对 presets 读写加锁。

## DEF-15 状态轮询每 2 秒读两遍日志

**严重度 P3 · 已修复（2026-11-01）**

- 修复：状态轮询只在日志页或非 llama.cpp「运行输出」面板可见时调用 `RefreshLog`；切换页面时按需刷新。
  无运行会话时跳过 `LogService.GetGpuSummary`，不再为空闲状态读取两个日志文件。
- 验证：完整回归 81/0；日志页仍保留渲染、刷新与“请求完成”断言，性能路径改动未削弱可见行为。

以下保留修复前证据：

- 位置：`gui/LlamaManager/MainWindow.Runtime.cs:74`。
- 现象：`LogService.GetGpuSummary(session!, backendId)` 在 `session` 为 `null`（服务未运行）时仍执行，
  且内部会读主服务 stdout/stderr（或引擎日志）两个文件。
- 影响：无服务时也在空转 IO，并与 `LogService` 的写入锁（`Gate`）争用。
- 修复建议：`running == false` 时跳过；或按 session 缓存结果，仅在日志长度变化时重算。

## DEF-16 `IncrementalLogReader` 实例与日志路径无界增长

**严重度 P3 · 已修复（2026-11-01）**

- 位置：`gui/LlamaManager/Services/LogService.cs:14-20`（每路径一个 reader，字典从不清理）、
  `IncrementalLogReader.Read(path, capacity = 500)`，而 `ReadFileTail(path, count)` 未把 `count` 传入容量。
- 影响：基准每轮产生 `benchmark-{i}.stdout/stderr.log` 等新路径，条目只增不减；
  `count > 500` 时静默截断（当前调用方都 ≤ 500，属潜在坑）。
- 修复：`LogService.Readers` 改为带最近使用时间的 LRU，最多 64 条；`ReadFileTail` 把
  `max(count, 64)` 作为容量传给 reader，调用方要多少就保留多少（之后仍 `TakeLast(count)` 精确截断）；
  `IncrementalLogReader.Read` 再对容量做 `[1, 20000]` 夹取，防止调用方把常驻内存撑爆。
- 验证：回归把 800 行日志按 700 行索取，断言拿满 700 行且首尾正确（旧实现只能给 500 行）。

## DEF-17 CUDA 设备缓存无 TTL

**严重度 P3 · 已修复（2026-11-01）**

- 修复：设备缓存改为带完成时间的条目，默认 TTL 30 秒；未完成的探测继续由同一 `Lazy<Task>` 共享，
  已完成的条目在 TTL 内直接复用，过期后由首个调用者替换。时间从探测完成起算，避免慢探测刚结束
  就被再次触发。
- `Invalidate()` 仍会立即清空缓存；显式刷新与启动/停止后的关键操作语义不变。
- 验证：回归把 TTL 缩短到 80 ms，断言并发调用只探测一次、TTL 内仍复用、过期后恰好增加一次。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Services/CudaService.cs:20-28`。
- 影响：显存/驱动状态变化后 UI 一直显示旧值（仅点「刷新」或 `Invalidate()` 才更新）。
- 修复建议：缓存加 30–60 秒 TTL，或按"是否有关键操作（启动/停止）"失效。

## DEF-18 `CudaService.Parse` 用 `int.Parse` 无溢出保护

**严重度 P3 · 已修复（2026-11-01）**

- 修复：MiB 字段改用 `long.TryParse`，负数夹到 0、超大值夹到 `int.MaxValue`，无法解析时为 0。
  设备列表仍可显示，不再因单个异常数值把整个结果变成“检测失败”。
- 验证：回归注入 30 位总显存与 30 位空闲显存文本，断言结果不抛异常且分别为上限与 0。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Services/CudaService.cs:47-51`。
- 现象：正则匹配任意长度数字，超过 `int.MaxValue` 时 `int.Parse` 抛 `OverflowException`，
  被上层折算成"检测失败：…"，掩盖真实原因。
- 修复建议：`int.TryParse` + 上限校验。

## DEF-19 归档 Strata 路径空 `PackageRoot` 抛 `ArgumentException`

**严重度 P3 · 已修复（2026-11-01）**

- 修复：q36 路径探测先检查 `PackageRoot` 非空，再调用 `Path.GetFullPath`；空值继续交给
  `ResolvePaths` / `ResolveQ36Paths` 返回已有的“未找到 Strata 包目录”友好错误。
- 验证：q36 回归先用空包目录和 qwen35moe 模型启动，断言得到 `DirectoryNotFoundException` 且文案指向包目录。

以下保留修复前证据：

- 位置：`gui/LlamaManager/Services/StrataService.cs`（`RunCoreAsync` 中
  `Path.GetFullPath(config.Strata.PackageRoot)`）。
- 影响：归档的 cyber/strata 一次性推理路径在未配置包目录时抛出非友好异常（应为
  "未找到 Strata 包目录"）。
- 修复建议：先判空再 `GetFullPath`。

## DEF-20 文档/注释与实现不一致

**严重度 P3 · 已修复（2026-11-01）**

- 已逐项校正下表：更新器 XML 注释改为“未知版本跳过且说明原因”；llama.cpp 体检现已真正检查更新；
  PolyStrata 不再硬编码 6GB/8GB；引擎更新指南改为自动更新优先、手工替换仅作后备。
- 历史 `CHANGELOG-DECISIONS` 保留当时“仅存在性检查”的事实，并注明该限制已由 REQ-133 修复，避免把历史记录改写成事后状态。

以下为修复前偏差：

| 位置 | 偏差 |
|---|---|
| `EngineAcquisitionService.UpdateAsync` XML 注释 | 声称无法读版本的引擎"留给调用方"，实现是无条件下载（DEF-02） |
| `docs/CHANGELOG-DECISIONS.md:266` | 声称体检检查 llama.cpp 更新，实现只查存在性（DEF-03） |
| `Engines/PolyStrataCompatibility.cs:29` | 静态"6GB 低于 8GB 最低要求"（DEF-07） |
| `docs/ENGINE-UPDATE-GUIDE.md` 4.2 节 | 仍写"手工替换 `strata-poly.exe`"，与现在的 `--update` 自动升级路径重复且未提自动/手动边界 |

修复建议：以代码为准逐条校正；`docs-gate` 已能保证需求编号与交叉引用，但无法发现这类语义偏差，
建议在改动实现时同步更新对应注释（本次会话已在 `docs/parameter-ui-design.md` 5.2 节补齐新增参数表）。

## DEF-21 回归套件长期带 1 项失败

**严重度 P3 · 已实测确认 → 已修复（2026-11-01）**

- 当前结果：完整回归 **81 项通过、0 失败**。benchmark 遥测回归不再绑定固定 8095。
  两项新增用例覆盖端口避让及失败/取消不影响既有服务；生命周期测试在进程退出后
  有界等待 TCP 表移除（最多 20 秒，适配 Defender/磁盘繁忙时的延迟），不会重试启动/停止或静默跳过残留监听。
  第二次运行实测 TCP 表延迟移除约 242 ms，说明“退出后立即查表”确实存在时序窗口。
- 修复前现象：`dotnet run --project tests/LlamaManager.Regression -c Release` 输出
  `Results: 77 passed, 1 failed`；失败项为 `benchmark: per-variant telemetry is collected end-to-end and rendered`。
- 证据：`git stash` 后在干净工作区复跑，失败项与改动后完全一致 → 该失败与近期改动无关，属环境相关的既有缺陷（根因见 DEF-01）。
- 影响：套件常态"77/78"，真实回归会淹没在已知失败里；也降低"全绿"作为提交门禁的可信度。
- 修复建议：修 DEF-01（基准端口动态化）后该用例应转绿；在此之前请在 README/AGENT-GUIDE 标注该用例的环境依赖，
  避免每次都要重新判断。

## DEF-22 接管会话后的停止偶发漏掉进程

**严重度 P2 · 已修复（2026-11-01）**

- 位置：`gui/LlamaManager/Services/ServerService.cs` 的 `GetProcess` / `RecoverByPort`。
- 现象：回归用例“配置记录丢失后重新接管、再停止”间歇性失败，`StopAsync` 返回“没有服务”。
- 根因：配置里已有接管的会话记录，但 `ValidateProcess` 暂时校验失败时，`RecoverByPort`
  仍受“每 5 秒最多一次 WMI 扫描”的节流限制，在节流窗口内直接返回 null；停止路径因此失去目标。
- 影响：用户看到服务仍在运行/端口仍被占用，停止按钮却报告没有可管理的服务。
- 修复：仅当配置里**存在**运行会话记录时，端口恢复跳过该节流并立即重新核对；
  无记录的状态轮询仍保留 5 秒节流，避免空转 WMI。接管仍要求引擎路径与模型路径同时匹配。
- 补充：若停止时记录仍在、但进程已经自然退出，`StopAsync` 清理记录并返回成功（停止这个目标
  状态已经达成）；只有连记录都没有时才返回“未管理”，避免用户看到自相矛盾的状态。
- 验证：完整回归在 Windows 本地路径通过 **81/0**，接管后停止用例连续运行通过。

## DEF-23 `TestPortInUse` 漏报刚绑定的监听，接管/停止误判“无服务”

**严重度 P2 · 已实测确认 → 已修复（2026-11-01）**

- 位置：`gui/LlamaManager/Services/ServerService.cs` 的 `TestPortInUse`，以及依赖它的
  `RecoverByPort` / `StopCoreAsync`。
- 现象：回归用例“配置记录丢失后重新接管、再停止”间歇性失败，报
  `detached server not re-adopted` 或 `could not stop adopted server`；失败时进程仍然存活，
  有时端口上确实有监听，但 `TestPortInUse` 返回 `false`。同一现象也解释过
  “benchmark 泄漏监听”的偶发误报。
- 证据：在本机用 200 次“绑定 0 → 立即查询”实验复现，`GetActiveTcpListeners()` 漏报
  **19/200（约 10%）**，且是连续端口（10481–10491）成片漏报；每轮重试 20 次、每次 5 ms 仍然漏。
  说明 Windows 监听表存在刷新延迟，靠重试无法消除。
- 根因：`TestPortInUse` 只枚举 `GetActiveTcpListeners()`；刚建立的监听套接字尚未进入该表时，
  `RecoverByPort` 会在端口检查处直接返回 null，接管和停止随之失去目标。
- 影响：用户丢失配置记录（崩溃、清理、手改）后，仍在运行的服务会被显示为“未运行”，
  停止按钮报“没有由本工具管理的服务”，而端口实际被占用。
- 修复：`TestPortInUse` 先做一次 loopback 绑定探测——只有 `AddressAlreadyInUse` 才算占用；
  `AccessDenied`（Windows 保留段）仍判为“不是活动监听”，保持与 `PortUnavailableReason`
  的语义分工；探测结束后再查一次监听表作为 IPv6-only 监听的兜底。
  （2026-11-01 后续：该监听表兜底已由 DEF-25 的 IPv6 绑定探测替代。）
- 旁证：单独实测确认 TIME_WAIT 连接不会让该绑定探测误报占用（有无 `ExclusiveAddressUse` 均能绑定成功），
  因此服务停止后立即查询端口不会被 2×MSL 的残留连接拖成“仍在监听”。
- 测试卫生：回归套件现在在收尾时按可执行文件路径清理本次隔离目录内的残留 fixture 进程，
  失败运行不再积累孤儿 `llama-server.exe`（它们会拖慢后续的 WMI 恢复扫描）。只匹配隔离根目录下的进程，
  不会碰用户的真实服务。
- 验证：接管后停止用例连续 6 次通过（修复前约 40% 失败）；完整回归连续 3 次 **81/0**，
  运行结束后残留 fixture 进程为 0。

---

## DEF-24 引擎更新覆盖运行中文件，失败留下半新半旧安装

**严重度 P2 · 代码审读 → 已修复（2026-11-01）**

- 位置：`gui/LlamaManager/Engines/EngineAcquisitionService.cs` 的 `AcquireAsync`，以及
  `gui/LlamaManager/Services/EngineDownloadService.cs` 的直接解压路径。
- 现象：`--update`、顶部「配置环境」和已完成更新的引导流程不检查服务是否仍运行；每个归档下载后
  立即直接解压到正式引擎目录。第二个归档/源码失败，或 Windows 因正在使用的 DLL/EXE 拒绝覆盖，
  会让第一个归档已经替换、其余仍为旧版本。
- 根因：缺少运行身份门禁，且下载、解压、发布三步没有事务边界。
- 影响：用户更新正在使用的引擎时可能中断服务，甚至得到无法启动的混合安装；重跑更新是唯一恢复手段。
- 修复：
  - `EngineArbiter.IsBackendRunning` 复用会话 PID、启动时间与引擎路径校验；llama.cpp 同时检查主服务与
    嵌入服务，其它后端检查自己的槽位；
  - `UpdateAsync` 在确认落后但服务仍存活时直接返回 `SkippedReason`；`AcquireAsync` 在联网前再拒绝一次；
  - 所有归档先下载，解压到管理器同卷暂存目录；发布前重新校验运行身份；
  - `PublishStagedDirectories` 先把旧文件重命名到本次备份目录，再放入新文件；任一步失败按逆序回滚；
  - 正常完成后清理暂存与备份。
- 限制：避免的是可处理失败与运行中覆盖，不宣称具备断电级原子性；进程在极短的提交阶段被强制终止时，
  暂存目录可能保留，用户可重新运行初始化环境修复。
- 验证：隔离 fixture 启动真实服务后：
  - `AcquireAsync` 在释放查询前即拒绝，发布查询计数为 0；
  - b12019 本机对 b12020 远端时，`UpdateAsync` 返回“正在运行”且不下载；
  - 事务发布正常替换目标并保留无关文件；
  - 锁定第二个目标文件制造失败，确认第一个已发布文件回滚到原内容；
  - 本地 HTTP 假发布端到端安装引擎与源码，确认 BUILD 版本和 packageRoot/serveRoot 均写回。

---

## DEF-25 端口探测的监听表兜底偶发拖慢数秒

**严重度 P3 · 已实测确认 → 已修复（2026-11-01）**

- 位置：`gui/LlamaManager/Services/ServerService.cs` 的 `TestPortInUse`。
- 现象：完整回归偶发输出 `TCP release after embedding backend stop: 7069 ms`；服务进程已经退出，
  但端口释放检查在首次调用中停留数秒。重复 5 次聚焦用例均未复现，说明是系统监听表/枚举的偶发延迟。
- 根因：DEF-23 已改用 IPv4 loopback 绑定作为权威判断，但为了覆盖 IPv6-only 监听仍会在每次探测后调用
  `GetActiveTcpListeners()`。该 API 返回的是延迟刷新的快照，枚举本身也可能被 Windows 服务拖慢。
- 影响：会话记录缺失时，接管/停止路径同步调用该探测可能让 UI 等待数秒；状态轮询中的恢复也会变慢。
- 修复：改为分别绑定 `127.0.0.1` 和 `::1`。任一协议族的绑定返回 `AddressAlreadyInUse` 即为占用，
  两个都成功才算空闲；不再调用监听表。保留段/权限错误仍判为“不是活动监听”。
- 验证：IPv4 监听/释放、IPv6-only 监听/释放均通过绑定探测；同一端口连续 5 次停止后立即重启成功。
- 限制：只判断 loopback 上是否已有监听，这与启动预检和“本机服务是否还在监听”的用途一致；
  绑定外部网卡但未绑定 loopback 的进程不会挡住宅机 127.0.0.1 上的新服务。

---

## 建议修复顺序

1. **已完成 DEF-01 / DEF-08 / DEF-21**：正式服务默认 8080，测速使用独立临时端口，小数保存一致，回归转绿。
2. **DEF-02 / DEF-03**（同一能力：llama.cpp 版本比对）：先实现版本读取，再接入 `UpdateAsync` 与 doctor。
3. **DEF-09 / DEF-10 已完成**（GPU 互斥下沉到后端启动入口 + 存活校验，回归覆盖 CLI/自启动）。
4. **DEF-04 待核实**（外部普通草稿与 MTP 头的语义区分）；**DEF-05 / DEF-13 已完成**。
5. **DEF-06 / DEF-07**（PolyStrata 预检）：合并重复实现并补齐白名单，配 GGUF fixture 用例。
6. **DEF-08 已完成**（数值输入一致性）。
7. **DEF-22 已完成**（接管会话的立即恢复）。
8. **DEF-23 已完成**（端口占用探测改为绑定探测，接管/停止不再误判）。
9. **DEF-02～DEF-04 / DEF-06 / DEF-07 / DEF-11 / DEF-12 / DEF-14 / DEF-16～DEF-20 已完成**。
   当前审查表只剩经验证后确认的实现，不再有待修复的 P1/P2 项；后续以真实使用回归和性能实测为主。
10. **DEF-24 已完成**：引擎更新与初始化增加运行中门禁、暂存发布和失败回滚。
11. **DEF-25 已完成**：端口存活探测改为 IPv4/IPv6 双绑定，去掉慢速监听表兜底。

## 验证方式（本次审查使用）

```powershell
# 绑定/占用实测（PowerShell）
$l=[System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback,8095)
try { $l.Start(); 'OK' } catch { $_.Exception.InnerException.SocketErrorCode } finally { $l.Stop() }
netsh int ipv4 show excludedportrange protocol=tcp

# 回归套件
dotnet build LlamaManager.sln -c Release
dotnet run --project tests/LlamaManager.Regression -c Release

# 文档门禁
pwsh -File tools/docs-gate.ps1
```

DEF-01 的定位过程（保留供后续参考）：在失败用例前临时打印 `report.Errors` 与
`logs/benchmark-{i}.stderr.log` 尾部，得到 `SocketException → TcpListener.Start` 栈；
对照 `netsh` 排除段确认 8095 位于 8081–8180 保留区内，且 `TestPortInUse` 对该端口返回 `false`。
临时打印已在定位后移除，仓库中不保留调试代码。
