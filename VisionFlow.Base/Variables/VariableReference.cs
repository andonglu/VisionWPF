using System;
using System.Collections;
using System.Linq;
using VisionFlow.Core;

namespace VisionFlow.Variables
{
    /// <summary>
    /// 变量引用，用于把上游输出绑定到下游输入或条件表达式。
    /// 支持的语法：
    ///   模块.变量            —— 整个变量值
    ///   模块.数组[下标]      —— 数组元素
    ///   模块.数组.Count      —— 数组长度
    ///   模块.对象.成员       —— 对象属性/字段（可多级）
    ///   Loop.Index           —— 最内层循环下标
    ///   Loop.Count           —— 最内层循环总次数
    ///   Loop.Current         —— 最内层 ForEach 当前元素
    ///   Loop.Current.成员    —— 当前元素的成员
    /// </summary>
    public sealed class VariableReference
    {
        public string ModuleName { get; private set; }
        public string VarName { get; private set; }
        public int? ElementIndex { get; private set; }
        public string[] MemberPath { get; private set; }

        public static VariableReference Parse(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new ArgumentException("变量引用不能为空");
            }

            var segments = text.Split('.');
            if (segments.Length < 2)
            {
                throw new FormatException($"变量引用 '{text}' 格式应为 模块名.变量名");
            }

            var reference = new VariableReference
            {
                ModuleName = segments[0],
                MemberPath = segments.Length > 2 ? segments.Skip(2).ToArray() : new string[0]
            };

            string varSegment = segments[1];
            int bracket = varSegment.IndexOf('[');
            if (bracket >= 0)
            {
                if (!varSegment.EndsWith("]"))
                {
                    throw new FormatException($"变量引用 '{text}' 下标格式错误");
                }
                reference.VarName = varSegment.Substring(0, bracket);
                reference.ElementIndex = int.Parse(varSegment.Substring(bracket + 1, varSegment.Length - bracket - 2));
            }
            else
            {
                reference.VarName = varSegment;
            }

            return reference;
        }

        /// <summary>解析引用，返回 object 值。</summary>
        public object Resolve(FlowContext ctx)
        {
            object current;

            if (ModuleName == "Loop")
            {
                var frame = ctx.CurrentLoop
                    ?? throw new InvalidOperationException($"引用 '{this}' 使用了循环变量，但当前不在任何循环内");
                switch (VarName)
                {
                    case "Index": current = frame.Index; break;
                    case "Count": current = frame.Count; break;
                    case "Current": current = frame.CurrentItem; break;
                    default:
                        throw new InvalidOperationException($"未知的循环变量 'Loop.{VarName}'，仅支持 Index / Count / Current");
                }
                if (ElementIndex.HasValue)
                {
                    throw new InvalidOperationException($"循环变量 'Loop.{VarName}' 不支持下标");
                }
            }
            else
            {
                Variable variable = ctx.GetVariable(ModuleName, VarName);
                current = ElementIndex.HasValue ? variable.GetElement(ElementIndex.Value) : variable.Value;
            }

            foreach (string member in MemberPath)
            {
                current = ResolveMember(current, member);
            }
            return current;
        }

        /// <summary>解析引用并转换为目标类型。</summary>
        public T Resolve<T>(FlowContext ctx)
        {
            object value = Resolve(ctx);
            if (value is T typed)
            {
                return typed;
            }
            if (value == null)
            {
                throw new InvalidOperationException($"引用 '{this}' 的值为 null，无法转换为 {typeof(T).Name}");
            }
            return (T)Convert.ChangeType(value, typeof(T));
        }

        private static object ResolveMember(object owner, string member)
        {
            if (owner == null)
            {
                throw new InvalidOperationException($"无法在 null 上读取成员 '{member}'");
            }
            if (member == "Count" && owner is ICollection collection)
            {
                return collection.Count;
            }
            Type type = owner.GetType();
            var property = type.GetProperty(member);
            if (property != null)
            {
                return property.GetValue(owner, null);
            }
            var field = type.GetField(member);
            if (field != null)
            {
                return field.GetValue(owner);
            }
            throw new InvalidOperationException($"类型 {type.Name} 上不存在成员 '{member}'");
        }

        public override string ToString()
        {
            string index = ElementIndex.HasValue ? $"[{ElementIndex.Value}]" : string.Empty;
            string members = MemberPath != null && MemberPath.Length > 0 ? "." + string.Join(".", MemberPath) : string.Empty;
            return $"{ModuleName}.{VarName}{index}{members}";
        }
    }
}
