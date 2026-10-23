# 第三方组件

- LibreHardwareMonitorLib **0.9.6-perfcompass.1**：基于上游 0.9.6 的源码修改版，依照 Mozilla Public License 2.0 提供。上游源码为 https://github.com/LibreHardwareMonitor/LibreHardwareMonitor 。修改涉及 GPU 查询失败后的缺失处理、计数基线、设备身份及源码构建配置；详情见 `vendor/LibreHardwareMonitor/PATCHES.md`。其第三方组件保留原仓库 notices。
- BlackSharp.Core、DiskInfoToolkit、RAMSPDToolkit-NDD：MPL-2.0，随 LibreHardwareMonitor 引入，未修改源码。
- HidSharp：包内 LICENSE.txt；Mono.Posix.NETStandard：随包分发的许可证及 Mono notices。
- System.IO.Ports、System.Management 等 Microsoft / .NET 组件：MIT。源码 https://github.com/dotnet/runtime 。精确版本、版权作者与源代码位置见 `licenses/dependency-inventory.json` 和各包原始 `.nuspec`。
- .NET / Windows Desktop Runtime：Microsoft / .NET，MIT 及其第三方声明。发布包包含运行所需组件。

完整依赖树由 `dotnet list src/PerfCompass/PerfCompass.csproj package --include-transitive` 列出。`licenses/` 附带各 NuGet 包中的许可文件、原始包元数据，以及 .NET 和 LibreHardwareMonitor 的完整第三方声明。清单同时包含构建依赖与其他平台运行资产；Windows 发布包按 win-x64 选择实际运行资产。

LibreHardwareMonitor 0.9.6 对应源代码提交：`3d331e3370efb858411f19511373eff65a218701`，可从 https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701 获取。

PerfCompass 分发的修改版源码包位于 `licenses/LibreHardwareMonitor-perfcompass/LibreHardwareMonitor-0.9.6-perfcompass.1-source.zip`，包含实际修改源码、资源、项目依赖锁、MPL 许可、上游第三方声明及修改说明。`source-package.json` 记录上游提交、修改版版本、PerfCompass 工作区提交状态和 ZIP SHA-256；`source-inventory.json` 记录该次源码快照的全部文件哈希。若标记 `SourceTreeDirty: true`，源码包才是该次实际快照，不能把工作区 HEAD 当作修改已提交的证明。

`scripts/verify-hardware-source.ps1` 检查未修改文件与原始 `UPSTREAM.json` 的一致性，并拒绝未在修改清单列出的源码变化；`scripts/package-notices.ps1` 在保留其他依赖许可的同时生成、逐文件验证修改版源码包，不把项目引用当作 NuGet 缓存包。
