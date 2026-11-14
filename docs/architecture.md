# 架构与维护

## 引擎后端（可插拔）

启动器把每个推理引擎抽象为「本地 HTTP 服务」，用统一的后端接口驱动，GUI、命令行与自启动共用同一注册表。

- `IEngineBackend` 提供 `ProbeAsync`（可用性/版本）、`InspectAsync`（GGUF 兼容性 + 资源估算）与 `StatusAsync`。
- `IServerBackend` 增加 `BuildLaunch`/`StartAsync`/`StopAsync`/`ReadyAsync`/`ResolvePort`/`HealthUrl`/`OpenWebUi`。
- 内置后端：`llamacpp`（委托既有 `ServerService`，行为不变）、`polystrata`（启动引擎自带的 `serve/server.py` HTTP 服务）。
- 已退役：`strata-upstream`（`strata.exe`）。它唯一独有的 `qwen4exp` 家族（Qwen3.8-Flash-Next）最小也要 29.6 GB 文件 / 32 GB 内存，本机 23.4 GB 跑不动；在共同支持的 qwen35moe 上它比 `polystrata` 慢约 30%，且没有 `--tail-skip`/`--kv`/`--vram-reserve-mib`。相关注册、下载与面板字段已移除（见 `docs/CHANGELOG-DECISIONS.md` 2026-10-18 条）。
- 插件后端：`ProcessPluginBackend` 由 `plugins/<id>/plugin.json` 清单驱动（`schema:1`），只拉起声明的进程、等待 `/health` 或端口；不加载插件代码，因此崩溃被隔离在子进程。内置 `polystrata` 清单。
- `BackendRegistry` 是唯一后端清单；`PluginLoader` 在启动时扫描 `AppContext.BaseDirectory/plugins`、`<ManagerRoot>/plugins`、`<LlamaCppRoot>/engines`。
- `EngineArbiter` 集中 GPU 重负载互斥：启动某后端前若另一后端持有 GPU，走统一确认；不再在各处重复冲突检查。
- `BootstrapService` 一键初始化运行环境（Python venv 与依赖、引擎定位）；依赖清单优先读引擎 `requirements.txt`，缺失时回退内置列表。

`EngineSession` 是会话身份（后端 id + PID + 创建时间 + 可执行路径），与 `ServerSession` 双向映射。会话持久化在 `ManagerConfig.Running` 的槽位中；旧版的 `RunningServer`/`RunningEmbedding` 与 PID 提示在读取时折叠进槽位，保存后删除（迁移即删）。

## 界面

顶部固定一条控制条：引擎下拉、状态、以及一套启动/停止/打开页面/测速按钮，所有引擎共用，避免多套按钮。标签页按功能分为模型 / 参数 / 高级 / 日志。参数页统一由后端 schema v2 动态生成（分组、默认值、单位、范围校验、`VisibleWhen` 条件显隐、包含超长上下文 KVMem 的二级联动折叠、高级折叠、搜索与恢复默认），llama.cpp 也走同一套，它额外保留「原始参数」文本框作为任意 flag 的兜底；嵌入服务等 llama.cpp 专属控件在选中其它引擎时隐藏。llama.cpp 的参数值仍存在原始参数串里，由 `LlamaCppParameters` 在面板与字符串之间双向投射，手写 flag 不会被面板覆盖。会话记录按后端各自的槽位保存，llama.cpp 仍用 `main` 槽。

## 运行实例

`ManagerConfig` 的 ModelPath/Args/Port 描述下一次启动的选择。
llama.cpp（含 Prism 路由）与 PolyStrata 的主服务统一默认 8080；配置中的自定义端口优先，
启动失败不自动换端口。`PortUnavailableReason` 按绑定地址试探端口，区分占用和权限/保留范围；
`TestPortInUse` 保留为活动监听查询，仍供状态、进程恢复和插件 TCP 就绪判断使用。
实测器由系统分配独立临时端口，排除 8080、原始参数中的端口、各引擎配置端口和运行会话端口；
分配和清理有界且支持取消，交接到子进程前再次检查端口；由于绑定交接有时序窗口，子进程退出仍要报告 stderr。
`ServerSession` 保存本次启动的引擎、模型、参数、端口、PID 和操作系统创建时间。
状态查询和停止使用会话身份，不随表单修改变化，也不会按端口搜索并接管外部服务器。
旧配置只允许迁移记录的 PID，迁移时核对引擎路径、创建时间和实际命令行。

同一配置根下，GPU启动按 `gpu-start.lock` → 后端/槽锁 → 短配置事务的顺序，
串行检查最新存活身份、创建进程与登记；登记失败先完整身份清理本次子进程再释放租约。
CPU嵌入和显式声明非GPU插件不占GPU租约。后端停止使用槽锁和完整身份校验，
只清除仍匹配的记录。此保证不覆盖其它配置根、外部程序或启动器登记前崩溃。
等待 API 就绪时释放锁，因此另一个窗口操作或 CLI 可以立即停止启动中的进程。
`LaunchSessionGuard` 在 PolyStrata/KVMem 与插件的就绪探测前后核对槽位和完整进程身份；
迟到响应不能把已停止或被替换的旧会话报为就绪。只取消等待与显式停止的既有区别保留。
UI 使用取消令牌和状态版本丢弃过期结果，每两秒最多运行一次状态检查。
运行会话仍会检查进程是否存活；配置里的历史会话不代表进程正在运行。

