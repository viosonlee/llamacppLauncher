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
| DEF-02 | P1 | 已修复（2026-11-07） | 真实 build 与同发布 runtime 比较，最新不下载，未知/变体不盲目覆盖 |
| DEF-03 | P1 | 已修复（2026-11-07） | llama.cpp doctor 与 updater 共用真实版本及适用发布选择 |
| DEF-04 | P2 | 待核实 | 外部普通草稿与外部 MTP 权重的自动选择未区分 |
| DEF-05 | P2 | 代码审读 | 模型列表过滤与配对规则不一致（`draft-*.gguf`） |
| DEF-06 | P2 | 部分证据、待核实 | PolyStrata Q2_K 预检白名单疑似过时；其它类型须按模型家族验证 |
| DEF-07 | P2 | 代码审读 | PolyStrata 预检硬编码"6GB 低于 8GB 最低要求"（且重复实现） |
| DEF-08 | P2 | 已修复（2026-11-01） | 小数点/逗号输入、逐项与整面板保存口径一致 |
| DEF-09 | P2 | 代码审读 | GPU 互斥只在 GUI 生效，CLI/自启动可双开吃显存引擎 |
| DEF-10 | P2 | 代码审读 | GPU 互斥只看配置记录，不看进程是否存活 |
| DEF-11 | P3 | 代码审读 | 下载临时文件名固定 `.part`，并发下载互相踩踏 |
| DEF-12 | P3 | 代码审读 | `FileSignature` 缓存无上限（键含 mtime） |
| DEF-13 | P3 | 代码审读 | 多个 mmproj/draft 候选时选择不确定 |
| DEF-14 | P3 | 代码审读 | 后台线程直接修改共享 `_config.ModelPresets` |
| DEF-15 | P3 | 代码审读 | 状态轮询每 2 秒读两遍日志（服务未运行时也读） |
| DEF-16 | P3 | 代码审读 | `IncrementalLogReader` 实例与日志路径无界增长；容量早于 count 截断 |
| DEF-17 | P3 | 代码审读 | CUDA 设备缓存无 TTL，只有显式刷新 |
| DEF-18 | P3 | 代码审读 | `CudaService.Parse` 用 `int.Parse` 无溢出保护 |
| DEF-19 | P3 | 代码审读 | 归档 Strata 路径空 `PackageRoot` 抛 `ArgumentException` |
| DEF-20 | P3 | 代码审读 | 文档/注释与实现不一致（多处，含本次新增偏差） |
| DEF-21 | P3 | 已修复（2026-11-01） | benchmark 脱离保留端口段，原回归 80 通过 / 0 失败 |
| DEF-22 | P1 | 已修复（2026-11-07） | KVMem 仅有面板/假协议，原版引擎无分页内核且裸开关不兼容 |
| DEF-23 | P2 | 已修复（2026-11-07） | TCP 监听表漏报阻止完整身份匹配的脱离会话恢复 |

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

**严重度 P1 · 代码审读**

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

**严重度 P1 · 已实测确认**

- 位置：`gui/LlamaManager/Services/DoctorService.cs:54-61`；文档 `docs/CHANGELOG-DECISIONS.md:266`。
- 现象：体检对 llama.cpp 仅做 `File.Exists` 判断，恒为 `[ok]`；文档写"检查 llamacpp / prism / … 的缺失与**更新**"。
- 证据：升级前（b11853，落后 167 个构建）执行 `--doctor` 输出 `[ok] llamacpp: …\llama-server.exe`。
- 影响：用户无法通过体检发现 llama.cpp 落后；文档承诺与实现不符。
- 修复建议：与 DEF-02 共用一套版本比对；或把文档改为"仅检查存在性"，并在体检输出里说明。

## DEF-04 外部草稿模型被自动配上 `--spec-type draft-mtp`

**严重度 P2 · 代码审读**

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

**严重度 P2 · 代码审读**

