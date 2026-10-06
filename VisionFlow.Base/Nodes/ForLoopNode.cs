using System.Collections;
using System.Collections.Generic;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Nodes
{
    public enum ForLoopMode
    {
        /// <summary>按次数循环：次数为常量，或引用上游的 Int 输出。</summary>
        Count,
        /// <summary>按集合循环：引用上游的数组输出，元素个数决定次数，逐个暴露当前元素。</summary>
        Each
    }

    /// <summary>
    /// For 循环节点。
    /// Count 模式：循环体可访问 Loop.Index / Loop.Count；
    /// Each  模式：额外可访问 Loop.Current（及其成员，如 Loop.Current.Score）。
    /// 循环体是子节点列表，支持任意嵌套。
    /// </summary>
    public sealed class ForLoopNode : LoopNodeBase
    {
        public ForLoopMode Mode { get; private set; }

        /// <summary>Count 模式的次数来源（常量或 Int 变量引用）。</summary>
        public Operand CountSource { get; set; }

        /// <summary>Each 模式的集合来源（数组变量引用，如 "匹配1.Items"）。</summary>
        public string ItemsPath { get; set; }

        private ForLoopNode(string name) : base(name)
        {
        }

        /// <summary>创建按次数循环（countSource 可为常量或 "模块.Int变量" 引用）。</summary>
        public static ForLoopNode Count(string name, Operand countSource)
        {
            return new ForLoopNode(name) { Mode = ForLoopMode.Count, CountSource = countSource };
        }

        /// <summary>创建按集合循环（itemsPath 指向上游数组输出）。</summary>
        public static ForLoopNode Each(string name, string itemsPath)
        {
            return new ForLoopNode(name) { Mode = ForLoopMode.Each, ItemsPath = itemsPath };
        }

        protected override NodeResult OnExecute(FlowContext ctx)
        {
            IList items = null;
            int count;

            if (Mode == ForLoopMode.Count)
            {
                if (CountSource == null)
                {
                    return NodeResult.Fail($"{Name} 未设置循环次数来源");
                }
                count = CountSource.GetInt(ctx);
                if (count < 0)
                {
                    return NodeResult.Fail($"{Name} 循环次数不能为负数（{count}）");
                }
            }
            else
            {
                var reference = VariableReference.Parse(ItemsPath);
                Variable variable = ctx.GetVariable(reference.ModuleName, reference.VarName);
                if (variable.Kind != VariableKind.Array || !(variable.Value is IList list))
                {
                    return NodeResult.Fail($"{Name} 的循环源 '{ItemsPath}' 不是数组变量");
                }
                items = list;
                count = list.Count;
            }

            ctx.AddLog(FlowLogLevel.Info,
                $"[循环] {Name}：{(Mode == ForLoopMode.Count ? "按次数" : "按集合")}，共 {count} 次",
                Id, Name);

            for (int i = 0; i < count; i++)
            {
                NodeResult result = RunIteration(ctx, new LoopFrame(i, count, items == null ? null : items[i]), out bool breakLoop);
                if (!result.IsSuccess)
                {
                    return result;
                }
                if (breakLoop)
                {
                    break;
                }
            }
            return NodeResult.Ok;
        }
    }
}
