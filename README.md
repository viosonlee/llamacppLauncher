# 本地大模型助手（LlamaManager）

Windows 本地大模型启动器，使用 .NET 8 WPF。
图形界面、命令行和登录自启动共用同一套模型路由、配置和进程管理逻辑。

## 引擎后端（可插拔）

启动器把每个推理引擎抽象为「本地 HTTP 服务」，由统一的后端注册表驱动：

- **llamacpp**（内置）：任意 GGUF 的通用后端，OpenAI 兼容。
- **polystrata**（内置）：PolyStrata（strata-poly）MoE 引擎，启动引擎自带的
  `serve/server.py` HTTP 服务（OpenAI/Anthropic 兼容）。
  > 原 `strata-upstream`（`strata.exe`）已于 2026-10-18 退役：它唯一独有的 `qwen4exp`
  > （Qwen3.8-Flash-Next）需 32 GB 以上内存，在共同支持的 qwen35moe 上又比 polystrata 慢约 30%。
  > 见 `docs/CHANGELOG-DECISIONS.md` 同日条目。
- **kvmem**（可选内置后端）：真实 KVMem PolyStrata 分层 KV 引擎，使用完整 unified 发布包，
  与原版 PolyStrata 分目录保留。当前仅接入 `qwen35moe` GGUF、单 GPU、fp16/q8_0；
  NInfer `.ninfer` 尚未接入。部署、预算与验收见 [KVMem 集成说明](docs/KVMEM-INTEGRATION.md)。
- **插件后端**：`plugins/<id>/plugin.json`（`schema:1`）声明如何启动自带 HTTP 服务的第三方引擎、
  如何探测就绪、参数表。插件是独立进程，崩溃不影响启动器，新增引擎无需重编译。

两个引擎在真实模型上的对比（解码速度、显存、MTP 开关、线程数）见
[docs/engine-benchmarks.md](docs/engine-benchmarks.md)。


「一键体检」（GUI 按钮 / CLI `--doctor`）检查所有引擎：缺失、可用、有更新（联网比对适用发布版本）、
需手动（Prism 等自定义变体不由官方引擎更新器覆盖）。顶部 **一键更新** 或 CLI `--update --all` 更新全部内置引擎和匹配运行库；再次点击可取消。
llama.cpp 按 GPU 和 driver 可支持的 CUDA minor 选择同一发布的 binary+cudart 配对：pre-Ampere 仅 CUDA 12.4，
其它显卡也不会安装高于 driver 能力的 CUDA 包。PolyStrata 使用对应发布的 Python/CUDA 依赖，KVMem 使用完整统一包。
启动器管理的版本及下载缓存按每个引擎/架构保留 **当前版 + 最近一版旧版**；手工目录、运行中的版本和用户数据不会自动删除。
详见 [更新与保留策略](docs/ENGINE-UPDATE-GUIDE.md)。

标签页：模型 / 参数 / 高级 / **嵌入** / 日志。嵌入页管理独立的向量化服务
（如 bge-m3，供资料量化 / RAG 使用），与主模型服务相互独立。

模型文件（`.gguf`）由用户自备；引擎二进制首次使用时下载，不随启动器分发。

旧的 Strata pack 页（cyber）已归档，仅作参考。

## 使用

发布后运行 `dist/LlamaManager.exe`，选择模型目录或单个 GGUF 文件。
选择模型会保存上一模型的参数；点击启动/重启后，新选择才会生效。
运行状态、停止按钮、Web UI 和测速始终对应实际运行的实例。

- llama.cpp（含 Prism 模型路由）与 PolyStrata 的主服务默认统一使用 **8080**：agent 基址保持
  `http://127.0.0.1:8080/v1`，切换引擎先停止旧服务再启动新服务。已有自定义端口保留，
  启动失败只报告原因，不会自动换端口。嵌入服务独立，默认 8081。
