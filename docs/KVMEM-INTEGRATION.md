# KVMem 实际引擎集成与验收（2026-11-07）

相关需求：REQ-124、REQ-125。此文区分参数、安装、运行与长输入验证，不把假服务器回归当作 GPU 推理成功。

## 集成范围

- 独立后端 ID：`kvmem`，显示名 **KVMem (PolyStrata · 分层 KV)**。保留原版 `polystrata` / `llamacpp` 与现有配置；不执行 Git 硬回滚。
- 上游：[KVMem v0.19.1 unified release](https://github.com/kvmem/kvmem/releases/tag/v0.19.1)。本机 RTX 3060 Laptop GPU，6144 MiB、Compute Capability 8.6，驱动 617.42。
- 本机资产：`kvmem-v0.19.1-windows-x64-nvidia-30-unified.zip`，1,169,737,483 bytes。
- 官方 SHA-256：`9dbfdcf1e893cd60ba05a7f8b5a60522a472c1a2a5772bcbcfcf6a2650c0ea9c`；下载后已独立校验一致。
- 包内 PolyStrata worker SHA-256：`3c3d2367631c8572ba4ad0633722bf11b1320c4fde5b8c6017de984a04463b80`；本机校验一致。
- 仅接入 `general.architecture=qwen35moe` 的 GGUF，单 NVIDIA GPU（当前使用 GPU0）、fp16/q8_0 KV。完整包包含 NInfer，但启动器 **尚未接入 `.ninfer` 后端**；稠密 GGUF 不会自动转换成 NInfer。
- 使用普通解码作为保守基线；不自动使用外部 MTP 权重。内置 MTP、多 GPU、并行生成与上游统一聊天控制台均不在本轮接入范围。

## 真包布局与安全安装

本机安装目录为 `engines/kvmem/v0.19.1-nvidia-30/`；版本、显卡专用的新目录不会覆盖旧引擎：

```text
workers/polystrata/strata-poly.exe
workers/polystrata/strata-vision.exe
workers/ninfer/ninfer-kvmem-server.exe
backends/polystrata/serve/server.py
backends/polystrata/serve/kvmem.py
backends/polystrata/tools/{strata_tokenizer,gguf_reader}.py
runtime/python/python.exe
runtime/cuda/{cublas64_13,cublasLt64_13}.dll
console-build.json
provenance/architecture-build.json
```

获取逻辑按精确 sm70/75/86/89/120 选择 dedicated unified 资产；不能把未知 GPU 或 sm80/sm90 猜成 RTX30/50。
下载必须有有效 SHA-256（发布 digest、匹配校验文件，或已核验的 v0.19.1 sm86 固定值）。
完整包先在未发布目录解压，校验越界路径、链接、布局、版本/架构、包内 Python、native capability 与可用 worker provenance hash，之后才记录新后端路径。
失败/取消不覆盖旧目录、不切换配置；更新不改已有模型、预设、活动后端或运行身份。

```powershell
.\dist\LlamaManager.exe --bootstrap --backend kvmem
.\dist\LlamaManager.exe --doctor --offline
.\dist\LlamaManager.exe --update --backend kvmem
```

未安装且未配置时，KVMem 是可选项，不会单独使全局 doctor 失败。
已配置后按真实包检查；native 不实现 `--version`，版本读取 `console-build.json`。
体检的 `--help` 允许该 native 的帮助退出码 2；`--kvmem-capabilities` 必须退出 0 并返回有效 schema，且在 `CUDA_VISIBLE_DEVICES=-1` 与包内 CUDA PATH 下执行，避免加载模型。

## 协议与默认参数

原 c3e3644 仅增加面板与假协议测试，而且裸 `--kvmem` 不被此版 PolyStrata 接受。
现在原版面板不再显示错误开关；陈旧启用配置/原始参数会明确拒绝，并提示选择独立后端。
新后端的 UI 开关是启动器设置，**并不直接生成同名 native flag**：正数 `--kvmem-budget` 才启用分页。

| 设置 | 默认 | 语义与约束 |
|---|---:|---|
| 逻辑 context | 65536 | 历史上限，不是 GPU 全量 KV 分配；必须 >= B+R |
| 活跃预算 B | 8192 tokens | 32 对齐，含选中历史；小预算可能影响稀疏检索质量 |
| 追加工作区 R | 512 tokens | 32 对齐；不是单次回复总长度硬上限 |
| Host KV 配额 H | 1024 MiB | 进程共享历史 payload 配额，不含模型/索引/状态/系统占用 |
| SSD 配额 D | 0 MiB | 关闭；正数必须提供独立 SSD 目录 |
| 保留历史 S | 2 | 包含当前 owner，不是 GPU 并行生成数；开启 history-reuse |
| 预填充分块 | 128 tokens | 必须 <= R，降低 6GB GPU 的工作区峰值 |
| 专家搬运工作区 | 128 MiB | 控制预填充 staging 峰值 |
| 显存预留 | 512 MiB | native 至少要求 256 MiB；仍需为桌面与其它程序留空间 |
| KV | q8_0 | 可选 fp16；int8/int8k 不适用于本集成 |
| 默认输出上限 | 1024 tokens | API 显式 max_tokens 可覆盖；防止按整个 context 余量进行 Host 准入 |

启用时同时生成 `--window 1 --no-draft --lookup 0`，清零旧 prompt/conversation cache 并开启 `--kvmem-history-reuse`。
Query 使用 page-softmax / mean / graph-alpha=0.2、block=32、sink=128、recent=1024，QueryMax=1024、ReplayMax=8192，LRFU 关闭。
关闭 KVMem 时不生成任何 `--kvmem-*`，即使保留了旧预算/磁盘路径；磁盘配额为 0 时保留 UI 路径但不把目录传给 native。
所有生效的非法预算、对齐与容量组合明确拒绝，不静默改写。

模型分词表通过发布包的 [assets.py](https://github.com/kvmem/kvmem/blob/v0.19.1/tools/polystrata/assets.py) 导出，记录同 GGUF 与导出器哈希，验证 vocab/merges/tokenizer/token_type/chat_template 五件套；不为纯导出安装 gguf、pip 或 venv。
Python 通过 `-X utf8 -u -m serve.server` 在服务源码根启动；native CUDA PATH 与 metrics/attention 设置通过 runconfig `env/lib_dirs` 真正传入。
可匹配 CPU 视觉组件（gpu=false），但文本长上下文验收不等于已验收图像输入。

## 启动、状态与停止

```powershell
.\dist\LlamaManager.exe --start --backend kvmem --model "C:\models\qwen35moe.gguf" --port 8080
.\dist\LlamaManager.exe --status --backend kvmem
.\dist\LlamaManager.exe --stop --backend kvmem
```

启动不会为测试停止用户的真实模型服务。已有记录的 GPU 服务仍存活时拒绝另起 KVMem；正式服务端口仍默认 8080，不自动改端口。
就绪要求 `/health.loaded=true`、`/props.model_path` 与会话一致、`backend=polystrata`、KVMem enabled/reuse 及有效 B/R/H/D/S/block 符合本次启动期望。
仅 HTTP 200、不加载模型或错误服务占用端口，都不能判定成功。启动失败/取消按 PID+创建时间+路径清理本次进程树；旧 session 不能清掉新 session。

此后端是 API 服务；“打开页面”显示 `/props` 实际能力，并不启动统一 console 或提供内置聊天 UI。

## 显式 GPU 验收（不进入普通回归）

[GPU smoke harness](../tools/KvmemSmoke/Program.cs) 调用真实共享后端的 Start/Ready/Stop，使用一次性隔离根与系统分配的测试端口，不污染正式配置。
[输入生成器](../tools/KvmemSmoke/prepare_requests.py) 使用该包 tokenizer 生成 32K/64K 文档，把测试 code 放在 sink/recent 之外，并显式标注短问题 Query 范围。
普通 `dotnet run --project tests/LlamaManager.Regression -c Release` 不下载模型、不使用 GPU。

```powershell
# 先确认没有用户模型引擎运行；准备本机已有的 qwen35moe MODEL。
$bundle = 'engines\kvmem\v0.19.1-nvidia-30'
$model = 'C:\models\qwen35moe.gguf'
& "$bundle\runtime\python\python.exe" tools\KvmemSmoke\prepare_requests.py $bundle $model downloads\kvmem\smoke-requests.json
# ROOT 必须未存在；每次测试新建隔离根，不复用配置或运行身份。
dotnet run --project tools/KvmemSmoke -c Release -- logs\kvmem-gpu-smoke-unique $bundle $model downloads\kvmem\smoke-requests.json
```

验收必须记录实际 API prompt_tokens（而非只设大 context）、非空正确输出、有效 B+R 与更长历史、RAM/SSD counters、GPU 显存采样、会话复用与停止清理。
`kvmem_query` 范围是最后 user 的 Unicode code points 半开区间，不能直接把 C# UTF-16 长度或 UTF-8 bytes 当坐标。
长 user 超过 QueryMax 时会选末尾有界 Query，不代表整个历史被截断；ReplayMax 不是模型 context 上限。

### 当前验证记录

- 发布包/worker 校验、包内 Python、sm86 native capabilities 已实测通过。
- 共享后端已在 RTX3060 上加载 Cyber-Tiel-Coder-35B-A3B.APEX-I-MiniPlus-V2.1，短请求实际 194 prompt tokens、3 completion tokens，返回 `OK.`。
- 真实 GPU 长输入与 SSD 验证已通过，所有请求 HTTP 200。测试逻辑 context=131072，B8192/R512、Host=256 MiB、SSD=8192 MiB、S2、KV=q8_0、P128/stage128、threads8，普通解码、无 tail-skip/cache-bias/GDN-MMA。

| 用例 | 实际 prompt tokens | cached tokens | HTTP 总耗时 | 结果 |
|---|---:|---:|---:|---|
| 短请求 | 194 | 0 | 13.3 s | `OK.` |
| 32K 文档 | 33,008 | 0 | 317.4 s | 冷区验证码正确：`739251` |
| 64K 文档 | 65,776 | 182 | 589.3 s | 冷区验证码正确：`739251` |
| 64K 会话复用 | 65,808 | 65,747 | 1.7 s | 同一验证码正确 |

- [原生日志](../logs/kvmem-gpu-smoke-20261107-01/logs/engine-kvmem.log)：32K/64K/复用均记录 `KVMEM_QUERY replay=1`；末次 Host payload=267,735,040 bytes，SSD payload=450,867,200 bytes，累计 SSD read=159,457,280、written=541,040,640 bytes，`errors=0`。
- [显存采样](../logs/kvmem-gpu-smoke-20261107-01/gpu-memory.csv)：569 次，约 1.5 s 间隔，最高 used=4751 MiB；这是采样最大值，不承诺捕获瞬时峰值。结束后 GPU used 恢复 0 MiB。
- 原生报告 32K 预填充 104.2 tok/s、64K 111.5 tok/s（含有界 Query 重放），短回复解码 23.2/35.7 tok/s；不是长生成性能基准，不能用七个生成 tokens 推断持续速度。
- 本次 Stop 校验主 Python PID=13328、创建时刻与解释器路径后停止进程树，确认主进程/native/视觉子进程退出，隔离配置 Running 清空；未停止用户服务。
- KVMem 阶段回归93项通过；后续一键更新/版本保留与完整验收完成后，最终构建0警告、0错误，回归 **108 passed / 0 failed**，文档门禁65条需求通过。覆盖安装成功、并发参数保留、错误校验、危险 ZIP、提交前取消、旧版保留与真实身份恢复。
- 已自包含发布并更新 [现有启动器入口](../dist/LlamaManager.exe)。随后追加的一键更新与版本保留任务也已发布；当前与最近备份的 SHA-256 见 [最终部署记录](../downloads/maintenance-20261107/launcher-deployment.json)。
- 仅保留 [最近一版启动器备份](../downloads/kvmem/launcher-backup-20261107/LlamaManager.exe)，SHA-256=`efafa802489d34a7830c5f80836faf626e3aaf9986cdd21c926fd2c7ed7828c5`。更新验收详见 [维护指南](ENGINE-UPDATE-GUIDE.md#44-本机验收记录2026-11-07)。
- 发布版 EXE 与 PowerShell 薄包装已通过隔离 help/service/status/stop 冒烟；本机 doctor 离线退出 0，KVMem 报 sm86、0.19.1 unified 可用。
- KVMem 初次部署时用户配置和原版引擎 SHA-256 均保持不变。追加一键更新验收后仅迁移了已校验的安装路径及仍引用旧公共路径的预设路径；原手工引擎文件、模型调优参数、活动后端、端口、自启与运行身份保留，没有留下测试模型服务。
- 此结果证明本机此模型、此配置的真实 32K/64K 分层推理与 SSD 读写/历史复用；不证明其它模型、256K/百万上下文、所有输入检索质量或避免所有 OOM。

## 限制：不是“杜绝所有 OOM”

KVMem 给 KV 活跃集设上限，不消除模型权重、专家缓存、GDN 状态、预填充工作区、图、视觉组件、其它 GPU 进程及 Windows/驱动显存预算。
Host payload 配额、pinned/scratch、索引、系统 RAM、SSD 空间与 I/O 都可能成为瓶颈；历史缓存只在当前进程有效，不跨重启恢复。
算法使用块稀疏注意力，小 B 可能改变答案/检索质量，并不等价于每步全量 attention。
RTX3060 大上下文配置、实际输入成功、SSD 有效写入、长文检索正确，必须分别给出证据，不能互相替代。
