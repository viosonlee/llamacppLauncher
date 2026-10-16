# 需求文档（REQUIREMENTS）

本文件是 LlamaManager（本地大模型助手）的需求基线。每个需求有稳定编号（REQ-xxx），
供变更文档（CHANGELOG-DECISIONS.md）与文档门禁引用。**改动行为前先来这里登记或修订需求。**

需求状态：`已实现` / `部分实现` / `规划中` / `已废弃`（废弃的需求保留编号与原因）。

## 1. 定位

- REQ-001 已实现：产品名「本地大模型助手」（界面标题），仓库/程序集名保持 LlamaManager。
- REQ-002 已实现：Windows x64 单机工具，.NET 8 WPF，自包含发布，双击即用。
- REQ-003 已实现：所有引擎统一抽象为「本地 HTTP 服务」（OpenAI/Anthropic 兼容），
  供 pi / Claude Code / Codex 等 agent 接入。

## 2. 引擎后端

- REQ-010 已实现：llamacpp 内置后端——任意 GGUF，OpenAI 兼容；Prism 模型（三元量化，
  `prism.hadamard.*`）自动路由到 Prism 定制引擎。
- REQ-011 已实现：strata-upstream 内置后端——Strata 原生 qwen35moe 路径，
  经 `serve/server.py` 提供 HTTP 服务；引擎须支持 `--families qwen35moe`。
- REQ-012 已实现：polystrata 内置后端——PolyStrata (strata-poly) MoE 引擎，
  同经 `serve/server.py`；兼容性矩阵独立于 Strata（支持更多专家格式）。
- REQ-013 已实现：插件机制——`plugins/<id>/plugin.json`（schema:1）声明自带 HTTP
  服务的第三方引擎；独立进程、崩溃隔离、无需重编译。
- REQ-014 已实现：引擎获取——「初始化环境」按后端自动下载并安装：
  - llamacpp：官方 nightly Windows CUDA 包 + cudart；**按 GPU 计算能力选 CUDA 大版本**
    （< 8.0 → CUDA 12.4，否则 13.4；13.4 nightly 不含 Turing/Volta SASS）。
  - strata-upstream：APEX 补丁构建（viosonlee/apex-on-strata release）+ upstream 分支源码
    归档（serve/）。
  - polystrata：VecSzn/PolyStrata release 引擎 + 源码归档。
  - 下载须校验 SHA-256（GitHub digest），解压防 zip-slip。
- REQ-015 已实现：Strata 家族自动建 Python venv 并按引擎 `requirements.txt` 安装依赖；
  源码缺失时回退内置依赖列表。
- REQ-016 已实现：分词表准备对工具变体鲁棒——`vocab.json` 落在 `--out/` 或
  `--out/tokenizer/` 均可接受，runconfig 指向实际目录。

## 3. 生命周期与并发

- REQ-020 已实现：GUI / CLI / 自启动共用同一后端注册表与生命周期逻辑，禁止旁路复制。
- REQ-021 已实现：会话身份 = PID + 创建时间 + 引擎路径；防 PID 重用误杀。
- REQ-022 已实现：会话槽位按后端隔离（llamacpp → "main"，其它 → 后端 id）；
  `--status`/`--stop`/`--open` 必须读对应槽位。
- REQ-023 已实现：GPU 重负载后端互斥（EngineArbiter），启动时统一确认。
- REQ-024 已实现：服务以分离进程运行（stdout/stderr 直写文件），GUI/CLI 退出不影响服务。
- REQ-025 已实现：引擎子进程的工作目录显式指定（`python -m` 模块启动必须用源码根）。

## 4. 界面

- REQ-030 已实现：顶部统一控制条——引擎下拉 + 一套启动/停止/打开页面/测速按钮。
- REQ-031 已实现：标签页按功能分：模型 / 参数 / 高级 / 嵌入 / 日志。
- REQ-032 已实现：llama.cpp 专属控件（原始参数、嵌入、自动开页）仅在选择 llama.cpp 时显示；
  其它引擎使用 schema 生成的参数面板。
- REQ-033 已实现：嵌入（Embedding）服务独立成「嵌入」页——模型选择、端口、启停；
  供资料量化 / RAG 使用，与主服务相互独立。
- REQ-034 已实现：模型列表按引擎感知——切换引擎时选择其自有的模型路径。

## 5. 体检与维护

- REQ-040 已实现：doctor 一键体检——每个引擎报告：缺失 / 可用 / 有更新 / 需手动；
  联网查询发布版本（`--offline` 或无网时降级为本地检查）。
- REQ-041 已实现：CLI `--doctor`（退出码：健康 0，有问题 1）；GUI「一键体检」按钮输出到运行输出。
- REQ-042 已实现：Prism 引擎（无公开下载源）在 doctor 中报状态与本机版本，更新提示为手动。
- REQ-043 部分实现：版本比较支持 `b11671`（llama.cpp）、`0.1.44.1-apex.1`（Strata fork）、
  `v0.1.44.1-poly.5`（PolyStrata）三种命名；不可比较的命名不误报更新。

## 6. 配置与数据

- REQ-050 已实现：配置原子写（临时文件 + 替换），损坏时从 `.bak` 恢复，坏原稿存 `.corrupt`。
- REQ-051 已实现：迁移即删——旧字段读时折叠进新结构，保存后删除。
- REQ-052 已实现：设置保存不得覆盖运行会话身份；运行时更新不得覆盖预设。
- REQ-053 已实现：预设按规范化绝对路径 + 文件签名；孤儿预设自动重绑定。
- REQ-054 已实现：引擎安装/下载/分词表等运行时数据位于 ManagerRoot 下的
  `engines/`、`downloads/`、`data/`、`venvs/`、`logs/`，均不入库（.gitignore）。

## 7. 安全与约束

- REQ-060 已实现：服务默认绑定 127.0.0.1；不默认开放 0.0.0.0。
- REQ-061 已实现：不阻塞 UI dispatcher；探测/就绪等待异步且缓存有界。
- REQ-062 已实现：回归测试使用隔离 LLAMA_MANAGER_ROOT 与假服务器；不停用户真实服务。
- REQ-063 已实现：不 stage 用户配置、日志、下载的引擎、模型、构建产物。

## 8. 验收

- REQ-070 已实现：回归套件（console runner）覆盖以上行为，含失败/取消路径；
  退出码非 0 即失败。
- REQ-071 部分实现：真实端到端冒烟（下载→启动→对话→停止）覆盖三个内置后端；
  Prism 引擎路径因本机缺引擎未覆盖启动，仅覆盖探测与 doctor 报告。

## 9. 已废弃

- REQ-090 已废弃：Strata cyber pack 一次性推理路径（2026-10 归档；统一走 HTTP 服务）。
- REQ-091 已废弃：polystrata 以 plugin.json 清单接入（strata-poly 说行协议而非 HTTP，
  清单无法表达 `serve/server.py` 包装，改为内置后端）。
