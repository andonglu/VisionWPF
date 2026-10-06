using VisionFlow.Core;
using VisionFlow.Expressions;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>表达式引擎（LD-01）：语法、运算语义、内置函数、引用解析与错误定位。</summary>
public class ExpressionTests
{
    private static object Eval(string text, Func<string, object>? resolve = null)
    {
        return ExpressionParser.Parse(text).Evaluate(resolve ?? (path => throw new InvalidOperationException("没有引用")));
    }

    private static ExpressionException ParseError(string text)
    {
        return Assert.Throws<ExpressionException>(() => ExpressionParser.Parse(text));
    }

    private static ExpressionException EvalError(string text, Func<string, object>? resolve = null)
    {
        CompiledExpression expression = ExpressionParser.Parse(text);
        return Assert.Throws<ExpressionException>(() =>
            expression.Evaluate(resolve ?? (path => throw new InvalidOperationException("没有引用"))));
    }

    // ---------------- 常量、运算符与优先级 ----------------

    [Theory]
    [InlineData("1 + 2 * 3", 7L)]
    [InlineData("(1 + 2) * 3", 9L)]
    [InlineData("10 - 4 - 3", 3L)]
    [InlineData("7 % 3", 1L)]
    [InlineData("2 * 3 % 4", 2L)]
    [InlineData("-3 + 1", -2L)]
    [InlineData("- (2 + 3)", -5L)]
    public void 整数运算_保持整数(string text, long expected)
    {
        Assert.Equal(expected, Eval(text));
    }

    [Theory]
    [InlineData("7 / 2", 3.5)]
    [InlineData("6 / 3", 2.0)]
    [InlineData("1.5 * 2", 3.0)]
    [InlineData("1 + 0.25", 1.25)]
    [InlineData("2e3", 2000.0)]
    [InlineData("1.5E-1", 0.15)]
    [InlineData("5.5 % 2", 1.5)]
    public void 小数运算与除法_得到小数(string text, double expected)
    {
        Assert.Equal(expected, (double)Eval(text), 10);
    }

    [Theory]
    [InlineData("3 > 2.5", true)]
    [InlineData("2 >= 2", true)]
    [InlineData("1 < 1", false)]
    [InlineData("1 <= 1.0", true)]
    [InlineData("1 == 1.0", true)]
    [InlineData("1 != 2", true)]
    [InlineData("\"abc\" < \"abd\"", true)]
    [InlineData("\"abc\" == \"abc\"", true)]
    [InlineData("\"123\" == 123", true)]
    [InlineData("\"1.50\" == 1.5", false)]
    [InlineData("true == true", true)]
    [InlineData("1 < 2 == true", true)]
    [InlineData("true || false && false", true)]
    [InlineData("!(1 > 2)", true)]
    [InlineData("!true || true", true)]
    public void 比较与逻辑运算(string text, bool expected)
    {
        Assert.Equal(expected, Eval(text));
    }

    [Fact]
    public void NaN参与比较_只有不等为真()
    {
        Func<string, object> resolve = _ => double.NaN;
        Assert.Equal(false, Eval("{a.b} > 1", resolve));
        Assert.Equal(false, Eval("{a.b} <= 1", resolve));
        Assert.Equal(false, Eval("{a.b} == {a.b}", resolve));
        Assert.Equal(true, Eval("{a.b} != 1", resolve));
    }

    [Fact]
    public void 逻辑运算短路_不计算另一侧()
    {
        Func<string, object> explode = _ => throw new InvalidOperationException("不应求值");
        Assert.Equal(false, Eval("false && {a.b} > 1", explode));
        Assert.Equal(true, Eval("true || {a.b} > 1", explode));
        Assert.Equal(1L, Eval("true ? 1 : {a.b}", explode));
        Assert.Equal(2L, Eval("if(false, {a.b}, 2)", explode));
    }

    [Fact]
    public void 条件运算_右结合()
    {
        Assert.Equal("b", Eval("1 > 2 ? \"a\" : \"b\""));
        Assert.Equal(2L, Eval("false ? 1 : true ? 2 : 3"));
    }

    [Fact]
    public void 字符串拼接与转义()
    {
        Assert.Equal("宽度1.5", Eval("\"宽度\" + 1.5"));
        Assert.Equal("A-1-true", Eval("\"A-\" + 1 + \"-\" + true"));
        Assert.Equal("a\"b\\c", Eval("\"a\\\"b\\\\c\""));
        Assert.Equal("x\ny", Eval("\"x\\ny\""));
    }

