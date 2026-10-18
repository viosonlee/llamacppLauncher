# 引擎与资源更新维护指南（ENGINE-UPDATE-GUIDE）

本指南说明如何在「本地大模型助手（LlamaManager）」中管理、集成与升级推理引擎（`polystrata`）、分词表（Tokenizer）及大模型文件。

> `strata-upstream` 已于 2026-10-18 退役（见 `docs/CHANGELOG-DECISIONS.md` 同日条目与 REQ-112）；
> 本文中仍出现的该引擎路径均为历史说明，实际只剩 `polystrata` 一个 Strata 家族引擎。

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

## 4. 引擎编译产物集成与升级（Engine Updates）

当上游源码更新或在本地（如 `C:\workspace\apex-on-strata`）编译出新版本时，请按以下步骤更新集成库：

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

## 5. 历史多余文件夹清理说明

在模型已移入 `C:\AIModel` 且引擎已集成至 `engines/` 后，原 C 盘根目录下的临时文件处置如下：

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

