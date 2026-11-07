# 引擎与资源更新维护指南（ENGINE-UPDATE-GUIDE）

本指南说明如何在「本地大模型助手（LlamaManager）」中管理、集成与升级推理引擎（`polystrata`）、分词表（Tokenizer）及大模型文件。

> `strata-upstream` 已于 2026-10-18 退役（见 `docs/CHANGELOG-DECISIONS.md` 同日条目与 REQ-112）；
> 本文中仍出现的该引擎路径均为历史说明；当前提供原版 `polystrata` 与独立 `kvmem`（PolyStrata 分页版），互不覆盖。

---

## 1. 架构总览与目录约定

启动器采用自包含与按需加载设计，所有引擎、依赖环境、分词缓存和模型均有严格分层的存放路径：

```text
C:\
├── AIModel\                               # 用户全局大模型目录（统一存放 .gguf）
│   ├── Ornith-1.5-35B-A3B-APEX-MTP-I-Mini.gguf
│   ├── Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled-APEX-MTP-I-Mini.gguf
│   └── ...
└── llama-b9822-bin-win-cuda-13.3-x64\
    ├── llama-server.exe                   # llamacpp 标准引擎
    └── llamacppLauncher\                  # 启动器工程根目录
        ├── engines\                       # 推理引擎与服务资源（集成库）
        │   └── polystrata\
        │       ├── engine-poly\strata-poly.exe  # PolyStrata 二进制
        │       ├── serve\                       # HTTP 服务后端
        │       ├── tools\                       # 打包与分词工具
        │       └── expert-profile.txt           # 预置专家激活画像（如有）
        ├── venvs\                         # 独立的 Python 隔离运行环境
        │   └── polystrata\Scripts\python.exe
        ├── data\                          # 自动生成的分词表与运行期配置缓存
        │   └── polystrata\<模型哈希>\tokenizer\
        └── logs\                          # 运行日志
```

---

## 2. 大模型存放与迁移（Model Storage）

1. **统一存放位置**：
   - 所有 `.gguf` 模型统一存放在 `C:\AIModel`（启动器设置的 `ModelsDirectory`）。
2. **迁移与识别**：
   - `C:\PolyStrata-models` 目录下的模型可直接移入 `C:\AIModel`。
   - 同在 C 盘时，移动为秒级文件重命名，不占用额外磁盘空间，不增加 SSD 磨损。
   - 移入后，启动器界面下拉框会自动扫描并列出该模型，无需手动配置路径。

---

## 3. 分词表管理机制（Tokenizer Lifecycle）

在 Strata / PolyStrata 体系中，**分词表不需要人工从外部手动复制或维护**：

1. **自动提取**：
   - 现代 GGUF 模型均内嵌了完整的 Tokenizer 元数据（包括 `vocab.json`、`merges.txt`、`chat_template.jinja` 等）。
   - 启动器在点击「启动服务」时，`StrataPackService.EnsureTokenizerDirAsync` 会自动调用内置工具 `tools/strata_tokenizer.py` 从模型 GGUF 文件中提取分词表。
