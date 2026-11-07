# KVMem 与引擎维护测试用例矩阵

日期：2026-11-07。对应 [变更规格](KVMEM-CHANGE-SPEC.md)、[需求REQ-124～126](REQUIREMENTS.md#L255-L276)。

## 1. 执行规则与结果口径

- 自动回归：Windows x64/.NET8控制台runner，不用`dotnet test`；隔离LLAMA_MANAGER_ROOT、生成GGUF头、模拟服务与临时端口，不下载模型或启停用户服务。
- GPU验收：显式工具，使用本机已有模型、新隔离根；有用户模型运行时不得为测试停服。与自动回归分开记录。
- “108通过”是runner测试组数量，不是下表每个边界子情形数量；新增参数组合由组内断言覆盖。
- 上一轮完整验收与本次提交前最终复验均通过：构建0警告0错误、108 passed/0 failed、文档门禁65条需求。本次复验未重复GPU长文测试；真实GPU结果沿用同一实现的已记录验收。
- 本机提交复验日志：[最终复验结果](../logs/commit-final-verification-20261107.log)；复验后删除本次再生成的4个bin/obj目录（约383 MB），[清理清单](../logs/commit-cleanup-20261107.json)保留。这些本机日志/构建资产均不入库。

```powershell
dotnet build LlamaManager.sln -c Release
dotnet run --project tests/LlamaManager.Regression -c Release
pwsh -File tools/docs-gate.ps1
```

## 2. 自动回归用例

| 编号 | 需求/范围 | 输入与关键场景 | 预期结果 | 实现 |
|---|---|---|---|---|
| KV-01 | 独立注册/界面 | 原版与KVMem注册、默认值、动态显隐 | 原版无分页控件，KVMem独立且无冲突控件 | [KvmemRegression](../tests/LlamaManager.Regression/KvmemRegression.cs) RunRegistryAndSchema |
| KV-02 | 精确资产 | sm70/75/86/89/120、未知/非有限能力、unsafe tag、缺digest/校验文件 | 仅选精确完整包；未知或未校验拒绝 | 同上 RunCatalog |
| KV-03 | 布局隔离 | 缺worker/Python/CUDA、坏manifest、错版本/架构、多架构目录 | 不报告可用，不混用旧Poly路径 | 同上 RunLayoutsAndResolvers |
| KV-04 | 运行时探测 | imports/help/capabilities/hash、IO、超时、预取消/中途取消 | 有界只读；错误不启用、不加载模型 | 同上 RunRuntimeProbeAsync |
| KV-05 | 真实协议 |默认与覆盖参数、配对外草稿、旧Strata地址/端口 | 无裸--kvmem，补齐安全args/env；默认127.0.0.1:8080独立 | 同上 RunLaunchDefaults |
| KV-06 | 关闭零污染 | 开关0且旧B/R/H/D/S非法、旧SSD目录保留 | 不发送任何分页参数，不受非生效设置影响 | 同上 RunDisabledZeroPollution |
| KV-07 | 合法性 | B/R范围与对齐、B+R、prefill、SSD无目录、KV/draft/cache冲突 | 明确拒绝非法组合；边界合法可生成 | 同上 RunInvalidParameters |
| KV-08 | CPU视觉 |匹配mmproj/companion、线程边界 | gpu=false、输出token/线程有界、路径对应同包 | 同上 RunCpuVision |
| KV-09 | 模型能力 | qwen35moe与dense/Gemma/GLM/未知/不支持专家 |仅允许已接入架构，缺模型/取消不假通过 | 同上 RunModelLimitsAsync |
| KV-10 | 原版保护 |旧分页flag与equals形式、普通prompt文本 |原版启动拒绝分页参数，普通文本不误判 | 同上 RunStockRawRejectionAsync |
| KV-11 | 严格就绪 |HTTP200但未loaded、错模型/预算/backend、坏JSON/503、恢复配置 |仅已加载匹配服务可ready，取消传播 | 同上 RunReadinessAsync |
| IN-01 | 安装事务 |真实小ZIP/SHA、并发编辑、重复同版、各阶段错误与提交前取消 |仅窄提交路径，旧配置/版本保护；失败不生效 | [KvmemAcquisitionRegression](../tests/LlamaManager.Regression/KvmemAcquisitionRegression.cs) RunInstallTransactionsAsync |
| IN-02 | 解压安全 |遍历/绝对/UNC/ADS/尾点空格/重复/link属性/pending/非空目标 |写第一项前拒绝，无路径越界或覆盖 | 同上 RunSafeExtractionAsync |
| UP-01 | 版本/driver |真实semver+build、旧numeric、compact、Prism、UMD header |正确build/driver，未知和变体不冒充官方 | [BuiltInUpdateRegression](../tests/LlamaManager.Regression/BuiltInUpdateRegression.cs) RunVersionParsing |
| UP-02 | CUDA配对 |同tag/minor与缺runtime；driver13.4遇13.5、preAmpere |只选同发布完整兼容配对 | 同上 RunManagedPlans |
| UP-03 | pins读取 |literal exact pins、表达式/注入、错版本/重复/未知包 |纯解析，不执行源码，非法拒绝 | 同上 RunCudaWheelPins |
| UP-04 | 视觉probe |无参数usage、unknown --help、缺DLL |按实际契约验证，不把unknown option当健康 | 同上 RunVisionProbeAsync |
| UP-05 | 最新/手工 |无序发布、已最新/更高本机、未知/变体、取消 |不盲目覆盖、不重复下载 | 同上 RunLatestAndManualAsync |
| UP-06 | 更新事务 |native+DLL小ZIP，错SHA/版本/依赖、并发参数、取消 |独立安装并校验；窄迁移共享路径，失败回滚 | 同上 RunLlamaTransactionsAsync |
| UP-07 | 全局更新 |所有注册builtin、单项失败、首项取消 |失败逐项继续，用户取消终止后续 | 同上 RunUpdateAllAsync |
| RT-01 | 最近旧版 |多个受管revision、repair同tag、不同CUDAminor/sm组 |当前+最近旧版；llama CUDAminor不无限积累 | [EngineRetentionRegression](../tests/LlamaManager.Regression/EngineRetentionRegression.cs) RunKeepCurrentAndNewestPrevious/RunVersionOrderingAndMultiGpu |
| RT-02 | 活跃/手工保护 |真实身份三元组、PID复用、未知probe、引用worker/root |活跃和保守未知保护，手工路径不删 | 同上 RunProtectedRootsAndSessions |
| RT-03 | ownership边界 |缺/坏/重复字段、错身份、越界、reparse变化、用户模型/config/log |不认领、不越界；新/改用户数据保护 | 同上 RunOwnershipAndPathSafety/RunReparseAndUserDataSafety |
| RT-04 | 随包日志 |显式原始SHA/长度、emptylog、修改/新增/非法表 |仅未变原始日志可随旧包删，不自动认领用户日志 | 同上 RunReparseAndUserDataSafety |
| RT-05 | 下载缓存 |平铺/一层release、receipt、shared asset、hash变、删除失败 |仅确属旧版且未变cache清理，失败可重试 | 同上 RunOwnedDownloadRetention |
| RT-06 | 失败/取消 |删除异常、预/中途取消、坏日志sink |不回滚已提交版本，返回明确结果 | 同上 RunFailuresAndCancellation |
| CF-01 | 配置 |未知Window/嵌套元数据、共享与手工preset路径 |未知字段保留；只迁移共享安装路径 | [runner](../tests/LlamaManager.Regression/Program.cs)、[DoctorRegression](../tests/LlamaManager.Regression/DoctorRegression.cs) |
| UI-01 | 一键维护 |按钮存在、二次点击取消、关闭时任务未结束、旧preset路径 |不保存陈旧路径；当前引擎级路径优先，调优恢复 | [UiRegression](../tests/LlamaManager.Regression/UiRegression.cs) |
| LC-01 | 生命周期 |无记录真实脱离进程、TCP快照漏报/延迟、取消/测速清理 |完整身份恢复；停止后PID退出+独占绑定成功+HTTP离线 | [runner](../tests/LlamaManager.Regression/Program.cs)、[Fixtures](../tests/LlamaManager.Regression/Fixtures.cs)、[EnginesRegression](../tests/LlamaManager.Regression/EnginesRegression.cs)、[BenchmarkRegression](../tests/LlamaManager.Regression/BenchmarkRegression.cs) |

原参数组、GGUF/路径、配置恢复、插件、CLI、自启、嵌入、测速与WPF基础测试继续执行，未因本次变更删掉安全验收。

## 3. 真机验收（已执行，非普通回归）

环境：Windows11、RTX3060 Laptop 6144 MiB、sm86、driver617.42/CUDA UMD13.4，约24GB RAM。结果只适用于所测模型/配置。

| 编号 | 操作 | 核心验收 | 实测结果 |
|---|---|---|---|
| HW-01 | KVMem完整包校验 |正确长度、SHA、worker及capabilities |v0.19.1 RTX30包与worker SHA一致，sm86能力通过 |
| HW-02 | KVMem短请求 |loaded/props匹配、真实token |194 prompt、3 completion，回答OK. |
| HW-03 | 32K冷区检索 |实际prompt>B+R，非截断，答案正确 |33,008 prompt，317.4秒，验证码739251正确 |
| HW-04 | 64K冷区检索 |实际长输入、Query replay、SSD读写 |65,776 prompt，589.3秒，答案正确，errors0 |
| HW-05 | 64K复用 |缓存token、正确回答、真实复用 |65,747 cached tokens，1.7秒，答案正确 |
| HW-06 | 显存与停止 |有界采样、身份校验退出 |569次采样最大4751MiB；结束GPU使用恢复0MiB |
| HW-07 | 一键更新 |binary/runtime同release兼容driver |llama b12125→b12182/CUDA12.4；13.5不强装 |
| HW-08 | Poly完整安装 |同tag native/source/exact wheels/private venv |poly.7/CUDA13.0成功；错误vision probe曾失败且不提交，修正后通过 |
| HW-09 | 更新后短推理 |新安装真实GPU API与停止 |llama17/2 tokens、1027MiB；Poly196/2、4751MiB，均回答OK |
| HW-10 | 再次一键更新 |三后端已最新不下载 |llama/poly/KVMem均无需更新，doctor退出0 |
| HW-11 | 发布/清理 |独立EXE及PowerShell wrapper、配置保护、清理后可运行 |发布冒烟通过，清理约34.7GB临时产物后doctor仍通过 |

KVMem模型：Cyber-Tiel-Coder-35B-A3B.APEX-I-MiniPlus-V2.1；context131072/B8192/R512/Host256MiB/SSD8192MiB/S2/q8_0/P128/stage128/threads8，普通解码。32K/64K并不证明131K输入成功。

关键存储结果：Host267,735,040 bytes、SSD450,867,200 bytes、累计read159,457,280/written541,040,640 bytes、errors0。显存数字是采样最大值，不是瞬时峰值保证。

完整摘要：[KVMem实测](KVMEM-INTEGRATION.md#当前验证记录)、[更新验收](ENGINE-UPDATE-GUIDE.md#44-本机验收记录2026-11-07)。本机详细日志在忽略的logs/downloads目录，不提交二进制/配置/日志；仓库文档保留上述可独立阅读的结果。

## 4. 重现显式冒烟

[可复用工具](../tools/KvmemSmoke/Program.cs)与[生成器](../tools/KvmemSmoke/prepare_requests.py)进入仓库。每次使用新隔离根，先确认没有用户模型服务运行，不自动停止已有服务。

```powershell
# 分页长文：按实际现有模型/包填写，先生成一次性请求文件
& "$bundle\runtime\python\python.exe" tools/KvmemSmoke/prepare_requests.py $bundle $model downloads/kvmem/smoke-requests.json
dotnet run --project tools/KvmemSmoke -c Release -- logs/kvmem-smoke-unique $bundle $model downloads/kvmem/smoke-requests.json
# 更新后短推理（读取真实安装路径，但运行会话隔离）
dotnet run --project tools/KvmemSmoke -c Release -- --builtin logs/llama-smoke-unique llamacpp $smallModel
dotnet run --project tools/KvmemSmoke -c Release -- --builtin logs/poly-smoke-unique polystrata $moeModel
# 发布包生命周期冒烟（隔离假服务）
dotnet run --project tests/LlamaManager.Regression -c Release -- --check-published dist/LlamaManager.exe
```

`--acquire`属于显式真实安装验证，会提交引擎安装路径，不是无副作用回归；只在明确需要验证安装时使用。GPU工具不进入普通runner。

## 5. 提交前检查

- 先构建/完整回归/文档门禁，失败保存诊断，修复后重新验证，不标成全通过。
- 提交范围仅源码、测试、工具源码、变更规格/维护/需求文档及忽略规则。
- 不提交本机路径配置、模型、引擎包、日志、截图、bin/obj或权限备份。
- 清理只删除明确的本次临时产物；保存关键测量摘要与结果日志，不停止正在使用的正式启动器。
- 108组结果在完整runner中确认；源码提交后不自动推送远端。
