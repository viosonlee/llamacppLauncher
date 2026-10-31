# 参数页统一升级设计方案（草案）

状态：设计草案（未实施）　目标版本：vNext　关联：REQ-032、REQ-106
代码基线：`gui/LlamaManager/MainWindow.Engine.cs`、`Engines/ParameterSpec.cs`、`Engines/StrataBackend.cs`

> **历史文档说明（2026-10-18）**：本文记录 2026-10-17 的设计基线，其中 `strata-upstream`
> 一列/一节已随该后端退役而失效（见 `docs/CHANGELOG-DECISIONS.md` 2026-10-18「退役
> strata-upstream 后端」与 REQ-112）。方案本身已按 P1–P3 实施完毕；现行字段清单以
> `Engines/StrataBackend.cs`、`Engines/LlamaCppParameters.cs` 与 `docs/engine-plugins.md` §6 为准。

---

## 1. 背景与问题

GUI 目前有两套参数界面：

| | 控件来源 | 字段数 | 体验 |
|---|---|---|---|
| llama.cpp | 手写 XAML（`MainWindow.xaml:218`）+ 逐字段帮助（`MainWindow.Parameters.cs:216`） | 16 个控件 | 精选下拉、每字段「?」说明、MoE/MTP 按 GGUF 自动显隐 |
| strata-upstream / polystrata | `ParameterSpec` 自动生成（`MainWindow.Engine.cs:102`） | 10–11 个（随 profile） | 数字=纯文本框、无校验、无推荐值、无单位、帮助为硬编码 switch |

由此产生的具体缺陷（均已核对代码）：

1. `ParameterSpec.VisibleWhen` 已声明但**全仓库无人消费**（`ParameterSpec.cs:19`），
   `expertSlots`（仅 upstream 有效）与 `vramReserveMib`（仅 poly 有效）只能靠构造函数分支控制，
   面板无法表达「A 引擎才显示 X」。
2. 数值参数一律渲染为 `TextBox`，`Min/Max` 只是声明，既没有输入校验也没有推荐值下拉；
   用户填 99999 的上下文要到启动失败才知道。
3. 帮助文案硬编码在 `GetEngineParamDetailedHelp` 的 switch 里（`MainWindow.Engine.cs:202`），
   新增字段必须改 UI 代码，违背「参数由 schema 驱动」的设计（REQ-032）。
4. 面板遗漏引擎真实支持的参数：`prefill`、`maxNew` 只在旧 `StrataConfig` 里读取，
   schema 中没有；`parallel`、`warm_start`、`fit_max_tokens`、`--adapt-async`、
   `STRATA_PIN_BY_NEED` 等 Strata 服务端真实开关完全没有入口。
5. 没有「恢复默认值」、没有搜索、没有分组折叠，字段一多就难用。

### 1.1 为什么不能直接照搬 llama.cpp 的面板

三个引擎的参数集不同（这是**引擎事实**，不是 UI 选择）：

| | llamacpp | strata-upstream | polystrata |
|---|---|---|---|
| 可执行 | `llama-server.exe` | `strata.exe` | `strata-poly.exe` |
| HTTP 前端 | 引擎自带 | `python -m serve.server --config <runconfig>` | 同左 |
| 参数载体 | CLI 参数 | runconfig `args[]` + 顶层键 | 同左 |
| 可调项 | `-c -ngl -ctk -ctv -b -ub -fa -t --spec-type -md --n-cpu-moe --cpu-moe …` | `--native --max-context --pool-workers --mtp --expert-slots --prefill --max-new` + `reasoning_budget_tokens` / `anthropic_thinking` | `--model --max-context --vram-reserve-mib` + 服务端键 |
| 显存策略 | 分层卸载 + KV 量化 | 专家槽位 + CPU 池 + MTP | 动态专家缓存 + 预留 |

把 `-ctk/-fa/-ub` 放到 Strata 面板上，`strata.exe` 会静默忽略——`docs/CHANGELOG-DECISIONS.md:10`
记录过它打印 `ignoring --vram-reserve-mib`。因此正确做法不是复制控件，而是**让 schema 具备
表达这些体验的能力**，再把 llama.cpp 面板也迁进来，最终只维护一套渲染器。

## 2. 目标 / 非目标

目标：

