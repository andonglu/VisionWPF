using System;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Expressions;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>变量计算中的一条计算式。</summary>
    public sealed class ExpressionCalcItem
    {
        /// <summary>在配置文本中的行号（从 1 开始），用于提示。</summary>
        public int LineNumber { get; set; }
        public string Name { get; set; }
        public VariableType Type { get; set; }
        public string Expression { get; set; }

        /// <summary>校验与日志中显示的参数名。</summary>
        public string Parameter
        {
            get { return $"计算式 第 {LineNumber} 行"; }
        }
    }

    /// <summary>
    /// 变量计算（LD-02）：按行书写多条表达式，每条产生一个由用户命名的单值输出。
    /// 后面的计算可以引用前面的结果（{本模块名.结果名}）。全部计算成功后才写出输出；
    /// 任何一条失败（引用无效、除零、类型不符）都按工具失败处理并指出行号，结果为 NaN 不算失败。
    /// </summary>
    public sealed class ExpressionCalcTool : ToolBase, IDynamicOutputTool, IExpressionTool, IToolConfigurationCheck
    {
        /// <summary>可选的结果类型。</summary>
        public static readonly IReadOnlyList<VariableType> SupportedTypes = new[]
        {
            VariableType.Double, VariableType.Int, VariableType.Bool, VariableType.String
        };

        /// <summary>
        /// 计算式，每行一条：名称|类型|表达式，类型为 Int / Double / Bool / String。
        /// 只按前两个 | 分隔，表达式中可以出现 ||。
        /// </summary>
        public string Expressions { get; set; } = string.Empty;

        public ExpressionCalcTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>解析计算式配置；格式问题写入 issues，格式正确的行（名称是否合法另行检查）返回。</summary>
        public static List<ExpressionCalcItem> ParseItems(string text, List<ToolConfigurationIssue> issues)
        {
            var items = new List<ExpressionCalcItem>();
            string[] lines = (text ?? string.Empty).Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].TrimEnd('\r');
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string parameter = $"计算式 第 {i + 1} 行";
                int first = line.IndexOf('|');
                int second = first < 0 ? -1 : line.IndexOf('|', first + 1);
                if (second < 0)
                {
                    issues?.Add(new ToolConfigurationIssue(parameter, "格式应为 名称|类型|表达式"));
                    continue;
                }

                string typeText = line.Substring(first + 1, second - first - 1).Trim();
                VariableType? type = SupportedTypes
                    .Where(t => string.Equals(t.ToString(), typeText, StringComparison.OrdinalIgnoreCase))
                    .Select(t => (VariableType?)t)
                    .FirstOrDefault();
                if (type == null)
                {
                    issues?.Add(new ToolConfigurationIssue(parameter, $"类型“{typeText}”无效，应为 Int / Double / Bool / String"));
                    continue;
                }

                items.Add(new ExpressionCalcItem
                {
                    LineNumber = i + 1,
                    Name = line.Substring(0, first).Trim(),
                    Type = type.Value,
                    Expression = line.Substring(second + 1).Trim()
                });
            }

            if (items.Count == 0 && (issues == null || issues.Count == 0))
            {
                issues?.Add(new ToolConfigurationIssue("计算式", "未配置计算式"));
            }
            return items;
        }

        /// <summary>把计算式写回配置文本（每行 名称|类型|表达式）。</summary>
        public static string FormatItems(IEnumerable<ExpressionCalcItem> items)
        {
            return string.Join("\n", items.Select(i => $"{i.Name}|{i.Type}|{i.Expression}"));
        }

        /// <summary>
        /// 检查第 index 条计算式（供编辑器实时提示）：结果名、重名、语法、局部名称，以及对本模块结果的引用顺序。
        /// 其他模块的引用交给 checkExternalReference（返回错误说明，无问题时返回 null；为 null 时不检查）。
        /// 没有问题时返回 null。
        /// </summary>
        public static string CheckItem(IReadOnlyList<ExpressionCalcItem> items, int index, string moduleName,
            Func<string, string> checkExternalReference)
        {
            ExpressionCalcItem item = items[index];
            if (string.IsNullOrWhiteSpace(item.Name))
            {
                return "结果名不能为空";
            }
            if (!ToolMetadata.IsValidOutputName(item.Name) || item.Name.IndexOf('|') >= 0)
            {
                return "结果名不能包含空白或 . [ ] { } | 字符";
            }
            if (items.Where((other, i) => i != index && string.Equals(other.Name, item.Name, StringComparison.OrdinalIgnoreCase)).Any())
            {
                return $"结果名“{item.Name}”重复";
            }
            if ((item.Expression ?? string.Empty).IndexOf('\n') >= 0)
            {
                return "表达式不能换行";
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
                VariableReference reference = VariableReference.Parse(path);
                if (string.Equals(reference.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase))
                {
                    bool earlier = items.Take(index)
                        .Any(p => string.Equals(p.Name, reference.VarName, StringComparison.OrdinalIgnoreCase));
                    if (!earlier)
                    {
                        return $"{{{path}}}：只能引用排在前面的计算结果";
                    }
                    continue;
                }
                string referenceError = checkExternalReference?.Invoke(path);
                if (referenceError != null)
                {
                    return referenceError;
                }
            }
            return null;
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            var issues = new List<ToolConfigurationIssue>();
            ParseItems(Expressions, issues);
            return issues;
        }

        public IReadOnlyList<ToolOutputDef> GetDynamicOutputs()
        {
            return ParseItems(Expressions, null)
                .Select(i => new ToolOutputDef { Name = i.Name, Kind = VariableKind.Single, Type = i.Type })
                .ToList();
        }

        public IEnumerable<ToolExpressionDef> GetExpressions()
        {
            List<ExpressionCalcItem> items = ParseItems(Expressions, null);
            for (int i = 0; i < items.Count; i++)
            {
                yield return new ToolExpressionDef(items[i].Parameter, items[i].Expression)
                {
                    SelfOutputs = items.Take(i).Select(p => p.Name).ToList()
                };
            }
        }

        public override NodeResult Run(FlowContext ctx)
        {
            var issues = new List<ToolConfigurationIssue>();
            List<ExpressionCalcItem> items = ParseItems(Expressions, issues);
            if (issues.Count > 0)
            {
                return NodeResult.Fail($"{ModuleName} {issues[0].Parameter}：{issues[0].Message}");
            }

            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ExpressionCalcItem item in items)
            {
                if (!ToolMetadata.IsValidOutputName(item.Name))
                {
                    return NodeResult.Fail($"{ModuleName} {item.Parameter}：结果名“{item.Name}”不能为空，也不能包含空白或 . [ ] {{ }} 字符");
                }
                if (!names.Add(item.Name))
                {
                    return NodeResult.Fail($"{ModuleName} {item.Parameter}：结果名“{item.Name}”重复");
                }
            }

            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (ExpressionCalcItem item in items)
            {
                try
                {
                    CompiledExpression expression = ExpressionParser.Parse(item.Expression);
                    object value = expression.Evaluate(path => Resolve(ctx, path, values));
                    values[item.Name] = ExpressionValues.ConvertTo(value, item.Type);
                }
                catch (ExpressionException ex)
                {
                    return NodeResult.Fail($"{ModuleName} {item.Parameter}（{item.Name}）：{ex.Message}");
                }
            }

            foreach (ExpressionCalcItem item in items)
            {
                SetOutput(ctx, Variable.Single(ModuleName, item.Name, item.Type, values[item.Name]));
            }
            ctx.AddLog(FlowLogLevel.Info,
                "[变量计算] " + string.Join("，", items.Select(i => $"{i.Name}={ExpressionValues.Format(values[i.Name])}")));
            return NodeResult.Ok;
        }

        /// <summary>本模块的引用取已算出的结果（只能引用排在前面的行），其余按流程变量解析。</summary>
        private object Resolve(FlowContext ctx, string path, Dictionary<string, object> computed)
        {
            if (path.IndexOf('.') < 0)
            {
                throw new InvalidOperationException($"未定义的名称 {path}");
            }
            VariableReference reference = VariableReference.Parse(path);
            if (!string.Equals(reference.ModuleName, ModuleName, StringComparison.OrdinalIgnoreCase))
            {
                return reference.Resolve(ctx);
            }
            if (reference.ElementIndex.HasValue || reference.MemberPath.Length > 0)
            {
                throw new InvalidOperationException("本工具的计算结果是单值，不支持下标或成员");
            }
            if (computed.TryGetValue(reference.VarName, out object value))
            {
                return value;
            }
            throw new InvalidOperationException("只能引用排在前面的计算结果");
        }
    }
}
