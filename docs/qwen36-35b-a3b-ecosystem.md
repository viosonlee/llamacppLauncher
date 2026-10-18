# Qwen3.6-35B-A3B 社区二创 / 魔改生态调研

> 调研时间 2026-10-18。数据主要来自 HuggingFace API/模型卡、作者 GitHub 与 arXiv 摘要页；ModelScope 未逐仓核对，本文数字均出自 HF 侧。

## 1. 基础模型：官方事实

[Qwen/Qwen3.6-35B-A3B](https://huggingface.co/Qwen/Qwen3.6-35B-A3B) 由 Qwen 团队于 2026-04-15 发布权重，Apache-2.0。35B 总参 / 3B 激活，40 层，256 路由专家 + 1 共享专家、每 token 激活 8+1；结构是混合注意力（30 层 Gated DeltaNet 线性注意力 + 10 层 Gated Attention），**自带视觉塔**，属 image-text-to-text。原生 262,144 上下文，YaRN×4 可扩到约 1,010,000。

**有 MTP**：模型卡写 "MTP: trained with multi-steps"，并给出 vLLM `qwen3_next_mtp`、SGLang `NEXTN` 的投机解码启动参数。官方自测：SWE-bench Verified 73.4、Terminal-Bench 2.0 51.5、MMLU-Pro 85.2、C-Eval 90.0、GPQA 86.0、LiveCodeBench v6 80.4。

一个被普遍忽略的工程事实：只有 10/40 层带 KV，其余是固定大小的 SSM 状态（约 39 MiB），所以 262k 上下文的 KV 在 q8_0 下只要约 3 GB。**低配机器的瓶颈是权重文件大小，不是上下文长度。**

## 2. 版本谱系：先纠正一个误解

[Ornith-1.5-35B-A3B](https://huggingface.co/ornith-ai/Ornith-1.5-35B-A3B) **不是 Qwen3.6 的微调**：它没有 `base_model` 标签，官方说是 Ornith-1.0（基于 Qwen3.5 与 Gemma4 继续预训练）再叠加自改进 RL，与 Qwen3.6 是"同架构、不同权重"的兄弟。因此生态实际是两棵树，很多"Qwen3.6 魔改"其实是 Ornith 的二创。

### 2.1 真正改权重的（微调 / 蒸馏 / 去审查）

| 仓库 | 作者 | 时间 | 基座 | 改了什么 | 许可证 |
|---|---|---|---|---|---|
| [Claude-4.7-Opus-Reasoning-Distilled](https://huggingface.co/lordx64/Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled) | lordx64 | 2026-04-18 | Qwen3.6-35B-A3B | ~7.8k 条 Opus 4.7 思维链 SFT；**仅 attention LoRA r=16**，可训练 3.44M/35.1B | Apache-2.0 |
| [Huihui-...-abliterated](https://huggingface.co/huihui-ai/Huihui-Qwen3.6-35B-A3B-abliterated) | huihui-ai | 2026-04-18 | 上述蒸馏版 | abliteration 去拒答，**MTP 头被一并剥掉** | Apache-2.0 |
| [Cyber-Tiel-Coder-35B-A3B-GGUF-MTP](https://huggingface.co/peculiar-ragdoll/Cyber-Tiel-Coder-35B-A3B-GGUF-MTP) | Saga Ishtardottir | 2026-08-31 | Huihui-Ornith-1.5-abliterated | 去审查基座 + 自建 cyber imatrix 重量化 + **嫁接 Ornith 的 MTP 头** | MIT |

注意：HauhauCS "Aggressive"、LuffyTheFox Genesis-Hermes、AEON-7 "Ultimate"、DavidAU 的 Fable-Fusion/Heretic/NEO-MAX 等系列也属此类，但仓库里只有标签堆叠，无同口径数字，本文不采信其宣传。

### 2.2 纯量化（不改权重，常被误当"新模型"）

| 仓库 | 作者 | 时间 | 基座 | 方法 / 档位 |
|---|---|---|---|---|
| [mudler/...-APEX-MTP-GGUF](https://huggingface.co/mudler/Ornith-1.5-35B-A3B-APEX-MTP-GGUF)（**你当前在用**） | mudler / LocalAI | 2026-08-19 | Ornith-1.5 | [APEX](https://github.com/localai-org/apex-quant) 按张量角色分层混合精度；6 档 14.37–26.17 GB，含 blk.40 MTP 头 |
| [Tiel-Coder-...-GGUF-MTP](https://huggingface.co/peculiar-ragdoll/Tiel-Coder-35B-A3B-GGUF-MTP) | peculiar-ragdoll | 2026-08-24 | Ornith-1.5 | Unsloth Dynamic + 自建 imatrix + Sharp 模板；13.6–38.8 GB |
| [pfeifferj/...-GSQ-RCO-GGUF](https://huggingface.co/pfeifferj/Ornith-1.5-35B-A3B-GSQ-RCO-GGUF) | Josephine Pfeiffer | 2026-09-24 | Ornith-1.5 | [GSQ](https://arxiv.org/abs/2604.18556) + [RCO](https://arxiv.org/abs/2605.00649)，3bit 13.0 GB / 3.5bit 15.2 GB |
| [unsloth/Qwen3.6-35B-A3B-MTP-GGUF](https://huggingface.co/unsloth/Qwen3.6-35B-A3B-MTP-GGUF) | unsloth | 2026-05-11 | Qwen3.6 | 官方事实标准 UD 量化 |
| [Nail-Qwen3.6-35B-A3B-GGUF](https://huggingface.co/peculiar-ragdoll/Nail-Qwen3.6-35B-A3B-GGUF) | peculiar-ragdoll | 2026-08-09 | Qwen3.6 | 重量化 + efficient-thinking 模板 |

**IQ 的歧义**：llama.cpp 里 IQ2/IQ3/IQ4 指 imatrix 量化类型；而 DavidAU 的 "IQ-Ultra" 是微调品牌名，两者无关。

### 2.3 结构改造

[REAP 19B](https://huggingface.co/mlx-community/Qwen3.6-35B-A3B-OptiQ-4bit-REAP-19B)（专家剪枝，仅 MLX）、[Cyronius/Qwen3.6-27B-A2.8B](https://huggingface.co/Cyronius/Qwen3.6-27B-A2.8B)（剪到 2.8B 激活）、[ManniX Coder/CoderX](https://huggingface.co/ManniX-ITA/Qwen3.6-27B-A3B-Coder-MTP-GGUF)（剪成 27B-A3B + MTP）、[DFlash2](https://huggingface.co/incoai/Qwen3.6-35B-A3B-DFlash2)（投机解码 draft 模型）。这些多数面向 vLLM/SGLang，**Windows 单机 + GGUF 基本用不上**。

## 3. 能力差异：实测与宣传语的分界

**有对照实验的只有一家。** Tiel 卡给出同口径 4-bit 横评：MMLU-Pro 上 Nail 84.0 > Ornith 78.0 > Tiel 73.7，原版 Qwen3.6 85.3；并做了关键对照——同一份量化换回 Ornith 自带模板，分数正好回到 78.0，说明那 4.3 分是 Sharp 模板"答得更短"换来的，不是量化损失。这是整个生态里最干净的一组消融。

SWE-bench-Live（25 题，单次运行）：Tiel 12、CyberTiel 13.7（3 seed 均值）、Ornith 8、原版 Qwen3.6 8、Qwen3.8-27B 16。作者自己标注样本小、差异当噪声。

厂商自测：Ornith 报 SWE-bench Verified 79 vs Qwen3.6 73.4、Terminal-Bench 2.1 67.8 vs 52.5，**无第三方复现**。

蒸馏版：[lordx64 的卡](https://huggingface.co/lordx64/Qwen3.6-35B-A3B-Claude-4.7-Opus-Reasoning-Distilled) 报 MMLU-Pro 74.9（5-shot multiturn、limit 500）与 GSM8K 84.3；**AIME/GPQA/MATH-500 因答案抽取失败而空缺，作者自己标了 pending**。其 MMLU-Pro 与 Qwen 官方 85.2 口径不同，不能直接比；作者也承认蒸馏只迁移推理风格、不增加知识。

唯一敢报负结果的是 GSQ-RCO：3-bit MMLU-Pro 43.70，**低于同尺寸 matched initializer 的 45.35**，PPL 3.5369 vs BF16 3.4112。

**没有实测的部分**：中文能力几乎无人评测——Qwen 官方 C-Eval 90.0 是唯一可引用的中文数字，Ornith/Tiel/CyberTiel 都只声明支持 en/zh，无中文基准，"中文变好或变差"目前**无法证实**。角色扮演只有 DavidAU 系列的口碑，无基准。工具调用方面 CyberTiel 的 Cybench 15/43、HarmBench 0/84 属安全域实测，不等于通用 Agent 能力。

## 4. MTP：谁带、能快多少、伤不伤质量

**带 MTP 的**：基座自带 nextn（block 40）；unsloth、mudler APEX-MTP、Tiel/CyberTiel MTP 梯子、Dyluhn 的 lordx64-MTP。**去审查版默认没有**——abliteration 是在无头模型上做的，CyberTiel 是把 Ornith 的头"嫁接"回去的。

**你手上那个文件很可能带着坏头。** [mudler 仓库](https://huggingface.co/mudler/Ornith-1.5-35B-A3B-APEX-MTP-GGUF)最后一次提交是 2026-08-20；而 Tiel 卡明确记录：Ornith 初版 nextn 头是随机初始化（kurtosis 3.00），**2026-08-23 才重传了训练过的头**（kurtosis 25.1）。也就是说 8-20 之前量化的 APEX-MTP 全部基于未训练的头。自查方法：对比开/关投机解码的 tok/s，若持平或下降即中招；Tiel 的 MTP 梯子建于 8-24，在修复之后。

实测提速（均为作者单机自测，非规格）：

| 来源 | 模型 | 结果 |
|---|---|---|
| Tiel 卡 | UD-Q4_K_XL | 77.4 → 94.4 tok/s（n-max=1）= **1.22x**，接受率 83.3% |
| CyberTiel 卡 | UD-Q4_K_XL | 78.2 → 94.3 tok/s = **1.21x**，82.1% |
| 同上 | n-max=8（长草稿） | 46.5 tok/s = **0.60x**，反跌 |
| PolyStrata 文档 | Qwen3.6 / Ornith | 1.2–1.5x / 1.1–1.2x（Ornith 草稿头更不准） |

**质量**：投机解码是"提议—验证—拒绝采样"，被接受的 token 流与原模型分布一致，所以理论上无损；CyberTiel 正是用这一点论证"把未去审查的头嫁接到去审查模型上不会带回拒答"。Tiel 的全部基准也是在非 MTP 主体上跑的。结论：**MTP 不改质量，只改速度；但头是否训练过，决定它是加速还是拖累。**

## 5. 针对 6 GB 显存 + 23.4 GB 内存的三档推荐

先给硬约束。[PolyStrata](https://github.com/VecSzn/PolyStrata) 官方门槛是 8 GB 显存（Ornith 约 4 GB 固定开销 + 每层专家 0.49 GB），**6 GB 属未验证区间**；23.4 GB 内存才是真瓶颈——APEX-I-Mini（14.4 GB）官方建议"8 GB 卡旁约 18 GB 内存"，6 GB 卡还要更多，已贴近上限。可参照的极端案例是 [4 GB 1050 Ti + 64 GB DDR3 跑 APEX-I-Quality 23.7 GB、32K 上下文、21–24 tok/s](https://github.com/Hypo30285/PolyStrata-on-13-year-old-Z420-with-RTX-1050Ti-4GB)，说明这条路可行，但人家内存是你的 2.7 倍。

- **稳妥（保持现状）**：留 APEX-MTP-I-Mini 14.37 GB，但①先验证 MTP 头（大概率该关），②纯文本不加载 `mmproj`（省 0.9 GB），③llama.cpp 下用 `--n-cpu-moe` 把路由专家压到 CPU 而不要降 `-ngl`，按 Tiel 公式 `40-floor((VRAM-4.3)/0.49)`，6 GB 约等于 **37**。代价：3-bit 以下的知识类题目明显掉分。
- **追求质量**：Tiel `UD-Q3_K_XL` 17.2 GB 或 Qwen3.6 `UD-Q3_K_M` 17 GB。**4-bit（21–23 GB）在 23.4 GB 内存下不要尝试**，会直接换页。17 GB 档要给系统留 5 GB，上下文压到 32–64k。收益来自更均匀的位宽分配，不是"更大的模型"。
- **追求速度**：Tiel `UD-IQ3_XXS` 13.6 GB（16 GB 档最优解）或 `UD-Q2_K_XL` 12.7 GB，配**训练过的** MTP 头（`n-max=1`、`p-min≈0.5`）。省下的 3–4 GB 留给 KV 和系统。作者明确警告 2-bit 会"真的变笨"。

中文/Agent 取向：中文没有任何本地实测支撑，别为"中文优化"的标签买单；Agent 侧只有 Tiel/CyberTiel 有 SWE-bench-Live 与 Claw-Eval 数据，Ornith 只有厂商自测。建议额外留一个原版 Qwen3.6 UD 量化做 A/B 对照——现有证据不支持"任何二创在中文上优于原版"。

## 一句话结论

这个生态里真正改权重的只有蒸馏与去审查两类，APEX/GSQ/Tiel/Nail 本质都是量化加模板；你的瓶颈是 23.4 GB 内存而非 6 GB 显存，而当下的 APEX-MTP 文件极可能带着 8-23 修复之前的坏 MTP 头——先验证它，再谈提速。
