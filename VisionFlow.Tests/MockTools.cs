#nullable disable
// 测试替身：仅供自动化测试使用，不随正式程序集发布（TR-15）。
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tests
{
    /// <summary>模拟的匹配结果项。</summary>
    public sealed class MatchItem
    {
        public double X { get; set; }
        public double Y { get; set; }
        public double Score { get; set; }

        public MatchItem(double x, double y, double score)
        {
            X = x;
            Y = y;
            Score = score;
        }

        public override string ToString()
        {
            return $"({X}, {Y}) Score={Score}";
        }
    }

    /// <summary>
    /// 模拟匹配工具：输出 MatchCount(Int 单值)、Scores(Double 数组)、Items(MatchItem 集合)。
    /// 分数从 BaseScore 开始，每个递增 ScoreStep。
    /// </summary>
    public sealed class MockMatchTool : ToolBase
    {
        public int MatchCount { get; set; }
        public double BaseScore { get; set; }
        public double ScoreStep { get; set; }

        public MockMatchTool(string moduleName, int matchCount, double baseScore, double scoreStep = 1.0)
            : base(moduleName)
        {
            MatchCount = matchCount;
            BaseScore = baseScore;
            ScoreStep = scoreStep;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            var items = Enumerable.Range(0, MatchCount)
                .Select(i => new MatchItem(i * 10, i * 5, BaseScore + i * ScoreStep))
                .ToList();

            SetOutput(ctx, Variable.Single(ModuleName, "MatchCount", VariableType.Int, items.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, items.Select(m => m.Score)));
            SetOutput(ctx, Variable.Array(ModuleName, "Items", VariableType.Object, items));
            return NodeResult.Ok;
        }
    }

    /// <summary>求和工具：读取一个 Double 数组输出，求和后写入 Total。</summary>
    public sealed class SumTool : ToolBase
    {
        /// <summary>数组来源，如 "匹配1.Scores"。</summary>
        public string ArrayPath { get; set; }

        public SumTool(string moduleName, string arrayPath) : base(moduleName)
        {
            ArrayPath = arrayPath;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            var reference = VariableReference.Parse(ArrayPath);
            Variable variable = ctx.GetVariable(reference.ModuleName, reference.VarName);
            if (variable.Kind != VariableKind.Array || !(variable.Value is IEnumerable list))
            {
                return NodeResult.Fail($"{ArrayPath} 不是数组变量，无法求和");
            }

            double total = 0;
            foreach (object item in list)
            {
                total += Convert.ToDouble(item);
            }

            SetOutput(ctx, Variable.Single(ModuleName, "Total", VariableType.Double, total));
            return NodeResult.Ok;
        }
    }

    /// <summary>记录工具：把引用解析结果追加到外部列表，供测试断言。</summary>
    public sealed class RecorderTool : ToolBase
    {
        public string Path { get; set; }
        public List<object> Sink { get; set; }

        public RecorderTool(string moduleName, string path, List<object> sink) : base(moduleName)
        {
            Path = path;
            Sink = sink;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            Sink.Add(VariableReference.Parse(Path).Resolve(ctx));
            return NodeResult.Ok;
        }
    }

    /// <summary>委托工具：执行任意动作，方便在测试中灵活组装流程。</summary>
    public sealed class DelegateTool : ToolBase
    {
        private readonly Action<FlowContext> _action;

        public DelegateTool(string moduleName, Action<FlowContext> action) : base(moduleName)
        {
            _action = action;
        }

        public override NodeResult Run(FlowContext ctx)
        {
            _action(ctx);
            return NodeResult.Ok;
        }
    }
}
