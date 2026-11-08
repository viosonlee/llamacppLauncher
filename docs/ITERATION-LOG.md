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

## 后续轮次

尚未推进；后续逐项复现已知缺陷再决定修复，不把代码审读风险冒称已实测bug，不承诺已找出所有缺陷。
