# LlamaManager 阶段交接（PROGRESS-HANDOFF）

> 更新时间：2026-11-01 18:20（Asia/Tokyo）
> 本阶段目标已完成并按用户要求暂停。恢复时先读本文件，再执行 `git log --oneline -10`。

## 一、当前版本与工作区

| 项 | 状态 |
|---|---|
| 工作树 | `/home/vioson/.codex/worktrees/launcher-polish-iteration/llamacppLauncher` |
| Windows 路径 | `\\wsl.localhost\Debian\home\vioson\.codex\worktrees\launcher-polish-iteration\llamacppLauncher` |
| 分支 | `codex/launcher-polish-iteration` |
| 功能/发布 HEAD | `4f32071` |
| 基线 | `d57ad11`（origin/main） |
| 本阶段提交 | **功能/修复 5 笔 + 交接文档 2 笔，共 7 笔** |
| 正式安装 | `C:\llama-b9822-bin-win-cuda-13.3-x64\llamacppLauncher\dist` |

主工作区 `C:\llama-b9822-bin-win-cuda-13.3-x64\llamacppLauncher` 仍停在 `d57ad11`，
并且在会话开始前就有未提交的源码/文档改动。本阶段没有回退、覆盖或清理这些既有改动；
部署只更新了根目录 `llama-manager.ps1` 以及 `dist` 中的 EXE/PDB。下次合入前必须先审阅这些
主工作区改动，不能直接 `reset` 或强拉正式分支。

## 二、本阶段完成的工作

按提交顺序：

1. **`18ef9dc feat: harden launcher workflows and polish daily use`**
   - 界面与日常操作：左侧导航增加中文标签；模型搜索/排序/空结果提示；窗口大小、位置和
     最大化状态持久化；常用快捷键；日志长行自动换行。
   - 服务与安全：主服务端口统一为 8080；实测使用隔离临时端口；GPU 重负载互斥下沉到共享
     启动入口，GUI、CLI 和自启动不能绕过。
   - 更新与一致性：引擎更新改为运行中拒绝、同卷暂存、逐文件事务发布和失败回滚；llama.cpp
     更新检查读取真实 nightly build 号。
   - 数据稳定性：孤儿预设重绑定不再从后台线程修改共享配置；并发下载各写独立临时文件；
     日志 reader、签名缓存和 CUDA 探测缓存有界；mmproj/draft 多候选选择确定化并避免错配。
   - 文档与测试：同步扩充需求、架构、决策和 `KNOWN-DEFECTS`；回归扩展到 **87 项**。

2. **`b25d703 fix: prevent recycled PIDs from claiming the GPU`**
   - GPU 会话在记录含创建时间时校验 PID + 创建时间，避免崩溃后 PID 被其它进程复用而误占显卡。
   - 保留“不比较引擎路径”的既有决策，用户移动安装目录后仍在运行的会话不会被误判退出。
   - 更新 REQ-127、决策记录、缺陷文档和针对性回归。

3. **`83bd1ce perf: reuse engine capability and selected release plans`**
   - 整批 release 选择只探测一次本机 GPU 计算能力，不再为每个候选重复启动 `nvidia-smi`。
   - 落后引擎更新直接复用比较阶段已选中的发布计划，避免二次查询 GitHub 和版本漂移。
   - 更新 REQ-133/REQ-136、决策记录和 update/doctor 回归。

4. **`7431a5f ui: show why start is blocked during model scanning`**
   - 模型扫描开始时立即禁用主启动按钮，并通过提示明确原因；扫描结束后按真实状态恢复。
   - 更新 REQ-138、决策记录和 WPF 回归。

5. **`4f32071 fix: make PowerShell wrapper wait for WinExe CLI`**
   - 实测发现 Windows PowerShell 5.1 不允许把 GUI 子系统的 `LlamaManager.exe` 放入管道，
     原包装脚本的 `-Start/-Status/-Stop` 会直接报“无法在管道中间运行文档”。
   - 改为 `Start-Process -PassThru` + 临时 stdout/stderr 文件 + 进程对象 `WaitForExit()`；
     只等待启动器本身，不会让 `--start` 连带等待常驻模型服务；保留 UTF-8 输出、退出码和
     含空格模型路径，并按 Windows 命令行规则转义参数。
   - 更新 REQ-104、决策记录、DEF-26 和发布冒烟。

每笔功能/修复提交都同步更新了需求、决策和缺陷记录，并执行了对应测试后再提交、推送。

## 三、验证证据