    [Fact]
    public void 常量名与函数名_不区分大小写()
    {
        Assert.Equal(true, Eval("TRUE"));
        Assert.Equal(3L, Eval("ABS(-3)"));
        Assert.Equal(Math.PI, Eval("pi"));
    }

    // ---------------- 内置函数 ----------------

    public static IEnumerable<object[]> FunctionCases()
    {
        yield return new object[] { "abs(-3)", 3L };
        yield return new object[] { "abs(-2.5)", 2.5 };
        yield return new object[] { "sqrt(16)", 4.0 };
        yield return new object[] { "pow(2, 10)", 1024.0 };
        yield return new object[] { "min(3, 1, 2)", 1L };
        yield return new object[] { "max(1, 2.5)", 2.5 };
        yield return new object[] { "round(2.345, 2)", 2.35 };
        yield return new object[] { "round(2.5)", 3.0 };
        yield return new object[] { "round(-2.5)", -3.0 };
        yield return new object[] { "round(7, 2)", 7L };
        yield return new object[] { "floor(2.7)", 2.0 };
        yield return new object[] { "ceil(2.1)", 3.0 };
        yield return new object[] { "deg(pi)", 180.0 };
        yield return new object[] { "rad(180)", Math.PI };
        yield return new object[] { "hypot(3, 4)", 5.0 };
        yield return new object[] { "atan2(1, 1)", Math.PI / 4 };
        yield return new object[] { "sin(0)", 0.0 };
        yield return new object[] { "cos(0)", 1.0 };
        yield return new object[] { "tan(0)", 0.0 };
        yield return new object[] { "isnan(num(\"x\"))", true };
        yield return new object[] { "isnan(1)", false };
        yield return new object[] { "isvalid(num(\"x\"))", false };
        yield return new object[] { "isvalid(\"\")", true };
        yield return new object[] { "len(\"abc\")", 3L };
        yield return new object[] { "substr(\"ABCDEF\", 2, 3)", "CDE" };
        yield return new object[] { "substr(\"ABC\", 1)", "BC" };
        yield return new object[] { "substr(\"ABC\", 5)", "" };
        yield return new object[] { "substr(\"ABC\", 1, 99)", "BC" };
        yield return new object[] { "contains(\"SN-123\", \"123\")", true };
        yield return new object[] { "startswith(\"SN-123\", \"SN\")", true };
        yield return new object[] { "endswith(\"SN-123\", \"12\")", false };
        yield return new object[] { "replace(\"a-b-c\", \"-\", \"\")", "abc" };
        yield return new object[] { "trim(\"  a b  \")", "a b" };
        yield return new object[] { "upper(\"ab\")", "AB" };
        yield return new object[] { "lower(\"AB\")", "ab" };
        yield return new object[] { "regex(\"2026-10-06\", \"^\\\\d{4}-\\\\d{2}-\\\\d{2}$\")", true };
        yield return new object[] { "regex(\"2026/10/06\", \"^\\\\d{4}-\\\\d{2}-\\\\d{2}$\")", false };
        yield return new object[] { "format(\"宽度 {0:F2} mm\", 1.234)", "宽度 1.23 mm" };
        yield return new object[] { "format(\"{0}-{1}\", \"A\", 7)", "A-7" };
        yield return new object[] { "str(1.5)", "1.5" };
        yield return new object[] { "str(true)", "true" };
        yield return new object[] { "num(\" 2.5 \")", 2.5 };
        yield return new object[] { "num(3)", 3.0 };
    }

