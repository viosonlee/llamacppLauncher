# 软件迭代记录

目标：每轮执行问题确认 → 修复 → 记录 → 回归 → 本机验证 → 提交 → 推送。至少15轮；轮数不是提交数，未验证项目不得计为完成。

## 第01轮（2026-11-08）：KVMem默认输出截断与工作区基线整理

状态：**第01轮完成**。修复、回归、本机验证、发布、源码提交与远程推送均已完成；整体目标继续，至少还需推进后续14轮。

### 问题确认

- 用户请求在1024 tokens处停止。服务日志明确为 `length, cancel=False`，服务仍存活。
- 启动器给KVMem前端写死 `kvmem_default_max_tokens=1024`；思考与答案共享该总预算，界面没有可调项。
- 同时整理此前已验证但未提交的倍数归一化、模型参数域、硬件保守配置与误加多轮测试撤除；不重新添加测试产品功能。

### 修复

- KVMem新增当前模型的「默认回复上限」基础参数 `maxOutputTokens`，默认8192。
- 合法范围1–131072，不能超过逻辑上下文；非法配置明确拒绝，不静默回落。
- 客户端显式输出预算仍优先；不改变B/R/原生KV参数，不给原版PolyStrata注入此默认项。
- 配置字段按现有引擎/模型域保存，一键硬件配置不覆盖用户自定义回复上限。

### 验证结果

- Release构建0警告/0错误；自动回归 **118通过、0失败**，含默认/空值、自定义、边界、非法输入、context关系、纯Build与原版隔离。
- 本机WPF验证：16384按模型A保存，模型B使用4096，往返恢复且硬件一键配置不覆盖两者；误加测试按钮仍不存在。
- RTX3060Laptop6GB / Qwen3.6-35B-A3B-APEX-MTP-I-Compact真实推理：不填写max_tokens时 **7893 completion tokens**、finish=stop；显式max_tokens128时 **128 tokens**、finish=length。服务实际props默认8192，原生B/R仍8192/512。
- 发布包的EXE help/service/status/stop和PowerShell包装器隔离验证通过。
- 原始证据：[回归](../downloads/iteration-20261108-round01/regression.log)、[WPF](../downloads/iteration-20261108-round01/ui-verification.log)、[原生输出](../logs/iteration01-output-20261108/logs/engine-kvmem.stdout.log)、[发布包](../downloads/iteration-20261108-round01/published-smoke.log)。这些是本机忽略目录，不随代码提交。
- 测试只管理隔离根中的自身实例，结束确认退出与显存释放；用户配置与既有引擎不提交。

### 提交／推送

- 源码提交：`e989d9b`，`fix: configure KVMem reply budget and preserve model settings (round 01)`。
- 已成功推送 `origin/main`（`ac9c8b2..e989d9b`）。此轮记录随后以文档提交补齐。
- 已部署验证过的发布EXE，新SHA256：`DC6C6139969465B8915BAE666AC9056D72D91A8537E9B24B41C3847258704BDE`；用户配置哈希保持不变，保留上版EXE回退副本。
- 模型、引擎、用户配置、日志和构建产物未暂存或提交。

## 第02轮（2026-11-08）：KVMem「打开页面」打开坏页面／JSON接口

状态：**第02轮完成**。修复、回归、本机验证、发布、提交与推送均完成。

### 问题确认

- 启动器对 KVMem 的“打开页面”打开 `http://127.0.0.1:PORT/props`，那是原始 JSON 能力接口，不是聊天界面。
- 更深一层：真实 KVMem 统一包的 `serve/web/` 只有 `history-export.html`，**没有 `index.html`**；服务根路径直接抛 `FileNotFoundError` 并中断连接，而前端控制台却打印“open http://…/ in a browser to chat”。
- 本机隔离实测：`package declares web UI: False`，根路径请求失败，`/props` 为 `application/json`，推理 HTTP 200 正常。原版 PolyStrata 包则确实带 `serve/web/index.html`。

### 修复

