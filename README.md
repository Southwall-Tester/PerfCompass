# PerfCompass

Windows 原生电脑占用监测与购机配置分析工具。记录常用和偶发任务，结合近期需求、体验与候选机结果，判断下一台电脑值得比较哪些配置，同时保留不足与冗余的依据。低平均占用不能直接证明配置买高了。

当前冻结版本为 **0.8.34**，分析方法 1.15，比较文件 `comparison-1.4`。实际启动版本以 `release-next/READY` 或程序左下角为准。[验证记录](docs/VALIDATION.md)包含冻结源码、EXE 摘要、本机流程与对应 CI；当前仍为开发试用版，商业成熟度尚未完成。

## 启动与数据位置

| 使用方式 | 启动入口 | 默认数据目录 |
| --- | --- | --- |
| 本机现有项目 | 双击根目录 `启动.cmd`，优先运行已标记 READY 的 `release-next` | 根目录 `release/data` |
| 独立便携包 | 完整解压后运行 `PerfCompass.exe` | EXE 旁的 `data` |
| GitHub 源码 | 先按下文构建，再运行发布目录中的 EXE | 所运行 EXE 旁的 `data` |

发布包自带运行时，不需要安装 Python 或预装 .NET，也不自动申请管理员权限。本机当前便携包为 `artifacts/PerfCompass-current-Windows-x64.zip`，后续覆盖更新这个当前包；校验文件为同目录 `SHA256SUMS.txt`。

