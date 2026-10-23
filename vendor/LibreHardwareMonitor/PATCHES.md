# PerfCompass 对 LibreHardwareMonitor 的修改

本目录是可独立构建的 LibreHardwareMonitor 库源码及必要资源，不包含上游图形界面项目。上游版本为 **0.9.6**，提交为 `3d331e3370efb858411f19511373eff65a218701`，来源为 [LibreHardwareMonitor 仓库](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701)。PerfCompass 修改版标识为 **0.9.6-perfcompass.1**。

`UPSTREAM.json` 保存引入时原始文件的字节数和 SHA-256，未按修改后的源码重写。`PATCH-MANIFEST.json` 固定该清单的哈希，并逐项列出允许修改、新增的文件及原因。未列出的原文件必须与上游清单一致；缺文件或未列新增文件会使校验失败。`.gitattributes` 禁用 Git 文本换行转换，保留上游原始字节；新检出不会改变 CRLF 文件或清单本身的哈希。`bin/`、`obj/` 是构建输出，不进入源码校验或分发。

修改内容：

- 将构建限定为 .NET 10 / Windows x64，锁定依赖并使用修改版版本号；向 PerfCompass 测试开放内部纯逻辑。
- D3D 查询明确适配器身份，在各退出路径释放句柄；使用经过时间和累计节点计数检查采样间隔、计数变化与拓扑，缺失后重新建立基线。
- NVIDIA、AMD 每轮清空动态观测，失败不能复用上一轮读数；保留 AMD 构造时取得的静态容量及控制设置。NVIDIA 稀疏域按实际索引匹配；跨 API 设备映射以全部 NVAPI 设备的 bus、外部 PCI 型号标识、子系统标识与 Windows 完整 BDF 唯一对应，存在歧义或候选无法读取时保留未知，不假定不同 API 的枚举索引一致，也不猜注册表序号。D3D 映射独立于 NVML；NVML 仅按实际 BDF 查询并核对返回的全部 PCI 标识，失败不丢弃独立的 D3D 数据。Windows BDF 未提供 PCI domain 时不猜非零 domain。
- AMD OD5 活动值必须在 0–100 范围；异常值保留未知，不把大于 100 的返回值截成 100% 重载。
- AMD PMLog 根据实际观察到的提供者时间戳推进判断是否出现新快照，保留同轮其他成功来源；不假定未公开的时间戳 epoch，也不把负载数值相同判为陈旧。
- Intel 集显使用相同的 D3D 节点保护；Intel 独显检查累计遥测的支持标记、单位与时间戳，失败或超过 15 秒的源时间间隔后重建基线。Intel 的 D3D 与 IGCL 关联要求唯一匹配的型号和完整 PCI 总线、设备、功能号；缺少 BDF 或存在多个候选时保留未知，不退化选择同型号的第一张卡，映射也不依赖第一次 D3D 查询成功。

这些修改处理已识别的采样失败和证据范围，不能证明驱动成功返回的数据就是物理真值。实际 NVIDIA 3D 查询中遇到的负数 RunningTime 在本版保留为未知；该观测本身不能证明驱动损坏。纯逻辑和故障注入测试也不替代真实驱动故障、跨硬件或长期稳定性验证。

NVIDIA 映射依据官方 [NVAPI GPU 文档](https://docs.nvidia.com/nvapi/group__gpu.html) 的 `GetPCIIdentifiers` / `GetBusId` 和 [NVML 设备查询文档](https://docs.nvidia.com/deploy/nvml-api/latest/api/group__nvmlDeviceQueries.html)。使用明确标为外部 PCI 标识的 `ExtDeviceId`；不拆解文档未定义位布局的内部 `DeviceId`，不将 NVML 索引等同于 NVAPI 索引。

## 校验和源码包

在 PerfCompass 仓库根目录执行：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/verify-hardware-source.ps1
powershell -NoProfile -ExecutionPolicy Bypass -File scripts/package-notices.ps1
```

第二条命令使用已有还原结果整理所有依赖许可证，不触发构建。修改版源码包写入 `licenses/LibreHardwareMonitor-perfcompass/`，附带 MPL-2.0、上游第三方声明、来源清单、本说明、修改清单、项目和依赖锁。源码包中的 `source-inventory.json` 固定该次实际源码快照的每个文件哈希；外部 `source-package.json` 记录 ZIP 哈希、版本与仓库提交。未提交工作区会明确标注，不能把当前 HEAD 误作已提交源码的证明。

源码包附有独立的 `NuGet.Config`。解压后可使用 .NET 10 SDK 构建库：

```powershell
dotnet restore LibreHardwareMonitorLib/LibreHardwareMonitorLib.csproj --configfile NuGet.Config --locked-mode -r win-x64
dotnet build LibreHardwareMonitorLib/LibreHardwareMonitorLib.csproj -c Release -r win-x64 --no-restore
```

首次还原需要下载锁文件中指定的 NuGet 依赖。包内 `Resources/PawnIo/*.bin` 是构建所需的上游资源，不是应排除的 `bin/` 构建目录。

## 许可

原文件及本修改依照 [Mozilla Public License 2.0](LICENSE) 提供；保留原版权声明。库包含的其他组件以 [上游第三方声明](THIRD-PARTY-NOTICES.txt) 和资源目录内原始声明为准。修改文件的源码随 PerfCompass 分发提供；本清单和哈希用于可追溯性核对，不构成发布者身份认证。