- 原始启动参数可直接编辑。可视控件只修改自己的选项，保留其他参数和引号。llama.cpp 服务未显式指定 `-a` / `--alias` 时，会自动以不含目录与扩展名的模型文件名作为 API 模型名称（可通过“模型别名”覆盖）。
- “默认”表示不传该选项，与显式 `off`、`0`、`-1` 分开。
- 新预设从保守的上下文和批大小开始。已有的模型预设会保留，点击“重新生成”才会替换。
- CUDA 检测按引擎缓存；模型列表旁的“刷新”可重新检测设备。
- 模型列表为空时（新机器第一次使用）会出现“下载推荐模型…”：从内置目录挑一个与本机显存
  匹配的单文件 GGUF 直接下载，下载完成自动选为当前模型。链接与字节数经过验证，下载前检查
  磁盘空间、下载后校验 GGUF 文件头。
- 参数页的“实测对比…”（llama.cpp）用同一模型、同一提示实测多组参数（如 `--n-cpu-moe`
  的不同取值），每组独占端口与进程，结果（均值/最差/★最快）写入日志页。报告还包含引擎自报的
  预填速率与投机草稿接受率，以及运行期间采样的显存峰值——MoE 调优最关心的就是离显存上限有多近。
  实测前关掉其它占显存的程序，报告会记录开始时的显存空闲量。测速变体使用系统分配的临时
  端口，避开 8080、配置中的服务端口与已记录运行端口；不会停止或改写正式服务。
- 超长上下文（KVMem）：选择独立 **kvmem** 后端后初始化完整引擎包，再开启分层 KV。
  原版 llama.cpp / PolyStrata 不支持这些参数，也不会再显示误导开关。KVMem 默认逻辑上下文 65536、
  B=8192、R=512、H=1024 MiB、保留历史数 2；SSD 需显式设置配额及目录。
  使用块稀疏注意力，降低长历史的 KV 显存增长，但不保证杜绝所有 OOM 或保持全量注意力质量。
  默认普通解码（MTP 暂不接入），API 请求未指定输出上限时默认 1024 tokens。
  此后端的“打开页面”显示 `/props` 实际能力；不启动上游统一控制台或承诺内置聊天页。
- 小数参数（如 PolyStrata `cacheBias`）支持 `0.5` 和 `0,5`，保存统一为点号小数；
  非有限值（NaN/Infinity）和超范围值会阻止参数保存。
- 关闭窗口后服务器继续运行，日志直接写入文件。重新打开启动器后可继续管理。
- 自启动只使用当前用户的一个注册表 Run 项；重新设置时会清理本启动器的旧启动文件和同名计划任务。

模型目录中带 `prism.hadamard.*` 元数据或 Prism 私有张量类型的文件使用 Prism 引擎。
读取元数据失败时才回退到文件名识别。缺少所需引擎时会明确报错。

显存问题请看加载日志中的 **GPU 权重 / CPU 权重**，单看“卸载了多少层”不够。
TQ1_0 的 CUDA 支持取决于引擎版本；见 [Sakura 兼容性说明](tools/Sakura-CUDA.md)。
启动器不会自动转换模型。

### 顶部速度徽章与日志页

- 顶部第三行的徽章显示实时速度。生成中显示引擎自报的窗口化速率（PolyStrata 来自
  `/metrics`，llama.cpp 来自 `/slots` 计数采样，采样值标“约”）；空闲时显示上一次完成的
  请求。徽章独占一列，长模型名只会截断自己，不会把速度挤出窗口。
- 日志页显示管理器日志与所选引擎的输出，自动刷新。“请求完成”一行是启动器根据
  PolyStrata `/metrics` 汇总写入的（引擎本身不输出逐请求日志），包含 token 数、tok/s、
  耗时与草稿接受率——开 MTP 时可以直接观察命中率。
- 日志读不到（文件被轮转/占用）只损失该次内容；“清空显示”后下一次轮询会重新填充。
- 排障入口：`--read-live-speed <backend>` 直接打印状态行与遥测，`--probe-logs <backend>`
  打印日志页将渲染的全部内容。

## 构建和验证

本次变更的规格与验收入口：[变更规格](docs/KVMEM-CHANGE-SPEC.md) · [测试用例矩阵](docs/KVMEM-TEST-CASES.md)。

需要 Windows x64 和 .NET 8 SDK。发布包包含运行时，使用者无需安装 .NET。
首次构建需要从 NuGet 还原 `System.Management 8.0.0`。

