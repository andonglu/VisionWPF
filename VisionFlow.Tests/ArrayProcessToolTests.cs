using System.Text.Json.Nodes;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>数组处理（LD-03）：统计、取值、排序、筛选、变换、拼接、逐元素运算、去重、文本数组与校验。</summary>
public class ArrayProcessToolTests
{
    [ToolOutput("Values", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Ints", VariableKind.Array, VariableType.Int)]
    [ToolOutput("Names", VariableKind.Array, VariableType.String)]
    [ToolOutput("Scalar", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Mixed", VariableKind.Array, VariableType.Object)]
    public sealed class ProducerTool : ToolBase
    {
        public ProducerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Array(ModuleName, "Values", VariableType.Double, new[] { 3.0, double.NaN, 1.0, 2.0, 3.0 }));
            SetOutput(ctx, Variable.Array(ModuleName, "Ints", VariableType.Int, new[] { 1, 2, 3 }));
            SetOutput(ctx, Variable.Array(ModuleName, "Names", VariableType.String, new[] { "b", "a", "b" }));
            SetOutput(ctx, Variable.Single(ModuleName, "Scalar", VariableType.Double, 10.0));
            SetOutput(ctx, Variable.Array(ModuleName, "Mixed", VariableType.Object, new object[] { 1.0, "x" }));
            return NodeResult.Ok;
        }
    }

    private static SequenceNode Root(ArrayProcessTool tool)
    {
        var root = new SequenceNode("根");
        root.Children.Add(new ToolNode(new ProducerTool("测量1")));
        root.Children.Add(new ToolNode(tool));
        return root;
    }

    private static ArrayProcessTool Tool(ArrayOperation operation, string path = "测量1.Values", Action<ArrayProcessTool>? configure = null)
    {
        var tool = new ArrayProcessTool("数组1") { Operation = operation, ArrayPath = path };
        configure?.Invoke(tool);
        return tool;
    }

    private static FlowContext Run(ArrayProcessTool tool)
    {
        var ctx = new FlowContext();
        NodeResult result = Root(tool).Execute(ctx);
        Assert.True(result.IsSuccess, result.Message);
        return ctx;
    }

    private static NodeResult RunFailing(ArrayProcessTool tool)
    {
        using var ctx = new FlowContext();
        NodeResult result = Root(tool).Execute(ctx);
        Assert.False(result.IsSuccess);
        return result;
    }

    private static T Get<T>(FlowContext ctx, string name) => (T)ctx.GetVariable("数组1", name).Value;

    private static void AssertNumbers(double[] expected, double[] actual)
    {
        Assert.Equal(expected.Length, actual.Length);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i].Equals(actual[i]) || Math.Abs(expected[i] - actual[i]) < 1e-9, $"第 {i} 个：期望 {expected[i]}，实际 {actual[i]}");
        }
    }

    [Fact]
    public void 统计_忽略NaN且序号指向原位置()
    {
        using FlowContext ctx = Run(Tool(ArrayOperation.Statistics));
        AssertNumbers(new[] { 3.0, double.NaN, 1.0, 2.0, 3.0 }, Get<double[]>(ctx, "Values"));
        Assert.Equal(5, Get<int>(ctx, "Count"));
        Assert.Equal(4, Get<int>(ctx, "ValidCount"));
        Assert.Equal(9.0, Get<double>(ctx, "Sum"));
        Assert.Equal(2.25, Get<double>(ctx, "Mean"));
        Assert.Equal(3.0, Get<double>(ctx, "Max"));
        Assert.Equal(1.0, Get<double>(ctx, "Min"));
        Assert.Equal(2.0, Get<double>(ctx, "Range"));
        Assert.Equal(Math.Sqrt(0.6875), Get<double>(ctx, "StdDev"), 9);
        Assert.Equal(0, Get<int>(ctx, "MaxIndex"));
        Assert.Equal(2, Get<int>(ctx, "MinIndex"));
        Assert.Equal(3.0, Get<double>(ctx, "Value"));
    }

    [Theory]
    [InlineData(2, 1.0, 2)]
    [InlineData(-1, 3.0, 4)]
    [InlineData(-5, 3.0, 0)]
    public void 取值_支持从末尾数(int index, double expected, int expectedIndex)
    {
        using FlowContext ctx = Run(Tool(ArrayOperation.ElementAt, configure: t => t.Index = index));
        Assert.Equal(expected, Get<double>(ctx, "Value"));
        Assert.Equal(new[] { expectedIndex }, Get<int[]>(ctx, "Indices"));
    }

    [Fact]
    public void 取值越界_默认失败_关闭失败策略时输出未找到()
    {
        Assert.Contains("结果为空", RunFailing(Tool(ArrayOperation.ElementAt, configure: t => t.Index = 10)).Message);

        using FlowContext ctx = Run(Tool(ArrayOperation.ElementAt, configure: t => { t.Index = 10; t.FailWhenNotFound = false; }));
        Assert.False(Get<bool>(ctx, "Found"));
        Assert.Equal(0, Get<int>(ctx, "Count"));
        Assert.True(double.IsNaN(Get<double>(ctx, "Value")));
    }

    [Theory]
    [InlineData(false, new[] { 1.0, 2.0, 3.0, 3.0, double.NaN }, new[] { 2, 3, 0, 4, 1 })]
    [InlineData(true, new[] { 3.0, 3.0, 2.0, 1.0, double.NaN }, new[] { 0, 4, 3, 2, 1 })]
    public void 排序_稳定且NaN排在最后(bool descending, double[] expected, int[] indices)
    {
        using FlowContext ctx = Run(Tool(ArrayOperation.Sort, configure: t => t.Descending = descending));
        AssertNumbers(expected, Get<double[]>(ctx, "Values"));
        Assert.Equal(indices, Get<int[]>(ctx, "Indices"));
    }

    [Fact]
    public void 筛选_使用Item与ItemIndex并对结果统计()
    {
        using FlowContext ctx = Run(Tool(ArrayOperation.Filter, configure: t => t.Expression = "{Item} > 1 && {itemindex} < 4"));
        AssertNumbers(new[] { 3.0, 2.0 }, Get<double[]>(ctx, "Values"));
        Assert.Equal(new[] { 0, 3 }, Get<int[]>(ctx, "Indices"));
        Assert.Equal(5.0, Get<double>(ctx, "Sum"));
        Assert.Equal(2, Get<int>(ctx, "Count"));
    }

    [Fact]
    public void 筛选结果不是布尔_失败()
    {
        Assert.Contains("应为布尔值", RunFailing(Tool(ArrayOperation.Filter, configure: t => t.Expression = "{Item} * 2")).Message);
    }

    [Fact]
    public void 变换_可引用流程变量()
    {
        using FlowContext ctx = Run(Tool(ArrayOperation.Map, "测量1.Ints", t => t.Expression = "{Item} * 0.5 + {测量1.Scalar}"));
        AssertNumbers(new[] { 10.5, 11.0, 11.5 }, Get<double[]>(ctx, "Values"));
    }

    [Fact]
    public void 拼接()
    {
        using FlowContext ctx = Run(Tool(ArrayOperation.Concat, "测量1.Ints", t => t.SecondPath = "测量1.Ints"));
        AssertNumbers(new[] { 1.0, 2.0, 3.0, 1.0, 2.0, 3.0 }, Get<double[]>(ctx, "Values"));
    }

    [Fact]
    public void 逐元素运算_等长一一对应_单值对多_除零为NaN()
    {
        using (FlowContext ctx = Run(Tool(ArrayOperation.ElementWise, "测量1.Ints", t => { t.SecondPath = "测量1.Scalar"; t.ElementWiseOperator = ArrayElementWiseOperator.Multiply; })))
        {
            AssertNumbers(new[] { 10.0, 20.0, 30.0 }, Get<double[]>(ctx, "Values"));
        }
        using (FlowContext ctx = Run(Tool(ArrayOperation.ElementWise, "测量1.Ints", t => { t.SecondPath = "测量1.Ints"; t.ElementWiseOperator = ArrayElementWiseOperator.Subtract; })))
        {
            AssertNumbers(new[] { 0.0, 0.0, 0.0 }, Get<double[]>(ctx, "Values"));
        }
        using (FlowContext ctx = Run(Tool(ArrayOperation.ElementWise, "测量1.Ints", t => { t.SecondPath = "测量1.Values"; t.ElementWiseOperator = ArrayElementWiseOperator.Divide; t.ArrayPath = "测量1.Scalar"; })))
        {
            AssertNumbers(new[] { 10.0 / 3, double.NaN, 10.0, 5.0, 10.0 / 3 }, Get<double[]>(ctx, "Values"));
        }
        Assert.Contains("3 对 5", RunFailing(Tool(ArrayOperation.ElementWise, "测量1.Ints", t => t.SecondPath = "测量1.Values")).Message);
    }

    [Fact]
    public void 去重_保持首次出现顺序()
    {
        using FlowContext ctx = Run(Tool(ArrayOperation.Distinct));
        AssertNumbers(new[] { 3.0, double.NaN, 1.0, 2.0 }, Get<double[]>(ctx, "Values"));
        Assert.Equal(new[] { 0, 1, 2, 3 }, Get<int[]>(ctx, "Indices"));
    }

    [Fact]
    public void 文本数组_输出字符串且统计为NaN()
    {
        ArrayProcessTool tool = Tool(ArrayOperation.Sort, "测量1.Names", t => t.ElementType = ArrayElementType.Text);
        using FlowContext ctx = Run(tool);
        Variable values = ctx.GetVariable("数组1", "Values");
        Assert.Equal(VariableType.String, values.Type);
        Assert.Equal(new[] { "a", "b", "b" }, (string[])values.Value);
        Assert.Equal("a", Get<string>(ctx, "Value"));
        Assert.True(double.IsNaN(Get<double>(ctx, "Sum")));
        Assert.Equal(-1, Get<int>(ctx, "MaxIndex"));

        using FlowContext distinct = Run(Tool(ArrayOperation.Distinct, "测量1.Names", t => t.ElementType = ArrayElementType.Text));
        Assert.Equal(new[] { "b", "a" }, (string[])distinct.GetVariable("数组1", "Values").Value);
    }

    [Fact]
    public void 数值数组中有非数值元素_失败并提示改为文本()
    {
        Assert.Contains("不是数值", RunFailing(Tool(ArrayOperation.Statistics, "测量1.Mixed")).Message);
    }

    [Fact]
    public void Values与Value的类型随元素类型变化_下游候选可见()
    {
        var consumerTarget = new ToolNode(new ArrayProcessTool("数组2") { ArrayPath = "数组1.Values" });
        SequenceNode root = Root(Tool(ArrayOperation.Sort));
        root.Children.Add(consumerTarget);
        Assert.Equal(typeof(double), RefCandidateService.ForNode(root, consumerTarget).Single(c => c.Path == "数组1.Value").ClrType);

        ((ArrayProcessTool)((ToolNode)root.Children[1]).Tool).ElementType = ArrayElementType.Text;
        Assert.Equal(typeof(string), RefCandidateService.ForNode(root, consumerTarget).Single(c => c.Path == "数组1.Value").ClrType);
        Assert.True(RefCandidateService.ForNode(root, consumerTarget).Single(c => c.Path == "数组1.Values").IsCollection);
    }

    [Theory]
    [InlineData(ArrayOperation.Filter, "", ArrayElementType.Number, "表达式", "不能为空")]
    [InlineData(ArrayOperation.Map, "{Other} + 1", ArrayElementType.Number, "表达式", "未定义的名称 {Other}")]
    [InlineData(ArrayOperation.Map, "{测量1.Missing} + {Item}", ArrayElementType.Number, "表达式", "测量1.Missing")]
    [InlineData(ArrayOperation.Concat, "", ArrayElementType.Number, "第二个数组", "需要配置第二个数组")]
    [InlineData(ArrayOperation.Statistics, "", ArrayElementType.Text, "Operation", "只支持数值数组")]
    public void 配置与表达式问题_校验报告(ArrayOperation operation, string expression, ArrayElementType elementType, string parameter, string messagePart)
    {
        ArrayProcessTool tool = Tool(operation, configure: t => { t.Expression = expression; t.ElementType = elementType; });
        FlowValidationIssue issue = Assert.Single(FlowValidator.Validate(Root(tool)).Issues);
        Assert.Equal(parameter, issue.Parameter);
        Assert.Contains(messagePart, issue.Message);
    }

    [Fact]
    public void 未使用表达式的操作_不校验表达式()
    {
        Assert.True(FlowValidator.Validate(Root(Tool(ArrayOperation.Sort, configure: t => t.Expression = "残留的 +"))).IsValid);
    }

    [Fact]
    public void 工具箱与流程文件_使用稳定ID并保存参数()
    {
        ToolboxRegistry.RegisterDefaults();
        ToolboxItem item = ToolboxRegistry.Items.Single(i => i.Id == "array-process");
        Assert.Equal("07 数据与判定", item.Category);

        ArrayProcessTool tool = Tool(ArrayOperation.Filter, configure: t =>
        {
            t.Expression = "{Item} > 1";
            t.ElementType = ArrayElementType.Number;
            t.Descending = true;
            t.Index = -2;
        });
        string json = FlowSerializer.Save(Root(tool));
        Assert.Equal("array-process", JsonNode.Parse(json)!["Children"]![1]!["Tool"]!["ToolId"]!.GetValue<string>());
        var loaded = Assert.IsType<ArrayProcessTool>(Assert.IsType<ToolNode>(FlowSerializer.Load(json).Children[1]).Tool);
        Assert.Equal((ArrayOperation.Filter, "{Item} > 1", true, -2), (loaded.Operation, loaded.Expression, loaded.Descending, loaded.Index));
    }
}
