# 自然语言需求理解：研究与诊断

日期：2026-10-22。产品保持 C#/.NET 原生栈。当前界面中的 `rules-1.0` 是有限规则基线，不是通用语义模型。

## 查阅的论文与开源实现

| 来源 | 可借鉴机制 | 对本项目的限制 |
|---|---|---|
| [UIE, ACL 2022](https://arxiv.org/abs/2203.12277)、[PaddleNLP 实现](https://github.com/PaddlePaddle/PaddleNLP/blob/develop/paddlenlp/taskflow/information_extraction.py) | 按 schema 抽取实体、关系和事件，保留文本位置 | 仍需验证本领域中文、否定及条件；Python 参考实现不等于产品需要改栈 |
| [GLiNER2 论文](https://arxiv.org/abs/2507.18546)、[代码](https://github.com/fastino-ai/GLiNER2) | schema 驱动的实体、分类和层级结构抽取 | 中文能力、数值归一化和部署成本需实测，不能用其其他任务指标代替 |
| [LangExtract](https://github.com/google/langextract) | 将抽取映射回原文位置，显示依据，支持不同模型提供方 | 定位到原文不证明语义正确；其文档也指出效果取决于模型和任务 |
| [nl2spec](https://arxiv.org/abs/2303.04864) | 将结构化结果映射回文字片段，支持用户修改歧义 | 研究对象为时序逻辑，本项目借鉴交互验证方式，不套用其任务指标 |
| [Outlines](https://github.com/dottxt-ai/outlines)、[llama.cpp JSON schema](https://github.com/ggml-org/llama.cpp/blob/master/grammars/README.md) | 用 schema/语法约束生成结构 | 只能约束结构，不能保证否定、时间、单位及推断正确 |
| [JSONSchemaBench](https://arxiv.org/abs/2501.10868) | 将约束覆盖、输出质量和效率分开评价 | JSON 可解析不能当作需求被正确理解 |
| [Semantic Kernel C# 样例](https://github.com/microsoft/semantic-kernel/blob/main/dotnet/samples/Concepts/ChatCompletion/OpenAI_StructuredOutputs.cs) | .NET 中的类型化结构输出和模型调用模式 | 不代表必须引入整个框架；接口校验与业务证据校验仍需本地实现 |

## 已执行的广度诊断

`tests/nlp-cases.json` 含 36 条人工编写的诊断输入，覆盖常见任务、CAD、音频、直播、数据分析、并发、容量增长、预算、便携、噪声、功耗、兼容性、时间变化、条件、指代、口语、中英混合、冲突、多用户和文本中的恶意指令。

执行 `PerfCompass.exe --nlp-evaluate`，输出 `artifacts/nlp-evaluation.json` 和 `.txt`。当前规则基线只有 12/36 条的诊断字段集合完全一致，其余缺失和额外字段全部列出。该结果不是抽样真实用户准确率，也不是任何模型的成绩。用例和预期由开发者人工编写，需继续审校和扩充独立输入。

这说明继续扩充少量关键词不能满足通用需求理解。不能为了让数字好看而删除陌生任务、条件、数值与时间用例，或只把规则已支持的字段当成正确答案。

## 下一步的实现与评价约束

采用开放任务名称、固定语义关系的结构：任务/软件、规模、资源或体验目标、预算与其他约束、肯定/否定/条件/不确定、过去/当前/未来、频率、原文证据和待澄清问题。陌生任务保留其原名，不强行归入既有五个场景。

模型只负责提取候选事实，不直接输出购买型号。C# 验证证据能否逐字定位、数值和单位、时间与条件关系、冲突及来源版本。用户核对后，才能成为配置建议的约束；低置信或缺失信息应提问或保留未知。

需要分别测量：结构合法性、原文定位、字段完整性、否定/条件/时间正确性、数字和单位、错误的硬件需求推断、澄清质量、延迟和内存占用。条件与时间表达不能压扁成一个“未来/非未来”布尔值。

本地研究候选为 [Qwen3-4B-GGUF 官方模型](https://huggingface.co/Qwen/Qwen3-4B-GGUF) 配合独立 llama.cpp 进程；下载物放在被 Git 排除的 `.tools/nlp/`，不加入默认安装包。选择只是实验起点，尚无本项目的模型效果结论；也没有自动向云端发送用户记录。
