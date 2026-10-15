# PolyStrata 实测：Qwen3.6-35B-A3B（RTX 3060 Laptop 6GB / 24GB RAM）

日期：2026-10-15。目的：在 6GB 显存的笔记本上验证 PolyStrata 能否跑 35B MoE，并量出
短/长提示词下的 **prefill（读提示）** 与 **decode（写回答）** 速度，作为后续多引擎对比的基线。

## 机器

| 项目 | 值 |
|---|---|
| GPU | NVIDIA GeForce RTX 3060 Laptop，6144 MiB，驱动 610.62，compute capability 8.6 |
| iGPU | AMD Radeon（Ryzen 7 5800H 核显，负责显示输出） |
| CPU | AMD Ryzen 7 5800H，8 核 / 16 线程，AVX2 |
| RAM | 23.4 GB（空闲约 12 GB），提交上限 30.2 GB |
| 磁盘 | C: 约 145 GB 空闲 |
| OS | Windows（Python 3.13.15） |

## 引擎与模型

- 引擎：PolyStrata `v0.1.44.1-poly.2`，预编译 `strata-poly.exe`
  （242,743,808 B，archs 75/80/86/89/120，CUDA 13.0；release sha256 `f4daa20984da…f6fe` 已核对）
- 服务：`python serve/server.py --engine strata --config <runconfig> --port 8080`
- 模型：`Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled-APEX-MTP-I-Mini.gguf`
  （14,272,221,568 B，arch `qwen35moe`，41 层含 MTP 块，256 专家 top-8，2-3bit APEX 专家）
- 上下文：32768；MTP：文件自带草稿块（引擎默认启用）
- 运行参数：`--vram-reserve-mib 256`（其余用 setup 默认）

> 说明：PolyStrata 的 `setup.py` 只接受它自己发布的固定文件（`verify_sha256` 无条件校验）。
> 为了用**已有**模型而不下载 14.4 GB 官方文件，本次对本地 `setup.py` 打了一行补丁：
> `poly_setup` 中当使用 `--gguf-dir` 时跳过该固定 SHA-256 校验；模型通过硬链接挂成期望文件名，
> 原文件未改动、未复制。

## 方法

用引擎自带的 benchmark（`python polystrata.py benchmark`），greedy，每次回答 256 token
（被模型自身 EOS 提前结束时输出更少）。短提示 + 三个长提示（512 / 2000 / 8000 token 量级）。
速度与命中率均为引擎自己的计数器；VRAM/RAM 是整机峰值。

```powershell
python polystrata.py benchmark --model ornith --prompt-tokens 512,2000,8000 --json _bench_result.json
```

## 结果

| 提示 | 输入 token | 输出 token | TTFT s | prefill tok/s | decode tok/s | 专家命中 | MTP 接受 | VRAM GiB | RAM GiB |
|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| short | 21 | 256 | 0.62 | 35.0* | **55.9** | 0.686 | 0.842 | 5.18 | 21.18 |
| long-512 | 800 | 200 | 2.39 | **335.8** | 53.7 | 0.412 | 0.829 | 5.00 | 21.10 |
| long-2000 | 2862 | 162 | 4.77 | **600.7** | 49.5 | 0.767 | 0.600 | 5.02 | 21.20 |
| long-8000 | 10987 | 185 | 9.99 | **896.3** | 50.5 | 0.708 | 0.807 | 5.03 | 21.29 |

\* 21 token 的提示太短，prefill 数字被首 token 延迟主导，不代表吞吐。

**结论**

- **decode 稳定在 50–56 tok/s**，与提示长度基本无关（长提示略低，专家缓存被挤占）。
- **prefill 随提示变长而上升**：335 → 601 → 896 tok/s（批次越大越能摊薄每 token 开销）。
  8000 token 提示约 12 s 读完。
- **MTP 草稿接受率 0.60–0.84**，短提示最高；这是 decode 能到 50+ tok/s 的主因。
- **专家缓存命中 41–77%**：6GB 卡只放得下约 2.5 GiB 专家（约 2300/10496 个），其余在 CPU 上算，
  所以整机 CPU 占用 42–80%。

## llama.cpp 对照（同一模型、同一上下文）

```powershell
llama-server.exe -m C:\PolyStrata-models\Ornith-1.5-35B-A3B-APEX-MTP-I-Mini.gguf `
  --host 127.0.0.1 --port 8080 -c 32768 -b 2048 -ub 512 -t 8 `
  -ngl all --n-cpu-moe 32 --fit on --fit-target 1024 `
  -ctk q8_0 -ctv q8_0 -fa on --jinja
