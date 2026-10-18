# 会话交接文档（PROGRESS-HANDOFF）

> 更新时间：2026-10-18 21:25（Asia/Tokyo）· 会话由 goal_round 驱动，本文件是下一轮会话的入口。
> 目标状态：**已暂停**（见文末）。恢复时先读本文件与 `git log --oneline -10`。

## 一、项目当前状态（截至 HEAD `89ef56f`）

仓库：`C:\llama-b9822-bin-win-cuda-13.3-x64\llamacppLauncher`，主分支 == origin/main，工作树干净。

| 项 | 状态 |
|---|---|
| 构建 | `dotnet build LlamaManager.sln -c Release` 0 警告 0 错误 |
| 回归 | **75 passed, 0 failed**（console runner，非 dotnet-test）|
| docs-gate | 56 条需求通过（`pwsh -File tools\docs-gate.ps1`）|
| dist | 已发布（含全部本轮功能），`--check-published` PASS |
| 运行服务 | polystrata 在 8080（Ornith-1.5-35B · threads 8 · tail-skip 6 · context 131072）|

## 二、本阶段（第 1–6 轮）完成的工作

按提交倒序，每项都有回归测试与文档记录：

1. **`89ef56f` 参数实测对比（REQ-117）**：参数页「实测对比…」按钮。计划格式 `标签|参数`（每行一组），
   每组独占端口（8095 起）启动独立 llama-server，同提示测 TTFT/tok/s，结果写日志页（均值/最差/★）。
   报告带显存快照（实测验证时发现：poly 占 5 GB 显存时数字低一半且不可比）。MoE 模型预填
   `--n-cpu-moe 26/30/34` 梯度。CLI：`--benchmark <model> <runs> <plan文件>`。
   真实验证：moe 26 = 11.76 vs moe 30 = 15.79 tok/s（方向与历史校准一致）。
2. **`a8f051d` 模型一键下载（REQ-116）**：空模型列表 →「下载推荐模型…」→ 内置目录 5 个单文件
   GGUF（显存升序）。**所有链接经 HTTP 200 验证、字节数取自 CDN 头**（第一版有个 404 被实测抓住）。
   防护：磁盘预检（2 GiB 余量）、下载后 GGUF 头校验、同名同大小跳过/不同大小重下。
   目标遵循用户 ModelsDirectory。CLI：`--download-model <名称|序号>`。真实下载验证过（806 MB）。
3. **`1e63a26` MoE 预设按专家字节规划（REQ-115）**：旧逻辑把整个文件当显存需求砍 `-ngl`（13 GiB A3B
   在 6 GiB 卡上得到近纯 CPU 起点）。新逻辑 `-ngl 999 --n-cpu-moe N`：显存减计算预留（768 MiB）与
   KV 缓存后装专家（文件 91%），N=装不下的层数。校准锚点：本机实测边界 26、最快 30，公式给 28。
   顺带修遥测边界：`generating` 且 `generated=0` 不再误报 idle。
4. **`5da92ca` 修复两个指名缺陷（REQ-114）**：
   - 界面看不到速度：根因①顶部条 StackPanel 裁剪溢出（速度被挤出窗口外，所有引擎）→ 改三行布局，
     速度徽章独占 Grid 列，UI 回归断言徽章不超卡片；根因②poly `/slots` 无 token 计数 → 新增
     `PolyMetricsService` 读 `/metrics`（live 状态机 + 窗口化 tok/s + 完成请求环 + 专家缓存 + 硬件）。
   - poly 日志不更新：引擎不写逐请求日志 → 启动器按 `requests[].time` 去重写「请求完成」行
     （含草稿接受率），poly 启停写持久日志。
   - 加固：日志读取失败不再拖垮状态轮询、日志页轮询开头刷新、清空后可重新填充、
     消失的进程不再显示为运行中（`ServerService.IsSessionAlive`）。
   - 修复发布级 bug：3 个 ps1 丢 UTF-8 BOM 导致 PowerShell 5.1 解析失败（`--check-published` 抓住）。
5. **`8056b7a` 测试补全 + 文档纠错**（前一会话）：回归 61→68，9 处文档事实错误修正。

## 三、验证命令（全绿才算一轮完成）

```powershell
dotnet build LlamaManager.sln -c Release
dotnet run --project tests/LlamaManager.Regression -c Release
pwsh -File tools/docs-gate.ps1
dotnet publish gui/LlamaManager/LlamaManager.csproj -c Release -r win-x64 --self-contained true -o dist
dotnet run --project tests/LlamaManager.Regression -c Release --no-build -- --check-published "<绝对路径>\dist\LlamaManager.exe"
```

约定：文档/行为改动必须过 docs-gate 再提交；PowerShell 脚本 UTF-8 **BOM**；
不提交 config/logs/engines/models/dist；**不要停用户的 8080 服务**；回归用隔离 root，安全。

## 四、诊断 CLI（本阶段新增，调试用）

```
--read-live-speed <backendId>   # 打印状态行 + 遥测（对真实服务）
--probe-logs <backendId>        # 打印日志页将渲染的全部内容
--download-model <名称|序号>    # 真实下载目录里的模型（注意磁盘占用，用完删）
--benchmark <model> <runs> <plan文件>  # 真实 A/B 实测（独占 8095+，注意显存）
--invoke-cli <...>              # 透传 CommandLineService
```

## 五、下一阶段候选（按价值排序）

1. **实测对比接入遥测过程数据**：现在每组只有完成后的 tok/s；`/metrics` 的 live 窗口速率、
   显存峰值、草稿接受率可以进报告，做成小表格（对 MoE 调优直接有用）。
2. **poly 的实测对比**：REQ-117 只支持 llama.cpp。poly 改参数要经 runconfig + 重启，可以
   复用 `StrataBackend.StartAsync`，但先要想清楚端口/显存独占策略。
3. **实测对比预设化**：把测出来的最优参数一键写回当前模型预设（现在要手动抄）。
4. **`AutostartService` 抽接口补单测**：最后一个无单测的服务（注册表/schtasks）。
5. **嵌入服务模型下载**：模型目录里没有 bge-m3 时嵌入页是死路，可挂进 ModelCatalog。
6. **待确认的小疑点**：`SpeedTestService` 的 `chat_template_kwargs.enable_thinking` 对不支持
   该模板的模型是否报错（Gemma 4 无思考模板）——实测对比对这类模型可能全失败。

## 六、已知边界（非 bug，但要知道）

- 实测对比会受显存共享影响：报告已带显存快照，但要给出干净数字，先停其它引擎。
- 默认模型目录 = llama-server.exe 旁的 `models/`（自 10 月 3 日即如此，四处一致，**有意为之**，
  上轮疑点已解除）。用户配置了 ModelsDirectory 则完全遵循配置。
- polystrata 引擎本身不写逐请求日志（无 verbose 选项），日志页的「请求完成」行是启动器
  从 `/metrics` 合成的——这是设计，不是缺陷。
- `EngineCatalog` 仍保留 Prism 相关代码（Prism 模型识别/服务器路径），是活代码不是遗留。

## 七、目标暂停

goal_round 目标：自我迭代（易用/好调试/多机型适配）+ 修复速度显示与 poly 日志。
两项指名缺陷已在 `5da92ca` 修复并验证；目标整体**未宣告完成**（界面打磨与多机型调优
是无终点项），按用户指示暂停。恢复时：
1. 读本文档 + `git log --oneline -10`；
2. 跑一遍「三、验证命令」确认基线仍绿；
3. 从「五、下一阶段候选」按序取任务；
4. 循环：改 → 测 → docs-gate → 提交 → 推送。