- 新增 `StrataBackend.WebUiUrl` 统一返回 serve 前端根路径 `/`，不再指向 `/props`。
- 新增 `StrataBackend.HasWebUi`：仅当实际安装包内存在 `serve/web/index.html` 才认为有聊天页面；KVMem 与原版各自按自己的 serve 根解析。
- GUI「打开页面」与 CLI `--open` 在无网页时**不打开浏览器**，明确说明缺页并以 `http://127.0.0.1:PORT/v1` 作为可用入口；有网页时正常打开根路径。
- 不改引擎包内容、不伪造页面、不替换上游前端；不新增代理或第二进程。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **119 通过、0 失败**，新增用例覆盖：URL 不含 props、根路径结尾 `/`、无 `index.html` 判定为不可用、补上 `index.html` 后判定为可用、原版 profile 不误用 KVMem 包页面、GUI/CLI 入口存在。
- 本机隔离实测（RTX3060 6GB / Qwen3.6-35B-A3B）：`package declares web UI: False`，根路径请求如预期失败，`/props` 返回 JSON，推理仍 HTTP 200；结束后自身进程退出、显存释放。
- 发布包隔离验证通过。证据：[回归](../downloads/iteration-20261108-round02/regression.log)、[原生网页契约](../downloads/iteration-20261108-round02/native-webui-b.log)、[发布包](../downloads/iteration-20261108-round02/published-smoke.log)。

### 提交／推送