`DetachedProcess` 使用 Windows CreateProcess 的标准文件句柄输出日志。
句柄继承白名单只包含 stdin/stdout/stderr，避免意外继承调用者的管道并阻塞 CLI 退出。
环境覆盖独立合并继承项，生成Windows Unicode环境块并设置 `CREATE_UNICODE_ENVIRONMENT`；
不通过临时修改全局环境传参。PolyStrata/KVMem和插件均把LaunchPlan.Environment传入此创建路径。
显式关闭加速传0，覆盖父进程及引擎默认值；KVMem runconfig 的worker环境仍由其专属规则生成。
因此 GUI/CLI 退出不要求服务器跟随退出。

## 持久化

`ConfigStore` 用路径对应的命名互斥锁保护读改写，临时文件落盘后通过 File.Replace 原子替换。
GUI 仅更新用户设置，生命周期仅更新运行实例；自启动也只更新自己的字段。
GUI 通过独占文件锁避免同一配置目录打开多个可写窗口。
原始参数输入不合法时，保存和启动会提示；不会静默丢弃用户配置。

预设按规范化绝对路径保存。重绑定优先使用长度及头尾采样签名，已知签名不一致时拒绝回迁。
旧预设缺少签名时仅对唯一同名候选使用文件名回退。采样签名不是全文件完整性校验。

## 参数与设备

`LauncherArguments` 解析 Windows 引号和反斜杠，保存原文位置。
改单个选项会处理其别名和重复项，保留其余文本。任意新参数仍可通过原始参数框传入；
启动参数是否合法最终由具体版本的引擎检查。

`GgufService` 只读 KV 元数据和张量描述符，跳过 tokenizer 大数组，不读权重数据。
解析不依赖 KV 顺序；缓存按路径、大小和修改时间失效。
设备检测同时消费 stdout/stderr，设真实超时，按引擎路径及更新时间共享同一检测任务。
设备内存是检测时快照，不能当成持续显存监控。

新预设优先使用引擎的 fit；不支持 fit 时，按真实层数、F16 KV 估算和显存保留量计算保守层数。
混合架构、MoE、上下文和工作缓冲区的分配因引擎而异，估算不能保证免于 OOM。
现有实测参数保留，最终以加载日志和推理实测为准。

## 日志和测速

日志读取保留偏移、UTF-8 解码状态和有界行缓存，处理追加、截断及文件替换；单次读取失败（截断、轮转、权限）
只损失该次内容，不再让整个状态轮询退出——日志页在每次轮询开头刷新，网络探测变慢不再冻结它。
状态显示从主服务日志提取实际 GPU/CPU 模型缓冲区，而非把 CUDA 可用视作已成功卸载。
`/slots` 优先使用服务提供的速度，否则按同一请求的 token 计数差和采样间隔显示实时速度。
请求切换或进程变化时重置采样；完成后保留主服务日志的解码速度，明确排除 prompt eval。
独立的增量日志筛选器保留完成记录，避免高频状态查询日志把上次速度挤出缓存。
接口暂不可用时仍展示该运行实例已有的完成记录；采样估算值标注“约”。
测速排除模型列表查询时间，显示首个内容片段延迟；只在服务报告 token 数量时估算吞吐。
SSE 事件数量不等于 token 数量。取消测速会关闭本次流式请求。

### PolyStrata 遥测（`/metrics`）

polystrata 的 `/slots` 只有 `is_processing`，没有 token 计数；引擎也不写逐请求日志行，
所以采样和日志都无法给出速度。它自己的 `/metrics` 才是唯一实时来源，`PolyMetricsService`
解析其中的 `live`（状态机：`reading` → `generating` → `idle`，自带窗口化 tok/s）、
`requests[]`（完成请求环：token 数、decode tok/s、草稿接受数）、`engine`（专家缓存、显存余量）
和 `hardware`（GPU 利用率/温度/显存、CPU、内存）。`/metrics` 不可用时回退到旧的采样路径。
日志页的“请求完成”一行由启动器在每个新请求号首次出现时写一次（`requests[].time` 去重，
GUI 启动前已存在的请求只记录不重复补写），这既是日志页的活动反馈，也是草稿接受率的实时出口。
顶部控制条分三行：引擎行、按钮行、状态行——速度徽章独占一列，长模型名只会截断自己，
不会再把速度挤出窗口外。

## 验证边界

回归程序使用本地 TCP 模拟服务器验证真实进程、日志文件和 HTTP 生命周期。
WPF 测试在 STA 线程创建控件并验证事件同步。
模拟服务器不能覆盖 GPU 内核兼容性、具体模型质量或所有 llama.cpp/Prism 版本。
实际注册表自启动、注销/登录行为和旧计划任务迁移需要在目标机器上单独操作验证；
测试不会修改使用者的登录启动设置。
