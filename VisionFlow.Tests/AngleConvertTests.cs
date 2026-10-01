using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Nodes;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>角度换算：弧度/角度互转与范围折算。</summary>
public class AngleConvertTests
{
    [Theory]
    [InlineData(100, -90, 90, -80)]
    [InlineData(-100, -90, 90, 80)]
    [InlineData(90, -90, 90, -90)]
    [InlineData(-90, -90, 90, -90)]
    [InlineData(270, 0, 180, 90)]
    [InlineData(-30, 0, 180, 150)]
    [InlineData(180, 0, 180, 0)]
    [InlineData(190, -180, 180, -170)]
    [InlineData(-190, -180, 180, 170)]
    [InlineData(-30, 0, 360, 330)]
    [InlineData(725, 0, 360, 5)]
    [InlineData(50, -45, 45, -40)]
    [InlineData(-46, -45, 45, 44)]
    [InlineData(10, 30, 60, 40)]
    public void 折算到半开区间(double degrees, double min, double max, double expected)
    {
        Assert.Equal(expected, AngleMath.Fold(degrees, min, max), 9);
    }

    [Fact]
    public void 折算保留NaN()
    {
        Assert.True(double.IsNaN(AngleMath.Fold(double.NaN, -90, 90)));
    }

    private static FlowContext ContextWith(Variable variable)
    {
        var ctx = new FlowContext();
        ctx.SetVariable(variable);
        return ctx;
    }

    [Fact]
    public void 弧度转角度并限制到负90到90()
    {
        using FlowContext ctx = ContextWith(Variable.Single("测量", "Phi", VariableType.Double, Math.PI * 0.6));
        var tool = new AngleConvertTool("角度1") { ValuePath = "测量.Phi", Range = AngleRange.Minus90To90 };

        Assert.True(tool.Run(ctx).IsSuccess);

        Assert.Equal(-72, (double)ctx.GetVariable("角度1", "Value").Value, 9);
        Assert.Equal(1, (int)ctx.GetVariable("角度1", "Count").Value);
    }

    [Fact]
    public void 角度转弧度并限制到0到180()
    {
        using FlowContext ctx = ContextWith(Variable.Single("输入", "Angle", VariableType.Double, -30.0));
        var tool = new AngleConvertTool("角度1")
        {
            ValuePath = "输入.Angle",
            InputUnit = AngleUnit.Degree,
            OutputUnit = AngleUnit.Radian,
            Range = AngleRange.ZeroTo180
        };

        Assert.True(tool.Run(ctx).IsSuccess);

        Assert.Equal(150 * Math.PI / 180, (double)ctx.GetVariable("角度1", "Value").Value, 12);
    }

    [Fact]
    public void 不限制范围时只换算单位()
    {
        using FlowContext ctx = ContextWith(Variable.Single("输入", "Angle", VariableType.Double, 3 * Math.PI));
        var tool = new AngleConvertTool("角度1") { ValuePath = "输入.Angle" };

        Assert.True(tool.Run(ctx).IsSuccess);

        Assert.Equal(540, (double)ctx.GetVariable("角度1", "Value").Value, 9);
    }

    [Fact]
    public void 数组逐项换算_NaN原样输出()
    {
        using FlowContext ctx = ContextWith(Variable.Array("测量", "Phis", VariableType.Double,
            new[] { 0.1, double.NaN, -Math.PI / 2 - 0.1 }));
        var tool = new AngleConvertTool("角度1") { ValuePath = "测量.Phis", Range = AngleRange.Minus90To90 };

        Assert.True(tool.Run(ctx).IsSuccess);

        var values = ctx.GetVariable("角度1", "Values").GetValue<double[]>();
        Assert.Equal(3, values.Length);
        Assert.Equal(0.1 * 180 / Math.PI, values[0], 9);
        Assert.True(double.IsNaN(values[1]));
        Assert.Equal(90 - 0.1 * 180 / Math.PI, values[2], 9);
        Assert.Equal(values[0], (double)ctx.GetVariable("角度1", "Value").Value);
        Assert.Equal(3, (int)ctx.GetVariable("角度1", "Count").Value);
    }

    [Fact]
    public void 空数组输出NaN单值()
    {
        using FlowContext ctx = ContextWith(Variable.Array("测量", "Phis", VariableType.Double, Array.Empty<double>()));
        var tool = new AngleConvertTool("角度1") { ValuePath = "测量.Phis" };

        Assert.True(tool.Run(ctx).IsSuccess);

        Assert.True(double.IsNaN((double)ctx.GetVariable("角度1", "Value").Value));
        Assert.Equal(0, (int)ctx.GetVariable("角度1", "Count").Value);
    }

    [Theory]
    [InlineData(90, 90)]
    [InlineData(90, -90)]
    [InlineData(0, 400)]
    public void 自定义范围无效时失败(double min, double max)
    {
        using FlowContext ctx = ContextWith(Variable.Single("输入", "Angle", VariableType.Double, 1.0));
        var tool = new AngleConvertTool("角度1") { ValuePath = "输入.Angle", Range = AngleRange.Custom, RangeMin = min, RangeMax = max };

        NodeResult result = tool.Run(ctx);

        Assert.False(result.IsSuccess);
        Assert.Contains("自定义角度范围无效", result.Message);
    }

    [Fact]
    public void 参数可保存加载_数组引用通过校验()
    {
        var root = new SequenceNode("根");
        var measure = new RectangleFollowMeasureTool("测量1") { ImagePath = "Input.Image" };
        root.Children.Add(new ToolNode(measure));
        root.Children.Add(new ToolNode(new AngleConvertTool("角度1")
        {
            ValuePath = "测量1.Phis",
            InputUnit = AngleUnit.Radian,
            OutputUnit = AngleUnit.Degree,
            Range = AngleRange.Custom,
            RangeMin = -10,
            RangeMax = 170
        }));

        SequenceNode loaded = FlowSerializer.Load(FlowSerializer.Save(root));

        var tool = Assert.IsType<AngleConvertTool>(((ToolNode)loaded.Children[1]).Tool);
        Assert.Equal(AngleRange.Custom, tool.Range);
        Assert.Equal(-10, tool.RangeMin);
        Assert.Equal(170, tool.RangeMax);
        FlowValidationResult validation = FlowValidator.Validate(loaded);
        Assert.DoesNotContain(validation.Issues, i => i.ToString().Contains("角度1"));
    }
}
