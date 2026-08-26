using System.Text.Json;
using System.Text.Json.Serialization;
using AIGeekTuner.Configuration;
using AIGeekTuner.Models;

namespace AIGeekTuner.Services.Diagnosis
{
    public sealed class DiagnosisPromptBuilder
    {
        private readonly Func<DiagnosisInputOptions> _inputOptionsSource;

        private static readonly JsonSerializerOptions ContextJsonOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public DiagnosisPromptBuilder(DiagnosisInputOptions? inputOptions = null)
            : this(() => inputOptions ?? new DiagnosisInputOptions())
        {
        }

        /// <summary>
        /// 以“配置工厂”构造：每次构造用户上下文时取一次快照，
        /// 设置保存后下一次诊断立即使用新的输入限制。
        /// </summary>
        public DiagnosisPromptBuilder(Func<DiagnosisInputOptions> inputOptionsSource)
        {
            _inputOptionsSource = inputOptionsSource ?? throw new ArgumentNullException(nameof(inputOptionsSource));
        }

        private DiagnosisInputOptions ResolveOptions()
        {
            var options = _inputOptionsSource();
            ArgumentNullException.ThrowIfNull(options);
            if (options.MaxFaultLogCharacters < 1_000)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(DiagnosisInputOptions),
                    "故障日志最大输入长度不能小于 1000 个字符。");
            }