2. **安全隔离缓存**：
   - 提取出的分词表自动缓存于：
     `<启动器根目录>\data\<引擎ID>\<模型哈希>\tokenizer\`
3. **缓存复用**：
   - 只要 GGUF 模型文件未发生变动，下次启动直接复用缓存，秒级就绪。模型文件更新时会自动感知并重新生成。
4. **结论**：
   - 散落在根目录（如 `C:\PolyStrata-data`）下的分词表为运行残留，不需要手工拷入启动器，启动器内部机制会自动搞定。

---

## 4. 内置引擎与必需运行库一键更新（REQ-126）

顶部「一键更新」与下列命令共用 C# 服务；不需要手动覆盖 EXE 或混拷 CUDA DLL：

```powershell
.\dist\LlamaManager.exe --update --all
# 或只更新一个内置后端
.\dist\LlamaManager.exe --update --backend llamacpp
.\dist\LlamaManager.exe --update --backend polystrata
.\dist\LlamaManager.exe --update --backend kvmem
.\dist\LlamaManager.exe --doctor --offline
```

- `llamacpp`：以实际 `--version` 的 build 比较；按 GPU/driver 支持范围选择同一 release、同一 CUDA minor 的原生包+cudart。新 driver 的 `CUDA UMD Version` 与旧 `CUDA Version` 均可识别。不因为 `/releases/latest` 仅含稳定 tag 而忽略 Windows nightly。
- `polystrata`：对应 release 的原生 ZIP、服务源码和 exact CUDA wheel pins 配套。私有 Python 环境在新版本目录中准备；外部用户指定 Python 不自动升级，必须能通过依赖检查。保留原生 BUILD，另存经校验的 release provenance，不伪改版本号。
- `kvmem`：完整 unified 包按精确 GPU 架构下载，包含 Python/CUDA/双 worker；启动器仍只接入 PolyStrata API，详细范围见 [KVMem 集成](KVMEM-INTEGRATION.md)。
- 已是最新且运行库完整时不再次下载。不可识别的手工引擎或 Prism 变体明确报告，不能用错误官方包盲目替换。
- 新版本采用 `engines/<backend>/<release>-<CUDA或GPU>/` 独立目录，SHA 校验、解压、原生版本/运行库/服务依赖验证后才原子提交路径；不停止或覆盖运行中的模型服务。
- 安装只改引擎路径。模型调优参数、活动后端与运行身份保留；引用旧公共安装路径的模型预设同步该路径，独立手工路径仍保留。
- 再次点击 GUI 更新按钮可取消。未完成安装不启用；已成功提交的其它引擎不撤销。CLI 遇到单项失败继续报告其它项，整体返回非零；取消终止后续任务。

### 4.0 版本和下载缓存保留

每个内置后端/架构保留当前版与最近一版旧版。仅由启动器新安装产生的 `.llama-manager-owned.json` 有效记录可参与清理；原手工目录不自动认领。
缓存须有对应 ownership receipt，记录的绝对路径、长度及 SHA-256 均匹配，且位于授权下载根或其一层 release 子目录。
运行中的版本、用户指定的恢复路径、模型/配置/新日志都优先保护，可超过两版并报告原因，不为满足数量限制删除用户数据。
发行包本身附带的历史 `.log` 只有记录了其原始 SHA/长度且内容未变化才随旧版删除。
清理失败或取消不回滚刚安装的版本；没有 ownership 的旧下载碎片不会按名字猜测删除。

以下旧手工编译/退役说明仅供了解历史，不是当前自动更新器的执行步骤；不要对运行中的引擎就地覆盖。
本次变更规格与测试索引见 [变更规格](KVMEM-CHANGE-SPEC.md) 和 [测试用例矩阵](KVMEM-TEST-CASES.md)。

### 4.1 更新 Upstream 引擎（`strata-upstream`）—— 已退役

该引擎已于 2026-10-18 退役，`engines\strata-upstream\`、`venvs\strata-upstream\` 与
`data\strata-upstream\` 均已删除，注册表、下载计划与参数面板中的对应项也一并移除。
它唯一独有的 `qwen4exp`（Qwen3.8-Flash-Next）最小 29.6 GB 文件、需 32 GB 内存，本机跑不动；
在共同支持的 qwen35moe 上它比 `polystrata` 慢约 30%。若将来换到 32 GB 以上内存的机器，
应改装 PolyStrata 的完整主引擎，而不是恢复这个定制分支。详见 `docs/CHANGELOG-DECISIONS.md`。

引擎的日常更新不再需要手工替换二进制：已安装的引擎落后于最新发布时，`--bootstrap` 会自动升级，
也可以直接执行：

```powershell
pwsh -File llama-manager.ps1 -Update            # 检查并更新可自动下载的引擎
```

### 4.2 更新 PolyStrata 引擎（`polystrata`）

1. **编译产物**：
   - 将新编译的 `strata-poly.exe` 复制并替换到：
     `llamacppLauncher\engines\polystrata\engine-poly\strata-poly.exe`
2. **配套动态库**：
   - 确保 `cublas64_13.dll` 和 `cublasLt64_13.dll` 放置在 `engine-poly\` 目录中。
3. **服务与工具**：
   - 同步更新 `engines\polystrata\serve\` 及 `engines\polystrata\tools\`。
4. **专家画像（可选）**：
   - 若模型附带特定调优的 `expert-profile.txt`，可直接存放在 `engines\polystrata\` 备用。

### 4.3 Python 虚拟环境与依赖更新

当 `serve/requirements.txt` 增加了新的 Python 依赖包时：

```powershell
# 自动同步并安装指定引擎的所有 Python 依赖
pwsh -File llama-manager.ps1 -Bootstrap -Backend polystrata
```

或在启动器图形界面中，点击「配置环境」按钮即可一键更新虚拟环境。

---

## 4.4 本机验收记录（2026-11-07）

- 本机 driver 617.42，`CUDA UMD Version: 13.4`，RTX3060 Laptop 6GB。
- [首次真实一键更新](../downloads/maintenance-20261107/update-first.log)：llama.cpp b12125 → b12182，选择同发布的 CUDA12.4 原生包+cudart（当前发布的13.5高于本机driver，不强装）；原手工入口未覆盖。
- PolyStrata 本机原目录已经是 poly.7，首次一键更新正确跳过。另执行了[真实受管安装验证](../downloads/maintenance-20261107/polystrata-acquire-fixed.log)，验证 poly.7 原生/source、`nvidia-cublas==13.0.2.14`、`nvidia-cuda-runtime==13.0.96` 以及新版本私有 Python 环境，成功后才切换安装路径。
- 现场发现配套 `strata-vision` 不接受 `--help`，会报 `unknown argument --help`；改成无参数 usage 检查（在加载模型前返回2）并补回归。第一次失败目录已清理、旧配置未切换，随后缓存复用重试成功。
- [llama.cpp 真 GPU 请求](../downloads/maintenance-20261107/llamacpp-gpu-smoke.log)：Qwen3.5-0.8B Q8_0，17 prompt / 2 completion tokens，HTTP200、回答 `OK`，GPU使用1027 MiB，随后停止。
- [PolyStrata 真 GPU 请求](../downloads/maintenance-20261107/polystrata-gpu-smoke.log)：Cyber-Tiel-Coder-35B-A3B，196 prompt / 2 completion tokens，HTTP200、回答 `OK`，GPU使用4751 MiB，随后停止。新 private venv 直接导出分词表，不再为 tokenizer 安装无关 gguf/另建打包环境。
- KVMem 本机完整包 v0.19.1 未重复下载；其 [32K/64K+SSD 真实验收](KVMEM-INTEGRATION.md) 保持有效。
- 配置完整对比额外发现原序列化模型会丢弃未知 `Window` 字段：已添加 JsonExtensionData 保留未知顶层用户元数据，并从本次操作前备份恢复原窗口字段。未改变模型/调优预设/活动后端/Running/自启；[配置核对结果](../downloads/maintenance-20261107/config-preservation.txt)。
- 旧回归的失联恢复发现 TCP 表 `false` 但 API仍就绪；保留身份校验、移除表漏报的绝对门槛后通过，不靠延长等待掩盖。详见 DEF-23。
- 最终构建 **0 警告 / 0 错误**；[完整回归](../downloads/maintenance-20261107/regression-release-verification.log) **108 passed / 0 failed**；文档门禁65条需求通过。
- [第二次真实 `--update --all`](../downloads/maintenance-20261107/update-second.log) 三个引擎均为“无需更新、不重复下载”，全部运行库检查通过；[离线 doctor](../downloads/maintenance-20261107/doctor-final.log) 退出0，Prism仅作为手工可选变体报告。
- 已更新 [正式启动器入口](../dist/LlamaManager.exe)，只保留 [最近一版启动器备份](../downloads/kvmem/launcher-backup-20261107/LlamaManager.exe)。实际部署路径、当前与备份的 SHA-256 见 [部署记录](../downloads/maintenance-20261107/launcher-deployment.json)，复制/替换前后均校验一致。
- 引擎版本清理的成功、失败、取消、运行保护、随包日志和跨CUDA升级情形由真实临时文件/ZIP回归验证；本机当前只新增一个受管 llama.cpp 版本和一个受管 PolyStrata 版本，无需删除手工旧目录。不会伪称已删除尚不存在的第二旧版。
- 本机所有 GPU 冒烟结束后确认无测试引擎残留，显存恢复0 MiB；正式服务未自动启动，活动后端仍按用户原设置。

## 4.5 本次项目目录清理

完成构建、108项回归、真实 GPU 推理和发布验证后，已清理30个历史回归临时目录、6个可再生成的 bin/obj 目录、2份发布中转副本及本次辅助临时文件。
[清理记录](../downloads/maintenance-20261107/cleanup-result.json) 记录了明确路径、文件数量与磁盘空闲变化：实际空闲增加约 **34.7 GB（32.3 GiB）**。

保留源码、模型、实际引擎/运行库、正式启动器、最近一版备份、用户配置及备份、GPU验收日志、更新/回归结果和权限恢复记录。
未终止用户服务或空闲 MSBuild worker，没有跟随符号链接/目录联接清理。构建产物下次运行标准 `dotnet build` / `dotnet run` 会重新生成；启动正式自包含 EXE 不依赖它们。

## 5. 历史多余文件夹清理说明

以下是历史现场记录，不是当前机器的删除授权或现状证明。删除前必须重新核对实际目录、模型/用户数据与配置/运行引用；本次提交整理不操作工作区外目录：

| 目录 | 状态 | 处置建议 | 说明 |
|---|---|---|---|
| `C:\PolyStrata-models` | 空（仅剩 `.done`） | **可直接删除** | 模型已安全移入 `C:\AIModel` |
| `C:\PolyStrata-data` | 临时下载碎片与旧缓存 | **可直接删除** | 含 2.5GB 未完成 `.part` 碎片；分词表已由启动器内置接管；激活画像已归档 |
| `C:\PolyStrata` | 早期旧编译目录（10/15） | **可直接删除** | 启动器 `engines/polystrata` 内置了更新版本（10/16），不再引用此目录 |

---

## 6. 关于历史 `cyber` 分支的归档与废弃说明

在早期阶段，本项目曾基于 `cyber-on-strata` 探索过离线生成 pack 并在启动器内直接调度 CLI 的单次推理路径（旧需求 REQ-090）。

### 为什么归档 `cyber`？
1. **架构限制**：离线 pack 路径预处理耗时长、产生多余分片碎片，且为进程级单次文本进出，无法作为长驻 HTTP 服务接入外部 Agent 工具（如 pi agent、Claude Code、Codex 等）。
2. **上游演进**：官方 Strata 0.1.44+ 重构了原生 qwen35moe 支持，无需离线 pack 即可原地流式加载 GGUF 专家权重，并自带了完善的 `serve/server.py` HTTP 服务（同时支持 OpenAI / Anthropic 协议）。
3. **正式归档状态（只读）**：
   - 启动器已于 2026-10 正式废弃 REQ-090，`strata-cyber` / `cyber` 分支**全面归档为只读（Read-Only）状态**，不再维护、不再构建、不再接受新改动。
   - 启动器后端注册表已彻底移除 `strata-cyber` 选项与旧 pack 界面。
   - 所有针对 APEX 和 Strata 的新特性、补丁与上游同步，曾统一在 `apex-on-strata` 仓库的 `upstream` 分支维护；该分支随 `strata-upstream` 引擎一并归档。
   - 启动器标准运行引擎统一对应为 `polystrata`（加上 `llamacpp` 与可选的 Prism）。