| 验证 | 结果 |
|---|---|
| Release 构建 | `0` 警告，`0` 错误 |
| 完整回归（功能代码 `7431a5f`） | **87 passed / 0 failed，连续两次** |
| 文档门禁 | **78 条需求通过** |
| 暂存发布包 `--check-published` | PASS |
| 正式 `dist` 发布包 `--check-published` | PASS |
| 包装脚本 PowerShell 5.1 语法检查 | PASS |
| 测试后残留 LlamaManager/llama-server/strata-poly/Python 服务 | 0 |

发布冒烟实际覆盖：发布 EXE 的 help/service/status/stop，以及 PowerShell 包装的
`-Start/-Status/-Stop`，模型路径包含空格。最终输出：

```text
PASS published EXE help/service/status/stop and PowerShell wrapper with spaces
```

`4f32071` 只改 PowerShell 包装器和文档，没有改 C# 二进制；因此完整 C# 回归保留
`7431a5f` 的连续两次 87/0 证据，新增包装行为由发布冒烟直接验证。

## 四、正式部署记录

- 新 EXE SHA-256：`b474cdb46249299cacefb9e5bdb3aae3e5bbcf6849b78f2594f80f82e2669671`
- 新 PDB SHA-256：`a4d05d3b67d2170ca23c69d4a1855d9c7f56b8da4e2387dbd1ee152daadce5b7`
- 新包装脚本 SHA-256：`c0baf8ae0039450d2d5d153041baea090355cdd0d91e7bcbbdc376f95d2de1f0`
- 备份目录：`dist\.backup-20261101-181818-4f32071`
- 备份内容：旧 `LlamaManager.exe`、`LlamaManager.pdb`、`llama-manager.ps1`

部署只替换上述文件；用户配置、运行会话、日志、模型、引擎、插件和 `dist` 下的数据目录
均未改写。部署前确认没有 LlamaManager、llama-server、strata-poly 或 Python 服务进程运行。
正式包冒烟使用隔离 `LLAMA_MANAGER_ROOT` 和 fake server，没有占用或停止用户的 8080 服务。

## 五、验证命令

在 Windows NTFS 路径运行；回归不能把测试输出复制到回归 `run/` 目录，否则会递归复制。

```powershell
dotnet build LlamaManager.sln -c Release
dotnet run --project tests/LlamaManager.Regression -c Release
pwsh -File tools/docs-gate.ps1
dotnet publish gui/LlamaManager/LlamaManager.csproj -c Release -r win-x64 --self-contained true -o <临时发布目录>
dotnet run --project tests/LlamaManager.Regression -c Release --no-build -- --check-published "<绝对路径>\LlamaManager.exe"
```

约定：行为或文档改动先通过对应回归与 docs-gate，再提交、推送；PowerShell 文件保持 UTF-8 BOM；
不提交 config/logs/models/engines/dist；普通回归不得停止用户真实服务。

## 六、已知边界与风险

- 主工作区原有未提交改动尚未审阅/合入；这是恢复工作时最优先处理的版本管理事项。
- 正式发布冒烟使用 fake server，不等同于三个真实引擎的完整人工对话验收；REQ-071 仍为部分完成。
- 引擎更新能对普通失败回滚，但不宣称断电级原子性；进程在提交阶段被强杀仍可能留下暂存/备份目录。
- 端口探测已避免 Windows 监听表延迟，但异常安全软件/驱动行为仍需真实机器观察。
- GUI 美观度、低配机器线程默认值、不同 GPU/模型组合仍应以真实使用数据继续调优。

## 七、下一阶段候选

1. 先审阅并提交主工作区原有改动，再将 `codex/launcher-polish-iteration` 合并回 `main`；
   合并后重新跑构建、87 项回归、docs-gate 和正式发布冒烟。
2. 用真实 GUI 对三个内置引擎做一轮日常操作验收：搜索模型、开始/停止、日志、快捷键、
   窗口恢复、嵌入服务和错误提示。
3. 实测对比接入过程遥测（live tok/s、显存峰值、草稿接受率），并把最优参数一键写回预设。
4. 为 PolyStrata 做独立实测对比，明确端口、显存独占和重启清理策略。
5. 给 `AutostartService` 抽接口并补单元测试，覆盖注册表/计划任务不可用的失败路径。
6. 清理过旧 `dist/.backup-*` 前先确认当前版本稳定；不要删除最近一次
   `.backup-20261101-181818-4f32071`。

## 八、目标状态

用户要求“完成这个阶段后暂停目标”。本阶段已完成代码迭代、测试、5 笔功能/修复提交推送、正式本机部署
和交接文件；交接文档及其最终校验修正作为另外 2 笔提交推送。恢复时从「下一阶段候选」第 1 项开始，
不自动继续新功能。