```powershell
dotnet build LlamaManager.sln -c Release
dotnet run --project tests/LlamaManager.Regression -c Release
dotnet publish gui/LlamaManager/LlamaManager.csproj -c Release -r win-x64 --self-contained true -o dist
```

回归测试使用独立配置、模拟服务器和临时端口，无需下载测试模型，不会控制现有模型服务。
这是控制台测试程序，应使用上述 `dotnet run`，而非 `dotnet test`。
测试覆盖参数与 schema 回退、配置恢复、GGUF 解析、路径归一化、模型元信息、进程身份、
取消与并发启动、嵌入服务、日志与 GPU 诊断、测速和命令行。
端口回归覆盖占用/保留范围错误分类、测速临时端口排除正式端口，以及变体失败/取消后服务身份不变；
UI 回归覆盖点号/逗号小数在逐项和整面板保存中的一致性。
已知缺陷、证据与修复状态见 [docs/KNOWN-DEFECTS.md](docs/KNOWN-DEFECTS.md)。
成功后清理临时服务器目录，失败时保留现场；界面渲染图保存在 `logs/ui-regression-*.png`。
发布包可额外执行：

```powershell
dotnet run --project tests/LlamaManager.Regression -c Release -- --check-published dist/LlamaManager.exe
```

## 文件布局

```text
llama.cpp/
├── llama-server.exe          # 标准引擎及配套 DLL
├── engines/prism/            # Prism 引擎及其自己的配套 DLL
└── llamacppLauncher/
    ├── dist/LlamaManager.exe
    ├── gui/LlamaManager/     # WPF 源码，按参数、模型、运行操作分文件
    │   ├── Engines/          # 后端抽象、注册表、插件驱动与内置后端
    │   └── plugins/          # 随包分发的插件清单（plugin.json）
    ├── tests/                # 无测试框架依赖的回归程序
    ├── tools/                # 可选 Sakura 格式转换工具
    ├── docs/                 # 架构、验证和维护说明
    ├── llama-manager.ps1     # 兼容旧开关的薄包装
    ├── llama-manager.cmd
    ├── llama-manager.config.json
    └── logs/
```

配置、备份、模型、日志和构建产物不纳入版本控制。
配置写入采用锁和原子替换，保留 `.bak`；损坏时读取备份，后续保存会把损坏文件另存为 `.corrupt`。
没有有效备份时显示错误并保留原文件。

## 命令行

```powershell
.\llama-manager.ps1 -Start
.\llama-manager.ps1 -Status
.\llama-manager.ps1 -Stop
.\llama-manager.ps1 -Start -ModelPath 'C:/Models/my model.gguf' -Port 8090
.\llama-manager.ps1 -PrintPreset
```

EXE 对应开关：`--start`、`--service`、`--stop`、`--status`、`--open`、
「初始化」会补齐缺失引擎、按 GPU 自动选 CUDA 版的引擎，并创建 Python venv（`--bootstrap`）；
若本机没有 Python 3.10+，会**自动安装**（winget 优先，回退 python.org，均用户级静默、免管理员），
完成后把 `http://127.0.0.1:<port>/v1` 交给 pi / Claude Code / Codex。GPU 独占时自动换用其它引擎。
`--bootstrap --backend ID` 一键初始化该后端运行环境（下载引擎 + Python venv 与依赖）。
`--doctor` 一键体检：报告各引擎缺失/可用/有更新；`--offline` 跳过联网查询。
`--start` 遇到已经运行的实例会保留它；切换需先停止再启动。
退出码：0 成功；1 错误（`--doctor` 有问题时也是 1）；2 进程仍在加载，但 90 秒内 API 尚未就绪。

开发或测试可设置 `LLAMA_MANAGER_ROOT` 指向独立目录，隔离配置、日志与进程锁。
详见 [架构和测试说明](docs/architecture.md)。

全新电脑部署请按 [agent 部署指南](docs/AGENT-GUIDE.md)（含 Python 自动安装与排障表）。
引擎升级与模型管理请参考 [引擎与资源更新维护指南](docs/ENGINE-UPDATE-GUIDE.md)。