- 位置：`gui/LlamaManager/MainWindow.Models.cs:25` 对比 `Services/ModelPairingService.cs:44-48`。
- 现象：模型列表只排除 `mmproj*` 与 `mtp-*`，但配对逻辑还接受 `draft-*`。
- 影响：`draft-*.gguf` 既作为可选主模型出现（可被误启动），又被当作配对草稿；用户可选中裸草稿权重。
- 修复建议：抽出统一的"辅助权重文件"判定（`mmproj*` / `mtp-*` / `draft-*`），列表过滤与配对共用。

## DEF-06 PolyStrata 专家格式白名单缺 Q2_K/Q8_K/BF16/F16/F32

**严重度 P2 · 代码审读**

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

**严重度 P2 · 代码审读**

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

**严重度 P2 · 代码审读**

- 位置：`Engines/EngineArbiter.cs`（实现）与唯一调用点 `MainWindow.Runtime.cs:139`。
- 现象：`CommandLineService` 的 `--start` / `--service` 与 `AutostartService` 直接调用
  `backend.StartAsync`，从不检查 `EngineArbiter.ConflictFor`。
- 影响：登录自启动拉起 polystrata 后，再用 CLI/GUI 启动 llamacpp → 两个引擎同时抢 6GB 显存。
- 修复建议：把互斥检查下沉到共享启动入口（例如 `IServerBackend.StartAsync` 的统一包装）；
  非交互场景（autostart/服务）遇到冲突应拒绝并写日志，而不是静默启动。

## DEF-10 GPU 互斥不校验进程存活

**严重度 P2 · 代码审读**

- 位置：`Engines/EngineArbiter.cs`（`ActiveGpuBackend` 仅判断 `config.GetRunning(slot) != null`）。
- 影响：崩溃/断电残留的 session 记录会让"启动另一个引擎"弹出不必要的显存冲突对话框；
  确认后 `StopAsync` 发现进程不存在只清理记录并返回 `false`，交互体验混乱。
- 修复建议：用 `ServerService.IsSessionAlive` / `ValidateProcess` 过滤后再判定占用。

## DEF-11 下载临时文件名固定 `.part`，并发下载互相踩踏

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/Services/EngineDownloadService.cs:30`。
- 影响：GUI 与 CLI 同时 bootstrap（或双开 GUI）下载同一资产时使用同一 `.part`。
  `FileShare.None` 阻止同时写入，所以不能断言会交错写数据；已能从代码确认并发任务会
  争用同一个临时路径，失败分支的清理可能与另一任务交接阶段冲突，需并发回归复现。
- 修复建议：临时名加进程/随机后缀，或复用项目已有的跨进程文件锁思路（`ServerService.LockLifecycle`）。

## DEF-12 `FileSignature` 缓存无上限

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/Services/FileSignature.cs:17-28`。
- 现象：缓存键为 `path|length|mtime`，模型每次改动都会新增一条且永不淘汰。
- 影响：长期运行（尤其反复下载/替换模型）内存缓慢增长。
- 修复建议：按路径只保留最新一条（`ConcurrentDictionary<string, Entry>` 以路径为键），或加 LRU 上限。

## DEF-13 多个 mmproj/draft 候选时选择不确定

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/Services/ModelPairingService.cs:22-27`、`:48`。
- 现象：`Directory.EnumerateFiles` 的顺序不作保证；多个候选且都不含 Q8_0/BF16/F16 时落到
  `candidates[0]`，draft 更是直接 `FirstOrDefault()`。
- 影响：同一目录下多个投影/草稿文件时，自动装配结果可能在不同次启动不同，难以复现问题。
- 修复建议：按文件名排序后取第一个，或候选不唯一时给出"检测到多个候选，请在面板手选"的提示。

## DEF-14 后台线程直接修改共享 `_config.ModelPresets`

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/MainWindow.Models.cs:33`（`Task.Run` 内调用 `ConfigService.RebindOrphanPresets(_config, …)`），
  实现见 `Services/ConfigService.cs:266-320`（增删 `config.ModelPresets`、写 `config.ModelPath`）。
