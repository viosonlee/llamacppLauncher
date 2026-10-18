# 引擎与模型实测基准（2026-10-18）

本页记录本机（AMD Ryzen 7 5800H / 23.4 GB 内存 / RTX 3060 Laptop 6 GB）上
`llamacpp` 与 `polystrata` 两个引擎在真实模型上的对比，以及 MTP（投机解码）与
CPU 线程数这两个变量的实测结论。**所有数字都来自引擎自己回报的 `timings`
（`predicted_per_second` / `prompt_per_second`），不是客户端估算。**

测量方法：同一提示词（749 token 中文段落）、`temperature=0`、生成 256 token；
每个引擎独占 8080 端口、独占显卡；服务就绪后先发一个 8 token 的预热请求。
poly 用 `context=40960`、`tail-skip=6`、`threads=8`；llama.cpp 用
`-t 8 -tb 8 -c 40960 -ub 256 -fa on --jinja`，`--n-cpu-moe` 按模型取最佳值。

## 1. 两个引擎的横向对比

| 模型 | 大小 GB | poly 解码 | poly 预填 | llama.cpp 解码 | llama.cpp 预填 | poly 倍数 |
|---|---|---|---|---|---|---|
| Qwen3.6-35B-A3B APEX-I-Mini（原版） | 13.29 | **70.2** | 320 | 30.4 | 164 | 2.3× |
| Ornith-1.5-35B APEX-I-Mini | 13.29 | **64.5** | 250 | 28.4 | 158 | 2.3× |
| Qwen3.6 Claude-4.7-Distill APEX-I-Mini | 13.29 | **66.1** | 395 | 29.8 | 159 | 2.2× |
| Qwen3.6-heretic（去审查）APEX-I-Compact | 15.85 | **64.7** | 302 | 24.5 | 118 | 2.6× |
| Qwopus3.6-Coder APEX-I-Compact | 16.14 | **57.2** | 285 | 25.0 | 118 | 2.3× |
| LFM2.5-24B-A2B APEX-I-Compact | 10.44 | **51.3** | 542 | 28.6 | 185 | 1.8× |
| Gemma 4 26B-A4B QAT Q4_0（Google 官方） | 13.45 | **44.8** | 401 | 19.1 | 152 | 2.3× |
| Ornith-1.5-9B（稠密） | 4.15 | 不支持 | — | 67.8 | 1291 | — |

结论：

- **在 poly 支持的模型上，poly 普遍快 1.8–2.6 倍**，且显存稳定在 5.0 GB 左右
  （llama.cpp 常在 5.5–6.0 GB，贴着 6144 的天花板）。
- **Ornith-1.5-9B 只能走 llama.cpp**：它是稠密模型，poly 只吃 MoE，加载会超时。
- **llama.cpp 的 `--n-cpu-moe` 不是越大越安全**：LFM2.5 在 22 时显存冲到 5905 MB、
  解码掉到 6.4 tok/s，改成 28 就回到 28.6。VRAM 撞墙时速度是断崖式下跌，不是线性变慢。
- poly 的优势来自动态专家缓存（按空闲显存伸缩）与 `--tail-skip`，不来自更快的单步计算。

## 2. MTP（投机解码）：取决于显存余量，不取决于模型

同一个模型（Qwen3.6 APEX-I-Mini），llama.cpp，只切换 `--spec-type draft-mtp`：

**4K 上下文（`-c 4096 -ub 512 -ctk q8_0 -ctv q8_0`）**

| 配置 | 短提示 | 中提示 | 显存 MB |
|---|---|---|---|
| `--n-cpu-moe 30` + **MTP** | 30.4 | **34.9** | 5911 |
| `--n-cpu-moe 30` 无 MTP | 27.5 | 31.0 | 4737 |
| `--n-cpu-moe 32` + **MTP** | 29.2 | **33.0** | 5337 |
| `--n-cpu-moe 32` 无 MTP | 25.9 | 28.9 | 4161 |

**40K 上下文（`-c 40960`）**

| 配置 | 中提示 | 显存 MB |
|---|---|---|
| `--n-cpu-moe 32` + MTP | 28.4 | 5613 |
| `--n-cpu-moe 30` + MTP | 19.4 | 5969（撞墙） |
| `--n-cpu-moe 32` 无 MTP | 28.0 | 4423 |
| `--n-cpu-moe 30` 无 MTP | **31.2** | 4999 |
| `--n-cpu-moe 26` 无 MTP | 33.8 | 5919（余量仅 225 MB） |

规则：

- **MTP 固定多吃约 1.2 GB 显存**（5337 − 4161）。
- 显存有余量时（4K），它换来 **+13%** 解码；余量被 KV 吃掉时（40K），
  它把模型推过 6144 的悬崖，反而更慢。
- 投机解码省的是"专家搬运次数"，验证多个 token 仍要付等量 CPU 计算。本机专家大多
  在 CPU 上算（显存只放得下 2162/10496 个），所以带宽不是瓶颈时收益就被抵消。
- **poly 侧同样规律**：`--window 1`（完全不猜）与默认（窗口 8 + 草稿）在 40K 下
  都是 64.5–66.6 tok/s，差异在噪声内；`--no-draft` 只关掉块草稿，重复猜测仍在。
- 接受率 66–67%（llama.cpp）/ 85–93%（poly）说明这些文件的 MTP 头**是训练过的**，
  不是"坏头"。

## 3. CPU 线程数（poly 的 `threads`）

同一模型、中提示、生成 256 token，各 3 次：

| threads | 解码 tok/s | 均值 |
|---|---|---|
| 0（自动 = 16 线程） | 55.4（128 token 口径） | — |
| 10 | 64.0 / 65.0 | 64.5 |
| 12 | 63.0 / 63.6 / 64.1 | 63.6 |
| **8** | **63.6 / 65.8 / 68.6** | **66.0** |

8 线程最快，比自动（16 线程）快约 18%，与 llama.cpp 的 `-t 8` 最优值一致：
超线程会与显存搬运争抢内存带宽。代码里的默认值仍是 0（自动），因为这是按核数
自适应的；本机在面板里填 8。

## 4. 复现方式

```powershell
# llama.cpp：直接起进程，避免被已保存的模型预设覆盖参数
& llama-server.exe --host 127.0.0.1 --port 8080 -m <model.gguf> -t 8 -tb 8 `
  -c 40960 -np 1 -b 2048 -ub 256 -ngl all --fit on -fa on --jinja `
  -ctk q8_0 -ctv q4_0 --reasoning on --reasoning-budget -1 --n-cpu-moe 30

# poly：经启动器启动（它会生成 runconfig）
.\LlamaManager.exe --start --backend polystrata --model <model.gguf> --port 8080
```

注意：`llama-manager.config.json` 里的 `ModelPresets` 会在启动时覆盖参数框的内容，
做参数对比时要先把对应模型的预设删掉，否则测的是旧预设。

## 5. 相关

- 二创版本生态：[qwen36-35b-a3b-ecosystem.md](qwen36-35b-a3b-ecosystem.md)
- 引擎注册与参数：[engine-plugins.md](engine-plugins.md) §6
- 决策记录：[CHANGELOG-DECISIONS.md](CHANGELOG-DECISIONS.md) 2026-10-18