- 三个引擎的参数页在**信息密度、可发现性、防错能力**上与 llama.cpp 面板一致，视觉不跳变；
- 参数定义、帮助文案、显示条件全部回到后端（schema），不改 UI 即可增删字段；
- 为 `strata-upstream` / `polystrata` 补上真实可用但缺失的参数；
- llama.cpp 面板逐步迁到同一套 schema，消除双份维护。

非目标：

- 不改变引擎生命周期、会话身份、runconfig 生成规则；
- 不为参数引入新的持久化结构（继续用 `EngineSettings.Parameters`）；
- 不把 Strata 不认识的 llama.cpp 参数"假装"放上去；
- 不改插件清单 `schema:1` 的既有语义（只做向后兼容扩展）。

## 3. 数据模型：ParameterSpec v2

`Engines/ParameterSpec.cs` 扩展（保持现有构造函数参数顺序，新增可选参数，插件清单与旧调用不受影响）：

```csharp
public enum ParamType { Integer, Decimal, Text, Boolean, Choice, FilePath, FolderPath, Multiline }

public sealed record ParamChoice(
    string Value,          // 真正写进配置/命令行的值
    string Label,          // 展示文案，如 "q8_0 (8-bit)"
    string? Note = null,   // 下拉里的一行说明
    bool Recommended = false);

public sealed record ParameterSpec(
    string Key, string Label, string Group, ParamType Type,
    string? Default = null,
    string? ToolTip = null,              // 悬浮提示（保留）
    double? Min = null, double? Max = null,
    ParamChoice[]? Choices = null,       // 替代 string[]
    string? VisibleWhen = null,          // 新增：布尔表达式
    string? Help = null,                 // 新增：详细帮助正文（替代 switch）
    string? Unit = null,                 // 新增：tokens / MiB / 线程 / 秒
    bool Editable = false,               // 新增：下拉可直接输入数字
    bool Advanced = false,               // 新增：默认折叠到「高级」
    string? RangeHint = null);           // 新增：如 "256–1,048,576，推荐 4096/8192"
```

`VisibleWhen` 取值（刻意做成小而可测的表达式，不引入脚本引擎）：

| 表达式 | 求值依据 |
|---|---|
| `profile==upstream` / `profile==poly` | 后端 profile id |
| `model.moe` / `model.mtp` | `GgufService.ReadShards` 元数据（llama.cpp 的 MoeSection/MtpSection 已用它） |
| `param.expertSlots>0` | 其他参数当前值 |
| `true` / 空 | 恒显示 |

求值器：`Engines/ParameterVisibility.cs`，纯函数 `bool Evaluate(string? expr, ParameterContext ctx)`，
`ParameterContext` 含 `ProfileId`、`GgufMetadata? Model`、`IReadOnlyDictionary<string,string> Values`。
未知变量按 `false`（隐藏）处理并在日志里记一条，绝不抛异常到 UI 线程。

## 4. 渲染器行为（MainWindow.Engine.cs 重写 RebuildEngineParameters）

| 类型 | 控件 | 行为 |
|---|---|---|
| Integer | 可编辑 `ComboBox`（`Editable=true`）或 `TextBox` | 数字键盘过滤；失焦 + 启动前校验 `Min/Max`；越界红框 + 内联错误，阻止启动并聚焦该字段 |
| Decimal | `TextBox` | 同上，按 `InvariantCulture` 解析 |
| Boolean | `ToggleSwitch` | 写 `"1"` / `"0"` |
| Choice | `ComboBox` | 显示 `Label`，保存 `Value`；`Recommended` 项加「推荐」后缀 |
| FilePath / FolderPath | `TextBox` + 「浏览…」 | 沿用现有 OpenFile/OpenFolder 对话框，文件类型按 `Unit` 之外的 `Choices` 过滤 |
| Text / Multiline | `TextBox` | Multiline 自动换行、可拉伸 |

通用规则：

- 分组顺序按后端声明顺序；`Advanced=true` 的字段收进「高级 ▸」折叠区，默认展开到基本组；
- 标签右侧灰字显示 `默认值：X`，鼠标悬停显示 `RangeHint`；
- 每行保留「?」按钮，内容取 `spec.Help`（不再走 switch）；`Help` 为空时回退 `ToolTip`；
- 每个分组标题右侧提供「恢复默认」，顶部提供「全部恢复默认」「搜索框（按标签/Key 过滤）」；
- `VisibleWhen=false` 的字段**隐藏但保留值**，重新满足条件时恢复显示，避免来回切换丢配置；
- 面板底部显示一行「将写入：`strata-runconfig.json` → args: …」，让用户看见参数最终落到哪里
  （llama.cpp 用 `ArgsTextBox` 的同一位置展示拼接后的命令行）；
