namespace LlamaManager.Services;

public static class StrataParameterHelp
{
    public const string Text = """
        新 GGUF 怎么用
        1. 选择包含 engine/ 和 tools/ 的 Strata 完整运行包。
        2. 选择 GGUF；分片模型选第一片，其他分片放在旁边。
        3. 点击“一键生成 / 复用 Pack”。首次需要 Python 3.10+；缺少 numpy、regex、gguf 时会联网安装到独立环境。
        4. 成功后自动关联 Pack 和初始 Profile。原 GGUF 必须保留；只移动同一份文件可重新选择已有 Pack。
        5. 换量化、微调或模型变种需要对应的 Pack；当前入口仅接受受支持的 qwen35moe 40 层主干。
        选择模型或恢复已保存的模型后会读取全部分片进行兼容性预检；状态行的提示框可查看完整报告。
        打包前会再次校验，报告也会写入 Pack 日志。预检只读取头部，不读取权重或运行 GPU。
        Pack 生成不使用 GPU，也不会停止正在运行的模型服务。停止按钮可以取消准备任务。

        Pack 复用条件与存放位置
        Pack 按模型文件 + 打包工具的指纹复用。模型放在本地硬盘：OneDrive / 坚果云等同步盘会频繁
        改动文件时间戳，导致缓存反复失效、重复生成。缓存位置默认在启动器 data\strata-packs，
        生成中断只留临时目录，不会破坏已有 Pack；同一模型反复生成会产生带后缀的副本，可手动清理。

        推荐起始配置（6GB 显卡）
        显存预留 800 MiB；上下文 4096；生成 128；Prefill 2048；KV int8；Spec 2。
        8 核 CPU 可以从 6 个 pool worker 起步；0 表示引擎自动决定。
        24GB RAM 笔记本可以将锁页 RAM 上限设为 12 GiB。这组值是起点，不代表已在笔记本实测。

        显存预留 --vram-reserve-mib
        为 KV、临时缓冲和桌面留空间。降低到 500 可以增加驻留专家，但占用更满并不保证更快。
        加载失败/显存不足时增大；不要只以任务管理器达到 95% 为目标。

        上下文 --max-context 与生成 --max-new
        上下文容量要覆盖输入 + 输出。上下文增大会多占 KV 显存并挤压专家缓存。
        最大生成是输出 token 上限，不是字数；首次 128，长回答可改 256/512。

        Prefill --prefill
        提示词批处理分块；2048 是起始值，临时显存不足时降到 1024/512。它不是总上下文长度。

        KV --kv
        int8：推荐的内存/精度折中。fp16：占用更大。k8v4：K 8bit / V 4bit。q4_0：更省显存。
        不同 KV 量化不保证逐位相同；先用 int8 建立基线。

        CPU 专家池 --pool-workers / --pool-affinity
        线程数不应按逻辑线程全部填满。0 自动；auto 亲和通用，all 使用物理核心，p-cores 面向混合架构 CPU。
        主机还需要处理调度和 PCIe 传输，更多 worker 有时反而更慢。

        Spec --spec 与 MTP
        native pack 需要 2-8 的验证窗口；建议 2。
        MTP 开启时，spec 2 最多起草 1 个 token，spec 4 最多 3 个。接受率下降不一定代表吞吐下降。
        无草稿时先关闭 MTP；本面板关闭 suffix draft，保留验证窗口不等于启用 MTP。
        MTP 选择的是含 dense.bin、dense.txt、experts.bin 的 runtime 目录，不是另一个完整 GGUF。
        APEX/Qwen 草稿通常使用 Plain RMSNorm。跨模型借用草稿需要实测；一键生成 Pack 不会生成 MTP runtime。

        锁页 RAM STRATA_ARENA_PIN_GIB
        这是主机内存上限，不是显存。0 让引擎自动决定；24GB RAM 可从 12 GiB 起步。
        锁页太多会压缩系统/其他程序内存，太少可能降低专家供数效率。

        Profile 与提示词
        初始 Profile 只是均衡的专家缓存排序，生成后即可运行；不是为你的工作负载训练的最优缓存。
        目前提示词原样分词，不自动套聊天模板；需要时粘贴该模型的完整模板。
        推理时 Strata 与 llama-server 共享显存；启动器会在停止主服务前询问。
        """;
}
