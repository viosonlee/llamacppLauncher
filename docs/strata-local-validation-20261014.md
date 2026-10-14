# Strata 本机验证：2026-10-14

测试机器：RTX 2060 6 GB、i7-9700、32 GB RAM、Windows x64。
RTX 3060 笔记本只列为兼容范围，本轮没有测试。

## 复现与证据

测试工具：`tools/validate_strata.py`。原始命令、stdout/stderr、提示词、token IDs、
llama.cpp API 响应及结果位于本机被 Git 忽略的
`logs/strata-validation-20261014-fixed-v2/`，汇总在 `summary.json`。
程序记录引擎 SHA256；恢复中断测试时核对提示词、token 数与引擎，所有模型顺序运行。
不会停止已有的 llama-server；测试自行启动的比较服务器在 finally 中清理。

```powershell
python tools/validate_strata.py --llama-root C:\Users\li.feng\.llama --strata-root C:\Users\li.feng\.llama\strata-build --output logs\strata-validation-new
# 在同一 output 上分别运行 --calibrate-occamy、--tune-occamy、--summary-only
```

提示词是带 assistant 前缀、关闭思考的 Python 去重函数问题。两引擎使用完全相同的
31 个输入 token；Strata 的 prefill 统计为 30（末个输入 token 的处理方式不同）。
本轮 tokenizer 的文本文件为 Windows 行尾，直接在 GUI 粘贴 LF 文本会得到不同 token，
因此应以保存的 IDs 复现，不能只比较屏幕上的提示词。

## 同模型比较：Occamy

模型：`Occamy-1.0.APEX-I-MiniPlus-V2.1-Abliterated.gguf`。
40 层、256 个专家、top-8；第 0–9、30–39 层的专家为 Q3_K（GGML type **11**），
第 10–29 层为 IQ3_XXS（type 18）。没有 blk.40 MTP 层。
此前将 type 11 说成 Q4_K 的描述有误：type 12 才是 Q4_K。

配置：Strata `spec=2`、MTP 关闭、reserve=800 MiB、workers=6、int8 KV、context=4096；
llama.cpp build 11149 / d2e54583c，`-ngl all --n-cpu-moe 32 --fit off`、6 线程、q8_0 KV、
context=4096，greedy、禁用 prompt cache。分配策略不同，比较的是这两种实际配置。

| 引擎 | decode 两次实测 (tok/s) | 中位数 |
|---|---|---|
| Strata | 16.06 / 18.68 | 17.37 |
| llama.cpp | 22.38 / 25.31 | 23.84 |

这一对测试的 128 个输出 token 完全一致，两次重复也各自一致。
**该配置下 Strata 比 llama.cpp 慢，不能据此宣称已经超过 llama.cpp。**

后续 profile/线程/显存扫参中，即使原配置也出现了输出分叉，不能把首轮一致推广为
所有运行逐位一致。引擎本身提示 GPU/CPU 专家计算的舍入不同；当前只观察到了分叉，
尚未通过同前缀 logits/路由逐层二分证明具体原因。调参吞吐也可能受生成路径变化影响。

### Profile 与线程/显存扫参

用 Fibonacci、Unicode、数据库索引三个独立问题收集 routing trace，生成了
`occamy-calibrated.bin`。原来的去重问题没有参与校准。

- 交替复测原 profile：17.92 / 17.48 tok/s。
- 校准 profile：17.68 / 18.54 tok/s。
- 差异很小，未证明普遍收益；保留原 profile，不据此替换用户配置。

继续使用原 profile，每个组合两次：

| workers | reserve MiB | decode tok/s |
|---|---|---|
| 6 | 800 | 16.69 / 17.22 |
| 6 | 500 | 18.26 / 18.02 |
| 7 | 500 | 14.05 / 15.02 |
| 4 | 500 | 17.44 / 17.99 |

`workers=6, reserve=500` 在这个短请求上较好，但仍未追上 llama.cpp。
7 个 worker 更慢，不能把“线程越多越快”作为调参依据。
一次 6/500 的详细统计：55.489 ms/token，CPU pool 13.278 ms/token，
verify 窗口 wait-for-rings 33.944 ms/round。下一轮应定位 ring 等待、PCIe 供数与调度，
不能仅凭较大的 CPU pool 数字就归因到 IQ dot kernel。

