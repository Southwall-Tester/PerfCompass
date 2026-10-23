# GPU 编解码独立记录工具

仅显式执行此工具时启动人工编解码任务。它是无产品项目引用、无额外 NuGet 依赖的 .NET 10 x64 控制台程序，不修改 PerfCompass 版本或用户数据。

编译后传入四个**绝对路径**，顺序如下：冻结的 `PerfCompass.exe`、`ffmpeg.exe`、`nvidia-smi.exe`、`artifacts` 下的新空输出目录。`ffprobe.exe` 必须位于 FFmpeg 同目录。例如：

```powershell
& 'C:\path\GpuWorkloadCheck.exe' `
  'C:\path\PerfCompass\release-next\PerfCompass.exe' `
  'C:\path\ffmpeg\bin\ffmpeg.exe' `
  'C:\Windows\System32\nvidia-smi.exe' `
  'C:\path\PerfCompass\artifacts\gpu-workload-unique-run'
```

已有非空目录、链接路径和 `data` 目录下的路径会被拒绝。工具只停止自己启动并持有的进程及其进程树；不会按名称清理其他 FFmpeg、PerfCompass 或持续验证进程。Ctrl+C 会标为取消并清理自有进程；强制结束工具自身则不能保证执行其清理逻辑。

流程为单张 NVIDIA 身份查询、CUDA UUID/BDF 核对、CPU `libx264` 生成 5 秒 1080p60 种子视频，然后启动冻结产品的 90 秒遥测和 500 毫秒 SMI 查询。阶段依次为 15 秒基线、20 秒 NVENC 编码、10 秒恢复、20 秒 CUVID 解码、10 秒恢复。FFmpeg 以 UUID 限定可见 CUDA 设备，再选择 ordinal 0。工作阶段不限制实时帧速率；编码设置约 128 MiB 的 `-fs` 输出保护，触发早退也会标为失败，不计为完整阶段。正常阶段末发送 `q`，超过 5 秒未退出则仅结束自有进程树并标异常。

FFmpeg 列出 `h264_nvenc` 并不证明当前驱动能运行该构建。若编码日志报告 NVENC API 版本不兼容，保留失败记录，改用与现有驱动兼容的独立工具副本后在新目录重试；本工具不会升级驱动或修改系统 PATH。软件回读只处理本次生成的视频。

产品进程从启动起最多等待 150 秒；记录完成后停止 SMI，使用 FFprobe 清点输出帧，再软件解码至多前 5 秒媒体验证可读。恢复从确认的工作进程退出开始计算，不将日志排空时间计入编码或解码。阶段包含初始化和退出排空，实际有效编码时长可能短于 20 秒；分析时应按各条采样区间筛选并排除边界，而非假定每个计划阶段都已有足够稳态样本。

`result.json` 保存状态、身份、`Phases` 及所有自有进程的路径、SHA256、参数、PID、启动/退出、首个与最后一个进度帧、停止原因。每个进程另有 `.process.json`、stdout/stderr 原文与带 UTC/经过时间的 `.received.jsonl`。产品原始记录在 `data/validation/telemetry-cross-check.json`。这些文件包含本机路径及人工负载记录，只留在本机 `artifacts`，不提交仓库。

记录结束后，在仓库根目录执行 `powershell -NoProfile -File scripts/summarize-gpu-workload.ps1 -RunDirectory <本次目录>` 生成 `analysis.json`。脚本只读取固定文件，不执行记录内的参数；保留输入摘要、实际进程结果、各阶段完整采样区间和不同来源，输出去除了本机绝对路径及 GPU UUID。每段前后剔除 2 秒，工作段另按首帧与退出请求收紧范围；`N/A`、`[Unknown Error]`、缺失和部分引擎读数均单独保留。失败且没有完整遥测文件的记录不能运行全量阶段分析，原始失败日志仍有效。

退出 0 与 `recording-completed` 仅表示记录流程完成、产物有可读帧、遥测 EXE 摘要及显卡型号一致。工具不证明生产采集选中的物理 GPU：同卡身份须另用本次同版本生产匹配探针核对。GPU 引擎有效样本数另列，缺失不填零；逐阶段响应、恢复及来源差异由后续分析核对。SMI 与生产驱动读数不是独立物理真值，完成此工具不能证明 GPU 准确性、购机结论、商业成熟度或长期稳定性。失败、取消、日志保存或清理失败均返回非零，原记录保留。