- 保存时机不变：失焦/启动前写入 `EngineSettings.Parameters`，不触碰运行中会话身份。

线框示意：

```
[引擎] Strata (upstream q36)   可用：…\strata.exe        兼容性：可运行，预计 5.8 GiB
[搜索…]                                                    [全部恢复默认]
─ 基本 ─────────────────────────────────────────────── [恢复默认]
  服务端口        [ 8091 ▾ ]                          (?)
  监听地址        [ 127.0.0.1 ▾ ]  默认 127.0.0.1       (?)
  上下文          [ 4096 ▾ ] tokens 256–1,048,576       (?)
  MTP 草稿数      [ 2 ▾ ]          0–7，推荐 2          (?)
  显存专家槽位    [ 16 ▾ ]         0–256，推荐 16       (?)
─ 高级 ▸ ──────────────────────────────────────────────
  并行请求 / warm_start / fit_max_tokens / --adapt-async / STRATA_PIN_BY_NEED …
─ 路径 ▸ ──────────────────────────────────────────────
  引擎包目录 / 服务源码根 / Python 解释器
```

## 5. 字段清单

### 5.1 strata-upstream

| 分组 | Key | 标签 | 类型 | 默认 | 范围/推荐 | 引擎落点 | 状态 |
|---|---|---|---|---|---|---|---|
| 基本 | servePort | 服务端口 | Integer | 8091 | 1024–65535 | `--port` | 已有 |
| 基本 | host | 监听地址 | Choice | 127.0.0.1 | 127.0.0.1 / 0.0.0.0 | `--host` + runconfig `host` | 已有 |
| 基本 | context | 上下文 | Integer | 4096 | 256–1048576；推荐 2048/4096/8192/16384 | `--max-context` | 已有 |
| 基本 | mtpDrafts | MTP 草稿数 | Integer | 2 | 0–7；推荐 2 | `--mtp` | 已有 |
| 基本 | expertSlots | 显存专家槽位 | Integer | 16 | 0–256；推荐 16/24/32 | `--expert-slots` | 已有 |
| 基本 | prefill | 预填 Token | Integer | 0 | 0–8192；0=引擎默认 | `--prefill` | **补入** |
| 基本 | maxNew | 单次生成长度 | Integer | 0 | 0–65536；0=不限 | `--max-new` | **补入** |
| 高级 | poolWorkers | CPU 池线程 | Integer | 0 | 0–256；0=自动 | `--pool-workers` | 已有 |
| 高级 | reasoningBudget | 思考预算 | Integer | 0 | 0–65536；0=不限 | runconfig `reasoning_budget_tokens` | 已有 |
| 高级 | anthropicThinking | Anthropic 思考 | Choice | model | model / on_request | runconfig `anthropic_thinking` | 已有 |
| 高级 | parallel | 并行请求数 | Integer | 1 | 1–8 | runconfig `parallel` | 待引擎确认 |
| 高级 | warmStart | 预热系统提示 | Boolean | 0 | — | runconfig `warm_start` | 待引擎确认 |
| 高级 | fitMaxTokens | 自动收缩 max_tokens | Boolean | 0 | — | runconfig `fit_max_tokens` | 待引擎确认 |
| 高级 | adaptAsync | 异步专家缓存 | Boolean | 0 | — | args `--adapt-async 1` | 待引擎确认 |
| 高级 | pinByNeed | 按需锁页（Windows） | Boolean | 0 | — | runconfig `env.STRATA_PIN_BY_NEED=1` | 待引擎确认 |
| 路径 | packageRoot / serveRoot / pythonPath | — | Folder/File | 空 | — | 定位 | 已有 |

### 5.2 polystrata

