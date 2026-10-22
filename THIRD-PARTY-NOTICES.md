# 第三方组件

- LibreHardwareMonitorLib 0.9.6：Mozilla Public License 2.0。源码 https://github.com/LibreHardwareMonitor/LibreHardwareMonitor 。本项目通过公开库接口使用，未修改其源码。其第三方组件以原仓库 notices 为准。
- BlackSharp.Core、DiskInfoToolkit、RAMSPDToolkit-NDD：MPL-2.0，随 LibreHardwareMonitor 引入，未修改源码。
- HidSharp：包内 LICENSE.txt；Mono.Posix.NETStandard：随包分发的许可证及 Mono notices。
- System.IO.Ports、System.Management 等 Microsoft / .NET 组件：MIT。源码 https://github.com/dotnet/runtime 。精确版本、版权作者与源代码位置见 `licenses/dependency-inventory.json` 和各包原始 `.nuspec`。
- .NET / Windows Desktop Runtime：Microsoft / .NET，MIT 及其第三方声明。发布包包含运行所需组件。

完整依赖树由 `dotnet list src/PerfCompass/PerfCompass.csproj package --include-transitive` 列出。`licenses/` 附带各 NuGet 包中的许可文件、原始包元数据，以及 .NET 和 LibreHardwareMonitor 的完整第三方声明。清单同时包含构建依赖与其他平台运行资产；Windows 发布包按 win-x64 选择实际运行资产。

LibreHardwareMonitor 0.9.6 对应源代码提交：`3d331e3370efb858411f19511373eff65a218701`，可从 https://github.com/LibreHardwareMonitor/LibreHardwareMonitor/tree/3d331e3370efb858411f19511373eff65a218701 获取。
