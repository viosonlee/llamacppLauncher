# 全新电脑部署指南（AGENT-GUIDE）

> **读者：自动化 agent。** 本文是一台**全新 Windows 电脑**上把「本地大模型助手」（程序集名
> `LlamaManager`）配置到可用的**唯一权威步骤**。请按顺序执行，用命令输出验证每一步，不要凭猜测跳过。
> 人类用户请先读 `README.md`。
>
> 相关需求编号：REQ-100（Python 自动安装）、REQ-101（体检为全局功能）、REQ-102（本文档纳入门禁）、
> REQ-103（CLI 无值开关）。总清单见 `docs/REQUIREMENTS.md`。

## 0. 一句话流程

```
解压 zip → 运行 --doctor 看缺什么 → 按提示 --bootstrap 补引擎 → --start 起服务 → 验证 /v1
```

## 1. 前置条件（先核对，不满足就别往下走）

| 条件 | 如何验证 | 不满足时的处理 |
|---|---|---|
| Windows x64 | `[Environment]::Is64BitOperatingSystem` 为 True | 不支持 32 位 |
| .NET 8 桌面运行时 | **无需检查**：发布包自包含（self-contained） | — |
| NVIDIA 驱动 | `nvidia-smi` 能输出驱动版本 | 提示用户升级驱动；**不需要**装 CUDA Toolkit |
| 驱动版本 ≥ 580 | `nvidia-smi --query-gpu=driver_version --format=csv,noheader` | ≥580 可用全部引擎；550–579 只能用 CUDA 12.4 的 llama.cpp |
| 网络（首次） | 能访问 github.com 与 python.org | 离线时不支持首次下载，只能 `--doctor --offline` 体检 |
| Python 3.10+ | 不必预装（见 §4） | 初始化时自动安装 |

**GPU 兼容性**：引擎按计算能力自动选 CUDA 大版本——计算能力 < 8.0（Turing/Volta，如 RTX 20 系列）
用 CUDA 12.4；≥ 8.0 用 13.4。不要手动改成 13.4：13.4 nightly 不含 Turing SASS，会报
`PTX was compiled with an unsupported toolchain`。

## 2. 路径布局（安装根 = 解压目录）

程序通过向上查找 `llama-manager.ps1`（最多 8 层）确定安装根，也可用环境变量
`LLAMA_MANAGER_ROOT` 强制指定。

| 内容 | 路径 |
|---|---|
| 管理器安装根 | 解压目录（含 `LlamaManager.exe`）；引擎装在 `<根>/engines/` |
| llama.cpp 根 | 含 `llama-server.exe` 的目录（自安装根向上最多 4 层查找） |
| llama.cpp 引擎 | `<llama.cpp 根>/llama-server.exe` |
| Prism 引擎 | `<llama.cpp 根>/engines/prism/llama-server.exe`（**无公开下载源，需手动放置**） |
| strata-upstream 引擎 | `<根>/engines/strata-upstream/engine/strata.exe` + `serve/` |
| polystrata 引擎 | `<根>/engines/polystrata/engine-poly/strata-poly.exe` + `serve/` |
| Python venv | `<根>/venvs/strata-upstream/`、`<根>/venvs/polystrata/` |
| 配置与日志 | `<根>/config.json`、`<根>/logs/` |

## 3. 部署步骤（CLI，逐条执行并核对退出码）

```powershell
# 1) 体检：先看缺什么。离线用 --offline（不联网查更新）。退出码 0=健康，1=有缺失/待更新
.\LlamaManager.exe --doctor --offline

# 2) 装引擎（按 doctor 提示的缺失项；可全部执行，重复执行是幂等的）
.\LlamaManager.exe --bootstrap --backend llamacpp
.\LlamaManager.exe --bootstrap --backend strata-upstream
.\LlamaManager.exe --bootstrap --backend polystrata

# 3) 再次体检（这次联网，确认引擎版本）
.\LlamaManager.exe --doctor

# 4) 起服务（backend 必须是已就绪的引擎；模型用绝对路径）
.\LlamaManager.exe --start --backend strata-upstream --model "D:\models\APEX-I-Mini.gguf" --port 8080

# 5) 运行期管理
.\LlamaManager.exe --status      # 人读状态；非零退出码表示未运行
.\LlamaManager.exe --open        # 打开 Web UI
.\LlamaManager.exe --stop        # 停止（会校验 PID + 进程创建时间 + 引擎路径）
```

**退出码约定**：`--doctor` 0=健康 / 1=有缺失或待更新；`--status` 0=运行中 / 非 0=未运行；
`--bootstrap` 失败会以非零退出并在日志说明原因。

### REST 接入（给 pi / Claude Code / Codex）

服务就绪后暴露 OpenAI 兼容接口（`serve/server.py` 同时提供 Anthropic 兼容）：