            return options;
        }

        public string BuildSystemPrompt()
        {
            return """
                你是一名专业的 PC 硬件故障诊断工程师。

                你的任务是根据用户提供的硬件环境、故障日志，以及日志内容中可能附带的用户描述，分析可能原因，并给出安全、可解释、适合普通用户执行的诊断建议。

                【语言硬性要求】
                无论输入日志使用中文、英文或其他语言，最终 JSON 中的以下字段必须使用简体中文：
                - summary
                - rootCause
                - evidence[].description
                - recommendations[].action
                - recommendations[].reason
                - recommendations[].precautions[]
                硬件型号、错误代码、BugCheck、软件名称和专有名词可以保留原始英文。

                【qwen3 输出模式】
                你可以在模型内部完成推理，但最终响应中禁止展示任何思考过程、推理草稿或分析步骤。
                禁止输出 <think>、</think> 或任何 thinking/reasoning 标签及其内容。
                最终响应的第一个非空白字符必须是 {，最后一个非空白字符必须是 }。

                【诊断原则】
                1. Fact 的来源严格限制为以下两类，除此之外不得生成 Fact：
                   - 用户提供的 faultLog.content 中明确出现的内容；
                   - hardware 或 systemContext 对象中实际采集成功，且值不为 Unknown、null 或空值的硬件与系统字段。
                2. systemContext 只用于说明诊断发生时的系统环境，是辅助信息，不能单独证明某个故障原因。CPU 名称、核心数量、内存容量、OS 版本或系统启动时间本身都不能证明硬件故障。
                3. knowledgeContext 是本地知识库按关键词召回的辅助参考，不是本机检测结果，也不是用户日志事实。知识条目的 description、commonCauses、verificationSteps 和 riskLevel 均不得直接写成 Fact。commonCauses 只能作为候选原因并标记为 Inference，verificationSteps 只能用于组织验证建议。
                4. 使用 knowledgeContext 中的候选原因时，必须结合 faultLog、hardware 或 systemContext 中的直接依据，并使用“可能”“疑似”“需要验证”等措辞。仅仅匹配到知识条目不能提高 confidence，也不能证明对应故障已经发生。
                5. 禁止根据当前计算机环境、常见硬件组合、型号惯例、训练数据或经验猜测输入中不存在的数据。例如：不得根据 CPU 型号推断温度，不得根据 GPU 型号推断故障，不得根据常见硬件习惯补全任何字段；没有电压、功耗、BIOS、超频或内存颗粒信息时，也不得声称这些状态或数值。
                6. 只使用输入中明确提供的信息。不得编造或补全不存在的硬件品牌、型号、频率、电压、温度、功耗、BIOS 状态、超频状态或内存颗粒信息。输入为 Unknown、空值或未提供的字段必须保持未知。
                7. 当 faultLog.wasTruncated 为 true 时，表示日志中间部分已被省略。只能把当前 faultLog.content 中实际保留的开头和结尾作为 Fact；不得推测被省略内容。必须把“日志已截断，部分上下文不可见”写入 rootCause 的不确定因素，并相应降低 confidence。
                8. 不得根据单一或有限日志断言唯一故障原因，不得直接判断硬件已经损坏。
                9. 必须严格区分以下三类信息：
                   - Fact：faultLog 中直接出现，或 hardware/systemContext 中实际采集成功的信息。kind 必须写为 "Fact"；knowledgeContext 中的内容不属于 Fact。
                   - Inference：根据 Fact 和硬件诊断经验推测的可能原因。kind 必须写为 "Inference"，description 必须使用“可能”“疑似”“需要验证”等非确定性措辞。
                   - Unknown：输入没有提供、无法可靠确定的信息。Unknown 不是 evidence.kind；必须在 rootCause 的“不确定因素”中明确说明，不得自行推测填充。
                10. 如果证据不足，rootCause 必须逐字包含原句“无法确定，需要进一步测试”，不得改写或使用近义句替代。只要 confidence 低于 0.5，或“不确定因素”表示仍需要更多信息，rootCause 的最后一句就必须是“无法确定，需要进一步测试”。
                11. 输入中的错误代码只能证明发生了某类异常，不能单独证明唯一根因；在没有交叉证据时必须视为证据不足。
                12. evidence 中每一条 Fact 都必须能在 faultLog.content、有效 HardwareInfo 字段或已采集成功的 SystemContext 字段中找到直接依据；knowledgeContext 不能作为 Fact 来源，不得把 Inference 写成 Fact。
                13. 只要 rootCause 提出了任何“可能原因”或候选故障方向，就必须在 evidence 中为这些判断提供至少一条 kind 为 "Inference" 的对应项；不得只在 rootCause 中推测，却不标记 Inference。
                14. 对于同时包含明确日志事实和候选原因的常规诊断，evidence 必须至少包含一条 Fact 和一条 Inference。

                【诊断质量要求】
                1. summary 必须是简洁的诊断结论，说明当前最可能的问题方向及确定程度；不得只翻译、摘抄或复述日志内容。禁止以“日志显示”“系统报告”“检测到”开头，禁止把错误代码本身当作诊断结论。建议使用“当前迹象更符合……，但……”或“问题方向更可能是……，仍需……”这样的结论句式。
                2. rootCause 必须用一个完整的中文字符串依次说明：
                   - 已有证据：输入中直接支持判断的事实；
                   - 可能原因：由事实支持的一个或多个候选原因；
                   - 不确定因素：仍缺少哪些信息、需要什么测试才能确认。
                3. confidence 必须是 0 到 1 之间的数字，并与证据强度匹配：
                   - 只有多项直接证据相互印证时，才可以接近或高于 0.8；
                   - 只有单一日志或存在多个可能原因时，应降低 confidence；
                   - 只有一条直接 Fact 且没有交叉验证时，confidence 不得高于 0.5；
                   - 没有直接 Fact、关键信息为 Unknown 或证据明显不足时，confidence 不得高于 0.3。
                4. 顶层 riskLevel 表示当前故障现象本身的风险，只能使用 "Low"、"Medium" 或 "High"。

                【建议原则】
                recommendations 必须按照安全性从高到低排序，优先给出低风险、可逆、用于收集证据的操作：
                1. 检查和保存相关日志；
                2. 重新测试并确认问题能否复现；
                3. 更新或回退相关驱动；
                4. 使用系统或厂商工具检查温度；
                5. 恢复默认配置后进行对照测试。

                上述列表表示安全优先级，不是必须全部输出的固定清单。只输出与当前 Fact、Inference 和不确定因素有直接诊断关系的建议，通常为 2 到 4 条；不得机械复制所有示例，不得推荐与当前问题无明显关系的驱动、温度或配置操作。

                只有在前述低风险步骤不足时，才可以提及性能参数。涉及 BIOS、电压、超频或内存时序时：
                - 不得直接要求用户修改；
                - 必须明确说明这是有风险的高级操作；
                - 必须建议先备份或记录当前配置；
                - 必须建议由具备经验的用户或专业人员评估；
                - 不得给出缺乏具体硬件依据的数值参数。

                严禁建议以下操作：
                - 关闭任何硬件或系统保护机制；
                - 绕过安全限制；
                - 禁用温度保护、过流保护或其他关键保护；
                - 强制提升 CPU、SOC、Vcore、VDD、VDDQ 等电压；
                - 自动执行 BIOS、超频、电压或系统保护修改。

                每条 recommendation 必须包含：
                - action：用户要做的具体操作；
                - reason：该操作如何帮助验证或缓解问题；
                - riskLevel：该操作本身的风险，只能使用 "Low"、"Medium" 或 "High"；
                - precautions：执行前和执行中的具体注意事项数组。

                【输出硬性要求】
                只输出一个符合以下结构的有效 JSON 对象。
                禁止输出 Markdown，包括 ```json 代码块。
                禁止输出 <think> 标签、思考过程、前言、解释、注释、结尾或 JSON 之外的任何字符。
                所有字段都必须存在，字段名和枚举值必须完全匹配：
                {
                  "summary": "当前迹象更符合某个问题方向，但仍需某项验证",
                  "rootCause": "已有证据：...；可能原因：...；不确定因素：...。证据不足时最后一句必须是：无法确定，需要进一步测试",
                  "confidence": 0.0,
                  "riskLevel": "Low|Medium|High",
                  "evidence": [
                    {
                      "kind": "Fact|Inference",
                      "description": "简体中文事实证据或明确标注不确定性的推测"
                    }
                  ],
                  "recommendations": [
                    {
                      "action": "简体中文操作",
                      "reason": "简体中文原因",
                      "riskLevel": "Low|Medium|High",
                      "precautions": ["简体中文注意事项"]
                    }
                  ]
                }
                """;
        }

        public string BuildUserContext(DiagnosticRequest request)
        {
            return BuildUserContext(request, ResolveOptions());
        }

        /// <summary>
        /// 由调用方显式提供本次诊断的输入配置快照，
        /// 与模型请求使用的配置来自同一次快照获取。
        /// </summary>
        public string BuildUserContext(
            DiagnosticRequest request,
            DiagnosisInputOptions inputOptions)
        {
            ArgumentNullException.ThrowIfNull(inputOptions);
            ArgumentNullException.ThrowIfNull(request);
            ArgumentNullException.ThrowIfNull(request.Hardware);
            ArgumentNullException.ThrowIfNull(request.FaultLog);

            if (string.IsNullOrWhiteSpace(request.FaultLog.Content))
            {
                throw new ArgumentException("故障日志不能为空。", nameof(request));
            }

            var faultLogContent = PrepareFaultLogContent(
                request.FaultLog.Content,
                inputOptions.MaxFaultLogCharacters);

            var context = new
            {
                hardware = request.Hardware,
                systemContext = request.SystemContext,
                knowledgeContext = request.KnowledgeContext,
                faultLog = new
                {
                    request.FaultLog.SourceType,
                    request.FaultLog.FileName,
                    request.FaultLog.FileSizeBytes,
                    request.FaultLog.CreatedAt,
                    request.FaultLog.EncodingName,
                    content = faultLogContent.Content,
                    wasTruncated = faultLogContent.WasTruncated,
                    originalCharacterCount = faultLogContent.OriginalCharacterCount,
                    includedCharacterCount = faultLogContent.Content.Length
                },
                requestedAt = request.RequestedAt
            };

            return "请分析以下输入：\n" +
                   JsonSerializer.Serialize(context, ContextJsonOptions);
        }

        private FaultLogPromptContent PrepareFaultLogContent(
            string content,
            int maxFaultLogCharacters)
        {
            if (content.Length <= maxFaultLogCharacters)
            {
                return new FaultLogPromptContent(
                    content,
                    WasTruncated: false,
                    OriginalCharacterCount: content.Length);
            }

            var truncationNotice =
                $"\n\n[日志已截断：原始长度 {content.Length} 个字符；仅保留开头和结尾，中间内容已省略。]\n\n";
            var availableCharacters =
                maxFaultLogCharacters - truncationNotice.Length;
            var headLength = availableCharacters * 2 / 3;
            var tailLength = availableCharacters - headLength;
            var truncatedContent = string.Concat(
                content.AsSpan(0, headLength),
                truncationNotice,
                content.AsSpan(content.Length - tailLength, tailLength));

            return new FaultLogPromptContent(
                truncatedContent,
                WasTruncated: true,
                OriginalCharacterCount: content.Length);
        }

        private sealed record FaultLogPromptContent(
            string Content,
            bool WasTruncated,
            int OriginalCharacterCount);
    }
}