- 发布包隔离验证通过（`--help`/`--service`/`--status`/`--stop` 与 PowerShell 包装器）。
- **`dist` 部署当时推迟**：用户启动器正在运行（PID 18600），按约束不得为部署停止用户程序。启动器关闭后，第03轮的构建已包含本轮改动，由第03轮一并部署，见 [轮次汇总](#轮次汇总)。

## 第03轮（2026-11-08）：兼容性说明含错误显存下限且从不显示

状态：**第03轮完成**。

### 问题确认

- 预检说明硬编码“6GB 卡低于其 8GB 最低要求，速度可能受限”，但本机 RTX3060 Laptop 6GB 已稳定运行该引擎；该结论与本机实测矛盾。
- 该判定只读 GGUF 元数据，**没有任何 GPU 观测**，却断言显存下限。
- 进一步发现：`CompatibilityReport.Notes` 被计算并随报告返回，**界面从未显示**，只显示摘要与估算；MTP 草稿、专家缓存限制等说明对用户不可见。
- `PolyStrataCompatibility.Analyze` 与 `ProcessPluginBackend.PolyStrataMatrixAsync` 是两份重复实现，文案已分叉。

### 修复

- 去掉无条件显存下限断言，改为事实性说明：显存外专家在 CPU 计算、受内存带宽限制，显存越小越需要更小的上下文与专家缓存。
- 插件 `polystrata-matrix` 改为复用同一 `PolyStrataCompatibility.Analyze`，删除重复实现。
- 兼容性提示框显示摘要、说明与阻断项；说明不再被静默丢弃。

### 验证结果

- 回归 **119 通过、0 失败**；新增断言覆盖：三份报告均不再含“8GB 最低要求”、保留专家缓存说明。
- 本机 WPF 实机（真实 Cyber-Tiel GGUF）验证：切到 poly-strata 后提示框包含“专家缓存”与“预检通过”，且不含“8GB 最低要求”。
- 证据：[回归](../downloads/iteration-20261108-round03/regression.log)、[WPF 实机](../downloads/iteration-20261108-round03/ui-verification.log)。

### 提交／推送

- 发布包隔离验证通过；`dist` 已部署本轮构建（同时包含第02轮修复），新 SHA256：`F238389294E4A546AD3865C238A4F740F62B971B8032AEA2736C3DD3563BA9B1`，用户配置哈希保持不变，保留回退副本。见“轮次汇总”。

## 第04轮（2026-11-08）：未填写的回复上限在小上下文下阻止启动

状态：**第04轮完成**。

### 问题确认

- 第01轮新增的 `maxOutputTokens` 默认固定 8192。当用户把 B/R 调小、逻辑上下文设为 4096（合法组合：B2048+R256）时，**从未填写的**这一字段仍为 8192，启动被“默认回复上限不能超过逻辑上下文”拒绝。
- 即：用户没有改动该字段，却被一个隐藏默认值挡住启动；错误信息也无法指明“这是未填写时的默认值”。

### 修复

- 未填写/空白时默认取 `min(8192, context)`，随逻辑上下文收缩，不再阻止启动。
- 用户显式填写的值仍严格校验：必须为整数、1–131072 且不超过 context；显式越界依旧明确报错，不静默改动用户输入。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **119 通过、0 失败**。新增断言：context4096/B2048/R256 且输出上限留空 → 生成 `kvmem_default_max_tokens=4096`；显式 2048 生效；显式 4097 仍被拒绝。
- 本机真实服务（隔离根、RTX3060 6GB、Qwen3.6-35B-A3B）：用 context4096/B2048/R256、输出上限留空的配置 `--start` **成功就绪**；`/props` 返回 `n_predict=4096`；生成的 runconfig 为 `--max-context 4096 / --kvmem-budget 2048 / --kvmem-gen-reserve 256`。修复前该组合会在启动前抛异常。
- 结束后 `--stop` 停掉本次自身实例，进程退出、显存回到 0 MiB。
- 证据：[回归](../downloads/iteration-20261108-round04/regression.log)、[管理器日志](../logs/iteration04-smallctx-20261108/logs/llama-manager.log)、[生成配置](../logs/iteration04-smallctx-20261108/data/kvmem/7203C732B350677C/strata-runconfig.json)。

### 提交／推送

- 发布包隔离验证通过；`dist` 已部署本轮构建，新 SHA256：`4D13F68D520AB187DE5A0174312D3352DF368E3BEB6B2E43105762D78F79F54D`，用户配置哈希保持不变，保留回退副本。见“轮次汇总”。

## 第05轮（2026-11-08）：GPU 互斥只看记录、且 CLI/自启动可绕过

状态：**第05轮完成**。

### 问题确认

- `EngineArbiter.ActiveGpuBackend` 只判断 `GetRunning(slot) != null`：崩溃/重启/手动结束留下的**过期记录**会被当成仍在占用显卡，导致之后任何引擎都“冲突”。
- 互斥只在 GUI 的启动按钮里调用；CLI `--start` 与自启动直接进 `StartAsync`。KVMem 后端有自己的一段检查（且仅 KVMem），原版 PolyStrata 与 llama.cpp 都没有，因此可同时加载两个占显存引擎。
- 同一后端在被记录为运行时也没有保护，可能加载第二个实例。

### 修复

- `ActiveGpuBackend` 改用完整身份校验（PID+创建时刻+可执行路径）确认进程仍存活；过期记录不再占用显卡。
- 新增 `EngineArbiter.EnsureGpuAvailable`：统一判定并给出指名冲突引擎的可读错误；同一后端重复启动也拒绝。
- 所有后端启动路径接入：`StrataBackend`（含原版 PolyStrata 与 KVMem，替换原先仅 KVMem 的重复循环）与 `LlamaCppBackend`（仅阻止**其它**引擎，同后端仍走既有“接管已有实例”逻辑）。
- `ConflictFor` 保持“后端不与自身冲突”的语义，供 GUI“停止它并继续”对话框使用。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **121 通过、0 失败**（新增 2 项：过期记录不阻塞/存活记录拒绝同名与异名引擎；后端 `StartAsync` 在存活冲突时拒绝且不启动进程）。
- 本机真实服务（隔离根、RTX3060 6GB、Qwen3.6-35B-A3B）：
  1. 配置里写入一条**过期** llama.cpp 记录（PID 999999）后，KVMem `--start` 正常就绪 —— 修复前会被误判冲突；
  2. KVMem 实际运行（显存约 4973 MiB）时再 `--start --backend polystrata`，被拒绝：`已有 KVMem … 服务占用显卡；请先停止它，再启动 PolyStrata`，退出码 1，进程数启动前后均为 3，未加载第二个引擎；
  3. `--stop` 停止后进程退出、显存回到 0 MiB。
- 证据：[回归](../downloads/iteration-20261108-round05/regression.log)、[KVMem 启动](../logs/iteration05-arbiter-20261108/logs/llama-manager.log)、[冲突拒绝输出](../downloads/iteration-20261108-round05/start-polystrata.log)。

### 提交／推送

- 发布包隔离验证通过；`dist` 已部署本轮构建，新 SHA256：`645A78B142D019BD631AC91EFC577804773F13E13596B8D5BB2634C8CBCE8D74`，用户配置哈希保持不变，保留回退副本。见“轮次汇总”。

## 第06轮（2026-11-08）：draft-*.gguf 被当作可启动主模型；配对结果不确定

状态：**第06轮完成**。

### 问题确认

- 模型列表只排除 `mmproj*` 与 `mtp-*`，但配对逻辑同时接受 `draft-*`。于是裸 `draft-*.gguf` 既出现在模型列表里可被选为“主模型”，又被当作草稿权重配对——用户可选中一个无法独立回答的权重文件。
- 两处规则各写一份，已经分叉（DEF-05）。
- 顺带确认：`FindPairedMmproj` / `FindPairedDraftModel` 直接取 `FirstOrDefault()`，目录枚举顺序不稳定，多个同类型候选时配对结果不可复现（DEF-13）。

### 修复

- 新增 `ModelPairingService.IsAuxiliaryWeight`：仅对 `.gguf` 判定，识别 `mmproj*` / `mtp-*` / `draft-*`；模型列表与配对共用同一规则。
- 候选按路径序排序后再选择，配对结果确定；同时存在 MTP 与普通草稿时优先 MTP 块。
- 不改变用户已有选择：被过滤的只是辅助权重，主模型列表内容不变。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **121 通过、0 失败**。新增断言：`mmproj*/mtp-*/draft-*`（含大小写混写）判为辅助；`Qwen.gguf`、`my-draft-tuned.gguf`、`mmproj-x.bin`、`mtpnotes.gguf` 判为主模型；重复 5 次采样配对结果稳定；仅有多份草稿时仍稳定选择同一份；MTP 优先于草稿。
- 本机真实模型目录验证：`C:\AIModel` 共 12 个 GGUF，其中 6 个为辅助权重；实际 WPF 界面列出 **6 个**可启动模型，与“全部 GGUF 减去辅助权重”的预期集合完全相等，且列表内无任何辅助权重。
- 证据：[回归](../downloads/iteration-20261108-round06/regression.log)、[真实界面](../downloads/iteration-20261108-round06/ui-verification.log)。

### 提交／推送

- 发布包隔离验证通过；`dist` 已部署本轮构建，新 SHA256：`F99500FD3C3BE36A412D9ACBCE36D2A3DD9B3CE3197790F01A43835E04C1D7CB`，用户配置哈希保持不变，保留回退副本。见“轮次汇总”。

## 第07轮（2026-11-08）：下载临时文件固定 `.part` 名称，并发下载互删

状态：**第07轮完成**。

### 问题确认

- `EngineDownloadService.DownloadAsync` 把临时文件写死为 `<目标>.part`，并以 `FileShare.None` 打开。
- 第二个针对同一目标的下载无法打开该文件；其失败/取消分支执行 `File.Delete(temp)`，**删除的是第一个下载正在写入的临时文件**，导致两个下载同时失败。
- 并发下载在“一键更新”（多引擎同时下载）等场景是真实存在的。

### 修复

- 每次调用使用独立临时名 `<目标>.<随机>.part`，结束时只移动自己的临时文件；失败只删除自己的临时文件。
- 不改变校验、原子替换（`File.Move(..., overwrite:true)`）与失败清理语义。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **121 通过、0 失败**。新增断言：失败下载不得删除其它尝试的临时文件（预置 `.other-attempt.part` 后仍原样存在）；两个不同目标并行下载均成功、内容与校验值一致、无残留 `*.part`。
- 本机验证：回归使用**真实 loopback HTTP 服务、真实磁盘写入与真实 SHA-256 校验**（非 mock）；真实 `downloads/` 目录扫描残留 `.part` 文件数为 **0**。
- 证据：[回归](../downloads/iteration-20261108-round07/regression.log)。

### 提交／推送

- 发布包隔离验证通过；`dist` 已部署本轮构建，新 SHA256：`FA8FDD25F2BA70B17EDC8360B2400713242986449120D83C8A0449A3B5D1733F`，用户配置哈希保持不变，保留回退副本。见“轮次汇总”。

## 第08轮（2026-11-08）：后台线程直接修改共享 `_config.ModelPresets`

状态：**第08轮完成**。

### 问题确认

- 模型扫描在 `Task.Run`（后台线程）里调用 `ConfigService.RebindOrphanPresets(_config, …)`，该方法会 `Remove`／插入 `config.ModelPresets`、改写 `config.ModelPath` 并保存配置。
- 同一时刻 UI 线程在枚举同一个字典（预设读取、配置序列化等），可抛 `InvalidOperationException: Collection was modified` 或读到半更新状态。

### 修复

- 扫描仍在线程池完成（读文件、预热 GGUF 元数据）；预设重绑定移到扫描完成后的 UI 线程执行。
- 只有确实存在孤立预设时才做昂贵签名采样，因此常见情况下不增加可感知延迟；语义与保存时机不变。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **121 通过、0 失败**（含既有预设恢复/签名用例）。
- 本机真实界面验证通过：真实模型目录扫描后列表与预期集合一致，管理器日志无“集合已修改”或异常记录。
- 证据：[回归](../downloads/iteration-20261108-round08/regression.log)、[真实界面](../downloads/iteration-20261108-round08/ui-verification.log)。

### 提交／推送

- 发布包隔离验证通过；`dist` 已部署本轮构建，新 SHA256：`726ECED9DD826FFE62ADB04F5522525022C9986E5818352CBC206A82A1060A4F`，用户配置哈希保持不变，保留回退副本。见“轮次汇总”。

## 第09轮（2026-11-08）：日志页不可见时仍每 2 秒重建两次

状态：**第09轮完成**。

### 问题确认

- `RefreshStatusAsync` 每 2 秒执行一次，并在一次轮询里调用 `RefreshLog()` **两次**（开头一次、结尾一次）。
- 日志页是独立标签页，但刷新不判断是否可见：即使用户停留在模型/参数页，也会持续读取并拼接近 500 行日志、比较全文、必要时重设文本框并滚动到底。
- 服务未运行时同样持续发生，属于长期常驻的无效 IO 与布局开销。

### 修复

- `RefreshLog(bool force)`：默认仅在日志页被选中时读取；轮询中删除重复的第二次调用。
- 标签页 SelectionChanged 时强制刷新一次，保证切回日志页能立即看到期间发生的内容；`清空显示` 仍按原语义强制重读。
- 无会话时不再调用 GPU 摘要读取（该调用会读引擎日志并持有日志写锁）。
- 状态栏、速度、显存等其它信息刷新频率不变。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **121 通过、0 失败**（含既有日志渲染与滚动用例，反射调用已适配新签名）。
- 本机真实界面验证：停留在参数页超过一个轮询周期（2.5s）后，日志框**没有**出现新写入的条目；切到日志页后该条目立即出现。
- 证据：[回归](../downloads/iteration-20261108-round09/regression.log)、[真实界面](../downloads/iteration-20261108-round09/ui-verification.log)。

### 提交／推送

- 发布包隔离验证通过；`dist` 已部署本轮构建，新 SHA256：`4364066771184E06D07F87F1AA68730CF01FF93D3E8955E66813760F8A952C86`，用户配置哈希保持不变，保留回退副本。见“轮次汇总”。

## 第10轮（2026-11-08）：CUDA 设备列表用 `int.Parse` 解析外部输出

状态：**第10轮完成**。

### 问题确认

- `CudaService.Parse` 直接用 `int.Parse` 解析 `llama-server --list-devices` 的显存数字。
- 该文本来自外部进程、不可信：一旦出现超大数值或格式异常，`int.Parse` 抛 `OverflowException`／`FormatException`，会从设备探测路径抛出，而不是降级为“未知”。

### 修复

- 改为 `int.TryParse`（InvariantCulture、仅数字），失败或非正数一律记为 0（“未报告”），不抛异常。
- 正则与 `Available` 判定不变，正常设备行解析结果与原实现一致。

### 验证结果

- Release 构建 0 警告 / 0 错误；回归 **122 通过、0 失败**。新增断言：正常行 `6144/5000` 正确；`99999999999999999999` 溢出值降级为 0 且另一字段仍正确；`0 MiB` 保留可用标记；空文本与无 CUDA 行不谎报可用。
- 本机真实输出（`llama-server --list-devices`：`CUDA0: NVIDIA GeForce RTX 3060 Laptop GPU (6143 MiB, 5130 MiB free)`）用**已构建的产品程序集**解析：`Available=True; Total=6143; Free=5130`，与驱动实际输出一致。
- 证据：[回归](../downloads/iteration-20261108-round10/regression.log)、[真实设备输出](../downloads/iteration-20261108-round10/cuda-real.txt)。

### 提交／推送

- 发布包隔离验证通过。
- **`dist` 部署推迟**：用户启动器正在运行，按约束不得为部署停止用户程序。发布包保留在 `downloads/iteration-20261108-round10/publish/`，待启动器关闭后按哈希校验部署。见“轮次汇总”。

## 后续轮次

尚未推进；后续逐项复现已知缺陷再决定修复，不把代码审读风险冒称已实测bug，不承诺已找出所有缺陷。
