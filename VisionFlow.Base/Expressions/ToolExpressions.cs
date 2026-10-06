using System.Collections.Generic;

namespace VisionFlow.Expressions
{
    /// <summary>写在工具参数中的一个表达式。校验器据此检查语法、引用作用域和局部名称。</summary>
    public sealed class ToolExpressionDef
    {
        private static readonly string[] Empty = new string[0];

        public ToolExpressionDef(string parameter, string text)
        {
            Parameter = parameter;
            Text = text;
        }

        /// <summary>校验结果中显示的参数名，如“计算式 第 2 行”。</summary>
        public string Parameter { get; private set; }

        public string Text { get; private set; }

        /// <summary>允许使用的局部名称（如数组处理的 Item、ItemIndex），不区分大小写；其他局部名称视为错误。</summary>
        public IReadOnlyCollection<string> LocalNames { get; set; } = Empty;

        /// <summary>
        /// 允许引用的本工具输出名（如变量计算中排在前面的结果），不区分大小写。
        /// 这些 {本模块名.输出名} 引用由工具自身按顺序提供取值，不按上游作用域检查。
        /// </summary>
        public IReadOnlyCollection<string> SelfOutputs { get; set; } = Empty;
    }

    /// <summary>
    /// 参数中含有表达式的工具。FlowValidator 会解析这些表达式，报告语法错误、
    /// 未定义的局部名称，并按与 [InputRef] 相同的作用域规则检查其中的变量引用。
    /// 实现不应抛出异常。
    /// </summary>
    public interface IExpressionTool
    {
        IEnumerable<ToolExpressionDef> GetExpressions();
    }
}