- 影响：`Dictionary` 非线程安全，UI 线程同时读写时可能抛 `InvalidOperationException`（枚举时修改）或出现数据错乱；
  与 AGENTS.md "不阻塞 UI 调度器"的约束方向一致，实现方式不安全。
- 修复建议：后台只计算（文件签名、孤儿判定），结果回到 UI 线程再落地；或对 presets 读写加锁。

## DEF-15 状态轮询每 2 秒读两遍日志

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/MainWindow.Runtime.cs:74`。
- 现象：`LogService.GetGpuSummary(session!, backendId)` 在 `session` 为 `null`（服务未运行）时仍执行，
  且内部会读主服务 stdout/stderr（或引擎日志）两个文件。
- 影响：无服务时也在空转 IO，并与 `LogService` 的写入锁（`Gate`）争用。
- 修复建议：`running == false` 时跳过；或按 session 缓存结果，仅在日志长度变化时重算。

## DEF-16 `IncrementalLogReader` 实例与日志路径无界增长

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/Services/LogService.cs:14-20`（每路径一个 reader，字典从不清理）、
  `IncrementalLogReader.Read(path, capacity = 500)`，而 `ReadFileTail(path, count)` 未把 `count` 传入容量。
- 影响：基准每轮产生 `benchmark-{i}.stdout/stderr.log` 等新路径，条目只增不减；
  `count > 500` 时静默截断（当前调用方都 ≤ 500，属潜在坑）。
- 修复建议：字典按最近使用裁剪；`ReadFileTail` 传 `capacity: count`。

## DEF-17 CUDA 设备缓存无 TTL

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/Services/CudaService.cs:20-28`。
- 影响：显存/驱动状态变化后 UI 一直显示旧值（仅点「刷新」或 `Invalidate()` 才更新）。
- 修复建议：缓存加 30–60 秒 TTL，或按"是否有关键操作（启动/停止）"失效。

## DEF-18 `CudaService.Parse` 用 `int.Parse` 无溢出保护

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/Services/CudaService.cs:47-51`。
- 现象：正则匹配任意长度数字，超过 `int.MaxValue` 时 `int.Parse` 抛 `OverflowException`，
  被上层折算成"检测失败：…"，掩盖真实原因。
- 修复建议：`int.TryParse` + 上限校验。

## DEF-19 归档 Strata 路径空 `PackageRoot` 抛 `ArgumentException`

**严重度 P3 · 代码审读**

- 位置：`gui/LlamaManager/Services/StrataService.cs`（`RunCoreAsync` 中
  `Path.GetFullPath(config.Strata.PackageRoot)`）。
- 影响：归档的 cyber/strata 一次性推理路径在未配置包目录时抛出非友好异常（应为
  "未找到 Strata 包目录"）。
- 修复建议：先判空再 `GetFullPath`。

## DEF-20 文档/注释与实现不一致

**严重度 P3 · 代码审读**

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

- 当前结果：连续两次 80 项通过、0 失败。benchmark 遥测回归不再绑定固定 8095。
  两项新增用例覆盖端口避让及失败/取消不影响既有服务；生命周期测试在进程退出后
  有界等待 TCP 表移除（最多 5 秒），不会重试启动/停止或静默跳过残留监听。
  第二次运行实测 TCP 表延迟移除约 242 ms，说明“退出后立即查表”确实存在时序窗口。
- 修复前现象：`dotnet run --project tests/LlamaManager.Regression -c Release` 输出
  `Results: 77 passed, 1 failed`；失败项为 `benchmark: per-variant telemetry is collected end-to-end and rendered`。
- 证据：`git stash` 后在干净工作区复跑，失败项与改动后完全一致 → 该失败与近期改动无关，属环境相关的既有缺陷（根因见 DEF-01）。
- 影响：套件常态"77/78"，真实回归会淹没在已知失败里；也降低"全绿"作为提交门禁的可信度。
- 修复建议：修 DEF-01（基准端口动态化）后该用例应转绿；在此之前请在 README/AGENT-GUIDE 标注该用例的环境依赖，
  避免每次都要重新判断。

