# 旧格式回归夹具

`task-measurement-1.0-input-restore.json` 和 `task-plan-1.0-input-restore.json` 由冻结的 0.8.20 实际 API 写入。程序集 SHA256 为 `353DB23BF15AE91E1AAB7A3EB87EB0FC1FD42B8BAE3D0792677189CCB3BE4B8F`，程序参数使用相对文件名，不含本机路径。该合成任务把 alpha 改成 beta，第二轮读取 beta，第三轮恢复 alpha；旧 API 仍记录三轮完成且可比较。脚本原文在 `TaskInputFixtures.RestoreScript`，只在显式开发测试的独立目录运行。

这些文件保留用于检查旧指纹、原始轮次、无损读取及模板复用；导入不执行脚本，不代表实际硬件性能。新方法不追认其输入完整性，也不改写旧格式和原始耗时。模板迁移先核对旧指纹，再用相同任务字段建立 1.1 方案；路径、机器身份与耗时不继承。

`comparison-1.2-uncertainty.json` 由 0.8.19 的真实 `CandidateComparison.SaveFile` 写入，保留两个同主体、不同内容的待定事实、两台分别通过其中一个选项的合成候选，以及共同内存目标。原生导入回归使用这些字节；不是用户历史、实际商品或兼容性实测。

旧程序集来自提交 `d78bdb93fef6ea5210cf10c79bb4ba9d34b0846c` 的模型探针构建，SHA256 为 `8260B2A86D415826EEFBA0E596CA62E4C0E76188CA7309657A97C7367EB322CC`。夹具的结果显示名称刻意保持旧 API 默认空值，导入后由候选及条件引用重建；数值、判定、协议、依据、可比性与原始签名须保持不变。源文件读取不能变成执行任务或自动核对。