```

| 提示 | 输入 token | llama.cpp prefill | llama.cpp decode | PolyStrata prefill | PolyStrata decode |
|---|---:|---:|---:|---:|---:|
| short | 21 | 17.0 | 26.2 | 35.0* | **55.9** |
| long-512 | 800 | 187.2 | 28.8 | **335.8** | **53.7** |
| long-2000 | 2578 / 2862 | 285.9 | 31.3 | **600.7** | **49.5** |
| long-8000 | 8641 / 10987 | 282.1 | 30.8 | **896.3** | **50.5** |

（两边的输入 token 数略有差异：两个 benchmark 的长提示文本与截断方式不同。）

**要点**

- **decode：PolyStrata 是 llama.cpp 的 1.7–2.1 倍**（50–56 vs 26–31 tok/s）。主因是 MTP 草稿：
  PolyStrata 用文件自带的 `blk.40` 草稿块，接受率 0.60–0.84；llama.cpp 这条路径**没有用它**——
  加载日志里 `blk.40.*` 全部是 `unused tensor ... ignoring`。
- **prefill：PolyStrata 是 llama.cpp 的 1.8–3.2 倍**（336–896 vs 187–286 tok/s）。
- 显存：llama.cpp 4751 MiB（`--n-cpu-moe 32`），PolyStrata 约 5 GiB。
- 这是**同机同模型**的实测，两边参数按各自推荐值给（llama.cpp `--n-cpu-moe 32` + q8_0 KV + fa；
  PolyStrata `--vram-reserve-mib 256` + 自带 MTP）。

## 观察：显存与核显/独显

### 显存占用

| 配置 | 驻留专家 | 显存 |
|---|---:|---:|
| setup 默认（`--stage-mib` 上限 2048） | 1940（2.2 GiB） | 4717 MiB（77%） |
| `--vram-reserve-mib 256 --stage-mib 64 --prompt-batch 128 --stage-ahead 0` | 2869（3.1 GiB） | 4973 MiB（81%） |
| 请求进行中 | — | 5125 MiB（83%） |

`strata-poly` **没有** `--expert-cache`（那是上游 strata-q36 的开关）；缓存由「空闲显存 −
`--vram-reserve-mib` − 先于缓存分配的部分（dense/KV/stage/prompt 缓冲）」自动决定，
`--vram-reserve-mib` 下限就是 256。剩下的约 1 GB 是引擎刻意的余量。

**不要为了吃满显存而把 reserve 压到 0。** `docs/DETAILS.md`（PolyStrata）实测：

| expert cache | slots | VRAM free | decode tok/s |
|---|---:|---|---:|
| `--expert-cache 8900` | 11,631 | 0 MiB | **13.7 / 14.7** |
| `auto` | 11,178 | 217 MiB | 102.1 / 122.3 |

> 453 slots（0.86 GiB）把 13.7 和 102 tok/s 分开……**处在 0 MiB free 才是 7 倍代价的来源**。

Windows 在显存超配时会把内存搬到系统内存（sysmem fallback），所以"吃满"反而大幅变慢。
`"vram_elastic": true` + `POST /v1/vram {"reserve_mib": 0}` 能强制吃满，但本引擎版本不认
`--vram-elastic`（启动即退），且按上述实测不建议。

### 核显利用率高、独显显示 0%

独显**在工作**。一次生成期间的 `nvidia-smi` 采样：

```
util.gpu  util.mem  VRAM      power
   0 %     100 %    4997 MiB   12.77 W   ← 空闲
  53 %     100 %    4997 MiB   77.81 W   ← 生成中
  41 %     100 %    4997 MiB   93.60 W
  44 %     100 %    4997 MiB   98.26 W
   0 %     100 %    4997 MiB   34.24 W
```

功耗 12 W → 98 W，显存常驻 4997 MiB，`util.memory` 恒为 100%（专家经 PCIe 搬运/带宽受限）。
看起来"独显 0%"是因为：

1. Windows 任务管理器默认只画 **3D 引擎**；CUDA 算力在 **Cuda / Compute** 图上，需手动切换。
2. 这是混合输出笔记本：**核显驱动显示**，所以桌面/终端一动核显就高。
3. 6GB 卡只缓存 2.5 GiB 专家，大量专家在 **CPU** 上算，负载是 CPU + 显存带宽型的、突发式的，
   不是持续 3D 满载。

## 复现

```powershell
cd C:\PolyStrata
python setup.py --family ornith --model APEX-I-MINI --gguf-dir C:\PolyStrata-models --vision no --yes --no-start
python serve/server.py --engine strata --config C:\PolyStrata\strata-ornith-apex-i-mini.json --port 8080
python polystrata.py benchmark --model ornith --prompt-tokens 512,2000,8000 --json _bench_result.json
```