## MTP：Compact 固定输入

模型：`Qwen3.6-35B-A3B-APEX-MTP-Compact.gguf`，使用其匹配的 `mtp-apex-compact`。
`STRATA_MTP_PLAIN=1`、`--suffix-draft 0`，不传 `--mtp-max-t`。
输入相同，最大生成 128；模型在 118 token 时输出 EOS。

| 配置 | decode 两次实测 (tok/s) | 接受率 |
|---|---|---|
| 无 MTP，spec 2 | 14.42 / 19.54 | — |
| MTP，spec 2 | 32.32 / 29.25 | 0.857 / 0.857 |
| MTP，spec 3 | 27.40 / 35.94 | 0.783 / 0.783 |
| MTP，spec 4 | 31.37 / 36.98 | 0.626 / 0.650 |

这组测试中，所有配置的 118 个输出 token 都与无 MTP 基线一致。
spec 4 接受率较低却吞吐更高，优化目标应是端到端速度，不是接受率单项。
0.857 仅是此提示词的结果，没有实现或证明通用的“接受率提升至 0.85”。
runtime 的主要投影原本已经是 Q8_0，不能把“改用 Q8_0”当作尚未实施的优化。
greedy 下也不应凭空引入温度校准来解释接受率。

## Kernel 数值验证

在 `strata-build/apex-on-strata` 的 `build-release` 编译并运行：

- `iq_parity`：10 种量化格式，32×2048，1–8 列；包括 Q3_K、IQ2_S、IQ3_XXS。
  权重解量化相对误差均为 0；这三种 MMVQ 相对 FP32 激活参考的误差约 0.50–0.54%，
  包含预期的 Q8_1 激活舍入，全部通过既有 2% 门限。
- `iq_multi_parity`：新增 2048/512 的 IQ2_S/IQ2_S 和 IQ3_XXS/IQ3_XXS grouped 专家路径；
  新旧 kernel 逐位一致，包含空组、多列与未写区域，共 0 failures。
- `prefill_mmq_kquant_test`：新增真实 2048/512 几何的 Q3_K gate/up 与 down；
  Q3_K 相对 L2 误差 0.00049–0.00145，全部通过既有门限。
- Compute Sanitizer memcheck 对扩展后的 MMQ 专项报告 **0 errors**。

专项初次失败来自测试夹具：默认 stream 的 memset/memcpy 与 nonblocking 测试 stream
没有排序，且 row-ID 缓冲没有预留 MMQ tile 的尾部读取空间。修复初始化 stream 和尾部后通过。
这不是通过放宽误差门限掩盖引擎错误，也不表示量化推理对全精度模型“无损”。

## GUI 与生命周期

新增 pack、profile、MTP runtime 路径选择；Spec=2–8、MTP 开关、Plain RMSNorm 设置。
子进程使用 ArgumentList 保留空格/中文路径；settings 保存保留实时 PID/创建时间/引擎路径与统计。
运行在分词前获取跨实例文件锁，取消会终止当前分词器或引擎并等待退出，非零退出码不被输出行掩盖。
修复 `output  :` 带空格标签的解码，UTF-8 跨 token 字节在组合后再解码。

回归通过 29/29，其中新增实际子进程成功、失败、并发拒绝、两阶段取消、取消后重启、
stale settings 不覆盖运行身份，以及 MTP 参数/环境与中文路径检查。
显式调用真实 `StrataService.RunAsync` 的 Occamy smoke 也完成：18.03 tok/s，成功返回完整中文/文本解码和统计。
该 smoke 使用独立 `LLAMA_MANAGER_ROOT`，未覆盖用户配置。

GPU 测试结束后通过共享 C# CLI 恢复 Hy-MT2 服务，8080 `/health` 返回 `ok`。

最终 `dotnet build LlamaManager.sln -c Release` 为 0 warnings / 0 errors，回归 29/29。
本轮可执行文件发布在 `dist/strata-reviewed/LlamaManager.exe`。
