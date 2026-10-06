using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Expressions;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>综合判定中的一个检测项。</summary>
    public sealed class ResultJudgeItem
    {
        /// <summary>在配置文本中的行号（从 1 开始），用于提示。</summary>
        public int LineNumber { get; set; }
        public string Name { get; set; }
        /// <summary>取值表达式，结果为数值（按上下限判定）或布尔（true 为合格）。</summary>
        public string Expression { get; set; }
        /// <summary>下限（含），null 表示不限制。</summary>
        public double? Lower { get; set; }
        /// <summary>上限（含），null 表示不限制。</summary>
        public double? Upper { get; set; }
        public int NgCode { get; set; } = 1;
        /// <summary>不合格信息；为空时使用“{名称}不合格”。</summary>
        public string NgMessage { get; set; }

        public string Parameter
        {
            get { return $"判定项 第 {LineNumber} 行"; }
        }

        public string EffectiveNgMessage
        {
            get { return string.IsNullOrWhiteSpace(NgMessage) ? Name + "不合格" : NgMessage; }
        }

        /// <summary>合格范围的文字形式，如 [4, 5]、≥ 4、不限。</summary>
        public string RangeText
        {
            get
            {
                if (Lower.HasValue && Upper.HasValue)
                {
                    return $"[{FormatBound(Lower)}, {FormatBound(Upper)}]";
                }
                if (Lower.HasValue)
                {
                    return "≥ " + FormatBound(Lower);
                }
                return Upper.HasValue ? "≤ " + FormatBound(Upper) : "不限";
            }
        }

        /// <summary>上下限的文字形式（不区分区域设置），null 为空串。</summary>
        public static string FormatBound(double? value)
        {
            return value.HasValue ? value.Value.ToString(CultureInfo.InvariantCulture) : string.Empty;
        }
    }

    /// <summary>
    /// 综合判定（LD-04）：把多个检测项汇总为 OK/NG 结论与不合格原因，输出名与流程输出默认的 Ok / Code / Message 对应。
    /// 每项的取值为数值时按闭区间判定，为布尔时 true 为合格；NaN、引用无法取值、类型不符都判为不合格并记录原因。
    /// 判定结果是数据而不是失败：只有配置错误时工具才运行失败。
    /// </summary>
    [ToolOutput("Ok", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Code", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Message", VariableKind.Single, VariableType.String)]
    [ToolOutput("NgCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("NgNames", VariableKind.Array, VariableType.String)]
    [ToolOutput("NgMessages", VariableKind.Array, VariableType.String)]
    [ToolOutput("NgDetails", VariableKind.Array, VariableType.String)]
    [ToolOutput("Values", VariableKind.Array, VariableType.Double)]
    [ToolOutput("ItemOks", VariableKind.Array, VariableType.Bool)]
    public sealed class ResultJudgeTool : ToolBase, IExpressionTool, IToolConfigurationCheck
    {
        /// <summary>
        /// 判定项，每行一项：名称|取值表达式|下限|上限|NG代码|NG信息。下限、上限留空表示不限制；
        /// NG 代码留空为 1；NG 信息留空为“名称不合格”。取值表达式中可以出现 ||（从行尾向前取后四个 | 分隔）。
        /// </summary>
        public string Items { get; set; } = string.Empty;

        /// <summary>遇到第一个不合格项后停止判定（默认判完全部项）。</summary>
        public bool StopAtFirstNg { get; set; }

        public int OkCode { get; set; }

        public string OkMessage { get; set; } = "OK";

        public ResultJudgeTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>解析一行判定项；格式错误时返回 false 并给出原因。</summary>
        public static bool TryParseLine(string line, int lineNumber, out ResultJudgeItem item, out string error)
        {
            item = null;
            error = null;
            int first = line.IndexOf('|');
            string rest = first < 0 ? string.Empty : line.Substring(first + 1);
            var separators = new int[4];
            int search = rest.Length - 1;
            for (int i = 3; i >= 0; i--)
            {
                separators[i] = search < 0 ? -1 : rest.LastIndexOf('|', search);
                if (separators[i] < 0)
                {
                    error = "格式应为 名称|取值表达式|下限|上限|NG代码|NG信息";
                    return false;
                }
                search = separators[i] - 1;
            }

            string name = line.Substring(0, first).Trim();
            string lowerText = rest.Substring(separators[0] + 1, separators[1] - separators[0] - 1).Trim();
            string upperText = rest.Substring(separators[1] + 1, separators[2] - separators[1] - 1).Trim();
            string codeText = rest.Substring(separators[2] + 1, separators[3] - separators[2] - 1).Trim();
            if (name.Length == 0)
            {
                error = "名称不能为空";
                return false;
            }
            if (!TryParseBound(lowerText, out double? lower))
            {
                error = $"下限“{lowerText}”不是数值";
                return false;
            }
            if (!TryParseBound(upperText, out double? upper))
            {
                error = $"上限“{upperText}”不是数值";
                return false;
            }
            if (lower.HasValue && upper.HasValue && lower.Value > upper.Value)
            {
                error = "下限不能大于上限";
                return false;
            }
            int code = 1;
            if (codeText.Length > 0
                && !int.TryParse(codeText, NumberStyles.Integer, CultureInfo.InvariantCulture, out code))
            {
                error = $"NG 代码“{codeText}”不是整数";
                return false;
            }

            item = new ResultJudgeItem
            {
                LineNumber = lineNumber,
                Name = name,
                Expression = rest.Substring(0, separators[0]).Trim(),
                Lower = lower,
                Upper = upper,
                NgCode = code,
                NgMessage = rest.Substring(separators[3] + 1).Trim()
            };
            return true;
        }

        /// <summary>解析上下限文字：空串表示不限制；非数值或 NaN 返回 false。</summary>
        public static bool TryParseBound(string text, out double? value)
        {
            value = null;
            text = (text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                return true;
            }
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
                && !double.IsNaN(parsed))
            {
                value = parsed;
                return true;
            }
            return false;
        }

        /// <summary>
        /// 检查第 index 项（供编辑器实时提示）：名称、重名、上下限、NG 代码、语法与局部名称。
        /// 模块引用交给 checkExternalReference（返回错误说明，无问题时返回 null；为 null 时不检查）。没有问题时返回 null。
        /// </summary>
        public static string CheckItem(IReadOnlyList<ResultJudgeItem> items, int index, int okCode,
            Func<string, string> checkExternalReference)
        {
            ResultJudgeItem item = items[index];
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return "名称不能为空";
            }
            if (item.Name.IndexOf('|') >= 0 || (item.NgMessage ?? string.Empty).IndexOf('|') >= 0)
            {
                return "名称和 NG 信息不能包含 | 字符";
            }
            if (items.Where((other, i) => i != index && string.Equals(other.Name, item.Name, StringComparison.OrdinalIgnoreCase)).Any())
            {
                return $"名称“{item.Name}”重复";
            }
            if (item.Lower.HasValue && item.Upper.HasValue && item.Lower.Value > item.Upper.Value)
            {
                return "下限不能大于上限";
            }
            if (item.NgCode == okCode)
            {
                return $"NG 代码 {item.NgCode} 与合格代码相同";
            }
            if ((item.Expression ?? string.Empty).IndexOf('\n') >= 0)
            {
                return "取值表达式不能换行";
            }
            if (!ExpressionParser.TryParse(item.Expression, out CompiledExpression expression, out ExpressionException error))
            {
                return error.Message;
            }
            if (expression.LocalNames.Count > 0)
            {
                return $"未定义的名称 {{{expression.LocalNames[0]}}}";
            }
            foreach (string path in expression.References)
            {
                string referenceError = checkExternalReference?.Invoke(path);
                if (referenceError != null)
                {
                    return referenceError;
                }
            }
            return null;
        }

        /// <summary>解析判定项配置；格式问题写入 issues（可为 null），格式正确的行返回。</summary>
        public static List<ResultJudgeItem> ParseItems(string text, List<ToolConfigurationIssue> issues)
        {
            var items = new List<ResultJudgeItem>();
            string[] lines = (text ?? string.Empty).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }
                if (TryParseLine(line, i + 1, out ResultJudgeItem item, out string error))
                {
                    items.Add(item);
                }
                else
                {
                    issues?.Add(new ToolConfigurationIssue($"判定项 第 {i + 1} 行", error));
                }
            }
            return items;
        }

        /// <summary>把判定项写回配置文本。</summary>
        public static string FormatItems(IEnumerable<ResultJudgeItem> items)
        {
            return string.Join("\n", items.Select(FormatLine));
        }

        public static string FormatLine(ResultJudgeItem item)
        {
            return string.Join("|", item.Name, item.Expression, ResultJudgeItem.FormatBound(item.Lower),
                ResultJudgeItem.FormatBound(item.Upper), item.NgCode.ToString(CultureInfo.InvariantCulture), item.NgMessage);
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            var issues = new List<ToolConfigurationIssue>();
            List<ResultJudgeItem> items = ParseItems(Items, issues);
            if (items.Count == 0 && issues.Count == 0)
            {
                issues.Add(new ToolConfigurationIssue("判定项", "未配置判定项"));
            }
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ResultJudgeItem item in items)
            {
                if (!names.Add(item.Name))
                {
                    issues.Add(new ToolConfigurationIssue(item.Parameter, $"名称“{item.Name}”重复"));
                }
                if (item.NgCode == OkCode)
                {
                    issues.Add(new ToolConfigurationIssue(item.Parameter, $"NG 代码 {item.NgCode} 与合格代码相同"));
                }
            }
            return issues;
        }

        public IEnumerable<ToolExpressionDef> GetExpressions()
        {
            return ParseItems(Items, null).Select(i => new ToolExpressionDef(i.Parameter, i.Expression));
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = CheckConfiguration().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }
            List<ResultJudgeItem> items = ParseItems(Items, null);
            var compiled = new List<CompiledExpression>();
            foreach (ResultJudgeItem item in items)
            {
                if (!ExpressionParser.TryParse(item.Expression, out CompiledExpression expression, out ExpressionException error))
                {
                    return NodeResult.Fail($"{ModuleName} {item.Parameter}（{item.Name}）：{error.Message}");
                }
                compiled.Add(expression);
            }

            var values = Enumerable.Repeat(double.NaN, items.Count).ToList();
            var itemOks = Enumerable.Repeat(false, items.Count).ToList();
            var ngItems = new List<ResultJudgeItem>();
            var ngDetails = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                bool ok = Judge(ctx, items[i], compiled[i], out double value, out string detail);
                values[i] = value;
                itemOks[i] = ok;
                if (ok)
                {
                    continue;
                }
                ngItems.Add(items[i]);
                ngDetails.Add($"{items[i].Name}：{detail}");
                if (StopAtFirstNg)
                {
                    break;
                }
            }

            bool allOk = ngItems.Count == 0;
            SetOutput(ctx, Variable.Single(ModuleName, "Ok", VariableType.Bool, allOk));
            SetOutput(ctx, Variable.Single(ModuleName, "Code", VariableType.Int, allOk ? OkCode : ngItems[0].NgCode));
            SetOutput(ctx, Variable.Single(ModuleName, "Message", VariableType.String,
                allOk ? OkMessage ?? string.Empty : ngItems[0].EffectiveNgMessage));
            SetOutput(ctx, Variable.Single(ModuleName, "NgCount", VariableType.Int, ngItems.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "NgNames", VariableType.String, ngItems.Select(i => i.Name).ToList()));
            SetOutput(ctx, Variable.Array(ModuleName, "NgMessages", VariableType.String, ngItems.Select(i => i.EffectiveNgMessage).ToList()));
            SetOutput(ctx, Variable.Array(ModuleName, "NgDetails", VariableType.String, ngDetails));
            SetOutput(ctx, Variable.Array(ModuleName, "Values", VariableType.Double, values));
            SetOutput(ctx, Variable.Array(ModuleName, "ItemOks", VariableType.Bool, itemOks));

            if (allOk)
            {
                ctx.AddLog(FlowLogLevel.Info, $"[综合判定] 合格，共 {items.Count} 项");
            }
            else
            {
                ctx.AddLog(FlowLogLevel.Warning, $"[综合判定] 不合格 {ngItems.Count} 项：{string.Join("；", ngDetails)}");
            }
            return NodeResult.Ok;
        }

        /// <summary>判定一项。value 为用于输出的数值（布尔记为 1 / 0，无法取值为 NaN），detail 为不合格原因。</summary>
        private static bool Judge(FlowContext ctx, ResultJudgeItem item, CompiledExpression expression,
            out double value, out string detail)
        {
            value = double.NaN;
            detail = null;
            object result;
            try
            {
                result = expression.Evaluate(ctx);
            }
            catch (ExpressionException ex)
            {
                detail = "无法取值，" + ex.Message;
                return false;
            }

            if (result is bool passed)
            {
                value = passed ? 1 : 0;
                if (item.Lower.HasValue || item.Upper.HasValue)
                {
                    detail = "取值为布尔，不能设置上下限";
                    return false;
                }
                detail = passed ? null : "条件不成立";
                return passed;
            }
            if (!ExpressionValues.IsNumber(result))
            {
                detail = $"取值为{ExpressionValues.TypeName(result)}，应为数值或布尔";
                return false;
            }

            value = ExpressionValues.ToDouble(result);
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                detail = "取值无效（" + ExpressionValues.Format(value) + "）";
                return false;
            }
            bool inRange = (!item.Lower.HasValue || value >= item.Lower.Value)
                && (!item.Upper.HasValue || value <= item.Upper.Value);
            if (!inRange)
            {
                detail = $"实测 {ExpressionValues.Format(value)}，要求 {item.RangeText}";
            }
            return inRange;
        }
    }
}