| 分组 | Key | 标签 | 类型 | 默认 | 范围/推荐 | 引擎落点 |
|---|---|---|---|---|---|---|
| 基本 | vramReserveMib | 显存预留 MiB | Integer | 256 | 0–60000；推荐 256/512 | `--vram-reserve-mib` |
| 基本 | context | 上下文 | Integer | 4096 | 256–1048576 | `--max-context` |
| 基本 | kv | KV 缓存格式 | Choice | 空（fp16） | fp16/q8_0/int8/int8k | `--kv` |
| 基本 | cacheBias | 显存专家路由偏置 | Decimal | 0 | 0–2；推荐 0.5；仅 qwen35moe | `--cache-bias`（v0.1.45-poly.6 起） |
| 基本 | conversationCache | 对话驻留缓存 | Choice | 空（关闭） | auto/2048/4096/8192 | `--conversation-cache-mib`（poly.4 起） |
| 高级 | tailSkip | 尾部跳过 | Integer | 0 | 0–64；推荐 6/1 | `--tail-skip` |
| 高级 | tensorCoreMma / polyFlashAttention | 加速开关 | Boolean | 1 | RTX 30+ 默认开 | env `STRATA_GDN_MMA` / `STRATA_FATTN`+`STRATA_FDEC`（poly.6 起引擎默认开） |
| 高级 | parallel / warmStart / adaptAsync / pinByNeed | 同 5.1 | — | — | — | 服务端共用键（待确认） |

`expertSlots`、`mtpDrafts` 在 poly 上通过 `VisibleWhen="profile==upstream"` 隐藏；
`vramReserveMib` 用 `profile==poly` 隐藏 upstream。

### 5.3 llama.cpp（迁移后）

字段与现有 16 个控件一一对应，控件的精选下拉改为 `Choices` + `Editable`：

| Key | 标签 | 类型 | Choices / 说明 |
|---|---|---|---|
| context | 上下文长度 (-c) | Integer, Editable | 4096/8192/16384/32768 |
| ctk / ctv | K/V 量化 | Choice | 默认(f16)/q8_0/q4_0/q4_1/f16 |
| ngl | 卸载层数 (-ngl) | Choice, Editable | all/auto/0… |
| reasoning | 思考模式 | Choice | 默认/off/on/auto |
| threads | CPU 核心数 (-t) | Choice | 1…物理核数（探测） |
| batch / ubatch | 批处理/物理批 | Integer, Editable | 2048/1024/512、512/256/128 |
| verbose | 详细日志 | Boolean | — |
| flashAttn | Flash Attention (-fa) | Choice | 默认/off/on/auto |
| specType | 投机解码 | Choice | 默认/draft-mtp/draft-simple/draft-eagle3/ngram-mod/none |
| reasoningBudget | 思考上限 | Integer, Editable | -1/0/1024/2048/4096/8192 |
| draftModel | 草稿模型 | FilePath | .gguf |
| nCpuMoe | MoE 卸载层数 | Integer, Editable | 0/1/2/4/8/16/32，`VisibleWhen="model.moe"` |
| cpuMoe | CPU MoE | Boolean | `VisibleWhen="model.moe"` |
| mtp | 内置 MTP | Boolean | `VisibleWhen="model.mtp"`，写 `--spec-type draft-mtp` |

迁移期间保留 `ArgsTextBox`（原始参数）作为兜底；两者冲突规则沿用现状：可视控件只改自己的
选项，其余参数与引号原样保留（README「使用」一节）。

## 6. 保存与生效

- 面板值 → `EngineSettings.Parameters[backendId]`（不变）；
- `StrataBackend.BuildRunConfig` 读取新键：顶层 `parallel` / `warm_start` / `fit_max_tokens`，
  `args` 追加 `--adapt-async 1`，`env` 块写 `STRATA_PIN_BY_NEED`（`LaunchPlan.Environment` 已支持）；
- 空值 = 不写该键，交引擎默认（与现有 `if (x > 0)` 风格一致）；
- 面板只影响**下次启动**；运行中会话的进程/命令行不变（AGENTS 约束）。

## 7. 插件清单扩展（向后兼容）

`plugin.json` `schema:1` 的 `parameters[]` 增加可选字段：`help`、`unit`、`editable`、`advanced`、
`visibleWhen`、`choices[].note/recommended`。缺省时行为与现在完全一致；`schema` 仍为 1，
不匹配的旧清单继续可用。`ParameterManifest.ToParameterSpec()` 做映射并单测。

## 8. 实施阶段与工作量

