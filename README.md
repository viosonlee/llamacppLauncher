# 本地大模型助手（LlamaManager）

Windows 本地大模型启动器，使用 .NET 8 WPF。
图形界面、命令行和登录自启动共用同一套模型路由、配置和进程管理逻辑。

## 引擎后端（可插拔）

启动器把每个推理引擎抽象为「本地 HTTP 服务」，由统一的后端注册表驱动：

- **llamacpp**（内置）：任意 GGUF 的通用后端，OpenAI 兼容。
- **strata-upstream**（内置）：Strata 原生 qwen35moe 路径（原地读 GGUF），启动引擎自带的
  `serve/server.py` HTTP 服务（OpenAI/Anthropic 兼容）。
- **polystrata**（内置）：PolyStrata（strata-poly）MoE 引擎，同样经 `serve/server.py`
  提供 HTTP 服务。
- **插件后端**：`plugins/<id>/plugin.json`（`schema:1`）声明如何启动自带 HTTP 服务的第三方引擎、
  如何探测就绪、参数表。插件是独立进程，崩溃不影响启动器，新增引擎无需重编译。


「一键体检」（GUI 按钮 / CLI `--doctor`）检查所有引擎：缺失、可用、有更新（联网比对发布版本）、
需手动（Prism 无公开下载源）。llama.cpp 下载按 GPU 计算能力选择 CUDA 大版本：
pre-Ampere（如 RTX 20 系）取 CUDA 12.4 构建，更新的卡取 13.4。

标签页：模型 / 参数 / 高级 / **嵌入** / 日志。嵌入页管理独立的向量化服务
（如 bge-m3，供资料量化 / RAG 使用），与主模型服务相互独立。

模型文件（`.gguf`）由用户自备；引擎二进制首次使用时下载，不随启动器分发。

旧的 Strata pack 页（cyber）已归档，仅作参考。

## 使用

发布后运行 `dist/LlamaManager.exe`，选择模型目录或单个 GGUF 文件。
选择模型会保存上一模型的参数；点击启动/重启后，新选择才会生效。
运行状态、停止按钮、Web UI 和测速始终对应实际运行的实例。

- 默认主服务端口为 8080，嵌入服务端口为 8081；可在配置中修改嵌入模型和端口。
- 原始启动参数可直接编辑。可视控件只修改自己的选项，保留其他参数和引号。
- “默认”表示不传该选项，与显式 `off`、`0`、`-1` 分开。
- 新预设从保守的上下文和批大小开始。已有的模型预设会保留，点击“重新生成”才会替换。
- CUDA 检测按引擎缓存；模型列表旁的“刷新”可重新检测设备。
- 关闭窗口后服务器继续运行，日志直接写入文件。重新打开启动器后可继续管理。
- 自启动只使用当前用户的一个注册表 Run 项；重新设置时会清理本启动器的旧启动文件和同名计划任务。

模型目录中带 `prism.hadamard.*` 元数据或 Prism 私有张量类型的文件使用 Prism 引擎。
读取元数据失败时才回退到文件名识别。缺少所需引擎时会明确报错。

显存问题请看加载日志中的 **GPU 权重 / CPU 权重**，单看“卸载了多少层”不够。
TQ1_0 的 CUDA 支持取决于引擎版本；见 [Sakura 兼容性说明](tools/Sakura-CUDA.md)。
启动器不会自动转换模型。

## 构建和验证

需要 Windows x64 和 .NET 8 SDK。发布包包含运行时，使用者无需安装 .NET。
首次构建需要从 NuGet 还原 `System.Management 8.0.0`。

```powershell
dotnet build LlamaManager.sln -c Release
dotnet run --project tests/LlamaManager.Regression -c Release
dotnet publish gui/LlamaManager/LlamaManager.csproj -c Release -r win-x64 --self-contained true -o dist
```

回归测试使用独立配置、模拟服务器和临时端口，无需下载测试模型，不会控制现有模型服务。
这是控制台测试程序，应使用上述 `dotnet run`，而非 `dotnet test`。
测试覆盖参数、配置恢复、GGUF 解析、进程身份、取消与并发启动、嵌入服务、日志、测速和命令行。
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
