# PerfCompass

Windows 原生电脑占用监测与购机配置分析工具。C# / .NET 10 / WPF；数据保存在本机。

双击根目录 `启动.cmd`。通过验证的新版本位于 `release-next/`，启动器优先使用它，并继续读取 `release/data/` 中的历史。已经运行的旧版本需要正常退出后再启动。发布版自带运行时，不依赖 Python 或预装 .NET。

GitHub 上的源码不包含 EXE。可从 [Windows 构建流程](https://github.com/Southwall-Tester/PerfCompass/actions/workflows/windows.yml) 中成功的运行下载 `PerfCompass-Windows-x64` 产物，解压后运行 `PerfCompass.exe`。当前为 0.3.0 试用版，尚未完成商业级验证。

![近期任务变化分析](docs/images/recent-demand.png)

上图使用隔离的合成测试轨迹，演示显卡过去低负载、最近一周新增重任务时的分析结果，不代表截图所在电脑的真实购机结论。

## 使用

1. 正常使用电脑，按实际任务切换右上角场景。
2. 在“配置分析”查看处理器、内存、显卡和存储的压力与余量。
3. 累计有效使用至少 10 小时、覆盖 3 天，完成常用任务后，在设置中确认记录具有代表性。
4. 导出 HTML 报告或 CSV 原始记录，供下次选机时比较。

在实时页展开“记录体验”，可保存卡顿或等待描述及此前两分钟采样线索；历史页可查看并标为已处理。未处理的近期体验问题会约束降档建议。暂停期间不关联旧采样；同时出现高占用不能证明部件是原因。

设置页的需求描述支持有限的规则预解析：列出任务、否定与未来线索，AI 描述还需核对本地或云端方式。用户确认保存后，字段才参与建议；修改原文会取消旧确认。当前规则不支持通用自然语言，可能遗漏条件、时间与数值关系。未识别片段保留原文，不悄悄转成硬件要求。论文、开源方案和 36 条广度诊断见 [NLP_RESEARCH.md](docs/NLP_RESEARCH.md)。

默认关闭窗口会收起到系统托盘并继续采集；从程序或托盘菜单退出才停止记录。可在“记录设置”更改关闭行为，并主动启用登录 Windows 后记录。暂停时停止采集与保存。程序不默认修改开机启动，不上传遥测。

## 数据与分析

- Windows API 采集每逻辑核心 CPU、物理内存、系统提交量；LibreHardwareMonitor 采集可用 GPU 和温度数据；Windows 性能计数器采集磁盘活动。
- 实时约 2 秒刷新，应用列表约 6 秒刷新，历史约 10 秒记录。历史保存有效时段平均占用，同时保留采样中的内存、显卡和最忙核心峰值，以及 CPU、GPU、内存高负载的采样持续时间。空闲时段不用于配置过剩判断；明显后台任务仍计入有效使用。休眠与暂停间隔不当作有效时长。
- 降档判断需要代表性确认、有效时长、跨日覆盖以及传感器覆盖率；缺失值保留未知，不当作零。
- 可按场景及最近 7/14/30/90 天分析；P95 按有效采样时长加权。另行对照最近 7 天与此前 7 天、最近 30 天与此前 30 天，避免大量历史闲置掩盖近期新任务。近期负载增加会约束降档建议；近期减少不会自动抹去过去重任务。缺少基线时继续观察。
- 历史充足也需要最近 7 天至少 30 分钟、跨 2 天的有效观测才能判断当前冗余；具体指标还需近期传感器证据。完整阈值与边界见 `docs/DEMAND_MODEL.md`。
- CPU 结合总体与最忙核心负载；内存保留采样峰值、提交量并设置容量余量；GPU 低负载输出待验证候选条件；磁盘容量和性能分开讨论。
- 10 小时、3 天、P95 等为透明启发式设置，尚未完成跨机型校准；采样可能漏掉短峰。具体型号比较需要候选硬件上的实际任务测试。进程名称只实时展示，不写入历史。
- 同一数据目录中的不同硬件配置按指纹隔离。指纹来自硬件型号与容量，不包含设备序列号。

通过根目录启动器运行时，数据位置为 `release/data/`；直接运行 EXE 默认使用 EXE 旁的 `data/`，也可通过 `--data-dir` 指定。`settings.json` 保存场景、设置与已确认需求，`samples-YYYY-MM-DD.jsonl` 保存采样，`experience-YYYY-MM-DD.jsonl` 保存体验及处理状态，`errors.log` 保存错误。新设置默认保留 90 天，可选 30/60/90 天；旧设置中的 30 天不会被自动改变，已清理的历史无法恢复。报告由用户主动选择保存位置。

## 开发与验证

需要 .NET 10 SDK。项目目录 `.tools/dotnet/` 存在时构建脚本优先使用该 SDK。

```powershell
.\build.ps1 -Test -Publish -OutputDirectory release-next
```

分析与持久化回归结果写入 `artifacts/self-tests.txt`。`PerfCompass.exe --diagnose --data-dir <目录>` 采集一次真实硬件快照到 diagnostics.json，返回码 0 表示 CPU 和内存基础采集成功；不表示所有传感器或推荐逻辑已通过硬件验证。

`--ui-test --data-dir <空测试目录>` 在隔离目录运行原生界面与采集交互测试，并保存 WPF 实际渲染图片；填充分析页使用合成轨迹，不是本机配置结论。`--smoke-test --minimized --data-dir <测试目录>` 进行后台运行与资源占用测量。详见 `docs/VALIDATION.md`。

卸载：如开启过开机记录，先在设置中关闭并保存；退出程序，然后删除发布文件夹。需要保留历史时先复制 `data/`。

## 开源基础与调研

- [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor)：C# 硬件传感器与可嵌入库，MPL-2.0。
- [System Informer](https://github.com/winsiderss/systeminformer)：Windows 系统与进程监测，MIT。
- [PresentMon](https://github.com/GameTechDev/PresentMon)：C++ 图形帧时间采集，MIT；后续图形场景验证的候选组件。
- [Headroom](https://github.com/cwooddgr/headroom)：Swift / Apple Silicon 使用监测与购机建议。当前代码的内存建议为维持或升级，CPU/GPU 的低负载规则可建议基础档位。规模小，作为思路参考。
- [ZabbixFinOps](https://github.com/Lfijho/ZabbixFinOps)：服务器冗余分析模块，适用于服务器而非个人购机。

本项目的采集、分析、界面和报告属于独立实现。依赖许可与分发说明见 `THIRD-PARTY-NOTICES.md`。
