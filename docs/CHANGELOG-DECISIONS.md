# 变更与决策记录（CHANGELOG-DECISIONS）

按时间倒序记录影响需求或架构的变更。每条引用需求编号（见 docs/REQUIREMENTS.md）。
普通修复不记；**改变行为、需求或取舍的**才记。

## 2026-10-16

- **产品更名**「本地大模型助手」（REQ-001）。界面标题变更；程序集/仓库名不变，
  因 ConfigService 依赖安装布局定位 ManagerRoot。
- **新增 doctor 一键体检**（REQ-040~043）：CLI `--doctor` + GUI「一键体检」。
  检查 llamacpp / prism / strata-upstream / polystrata / python 的缺失与更新；
  Prism 无公开下载源，报手动状态。
- **llama.cpp 下载按 GPU 选 CUDA 大版本**（REQ-014）：实测发现 13.4 nightly 在 RTX 2060
  （sm_75）报 `PTX was compiled with an unsupported toolchain`——官方 13.4 构建不再携带
  Turing/Volta SASS。现在计算能力 < 8.0 选 CUDA 12.4，并已在本机验证 12.4 构建可启动推理。
- **嵌入服务独立成 TAB**（REQ-033）：从「高级」页底栏升级为完整「嵌入」页
  （模型选择 + 端口 + 启停）。定位为资料量化 / RAG 用向量化服务。
- **修复 CLI 状态/停止读错槽位**（REQ-022）：`--status`/`--stop`/`--open` 此前硬读
  `main` 槽，任何非 llamacpp 后端都误报"未运行"；现按后端槽位解析。
  实测：strata-upstream 启动 → status 报 ready → stop 干净退出。

## 2026-10-15

- **PolyStrata 由插件改为内置后端**（REQ-012；废弃 REQ-091）：`strata-poly.exe` 与
  `strata.exe` 一样只说行协议（`--serve`），HTTP API 来自 `serve/server.py`；原 plugin.json
  的 `--port`/`/health` 启动方式永远无法就绪。两者合并为 `StrataBackend` 的两个 profile。
- **修复分词表工具变体不匹配**（REQ-016）：PolyStrata 的 `strata_tokenizer.py` 把
  `vocab.json` 写进 `--out/tokenizer/` 子目录，upstream 版直写 `--out/`；改为探测实际产物。
- **修复 DetachedProcess 忽略工作目录**（REQ-025）：`python -m serve.server` 此前在
  Python 安装目录启动，报 `No module named 'serve'`。工作目录改为显式参数。
- **引擎一键下载落地**（REQ-014/015）：EngineDownloadService（下载+SHA-256+zip-slip 防护）、
  EngineCatalog（发布 JSON → 安装计划，纯函数可测）、EngineAcquisitionService（查询→下载→
  解压→写配置）。llama.cpp 二进制在 prerelease，扫描发布列表取首个含 Windows CUDA 的版本。
- **Strata 引擎发布源切换到 fork**（REQ-014）：官方 Niko1221/Strata 拒绝 Q3_K 专家，
  APEX 模型必须用补丁构建；发布 `v0.1.44.1-apex.1`（sm_75/86/89/120 + PTX）到
  viosonlee/apex-on-strata，下载含 SHA-256 校验。

## 2026-10-15（早）

- **UI 重设计**（REQ-030/031/032/034）：顶部统一控制条 + 功能标签页；删除两套重复的
  启动/停止按钮。
- **UI 修复**：schema 面板路径输入框误入标签列（三个窄框）；区块分隔线画成竖线；
  CPU MoE 开关文字被截断。

## 2026-10-14 及以前

- **可插拔引擎后端架构**（REQ-003/010~013/020~023）：BackendRegistry + IEngineBackend/
  IServerBackend + EngineArbiter；配置迁移即删（REQ-051）。
- **Strata cyber pack 路径归档**（废弃 REQ-090）：统一走引擎自带 HTTP 服务。