GitHub 源码不包含 EXE。可在[成功的 Windows 构建运行](https://github.com/Southwall-Tester/PerfCompass/actions/workflows/windows.yml)中下载 `PerfCompass-Windows-x64` 产物，完整解压后运行。选择与需要版本对应的成功运行；CI 只证明该提交执行了相应流程，不证明与本机发布 EXE 逐字相同。

升级前先确认正在使用的数据目录，备份数据并正常退出旧实例。新包先解压到独立目录，不覆盖运行中的 EXE；切换目录或回退格式时按[支持范围与升级](docs/SUPPORT_SCOPE.md)操作。关闭窗口默认可能继续在托盘记录，停止运行请使用“退出程序”。

## 从实际任务走到候选取舍

1. 正常使用电脑，按实际任务切换场景；出现卡顿或等待时记录体验，保留常用和偶发重任务。
2. 填写近期新增需求，核对任务、时间、数量、否定与触发条件。过去闲置不能抵消最近新增的重任务；修改原文或事项后需重新核对。
3. 在“配置分析”查看四部件配置草案、近期与较早时期的观测，以及每项依据。读数缺失保持未知；初步比较方向不等于候选已经达标。
4. 在“候选配置”添加一至三套具体整机。可从[规格摘录](docs/SPECIFICATION_IMPORT.md)或[支持的商品链接](docs/PRODUCT_PAGE_IMPORT.md)生成预览，核对准确 SKU、容量、独立规格来源和报价来源，再查看自动初筛。
5. 把能够改变选择的实际任务带到候选机，用同一[测量方案](docs/TASK_MEASUREMENT.md)复测。同一会话刚完成的测量可直接到候选页核对并填入所选行；其他电脑的原始结果通过 JSON 导入。核对机器、步骤、产物与可比性后，再比较预算和加价收益。

完整流程见[一次购机核对](docs/BUYING_SESSION.md)。必需条件与不同条件场景分别比较；没有报价也可以先核对规格和任务，缺少报价来源的金额不参与低价或价差计算。容量、硬件型号和旧机利用率不会被换算成候选机性能。

## 当前能力与边界

- CPU/最忙逻辑核心、内存峰值与提交量、GPU 执行引擎和磁盘线索分别保留覆盖与来源。初步观测、繁忙线索和正式余量结论使用不同条件，见[分析方法](docs/DEMAND_MODEL.md)。
- 历史按硬件配置、场景和所选窗口分析；近期新增需求、体验问题与较早重任务不会被大量轻载记录自动抹去。损坏、重叠、来源混合或时间中断保留异常与未知。
- 实际原生入口已读取三款 Dell 官网 SKU，支持范围见[商品链接导入](docs/PRODUCT_PAGE_IMPORT.md)。联想两款已尝试页面读取失败，仍需规格摘录；页面标示有货也不保证地区配送、结算或任务性能。
- 自然语言规则只有有限预解析能力，需要逐项核对。可选模型默认关闭，不内置或自动启动模型，不自动选择收费服务；模型语义质量仍未通过独立验收，见[研究记录](docs/NLP_RESEARCH.md)与[可选接口说明](docs/MODEL_REQUIREMENTS.md)。
- 尚缺真实候选机的同任务收益、独立用户验证、更广设备与 DPI、代码签名等直接证据。独立 0.8.29 八小时试验仍在运行，不作为 0.8.34 长期稳定性证明。缺口见[商业级验收账本](docs/RELEASE_READINESS.md)。

## 本地数据与恢复

采样、设置、体验、候选草稿和测量保存在所选数据目录，不自动上传。可选模型仅在主动解析时向所配服务发送需求文字；商品读取仅访问所填的支持网址，不发送使用历史。

`settings.json` 保存设置与已确认需求，`samples-YYYY-MM-DD.jsonl` 保存采样，`experience-YYYY-MM-DD.jsonl` 保存体验，`comparison.json` 保存候选与核对，`measurements/` 保存原始任务测量。新设置默认保留 90 天采样，可选 30/60/90 天；旧设置不自动改变，候选和测量不随采样保留期清理。

在“记录设置 → 数据保护”备份、预览和恢复。导出到数据目录外的草稿或测量需另外保管。读取损坏、保存失败或清理日期待核对时，按界面处理并保留唯一内容；不要为让结果变绿而删除原始失败或勾选未核对条件。具体范围见[数据保护](docs/DATA_PROTECTION.md)。

分享日志或报告前自行检查需求、备注、参数与路径。卸载前关闭并保存开机记录设置、退出程序，再移除发布目录；需要保留的数据先备份。

## 从源码构建

使用 `global.json` 指定的 .NET SDK（10.0.402 及其允许的补丁版本）。构建脚本优先使用项目中的 `.tools/dotnet`，缺少时使用 PATH 中的 `dotnet`。产品采用 C# / .NET / WPF，不依赖 Python 运行。

```powershell
.\build.ps1 -Test -Publish -OutputDirectory artifacts\candidate
```

发布后可直接运行 `artifacts/candidate/PerfCompass.exe`。独立输出目录用于保护现有使用目录；构建自测通过不能代替该发布 EXE 的原生入口、任务和长期验证。现有检查入口与证据要求见[验证记录](docs/VALIDATION.md)及[进程监督说明](docs/VALIDATION_SUPERVISION.md)。不要将测试数据写入用户数据目录。

完成发布包验收、记录版本与摘要并标记该目录的 `READY` 后，使用已验收目录打包：

```powershell
.\scripts\package-portable.ps1 -SourceDirectory artifacts\candidate
```

默认更新 `artifacts/PerfCompass-current-Windows-x64.zip` 与同目录校验文件。打包会拒绝用户数据、模型和非分发文件，并核对包内 EXE 摘要；压缩成功本身不是功能验收通过。

## 开源基础

采集复用固定上游版本的 LibreHardwareMonitor。修改清单、可对应源码和许可证随包提供，见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)；补丁出处、GPU 来源和单文件构建边界见[随包补丁说明](licenses/LibreHardwareMonitor-perfcompass/PATCHES.md)。GPU 任务工具为显式运行的开发工具，见[源码说明](https://github.com/Southwall-Tester/PerfCompass/blob/main/tools/GpuWorkloadCheck/README.md)，不会自动成为用户需求或购机收益证据。

各版本变化、失败与验证范围统一保留在[验证记录](docs/VALIDATION.md)和 Git 历史中。