| 阶段 | 内容 | 预估 |
|---|---|---|
| P1 | `ParameterSpec` v2 + `ParameterVisibility` + 渲染器（校验/推荐值/单位/显隐/恢复默认/搜索）+ 帮助文案迁入后端 + Strata/Poly 字段补齐 | 1.0–1.5 人日 |
| P2 | llama.cpp 面板迁到 schema，删除 `MainWindow.xaml:218` 手写块与 `MainWindow.Engine.cs:202` switch；离屏渲染图对比 | 0.5–1.0 人日 |
| P3 | 插件清单扩展、文档（architecture/REQUIREMENTS/CHANGELOG-DECISIONS）、回归补测 | 0.5 人日 |

P1 完成后即可交付「Strata 参数页与 llama.cpp 同样好用」；P2 消除双维护；P3 收尾。

**实施状态**：P1、P2、P3 均已落地。P2 采用「面板由 schema 生成、原始参数串仍为唯一数据源」的做法
（`Engines/LlamaCppParameters.cs`），手写 XAML 块与逐控件同步代码已删除，回归改为按 `Param_<key>`
访问控件，并保留离屏渲染 `logs/ui-regression-*.png` 作为视觉回归。

## 9. 验收标准

1. 三个引擎的参数页字段数与本文一致，`VisibleWhen` 生效（切引擎/切模型即时刷新）；
2. 越界输入被内联拒绝且不写入配置；合法值落盘后重启仍在；
3. 每个字段都有非空帮助（来自后端），帮助内容与引擎 `--help` 不矛盾；
4. 「恢复默认」「搜索」可用；默认值不写入配置，除非用户显式修改；
5. 新增回归用例（预计 10–14 条）：schema→控件映射、VisibleWhen 求值、校验、默认值恢复、
   参数→runconfig 映射（mtp/expertSlots/context/vramReserve/parallel）、插件清单兼容；
6. 离屏渲染回归：3 引擎 × 参数页，与 llama.cpp 页视觉一致；
7. `dotnet build -c Release` 与 `tests/LlamaManager.Regression` 全绿；`docs-gate` 通过。

## 10. 风险与取舍

| 风险 | 处理 |
|---|---|
| runconfig 键随 Strata 版本变化 | 只写已确认键；「待引擎确认」字段默认隐藏，确认后一行配置打开 |
| 隐藏字段保留旧值导致困惑 | 隐藏时在「高级」里提供「另有 N 项不适用于当前引擎」提示，且启动前打印最终 args |
| schema 迁移期两套面板并存 | P2 一次性替换 + 离屏图对比，回归不放行不合并 |
| 校验过严挡住高级用户 | 越界一律拒绝并说明原因，不做静默修正；llama.cpp 的「原始参数」兜底仍可手填任意 flag |
| 插件作者不填新字段 | 全部可选，缺省行为不变 |

## 11. 待确认清单（已闭环）

用真实模型 + `--serve` 实测，判定只看引擎 stderr：出现张量加载横幅即接受，出现
`ignoring <flag>` 即该引擎不认。

1. upstream `strata.exe`：`--tail-skip`、`--adapt-async` 均被标记
   `ignoring … (not an option of this engine)`，不露出；`--expert-slots` 被接受
   （日志 `753 tensors (layout checked)`），保留。poly `strata-poly.exe`：`--tail-skip` 被接受
   （日志确认 `tail skip 4`），`--vram-reserve-mib` 亦被接受（254 MiB free 对比 510 MiB 基线）；
   `--expert-slots` 是硬错误（`unknown option --expert-slots` + usage），只在 upstream 露出。
2. `parallel` / `warm_start` / `fit_max_tokens` 不在 `strata.exe` 的接受范围内（全部 ignoring），
   因此不作为面板字段；若将来要批量并发，应走 `serve/server.py` 把 `parallel` 转成
   `--batch`/`--slots`（`PARALLEL_MAX = 8`），而不是直接发 CLI 标志。
3. `prefill` / `max-new` 在 poly 上未验证有效，故仍只给 upstream；upstream 两者均被接受，
   已从「只读旧 StrataConfig」改为面板字段驱动（旧值作回退）。

**方法论教训**：探测标志必须使用真实模型路径 + `--serve <mode>`。用不存在的模型路径会让引擎
进入 `strata generate` 解析器，输出 `unknown argument: <flag>` 加帮助文本，从而把所有标志都
误判为 REJECTED。