---

## DEF-22 KVMem 仅有参数面板，实际引擎不支持且协议错误

**严重度 P1 · 用户交接指出 → 已实测确认 → 已修复（2026-11-07）**

- 原 c3e3644 在原版 llama.cpp/PolyStrata 显示 KVMem，假测试只检查参数数组，未部署分页内核。
- 上游 v0.19.1 PolyStrata 原生不接受裸 `--kvmem`；以正数预算启用，且需要窗口/草稿/lookup 与 history-reuse 合同。旧 context=4096 与 B32768+R2048 不相容。
- 修复：独立 `kvmem` profile + 完整 sm86 unified 包，保留原版；原版控件移除、陈旧启用参数拒绝，新后端真实转发 CUDA/metrics 环境、同模型分词表与严格 loaded/预算就绪判据。
- 实测：RTX3060 6GB 上实际 prompt=33008/65776、冷区检索正确、SSD 累计读写有效且 errors=0；65747-token 复用，采样显存最大4751 MiB，停止后主/子引擎退出。
- 证据与范围：[KVMem 集成说明](KVMEM-INTEGRATION.md)。仅证明本机该模型/配置；不保证所有 OOM 消失、所有输入质量、256K/百万上下文或未接入的 NInfer/MTP。

## DEF-23 TCP 表漏报导致无记录脱离服务无法恢复

**严重度 P2 · 已实测确认 → 已修复（2026-11-07）**

- 旧失联恢复先检查 `GetActiveTcpListeners()`，返回无监听便完全跳过身份扫描。重复回归中 API 仍 HTTP 就绪、PID/创建时刻/引擎路径/命令行全部匹配，但 25 秒内不能恢复。
- 失败现场：[诊断日志](../downloads/maintenance-20261107/regression-adoption-diagnostic.log) 中 `port=7617 table=False api=True`，引擎与模型路径、实际创建 ticks 均一致，WMI 命令行可读取；不是新增更新测试留下不同引擎路径。
- 最小修复：恢复路径不再以 TCP 表的 false 为绝对门槛，仍保持 5 秒限频和完整进程名称、可执行路径、模型、端口、embedding 校验；WMI 枚举设置有界超时。
- 没有把“绑定失败”当作“端口有服务”，没有放宽为任意同端口进程可接管，也没有通过增加测试等待时间掩盖失败。原恢复回归继续验收实际脱离进程的完整身份。
- 停止回归曾出现相反的快照延迟（原 PID 已退出后表仍报告监听）。验收改为同时要求：原 PID+创建时间+路径已不存活、同地址同端口可独占绑定、HTTP 不再响应；仍保持原 5 秒上限。若只有 TCP 快照仍陈旧则明确输出诊断，不把快照收敛时间等同于实际服务是否残留。

## 建议修复顺序

1. **已完成 DEF-01 / DEF-08 / DEF-21**：正式服务默认 8080，测速使用独立临时端口，小数保存一致，回归转绿。
   （2026-11-07 REQ-124 KVMem 超长上下文参数组集成：回归套件扩充至 80 项，全部通过）
2. **DEF-02 / DEF-03**（同一能力：llama.cpp 版本比对）：先实现版本读取，再接入 `UpdateAsync` 与 doctor。
3. **DEF-09 / DEF-10**（GPU 互斥下沉 + 存活校验）：涉及 CLI/自启动行为，需回归覆盖。
4. **DEF-04 / DEF-05 / DEF-13**（模型配对一致性）：可合并为一个"辅助权重识别"重构。
5. **DEF-06 / DEF-07**（PolyStrata 预检）：合并重复实现并补齐白名单，配 GGUF fixture 用例。
6. **DEF-08 已完成**（数值输入一致性）。
7. DEF-11…DEF-20：按改动成本择机处理。

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