    [Theory]
    [MemberData(nameof(FunctionCases))]
    public void 内置函数(string text, object expected)
    {
        object actual = Eval(text);
        if (expected is double d)
        {
            Assert.Equal(d, (double)actual, 10);
        }
        else
        {
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void 数组函数_忽略NaN且元素类型被规范()
    {
        Func<string, object> resolve = path => path switch
        {
            "测.Values" => new[] { 1.0, double.NaN, 3.0 },
            "测.Ints" => new List<int> { 1, 2, 3 },
            "测.Empty" => new double[0],
            "测.Names" => new List<string> { "a", "b" },
            _ => throw new InvalidOperationException(path)
        };

        Assert.Equal(3L, Eval("count({测.Values})", resolve));
        Assert.Equal(4.0, Eval("sum({测.Values})", resolve));
        Assert.Equal(2.0, Eval("mean({测.Values})", resolve));
        Assert.Equal(3.0, Eval("maxof({测.Values})", resolve));
        Assert.Equal(1.0, Eval("minof({测.Values})", resolve));
        Assert.Equal(2L, Eval("at({测.Ints}, 1)", resolve));
        Assert.Equal("b", Eval("at({测.Names}, 1)", resolve));
        Assert.True(double.IsNaN((double)Eval("at({测.Ints}, 5)", resolve)));
        Assert.True(double.IsNaN((double)Eval("at({测.Ints}, -1)", resolve)));
        Assert.Equal(0.0, Eval("sum({测.Empty})", resolve));
        Assert.True(double.IsNaN((double)Eval("mean({测.Empty})", resolve)));
        Assert.Equal(6.0, Eval("sum({测.Ints})", resolve));
        Assert.Contains("非数值", EvalError("sum({测.Names})", resolve).Message);
        Assert.Contains("需要数组", EvalError("count(1)", resolve).Message);
    }

    [Fact]
    public void 函数表_名称唯一且有说明()
    {
        Assert.NotEmpty(ExpressionFunctions.All);
        Assert.Equal(ExpressionFunctions.All.Count,
            ExpressionFunctions.All.Select(f => f.Name.ToLowerInvariant()).Distinct().Count());
        Assert.All(ExpressionFunctions.All, f => Assert.False(string.IsNullOrWhiteSpace(f.Description)));
    }

    // ---------------- 引用 ----------------

    [Fact]
    public void 引用列表_去重保序并区分局部名称()
    {
        CompiledExpression expression = ExpressionParser.Parse("{a.x} + {b.Rows[0]} + {A.X} + {Item} * {ItemIndex}");
        Assert.Equal(new[] { "a.x", "b.Rows[0]" }, expression.References);
        Assert.Equal(new[] { "Item", "ItemIndex" }, expression.LocalNames);
    }

    [Fact]
    public void 流程上下文求值_支持数组下标成员与循环变量()
    {
        using var ctx = new FlowContext();
        ctx.SetVariable(Variable.Single("测量1", "Row", VariableType.Double, 10.5));
        ctx.SetVariable(Variable.Single("测量2", "Row", VariableType.Double, 4.0));
        ctx.SetVariable(Variable.Array("测量1", "Rows", VariableType.Double, new[] { 1.0, 2.0, 3.0 }));
        ctx.SetVariable(Variable.Single("读码1", "FirstCode", VariableType.String, "SN-001"));

        Assert.Equal(6.5, ExpressionParser.Parse("{测量1.Row} - {测量2.Row}").Evaluate(ctx));
        Assert.Equal(2.0, ExpressionParser.Parse("{测量1.Rows[1]}").Evaluate(ctx));
        Assert.Equal(3L, ExpressionParser.Parse("{测量1.Rows.Count}").Evaluate(ctx));
        Assert.Equal(true, ExpressionParser.Parse("startswith({读码1.FirstCode}, \"SN\")").Evaluate(ctx));

        ctx.PushLoop(new LoopFrame(2, 5, 0.75));
        try
        {
            Assert.Equal(3L, ExpressionParser.Parse("{Loop.Index} + 1").Evaluate(ctx));
            Assert.Equal(1.5, ExpressionParser.Parse("{Loop.Current} * 2").Evaluate(ctx));
        }
        finally
        {
            ctx.PopLoop();
        }
    }

    [Fact]
    public void 局部名称_由调用方提供取值()
    {
        using var ctx = new FlowContext();
        CompiledExpression expression = ExpressionParser.Parse("{Item} * 2 + {ItemIndex}");
        Assert.Equal(7L, expression.Evaluate(ctx, name => name == "Item" ? 3 : 1));

        var error = Assert.Throws<ExpressionException>(() => expression.Evaluate(ctx));
        Assert.Contains("未定义的名称 Item", error.Message);
    }

    [Fact]
    public void 引用不存在_报告引用与位置()
    {
        using var ctx = new FlowContext();
        CompiledExpression expression = ExpressionParser.Parse("1 + {没有.变量}");
        var error = Assert.Throws<ExpressionException>(() => expression.Evaluate(ctx));
        Assert.Equal(4, error.Position);
        Assert.Contains("{没有.变量}", error.Message);
        Assert.StartsWith("第 5 个字符处", error.Message);
    }

    // ---------------- 错误 ----------------

    [Theory]
    [InlineData("1 +", 3, "不完整")]
    [InlineData("1 = 2", 2, "==")]
    [InlineData("1 & 2", 2, "&&")]
    [InlineData("1 2", 2, "多余")]
    [InlineData("(1 + 2", 6, "右括号")]
    [InlineData("foo(1)", 0, "未知的函数")]
    [InlineData("宽度 + 1", 0, "花括号")]
    [InlineData("abs", 0, "缺少括号")]
    [InlineData("abs(1, 2)", 0, "需要 1 个参数")]
    [InlineData("min(1)", 0, "至少 2")]
    [InlineData("{a.b", 0, "右花括号")]
    [InlineData("{}", 0, "不能为空")]
    [InlineData("{a.b[x]}", 0, "格式错误")]
    [InlineData("{a b}", 0, "不能包含")]
    [InlineData("\"abc", 0, "双引号")]
    [InlineData("\"a\\q\"", 2, "转义")]
    [InlineData("1 ? 2", 5, ":")]
    [InlineData("12abc", 2, "字母")]
    [InlineData("99999999999999999999", 0, "超出范围")]
    [InlineData("1 # 2", 2, "无法识别")]
    public void 语法错误_给出位置与原因(string text, int position, string messagePart)
    {
        ExpressionException error = ParseError(text);
        Assert.Equal(position, error.Position);
        Assert.Contains(messagePart, error.Message);
    }

    [Fact]
    public void 空表达式_报错()
    {
        Assert.Throws<ExpressionException>(() => ExpressionParser.Parse("  "));
        Assert.False(ExpressionParser.TryParse("1 +", out _, out ExpressionException? error));
        Assert.NotNull(error);
    }

    [Theory]
    [InlineData("1 / 0", 4, "除数为 0")]
    [InlineData("1.5 % 0", 6, "除数为 0")]
    [InlineData("5 % 0", 4, "除数为 0")]
    [InlineData("\"a\" * 2", 4, "不支持字符串与整数")]
    [InlineData("true + 1", 5, "不支持布尔与整数")]
    [InlineData("true < false", 5, "不支持")]
    [InlineData("true == 1", 5, "不支持")]
    [InlineData("!1", 0, "需要布尔值")]
    [InlineData("-\"a\"", 0, "需要数值")]
    [InlineData("1 && true", 0, "需要布尔值")]
    [InlineData("1 ? 2 : 3", 0, "需要布尔值")]
    [InlineData("9223372036854775807 + 1", 20, "溢出")]
    [InlineData("sqrt(\"a\")", 5, "第 1 个参数需要数值")]
    [InlineData("len(1)", 4, "需要字符串")]
    [InlineData("round(1.5, 99)", 11, "0 到 15")]
    [InlineData("substr(\"a\", -1)", 12, "负数")]
    [InlineData("replace(\"a\", \"\", \"b\")", 13, "空字符串")]
    [InlineData("regex(\"a\", \"(\")", 11, "正则")]
    [InlineData("format(\"{0\", 1)", 7, "格式模板")]
    public void 求值错误_给出位置与原因(string text, int position, string messagePart)
    {
        ExpressionException error = EvalError(text);
        Assert.Equal(position, error.Position);
        Assert.Contains(messagePart, error.Message);
    }

    // ---------------- 结果类型转换与缓存 ----------------

    [Fact]
    public void 结果转换为变量类型()
    {
        Assert.Equal(3, ExpressionValues.ConvertTo(3L, VariableType.Int));
        Assert.Equal(3, ExpressionValues.ConvertTo(3.0, VariableType.Int));
        Assert.Equal(2.0, ExpressionValues.ConvertTo(2L, VariableType.Double));
        Assert.Equal(true, ExpressionValues.ConvertTo(true, VariableType.Bool));
        Assert.Equal("1.5", ExpressionValues.ConvertTo(1.5, VariableType.String));
        Assert.Equal("[1, 2]", ExpressionValues.ConvertTo(new[] { 1, 2 }, VariableType.String));
        Assert.Throws<ExpressionException>(() => ExpressionValues.ConvertTo(3.5, VariableType.Int));
        Assert.Throws<ExpressionException>(() => ExpressionValues.ConvertTo(double.NaN, VariableType.Int));
        Assert.Throws<ExpressionException>(() => ExpressionValues.ConvertTo(5_000_000_000L, VariableType.Int));
        Assert.Throws<ExpressionException>(() => ExpressionValues.ConvertTo("1", VariableType.Double));
        Assert.Throws<ExpressionException>(() => ExpressionValues.ConvertTo(1L, VariableType.Bool));
    }

    [Fact]
    public void 相同文本_复用解析结果且可并发求值()
    {
        CompiledExpression first = ExpressionParser.Parse("{a.b} * 2");
        Assert.Same(first, ExpressionParser.Parse("{a.b} * 2"));

        Parallel.For(0, 200, i => Assert.Equal((long)i * 2, first.Evaluate(_ => i)));
    }
}
