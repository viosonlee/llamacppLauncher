# 2026-10-15 验证记录：引擎插件化

- .NET SDK 8.0.425，Windows x64。Release 解决方案构建：0 警告、0 错误。
- 回归程序：**47 项通过，0 失败**（原 36 项 + 新增 11 项引擎插件化用例）。

## 新增覆盖

- 引擎门面：`llamacpp` 后端经 `ServerService` 启动/停止假服务器、身份映射一致；嵌入服务同门面。
- 注册表与仲裁：后端解析、默认后端、GPU 占用与冲突判定（含仅嵌入会话不占用主 GPU 槽位）。
- 配置迁移：旧 `RunningServer`/`RunningEmbedding` + PID 提示折叠进 `Running` 槽位；保存后旧键消失；空配置不臆造会话。
- Strata 后端：`python -m serve.server --engine strata --config <json> --port N` 命令行与生成的 runconfig 字段；解析器对缺失引擎/源码诚实报错。
- 插件：清单解析/校验、`{model}`/`{port}`/参数模板替换、内置 id 冲突拒绝。
- 兼容性：三个后端对同一 APEX fixture 与缺失模型的判定 + 资源估算。
- CLI：`--backend` 路由、未知 id 报错、`--help`、bootstrap 依赖读取（引擎 `requirements.txt` 优先 + 回退）。
- WPF：6 个导航页、引擎控件存在、离屏渲染 6 张 PNG（`logs/ui-regression-*.png`）。

## 边界

- 引擎探测（`--version`）有界超时并在超时后强制结束子进程，避免探测卡死 UI。
- 回归使用隔离 `LLAMA_MANAGER_ROOT` 与本地假服务器/假 `serve/server.py`，不下载 GPU/模型，不停止用户真实服务。
- 真实 GPU 推理（Strata/PolyStrata 端到端）与登录自启动行为仍需在目标机器单独验证。

---

# 2026-10-03 验证记录

- .NET SDK 8.0.425，Windows x64。
- Release 解决方案构建：0 警告、0 错误。
- 回归程序：22 项通过，0 失败。包括真实 Windows 子进程、跨进程并发启动、
  选择其他引擎/端口后的停止、启动取消、PID 创建时间校验、配置损坏恢复、
  1,000 组引号随机往返、GGUF 截断输入、设备检测缓存及流式测速。
- WPF 控件测试覆盖 off/auto、预算 0/-1/默认、独立 -t/-tb、独立 K/V 缓存，
  以及可编辑下拉框的事件时序。两个修改面板已离屏渲染检查。
- Windows x64 自包含单文件发布成功；发布 EXE 的 help/service/status/stop
  及 Windows PowerShell 5.1 包装脚本通过隔离测试，覆盖带空格模型路径。
- Sakura 转换工具 self-test：2,097,152 个权重通过 Prism 原生解码器逐位比较。
- Git whitespace 检查通过。

本次 launcher 验证使用模拟模型服务器，没有切换用户的模型或重新测量 GPU 推理吞吐。
登录自启动的注册表写入、真实注销/登录和旧计划任务权限未自动操作；
相关代码统一到已测试的 `--service` 启动路径，仍需部署环境验证登录行为。
