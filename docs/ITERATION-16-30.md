# 第16–30轮：15个新增问题的测试—修复—校验（2026-11-14）

本批是用户要求的**新增至少15轮**，不拿第01–15轮抵数，不把一次缺陷的多个测试数据算多轮。
每行是一个独立的缺陷/契约修复；实现、测试与复核分区进行，最后在同一Windows WPF构建中集成。
当前状态：**15项工程修复已集成验证，未提交/推送，未替换正式dist**。独立发布与最终校验状态见文末。

## 每轮记录

| 轮次 / 需求 / 缺陷 | 问题证据 | 修复与校验 |
|---|---|---|
| 16 / REQ-146 / DEF-33 | 插件选了自己的模型，却把主后端ModelPath写进命令；测试期望plugin selected.gguf实际为standard model.gguf | 模板和会话ModelPath共用后端模型解析，未设置才回退主字段；含空白值回退测试 |
| 17 / REQ-147 / DEF-34 | EngineStartRequest.Port=18083却得到持久化参数18082，CLI一次性端口无效 | 明确一次性覆盖→参数→EngineSettings.Port→清单默认→主默认；验证命令行落点和非法端口 |
| 18 / REQ-148 / DEF-35 | ready.path=/ready-custom仍探测/health；TCP插件启动与后续ReadyAsync口径不同 | 共用有界本地HTTP/TCP探测，尊重自定义路径与TCP连接，不把保留端口当就绪；GUI/CLI也走backend.ReadyAsync |
| 19 / REQ-149 / DEF-36 | 路径型id、null嵌套对象、未知探测类型/无界等待均可进入清单处理 | Validate前置检查结构、安全名称、参数重复、环境、ready协议/路径/超时；Initialize也验证，非法项明确拒绝 |
| 20 / REQ-150 / DEF-37 | 只要传入非null会话，三个后端StatusAsync都返回Running=true，过期创建时刻也通过 | 共享EngineSessionStatus，探测前后校验PID/时刻/引擎路径；存活与就绪分开，取消传播 |
| 21 / REQ-151 / DEF-38 | 插件版本进程退出7仍被报Available；同步WaitForExit不响应运行中的取消 | 复用有界ProcessProbe，失败/超时报不可用，用户取消传播；正则匹配200ms边界与非法模式降级；检查取消后子进程退出 |
| 22 / REQ-152 / DEF-39 | Python安装器重定向双管道却未排空，两个1MiB输出导致卡死；取消后进程树残留 | 同时排空并丢弃输出，保留退出码，启动前检查取消，取消后仅清理本次进程树并有界等待 |
| 23 / REQ-153 / DEF-40 | GPU显存探测stderr满阻塞、无超时、非零退出仍采样、取消泄漏 | 三秒探测期限；stdout只保留4096字符但持续排空，stderr排空；非零/超时返回未知，用户取消传播并清理 |
| 24 / REQ-154 / DEF-41 | HttpClient.Timeout在ResponseHeadersRead后不限制正文，头后无内容/半行可永久等候 | 600秒端到端令牌覆盖模型发现、响应头、正文/ReadLine；回归用1秒注入期限，区分超时与用户取消 |
| 25 / REQ-155 / DEF-42 | 已输出部分token后error或干净EOF仍返回Success，错误被当有效测速 | 检测JSON error与event:error，要求DONE或finish_reason；保留兼容的finish_reason+EOF及usage/timings |
| 26 / REQ-156 / DEF-43 | 可编辑下拉重建时未知于精选列表的-ngl35等值回落第一项，失焦后覆盖原参数 | 保留自由文本，预设标签映射机器值；20个UI数据用例中的01–11覆盖重建、恢复默认、失焦及保存 |
| 27 / REQ-157 / DEF-44 | 嵌入启动时按钮显示“终止启动”却被轮询禁用 | 即时状态和轮询共用按钮策略，启动中取消可点；UI用例18–20含实际按钮自动化触发取消，仅操作自身假服务 |
| 28 / REQ-158 / DEF-45 | llama.cpp启动绕过schema校验，可能保存无效值并先停掉现有服务 | 副作用前校验，启动前所有后端共用保存校验；整面板保存先提交已验证自由文本。UI用例12–17覆盖失败无写入和纠正后保存 |
| 29 / REQ-159 / DEF-46 | Engines:null/空设置/Strata:null在Normalize抛NRE，绕过备份恢复 | Normalize前验证对象图与相关模型路径；损坏数据恢复备份并保留证据，实际读写权限错误仍作为操作错误报告 |
| 30 / REQ-160 / DEF-47 | 模型下载只查4字节魔数，忽略预期长度；同大小坏文件复用，验证失败前已覆盖旧目标 | 校验精确目录字节数和有界GGUF元数据/描述表；只发布通过检查的独立staging，失败/取消不删除旧目标；同大小缓存也检查 |

## 测试先失败的证据与边界

- 插件16–21：集中新增测试时，原代码 **145通过/6失败**。随后逐轮修改及回归，各自转绿；
  中间完整套件仍包含其它尚未修完的红灯，不能把这些阶段日志说成全绿。
  原始日志：[plugin-red](../logs/cycles16-30-20261114/plugin-red.log)、
  [16](../logs/cycles16-30-20261114/round16.log)、[17](../logs/cycles16-30-20261114/round17.log)、
  [18](../logs/cycles16-30-20261114/round18.log)、[19](../logs/cycles16-30-20261114/round19.log)、
  [20](../logs/cycles16-30-20261114/round20.log)、[21](../logs/cycles16-30-20261114/round21.log)。
- 进程/SSE22–25：独立Windows .NET8源码快照harness先7通过/15失败，修复后22个数据用例通过；
  这些是**4个缺陷、22个测试数据**，不是15个独立修复。真实进程、管道、loopback HTTP参与，
  无GPU/安装/外网下载。完整说明：[EVIDENCE](../logs/iteration-new-20261114-process/EVIDENCE.md)。
- 界面26–28：旧代码的下拉回退/禁用按钮/绕过校验均有明确分支证据，修复后由实际WPF调度器和控件验证。
  集成中发现测试错误地设EmbeddingPort=0导致ConfigStore恢复旧备份；修正为合法隔离端口，
  **没有削弱参数持久化断言，不把测试夹具错误计入产品15轮**。
- 配置/模型29–30：隔离的Windows源码副本harness先复现对象图NRE和错误目标发布，修复后两个测试组通过。
  主套件旧HTML断言随更明确的InvalidDataException契约更新；合法GGUF仍用生成的完整描述表，而非只有magic。

## 集成验证与限制

- Windows Release构建：0警告、0错误。
- 首次完整集成回归：**157通过、0失败**。20个UI细分用例仍归在原有WPF测试组内，不冒充20个顶层测试。
- [构建日志](../logs/cycles16-30-20261114/integrated-build.log)、
  [完整回归](../logs/cycles16-30-20261114/integrated-regression.log)。证据路径在本机忽略目录，不随Git提交。
- 所有服务/进程测试仅使用隔离根、生成GGUF、假服务与本地HTTP。没有停止用户真实服务，没有修改agent地址。
- 模型结构检查最多读128MiB头/描述表；目录精确长度是完整传输的依据，不是权重内容hash或所有tensor offset验证。
- SSE保持单行JSON事件约定，不宣称实现通用多行SSE；安装器不能保证清理操作系统拒绝访问或脱离进程树的提权子进程。
- 插件仍需真实引擎做最终协议/性能验证；这些回归不证明所有模型都可用，也不表示项目已无缺陷。
