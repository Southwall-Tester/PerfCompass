# 持续验证的进程与退出见证

这是开发验证入口，不会读取用户的 `release/data`。先完成独立发布包的本机检查并设置候选标记 `READY`，再运行：

```powershell
.\scripts\validation-soak.ps1 -Action Start -SourceDirectory release-next -Seconds 28800
.\scripts\validation-soak.ps1 -Action Status
```

`Start` 在新的 `artifacts/soak-*` 目录复制 EXE、说明和许可证，同时冻结本次监督脚本。独立 PowerShell 监督进程用固定参数启动测试程序，立即持有其进程句柄，再写入 PID、启动时刻、EXE 摘要和本次运行标识。用户不能通过 intent 文件提供额外执行参数。`READY` 是检查标记，不是代码签名或身份认证。

监督进程会在测试程序结束后写 `exit-witness.json`。即使程序很快失败，也能保留它的实际退出码。监督进程自己的退出码只表示记录流程是否完成，不能当成测试程序退出码。启动检查失败另存 `supervisor-error.json`；不通过杀进程、重新启动或覆盖旧目录来制造完成结果。

同一工作目录的启动入口有短期共享锁；已有测试程序或监督进程仍在线时，会拒绝另起一轮。启动观察窗口到期后应继续查同一目录；如果只看见监督进程、尚无子进程清单，状态仍为 `starting`。读取进程身份失败会报错，不能据此认定进程已结束。

状态字段含义如下：

| 字段 | 含义 |
| --- | --- |
| `State` | 应用记录的运行阶段，或启动/缺少结果阶段；`completed` 本身不表示通过 |
| `ProcessLive` / `SupervisorLive` | 核对实际 PID、路径和启动时刻后是否仍在线 |
| `ApplicationChecksPassed` | 当前 EXE 摘要、请求时长与应用完成结果是否匹配且应用声明检查通过 |
| `ExitWitnessStatus` | `pending` 等待退出、`matched` 身份匹配、`missing` 缺见证、`identity-mismatch` 身份或退出码类型不符 |
| `ExitCode` | 仅匹配的见证才提供实际整数退出码；缺失时为 null，不填成 0 |
| `ChecksPassed` | 应用检查通过、应用进程已结束、退出见证身份匹配且真实退出码为 0，才为 true |

旧运行没有退出见证时，原始应用结果仍可读，但新状态查询不会为其补造退出码或把它升级为完整通过。原始 `result.json` 和旧失败结论不改写。当前 0.8.25 八小时试验已使用独立临时观察器持有句柄，正式脚本不会中途替换它；新启动才使用正式监督进程。

需要提前结束自己启动的验证时，显式指定对应目录：

```powershell
.\scripts\validation-soak.ps1 -Action Stop -RunDirectory artifacts/soak-对应目录
```

`Stop` 只向已核对身份且初始化完成的测试写停止请求。提前停止保留 `stopped`，真实退出码通常为 2，不能计作完整时长通过。进程无响应时保留未知，不强行改为完成。

脚本回归从真实发布 EXE 开始，在独立工具目录运行正常短测和提前停止；快速失败另用明确标注的原生 `whoami.exe` 无效参数夹具，只验证进程退出记录，不冒充性能采集。其余状态元数据为不执行的合成夹具。回归不触碰正在进行的长测或用户历史。

这些文件与摘要用于对照本机记录，不构成防篡改认证、崩溃/断电恢复、物理读数准确性或长期稳定性保证。一次短测通过不等于八小时通过，八小时通过也不等于跨硬件或商业成熟。
