# 专用抽取模型的购机语义实验

这是独立研究工具，不是 PerfCompass 的新运行依赖。产品继续使用 C#/.NET/WPF，模型默认关闭。Python 环境、权重和运行日志均留在忽略的 `.tools/nlp`、`artifacts` 目录，不进入发布包。

## 对象与协议

- [GLiNER2 论文](https://arxiv.org/abs/2507.18546)与[官方实现](https://github.com/fastino-ai/GLiNER2)提供统一的实体、分类及结构化抽取接口；这不等于证明中文购机语义可靠。
- 本次固定 [fastino/gliner2.5-multi-v1](https://huggingface.co/fastino/gliner2.5-multi-v1) 提交 `cf5593a5d45e3bbf204b9df621b13b1c0cf25ee3`、SDK 2.1.0。实际权重为 1,149,461,028 字节，SHA256 `c1ff4ec0bc00031c15530b8f3c33d3677f27949e6a0cb52e1247a6224b6c5395`。来源元数据见 `model-source.json`；小文件校对 Git blob，权重校对 LFS SHA256。模型卡标注 Apache-2.0，未将模型随产品分发。
- 输入保持原有 `tests/nlp-extended-cases.json` 的 24 条合成开发输入。两轮分别使用简化类别和覆盖全部产品约束类别的 schema；每轮比较 records/attributes × char/whitespace，阈值 0.5、CPU 四线程、随机种子 42，未按某条结果调整阈值。
- 共 8 个组合、192 次提取，但只有 24 条不同输入，不能当成 192 条独立样本。没有独立人工标注，也未计算通用语义准确率。原有 Qwen 输出、分类和分数不改写。
- 推理使用已校验的本机快照、离线开关及 CPU；仅下载公开文件，需求文字不发送到外部服务。进程、代码、schema、输入和每个输出均有摘要或终态记录。

## 实际结果

八组进程均真实退出 0、每组完成 24 条。所有提取型片段的位置检查通过；类别标签携带的锚点位置不作为原文引用检查。这些结果只证明调用和定位，不证明类别与逻辑正确。

| 组合 | 实体数 | 结构化记录数 | 单句耗时中位数（秒） |
| --- | ---: | ---: | ---: |
| compact records / char | 46 | 9 | 0.950 |
| compact records / whitespace | 5 | 1 | 0.861 |
| compact attributes / char | 59 | 0 | 0.321 |
| compact attributes / whitespace | 6 | 0 | 0.345 |
| wide records / char | 43 | 8 | 1.214 |
| wide records / whitespace | 7 | 1 | 1.212 |
| wide attributes / char | 59 | 0 | 0.890 |
| wide attributes / whitespace | 5 | 0 | 0.772 |

单句耗时排除了加载，包含该批的首次调用；不能当作冷启动或与旧 8B 管线等价的性能比较。进程峰值工作集约 2.91 GiB，包含模型初始化，是 OS 进程读数，不是独占物理内存。

重点核对的反例见[绑定输出摘要的记录](../../tests/semantic-reviews/gliner-scope-findings.json)。这不是全部输出的独立人工打分：

- h01：找到 Ansys，但未分开过去使用、当前停止和未来未定。
- h04：本机 SSH 在 wide attributes / char 中被标为引文；本机浏览器的执行位置被标为远程。其他多个组合没有输出。
- h16：128GB 属于朋友的电脑；属性组合将其标为非个人引文，提供了可用线索，但把本人 KiCad 等任务标为条件需求；记录组合只有带 128GB 的“电脑”记录，归属仍未确定，不能用于本机容量目标。
- h20：即使扩大硬件类别，四个 wide 组合仍未保留“独显尚未确定”的事项。
- h22：属性组合把 512GB 标为引文，保留了一个正确线索；同时仍有类型/执行位置问题，记录组合未恢复完整语义。不得将这个数值转成本机目标。
- h24：找到 6000/8000 元，但属性组合把预算标为引文，记录组合未连接金额、触发条件与两种形态。高实体得分没有保住条件预算。

当前结论：不将这组零样本配置替换成产品默认需求解析器，也不把它的原始标签接进购机判断。字符分词更有利于本批词语定位，仍不能解决归属、时间分期和条件关系。下一步需要比较能保留事件及目标设备关系的语义方法，并独立核对事实是否受原文支持；不能靠增加实体标签宣称已获得通用理解。

## 原始证据与限制

- [汇总及每个输出的 SHA256](../../docs/experiments/gliner-scope-research.json)；原始 case 文件位于 `tests/semantic-results/gliner2.5-research/`。公开运行清单移除了 PID，未包含服务日志、用户采样或机器路径。
- [逐条对照页](../../docs/gliner-scope-review.html)可切换输入与组合；它是研究报告草稿。浏览器工具无可用会话，终端无界面渲染被自动审批拒绝（`blocked by policy`）；尚未完成浏览器呈现和交互验收。
- 初次技术试运行把 `Schema.build()` 的裸字典传给运行器，丢失字段/属性元数据；正式矩阵保留 Schema 对象，`to_dict()` 仅用于审计。初次试运行和原始脚本在本地保留，不作为正式语义结果。类别标签有时带锚点偏移，初版审计将其误算为原文片段，正式矩阵已分开。
- 完整研究环境的四项回归检查通过，覆盖元数据保留、序列化往返及 Unicode/无效偏移；不是四项模型准确性检查。首次断言误认为字段元数据为嵌套字典，已依实际接口修正并保留原始失败。
- `verify_public.py` 使用标准库核对 192 个原始输出、8 个清单、六条审阅对应的 24 份输出绑定及 HTML 结构；已在未安装 PyTorch 的环境运行通过，并加入 CI。它不加载模型、不运行浏览器，也不证明语义或呈现正确。研究命令对非空输出目录的实际拒绝已验证，哨兵文件及目录内容保留。
- SDK 2.1.0 在本机只安装核心依赖时，导入即因缺少 `torch` 失败，未运行任何测试。此安装路径不可称作无需 PyTorch；正式矩阵使用 `[local]` 完整环境。模型类型与 TorchScript 警告保留在私有原日志，没有把载入成功作为语义质量证明。
- 本次开发、下载及 CPU 提取发生在独立的 0.8.18 冻结持续试验期间，已记录工作负载上下文。它们不是空闲开销基线、用户任务或 0.8.24 的长期验证。

## 复现

需本机 Python 3.13 与 uv。以下均在仓库根目录运行，环境与缓存独立；已有输出不覆盖：

```powershell
uv venv --python 3.13 .tools/nlp/gliner-venv
$env:UV_CACHE_DIR = "$PWD\.tools\nlp\uv-cache"
uv pip install --python .tools/nlp/gliner-venv/Scripts/python.exe -r research/gliner/requirements-versions.txt --index https://download.pytorch.org/whl/cpu --default-index https://pypi.org/simple --index-strategy unsafe-best-match
.tools/nlp/gliner-venv/Scripts/python.exe research/gliner/download_model.py research/gliner/model-source.json .tools/nlp/gliner2.5-multi-cf5593a
.tools/nlp/gliner-venv/Scripts/python.exe research/gliner/test_probe.py
research/gliner/run_matrix.ps1 -Label gliner-reproduction-compact -SchemaFlavor compact
research/gliner/run_matrix.ps1 -Label gliner-reproduction-wide -SchemaFlavor wide
```

`inspect_results.py <matrix-directory> --id h16` 可读取四种组合的同一输入。`publish_results.py --help` 查看新目录的证据导出；不会覆盖已保存实验。文件摘要保障来源和绑定，不认证语义、硬件准确性或跨平台数值一致性。

不安装模型环境也可执行 `python research/gliner/verify_public.py` 核对已提交证据。`.gitattributes` 为已绑定摘要的研究源码、schema 和原始结果保留字节，避免 Git 换行转换破坏校对。