- 基址：`http://127.0.0.1:<主端口>/v1`（默认主端口 8080，嵌入端口 8081）
- 就绪判定：先 `--status` 报运行中，再 `curl http://127.0.0.1:8080/v1/models`

## 4. Python 自动安装（REQ-100）

strata-upstream 与 polystrata 的 HTTP 服务由 Python 提供，`--bootstrap` 检测不到 Python 3.10+ 时会
**自动安装**，无需人工：

1. **winget 优先**：`winget install --id Python.Python.3.12 --exact --scope user --silent
   --accept-package-agreements --accept-source-agreements`（用户级，无管理员弹窗）。
2. **回退 python.org**：下载 `python-3.12.10-amd64.exe`，以
   `/quiet InstallAllUsers=0 PrependPath=1 Include_test=0` 静默安装，自动加入 PATH。
3. 安装后创建 venv：`<根>/venvs/<backendId>/`，再按引擎自带 `requirements.txt` 用 pip 安装依赖。

失败时**不会静默**：日志会打印「请手动安装 Windows x64 Python 3.10+」，体检（doctor）把 python 标为
`missing` 并在建议里指向自动路径。此时需人工装 Python 后重跑 `--bootstrap`。

## 5. 引擎与模型选择矩阵

| 引擎 | 适用模型 | 说明 |
|---|---|---|
| `llamacpp` | 通用 GGUF（含 Prism 三元量化） | Prism 模型需先手动放置 Prism 引擎，否则回退到普通 llama.cpp |
| `strata-upstream` | qwen35moe 家族（APEX 系列，含 MTP） | 内置多架构补丁引擎（sm_75/86/89/120 + PTX） |
| `polystrata` | PolyStrata MoE | 专家格式由兼容性矩阵判定，见 `docs/engine-plugins.md` |

**模型文件要求**：必须是完整的 `.gguf`；被截断的文件（大小明显偏小）不要用。q36 hybrid 按上游设计
把专家放在主机内存（约 12 GiB pinned），主机内存不足会启动失败。

## 6. 排障表（现象 → 处理）

| 现象 | 原因 | 处理 |
|---|---|---|
| 报 `PTX was compiled with an unsupported toolchain` | 用了 CUDA 13.4 构建跑 < 8.0 的 GPU | 改用 12.4 构建：`--bootstrap --backend llamacpp` 会自动按计算能力选择 |
| 首次对话前报 `No module named 'serve'` | venv 依赖未装或工作目录不对 | 重跑 `--bootstrap --backend <id>`；CLI/服务已在源码根作为工作目录启动 |
| 找不到分词表 `vocab.json` | 分词输出目录差异 | 已兼容 `--out/` 与 `--out/tokenizer/`；若仍失败，检查 `strata_tokenizer.py` 是否产出 vocab.json |
| 引擎迟迟不就绪（>5s） | 首次冷启动 / Defender 扫描 | 探测超时已放宽到 15s；仍未就绪看 `<根>/logs/` |
| doctor 报 prism `missing` | 无公开下载源 | 手动放置到 `<llama.cpp 根>/engines/prism/llama-server.exe` |
| doctor 报「有更新」 | 引擎有新发布 | 重跑对应 `--bootstrap` 覆盖更新 |
| `--status` 说未运行但服务在跑 | 会话槽位不匹配 | 已修复：状态按所选后端槽位查询；确认 `--backend` 与服务一致 |

## 7. Agent 约束（务必遵守）

- **不要停掉用户正在运行的真实模型服务**去做测试或重装。
- **不要提交**用户配置、日志、下载的引擎、模型、构建产物。
- **不要向上游仓库提 PR**；本项目的引擎补丁发到自己的 fork release。
- 需要跑回归测试时用**隔离的** `LLAMA_MANAGER_ROOT`，不要污染真实安装：
  `dotnet run --project tests/LlamaManager.Regression -c Release`。
- 不要做大模型性能扫测（耗时且无必要）；功能验证以「能启停 + 能出 token」为准。
- 改动力度最小化：GUI、CLI、自启必须共用同一套 C# 服务，**不得在 PowerShell 里重复生命周期逻辑**。

## 8. 验收清单（部署完成的证据）

- [ ] `--doctor` 退出码为 0，且每项引擎显示 `[ok]`
- [ ] `--start --backend <id> --model <gguf>` 后 `--status` 报运行中
- [ ] `curl http://127.0.0.1:8080/v1/models` 返回模型列表（HTTP 200）
- [ ] 一次对话请求返回 token（非空 `choices[0].message.content`）
- [ ] `--stop` 后 `--status` 报未运行，进程消失

## 9. 相关文档

- `README.md` — 面向人类的总览与使用
- `docs/REQUIREMENTS.md` — 需求编号与状态
- `docs/CHANGELOG-DECISIONS.md` — 关键决策与变更
- `docs/architecture.md` — 组件与数据流
- `docs/engine-plugins.md` — 引擎 / 插件清单格式
