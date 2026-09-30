using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// VF-01 回归：引用校验与运行时解析使用同一套语义；If/Else 与循环体作用域一致。
/// </summary>
public class ReferenceValidationTests
{
    public sealed class FakeItem
    {
        public double Score { get; set; }
        public double[] Scores { get; set; } = new[] { 0.5, 0.75 };
    }

    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Counts", VariableKind.Array, VariableType.Int)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(FakeItem),
        Members = new[] { "Score" })]
    private sealed class ProducerTool : ToolBase
    {
        public ProducerTool(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "MatchCount", VariableType.Int, 2));
            SetOutput(ctx, Variable.Array(ModuleName, "Counts", VariableType.Int, new[] { 2, 3 }));
            SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, new[] { 0.5, 0.75 }));
            SetOutput(ctx, Variable.Array(ModuleName, "Items", VariableType.Object,
                new[] { new FakeItem { Score = 0.5 }, new FakeItem { Score = 0.75 } }));
            return NodeResult.Ok;
        }
    }

    private sealed class ConsumerTool : ToolBase
    {
        [InputRef("Double输入", typeof(double), Optional = true)]
        public string? DoublePath { get; set; }

        [InputRef("Int输入", typeof(int), Optional = true)]
        public string? IntPath { get; set; }

        [InputRef("图像", typeof(HalconImage), Optional = true)]
        public string? ImagePath { get; set; }

        public List<double> DoubleValues { get; } = new();
        public List<int> IntValues { get; } = new();

        public ConsumerTool(string moduleName) : base(moduleName) { }
        public override NodeResult Run(FlowContext ctx)
        {
            if (!string.IsNullOrWhiteSpace(DoublePath))
                DoubleValues.Add(Input<double>(ctx, DoublePath));
            if (!string.IsNullOrWhiteSpace(IntPath))
                IntValues.Add(Input<int>(ctx, IntPath));
            if (!string.IsNullOrWhiteSpace(ImagePath))
                _ = Input<HalconImage>(ctx, ImagePath).Object;
            return NodeResult.Ok;
        }
    }

    private sealed class CollectionConsumerTool : ToolBase
    {
        [InputRef("Double输入", typeof(double), AcceptsCollection = true)]
        public string Path { get; set; } = "";

        public double Sum { get; private set; }

        public CollectionConsumerTool(string moduleName) : base(moduleName) { }

        public override NodeResult Run(FlowContext ctx)
        {
            object value = Input<object>(ctx, Path);
            Sum = value is IEnumerable<double> values ? values.Sum() : (double)value;
            return NodeResult.Ok;
        }
    }

    private static ToolNode Producer(string moduleName) => new ToolNode(new ProducerTool(moduleName));

    private static ToolNode Consumer(string moduleName, Action<ConsumerTool> configure)
    {
        var tool = new ConsumerTool(moduleName);
        configure(tool);
        return new ToolNode(tool);
    }

    private static SequenceNode Root(params FlowNode[] children)
    {
        var root = new SequenceNode("根");
        root.Children.AddRange(children);
        return root;
    }

    private static FlowValidationResult Validate(SequenceNode root) => FlowValidator.Validate(root);

    private static void AssertValid(SequenceNode root, bool execute = true)
    {
        FlowValidationResult result = Validate(root);
        Assert.True(result.IsValid,
            "期望校验通过，实际错误：" + string.Join("；", result.Issues.Select(i => i.ToString())));
        if (execute)
        {
            using var ctx = new FlowContext();
            NodeResult execution = root.Execute(ctx);
            Assert.True(execution.IsSuccess, execution.Message);
        }
    }

    private static void AssertInvalid(SequenceNode root, string messagePart)
    {
        FlowValidationResult result = Validate(root);
        Assert.False(result.IsValid, $"期望校验失败（{messagePart}），实际通过");
        Assert.Contains(result.Issues, i => i.Message.Contains(messagePart));
    }

    // ---------------- 数组下标与成员链（此前被候选精确匹配误拒） ----------------

    [Fact]
    public void 数组元素引用_不再被误拒()
    {
        AssertValid(Root(
            Producer("产"),
            Consumer("消", t => t.DoublePath = "产.Scores[0]")));
    }

    [Fact]
    public void 数组长度引用_不再被误拒()
    {
        AssertValid(Root(
            Producer("产"),
            Consumer("消", t => t.IntPath = "产.Scores.Count")));
    }

    [Fact]
    public void 数组成员链引用_不再被误拒()
    {
        AssertValid(Root(
            Producer("产"),
            Consumer("消", t => t.DoublePath = "产.Items[0].Score")));
    }

    [Fact]
    public void 数组整体引用_默认标量消费者拒绝且运行转换失败()
    {
        var producer = Producer("产");
        var consumer = Consumer("消", t => t.DoublePath = "产.Scores");
        var root = Root(producer, consumer);
        AssertInvalid(root, "不能赋给");
        using var ctx = new FlowContext();
        Assert.True(producer.Execute(ctx).IsSuccess);
        Assert.Throws<InvalidCastException>(() => consumer.Tool.Run(ctx));
        Assert.DoesNotContain(RefCandidateService.ForInput(root, consumer, typeof(double)),
            c => c.Path == "产.Scores");
    }

    [Theory]
    [InlineData("产.Scores", 1.25)]
    [InlineData("产.Scores[0]", 0.5)]
    [InlineData("产.Items[0].Scores", 1.25)]
    public void 显式声明集合消费者_接受数组和单值并实际消费(string path, double expected)
    {
        var tool = new CollectionConsumerTool("消") { Path = path };
        var consumer = new ToolNode(tool);
        var root = Root(Producer("产"), consumer);
        AssertValid(root);
        Assert.Equal(expected, tool.Sum);
        ToolInputRefDef def = Assert.Single(ToolMetadata.GetInputRefs(tool.GetType()));
        Assert.True(def.AcceptsCollection);
        Assert.Contains(RefCandidateService.ForInput(root, consumer, def.ExpectedType, def.AcceptsCollection),
            c => c.Path == "产.Scores");
        Assert.Contains(RefCandidateService.ForInput(root, consumer, typeof(double[])),
            c => c.Path == "产.Scores");
    }

    [Fact]
    public void Int数组不能绑定次数_实际循环执行到转换失败()
    {
        var producer = Producer("产");
        var loop = ForLoopNode.Count("循环", Operand.Ref("产.Counts"));
        loop.Body.Add(Consumer("消", t => t.IntPath = "Loop.Index"));
        var root = Root(producer, loop);
        AssertInvalid(root, "不能赋给");
        using var ctx = new FlowContext();
        NodeResult result = root.Execute(ctx);
        Assert.False(result.IsSuccess);
        Assert.Contains(ctx.NodeReports, r => r.NodeId == loop.Id && r.Status == NodeStatus.Failed);
        Assert.Throws<InvalidCastException>(() => loop.CountSource.GetInt(ctx));
    }

    [Fact]
    public void Int数组下标或长度可绑定次数_执行正确次数()
    {
        var tool = new ConsumerTool("消") { IntPath = "Loop.Index" };
        var loop = ForLoopNode.Count("循环", Operand.Ref("产.Counts[0]"));
        loop.Body.Add(new ToolNode(tool));
        AssertValid(Root(Producer("产"), loop));
        Assert.Equal(new[] { 0, 1 }, tool.IntValues);
        tool.IntValues.Clear();
        loop.CountSource = Operand.Ref("产.Counts.Count");
        AssertValid(Root(Producer("产"), loop));
        Assert.Equal(new[] { 0, 1 }, tool.IntValues);
    }

    [Fact]
    public void 非数组使用下标_拒绝()
    {
        AssertInvalid(Root(
            Producer("产"),
            Consumer("消", t => t.DoublePath = "产.MatchCount[0]")), "不是数组输出");
    }

    [Fact]
    public void 已知类型上不存在的成员_静态拒绝()
    {
        AssertInvalid(Root(
            Producer("产"),
            Consumer("消", t => t.DoublePath = "产.Items[0].Missing")), "不存在成员");
    }

    [Fact]
    public void 不存在的变量_拒绝()
    {
        AssertInvalid(Root(
            Producer("产"),
            Consumer("消", t => t.DoublePath = "没有.MatchCount")), "不存在");
    }

    [Fact]
    public void 类型不符_拒绝()
    {
        AssertInvalid(Root(
            Producer("产"),
            Consumer("消", t => t.IntPath = "产.Scores[0]")), "不能赋给");
    }

    [Fact]
    public void 外部输入图像_可用()
    {
        AssertValid(Root(Consumer("消", t => t.ImagePath = "Input.Image")), execute: false);
    }

    // ---------------- If / Else 分支作用域 ----------------

    private static IfElseNode IfElse(string name, Action<IfElseNode> configure)
    {
        var node = new IfElseNode(name, new ComparisonCondition
        {
            Left = Operand.Const(1),
            Right = Operand.Const(1),
            Operator = ComparisonOperator.Equal
        });
        configure(node);
        return node;
    }

    [Fact]
    public void Else分支_不能引用If分支私有输出()
    {
        var ifElse = IfElse("分支", n =>
        {
            n.IfBranch.Add(Producer("If内"));
            n.ElseBranch.Add(Consumer("Else消", t => t.DoublePath = "If内.Scores[0]"));
        });
        AssertInvalid(Root(ifElse), "作用域");
    }

    [Fact]
    public void If分支_不能引用Else分支私有输出()
    {
        var ifElse = IfElse("分支", n =>
        {
            n.IfBranch.Add(Consumer("If消", t => t.DoublePath = "Else内.Scores[0]"));
            n.ElseBranch.Add(Producer("Else内"));
        });
        AssertInvalid(Root(ifElse), "作用域");
    }

    [Fact]
    public void 分支内_引用本分支上游_允许()
    {
        var ifElse = IfElse("分支", n =>
        {
            n.IfBranch.Add(Producer("If内"));
            n.IfBranch.Add(Consumer("If消", t => t.DoublePath = "If内.Scores[0]"));
        });
        AssertValid(Root(ifElse));
    }

    [Fact]
    public void 分支外_不能引用分支内部输出()
    {
        var ifElse = IfElse("分支", n => n.IfBranch.Add(Producer("If内")));
        AssertInvalid(Root(ifElse, Consumer("消", t => t.DoublePath = "If内.Scores[0]")), "作用域");
    }

    [Fact]
    public void 分支公共输出_对外可见()
    {
        var ifElse = IfElse("分支", n =>
        {
            n.IfBranch.Add(Producer("If内"));
            n.ElseBranch.Add(Producer("Else内"));
            n.Outputs.Add(new BranchOutputDef
            {
                Name = "Count",
                Kind = VariableKind.Single,
                Type = VariableType.Int,
                IfValue = Operand.Ref("If内.MatchCount"),
                ElseValue = Operand.Ref("Else内.MatchCount")
            });
        });
        AssertValid(Root(ifElse, Consumer("消", t => t.IntPath = "分支.Count")));
    }

    [Fact]
    public void 分支公共输出_跨分支引用_拒绝()
    {
        var ifElse = IfElse("分支", n =>
        {
            n.IfBranch.Add(Producer("If内"));
            n.ElseBranch.Add(Producer("Else内"));
            n.Outputs.Add(new BranchOutputDef
            {
                Name = "Count",
                Kind = VariableKind.Single,
                Type = VariableType.Int,
                // If 取值错误地引用了 Else 分支的私有输出
                IfValue = Operand.Ref("Else内.MatchCount"),
                ElseValue = Operand.Ref("Else内.MatchCount")
            });
        });
        AssertInvalid(Root(ifElse), "作用域");
    }

    [Fact]
    public void 嵌套IfElse_只暴露公共输出()
    {
        var inner = IfElse("内层", n => n.IfBranch.Add(Producer("深层")));
        var outer = IfElse("外层", n => n.IfBranch.Add(inner));
        AssertInvalid(Root(outer, Consumer("消", t => t.DoublePath = "深层.Scores[0]")), "作用域");
    }

    // ---------------- 循环作用域 ----------------

    [Fact]
    public void 循环体输出_对循环外不可见()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(3));
        loop.Body.Add(Producer("体内"));
        AssertInvalid(Root(loop, Consumer("消", t => t.IntPath = "体内.MatchCount")), "作用域");
    }

    [Fact]
    public void 循环体内_引用体内上游_允许()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(3));
        loop.Body.Add(Producer("体内"));
        loop.Body.Add(Consumer("消", t => t.IntPath = "体内.MatchCount"));
        AssertValid(Root(loop));
    }

    [Fact]
    public void 按次数循环_LoopIndex可用_LoopCurrent拒绝()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(3));
        loop.Body.Add(Consumer("消", t => t.IntPath = "Loop.Index"));
        AssertValid(Root(loop));

        var loop2 = ForLoopNode.Count("循环", Operand.Const(3));
        loop2.Body.Add(Consumer("消", t => t.DoublePath = "Loop.Current"));
        AssertInvalid(Root(loop2), "Loop.Current");
    }

    [Fact]
    public void 按集合循环_LoopCurrent可用()
    {
        var loop = ForLoopNode.Each("循环", "产.Scores");
        loop.Body.Add(Consumer("消", t => t.DoublePath = "Loop.Current"));
        AssertValid(Root(Producer("产"), loop));
    }

    [Fact]
    public void 循环外_不能使用Loop变量()
    {
        AssertInvalid(Root(Consumer("消", t => t.IntPath = "Loop.Index")), "不在任何循环内");
    }

    // ---------------- 嵌套分支与循环组合 ----------------

    [Fact]
    public void 循环内的分支_互斥作用域仍然成立()
    {
        var loop = ForLoopNode.Count("循环", Operand.Const(2));
        var ifElse = IfElse("分支", n =>
        {
            n.IfBranch.Add(Producer("If内"));
            n.ElseBranch.Add(Consumer("Else消", t => t.DoublePath = "If内.Scores[0]"));
        });
        loop.Body.Add(ifElse);
        AssertInvalid(Root(loop), "作用域");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void 循环内分支公共输出_使用当前循环Index且不泄漏分支作用域(bool takeIf)
    {
        var branch = IfElse("分支", n =>
        {
            n.Condition.Right = Operand.Const(takeIf ? 1 : 0);
            n.Outputs.Add(new BranchOutputDef
            {
                Name = "Index",
                Kind = VariableKind.Single,
                Type = VariableType.Int,
                IfValue = Operand.Ref("Loop.Index"),
                ElseValue = Operand.Ref("Loop.Index")
            });
        });
        var consumer = new ConsumerTool("消") { IntPath = "分支.Index" };
        var loop = ForLoopNode.Count("循环", Operand.Const(3));
        loop.Body.Add(branch);
        loop.Body.Add(new ToolNode(consumer));
        var root = Root(loop);
        AssertValid(root);
        Assert.Equal(new[] { 0, 1, 2 }, consumer.IntValues);
        RefScope scope = RefCandidateService.ScopeForBranchOutput(root, branch, IfBranch.If);
        Assert.Equal(RefLoopMode.Count, scope.LoopMode);
        Assert.Contains(scope.Candidates, c => c.Path == "Loop.Index");
    }

    [Fact]
    public void 嵌套循环分支公共输出_使用最内层Each元素及成员()
    {
        var branch = IfElse("分支", n => n.Outputs.Add(new BranchOutputDef
        {
            Name = "Score",
            Kind = VariableKind.Single,
            Type = VariableType.Double,
            IfValue = Operand.Ref("Loop.Current.Score"),
            ElseValue = Operand.Ref("Loop.Current.Score")
        }));
        var consumer = new ConsumerTool("消") { DoublePath = "分支.Score" };
        var inner = ForLoopNode.Each("内循环", "产.Items");
        inner.Body.Add(branch);
        inner.Body.Add(new ToolNode(consumer));
        var outer = ForLoopNode.Count("外循环", Operand.Const(2));
        outer.Body.Add(inner);
        var root = Root(Producer("产"), outer);
        AssertValid(root);
        Assert.Equal(new[] { 0.5, 0.75, 0.5, 0.75 }, consumer.DoubleValues);
        RefScope scope = RefCandidateService.ScopeForBranchOutput(root, branch, IfBranch.Else);
        Assert.Equal(RefLoopMode.Each, scope.LoopMode);
        Assert.Equal(typeof(FakeItem), scope.LoopCurrentElementType);
    }

    [Fact]
    public void 内层Count屏蔽外层Each的Current_分支公共输出也拒绝()
    {
        var branch = IfElse("分支", n => n.Outputs.Add(new BranchOutputDef
        {
            Name = "Score",
            Kind = VariableKind.Single,
            Type = VariableType.Double,
            IfValue = Operand.Ref("Loop.Current.Score"),
            ElseValue = Operand.Ref("Loop.Current.Score")
        }));
        var inner = ForLoopNode.Count("内循环", Operand.Const(2));
        inner.Body.Add(branch);
        var outer = ForLoopNode.Each("外循环", "产.Items");
        outer.Body.Add(inner);
        AssertInvalid(Root(Producer("产"), outer), "按次数循环不提供 Loop.Current");
    }

    [Fact]
    public void 分支公共数组输出_仍可声明和索引但不能作为标量()
    {
        var branch = IfElse("分支", n => n.Outputs.Add(new BranchOutputDef
        {
            Name = "Counts",
            Kind = VariableKind.Array,
            Type = VariableType.Int,
            IfValue = Operand.Ref("产.Counts"),
            ElseValue = Operand.Ref("产.Counts")
        }));
        var consumer = Consumer("消", t => t.IntPath = "分支.Counts[1]");
        var root = Root(Producer("产"), branch, consumer);
        AssertValid(root);
        Assert.Equal(new[] { 3 }, ((ConsumerTool)consumer.Tool).IntValues);
        ((ConsumerTool)consumer.Tool).IntPath = "分支.Counts";
        AssertInvalid(root, "不能赋给");
        branch.Outputs[0].Kind = VariableKind.Single;
        AssertInvalid(root, "不能作为");
    }

    [Fact]
    public void 外部数组声明_候选标记集合_下标成员及Each与运行一致()
    {
        const string path = "Input.ReferenceItems";
        ExternalInputRegistry.Register(new ExternalInputDef(path, typeof(FakeItem[])));
        try
        {
            var indexed = new ConsumerTool("下标") { DoublePath = path + "[0].Score" };
            var each = new ConsumerTool("循环消") { DoublePath = "Loop.Current.Score" };
            var loop = ForLoopNode.Each("循环", path);
            loop.Body.Add(new ToolNode(each));
            var root = Root(new ToolNode(indexed), loop);
            AssertValid(root, execute: false);
            RefCandidate candidate = Assert.Single(RefCandidateService.Collections(root, loop));
            Assert.Equal(path, candidate.Path);
            Assert.Equal(typeof(FakeItem), candidate.ClrType);
            Assert.Equal(typeof(FakeItem),
                RefCandidateService.ScopeForNode(root, loop.Body[0]).LoopCurrentElementType);
            using var ctx = new FlowContext();
            ctx.SetVariable(Variable.Array("Input", "ReferenceItems", VariableType.Object,
                new[] { new FakeItem { Score = 0.2 }, new FakeItem { Score = 0.8 } }));
            NodeResult result = root.Execute(ctx);
            Assert.True(result.IsSuccess, result.Message);
            Assert.Equal(new[] { 0.2 }, indexed.DoubleValues);
            Assert.Equal(new[] { 0.2, 0.8 }, each.DoubleValues);
        }
        finally
        {
            ExternalInputRegistry.Unregister(path);
        }
    }

    [Theory]
    [MemberData(nameof(FollowMeasureMatrixTests.ToolTypes), MemberType = typeof(FollowMeasureMatrixTests))]
    public void 跟随工具矩阵允许集合_序号仍严格标量(Type toolType)
    {
        ExternalInputRegistry.Register(new ExternalInputDef("Input.ReferenceMatrices", typeof(HomMat2D[])));
        try
        {
            var tool = (ToolBase)Activator.CreateInstance(toolType, "测量")!;
            var node = new ToolNode(tool);
            var root = Root(Producer("产"), node);
            ToolInputRefDef matrix = ToolMetadata.GetInputRefs(toolType).Single(d => d.PropertyName == "MatrixPath");
            ToolInputRefDef index = ToolMetadata.GetInputRefs(toolType).Single(d => d.PropertyName == "IndexPath");
            ToolMetadata.GetInputRefs(toolType).Single(d => d.PropertyName == "ImagePath").Property.SetValue(tool, "Input.Image");
            matrix.Property.SetValue(tool, "Input.ReferenceMatrices");
            Assert.True(matrix.AcceptsCollection);
            Assert.False(index.AcceptsCollection);
            AssertValid(root, execute: false);
            Assert.Contains(RefCandidateService.ForInput(root, node, matrix.ExpectedType, matrix.AcceptsCollection),
                c => c.Path == "Input.ReferenceMatrices");
            index.Property.SetValue(tool, "产.Counts");
            AssertInvalid(root, "不能赋给");
            Assert.DoesNotContain(RefCandidateService.ForInput(root, node, index.ExpectedType, index.AcceptsCollection),
                c => c.Path == "产.Counts");
        }
        finally
        {
            ExternalInputRegistry.Unregister("Input.ReferenceMatrices");
        }
    }
}
