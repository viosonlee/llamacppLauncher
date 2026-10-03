# Sakura 的 CUDA 兼容副本

`Sakura-Micro-Bonsai-2-GSQ-RCO-5.3GiB.gguf` 混用了上游的 `TQ1_0`
（每 256 个权重一组）和 Prism 的 `Q1_0`。Prism `prism-b10743-adfffbe`
支持 `PTQ1_0` 的 CUDA 计算，但不支持 `TQ1_0` 的 CUDA 计算。
因此即使 `-ngl all` 报告所有层已卸载，主要权重仍可能留在 CPU。
应检查实际 `CUDA0 model buffer size`，不能只看 offloaded 层数。

`repack_sakura_cuda.py` 将每个 TQ1_0 块拆成两个 PTQ1_0 块：

- 保留全部三值编码，只调整打包顺序。
- 将原始 FP16 缩放系数的两个字节原样复制到两个新块。
- 保留其他张量及所有 GGUF 元数据，包括 Hadamard 参数和聊天模板。
- 用已安装 Prism DLL 的两个独立原生解码函数逐块核对所有转换权重，
  比较解码后 float32 的位模式，包括正负零。
- 写出后重新读取所有张量校验 SHA-256，并生成 `.verification.json`。
- 拒绝覆盖已存在的输出，原始模型以只读方式打开。

这是无损格式转换，不是重新量化，也不代表 CPU 和 CUDA 的推理结果必然逐字一致：
不同计算内核的浮点累加顺序仍可能不同。

## 使用

需要 Python、NumPy，以及 Prism 发布包中的 `ggml-base.dll`。
先验证转换器：

```powershell
python tools/repack_sakura_cuda.py --prism-dll ../engines/prism/ggml-base.dll --self-test
```

生成兼容副本（请调整路径）：

```powershell
python tools/repack_sakura_cuda.py `
  --prism-dll ../engines/prism/ggml-base.dll `
  --input C:/AIModel/lmstudio-community/Sakura-Micro-Bonsai-2-GSQ-RCO-5.3GiB.gguf `
  --output C:/AIModel/lmstudio-community/Sakura-Micro-Bonsai-2-GSQ-RCO-CUDA-PTQ1_0.gguf `
  --expected-sha256 5054d9b3b3b95145ebf1def33803fd93a1962dda37ca50bd53dbcbbbe4655eb3
```

只有脚本成功退出并生成验证报告后，才使用新文件。转换中断时应将未完成输出视为无效。
在启动器中选择名称带 `CUDA-PTQ1_0` 的副本，仍然使用 Prism 引擎。
显存不足时可使用 `-ngl auto --fit on --fit-target 512`，并控制上下文和 batch。
不要把带 Hadamard 元数据的模型交给不支持其变换的普通 llama.cpp。

## 本机验证结果（2026-10-02）

- 原文件：5,686,593,888 字节；兼容副本：5,890,943,328 字节。
- 转换 394 个张量，逐位验证 26,156,728,320 个权重，另外 457 个张量原样保留。
- 副本 SHA-256：`b5ff5e700f55f4bd4f4da8c6b8460d14bb61f59fd50c4c73f908919c7a20cf23`。
- RTX 3060 Laptop 6 GB：自动卸载 54/65 层，GPU 权重 4458.01 MiB，
  CPU 权重 1149.43 MiB，`nvidia-smi` 显存占用 4875 MiB。
- 4K 上下文，`-b 512 -ub 128 -ngl auto --fit on --fit-target 256`，
  Flash Attention，K/V 均为 q4_0；自动分配层数会随可用显存变化。
- `/health` 返回 `ok`；算术回答正确；中文短句测试正常结束，37 个输出 token，
  约 5.79 token/s。这是一次短请求测量，不代表所有提示词的速度。
- 本次验证保留了原始文件。这是当时的测试记录，不代表启动器目前选择的模型。

格式参考：

- [Sakura 模型说明](https://huggingface.co/webmp3/Sakura-Micro-Bonsai-2-GSQ-RCO)
- [Prism 固定版本类型定义](https://github.com/PrismML-Eng/llama.cpp/blob/adfffbe41/ggml/include/ggml.h)
- [Prism 三值编解码](https://github.com/PrismML-Eng/llama.cpp/blob/adfffbe41/ggml/src/ggml-quants.c)
