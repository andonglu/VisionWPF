using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VisionFlow.Core;

namespace VisionFlow.Variables
{
    /// <summary>
    /// 标记工具上的"变量引用"属性（字符串路径）。
    /// 编辑器据此生成下拉选择（候选项来自上游输出），而不是手输字符串。
    /// </summary>
    [AttributeUsage(AttributeTargets.Property)]
    public sealed class InputRefAttribute : Attribute
    {
        /// <summary>界面显示名。</summary>
        public string DisplayName { get; private set; }

        /// <summary>期望的值类型（如 typeof(HObject) / typeof(HTuple) / typeof(int)），用于过滤候选项。</summary>
        public Type ExpectedType { get; private set; }

        /// <summary>是否可选（可选引用允许留空）。</summary>
        public bool Optional { get; set; }

        /// <summary>是否同时接受期望类型的单值和集合；默认仅接受声明的类型。</summary>
        public bool AcceptsCollection { get; set; }

        public InputRefAttribute(string displayName, Type expectedType)
        {
            DisplayName = displayName;
            ExpectedType = expectedType;
        }
    }

    /// <summary>
    /// 声明工具的一个输出变量（可多个叠加）。编辑器据此枚举上游输出候选项。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
    public sealed class ToolOutputAttribute : Attribute
    {
        public string Name { get; private set; }
        public VariableKind Kind { get; private set; }
        public VariableType Type { get; private set; }

        /// <summary>元素/内容的 CLR 类型（Array 为元素类型，Object 为内容类型）。</summary>
        public Type ElementClrType { get; set; }

        /// <summary>Array 元素类型上可用于成员链引用的成员名（如 MatchResultItem 的 HomMat）。</summary>
        public string[] Members { get; set; }

        public ToolOutputAttribute(string name, VariableKind kind, VariableType type)
        {
            Name = name;
            Kind = kind;
            Type = type;
            Members = new string[0];
        }
    }

    /// <summary>一个工具输出声明（运行期读取结果）。</summary>
    public sealed class ToolOutputDef
    {
        public string Name { get; set; }
        public VariableKind Kind { get; set; }
        public VariableType Type { get; set; }
        public Type ElementClrType { get; set; }
        public string[] Members { get; set; }
    }

    /// <summary>
    /// 按当前配置声明额外输出的工具（输出名由用户配置决定，如变量计算的结果名）。
    /// 输出名来自工具的可序列化参数，随流程文件保存，加载后自然恢复。
    /// 实现不应抛出异常：无法解析的配置项直接跳过，由工具运行时报告。
    /// </summary>
    public interface IDynamicOutputTool
    {
        /// <summary>按当前配置声明的输出（不含类型上 [ToolOutput] 声明的静态输出）。</summary>
        IReadOnlyList<ToolOutputDef> GetDynamicOutputs();
    }

    /// <summary>工具参数配置中的一个问题（如多行配置的格式错误）。</summary>
    public sealed class ToolConfigurationIssue
    {
        public ToolConfigurationIssue(string parameter, string message)
        {
            Parameter = parameter;
            Message = message;
        }

        /// <summary>校验结果中显示的参数名，如“计算式 第 2 行”。</summary>
        public string Parameter { get; private set; }
        public string Message { get; private set; }
    }

    /// <summary>
    /// 能自行检查参数配置的工具（如按行书写的配置格式），FlowValidator 把返回的问题作为错误报告。
    /// 实现不应抛出异常。
    /// </summary>
    public interface IToolConfigurationCheck
    {
        IEnumerable<ToolConfigurationIssue> CheckConfiguration();
    }

    /// <summary>一个工具引用输入声明（运行期读取结果）。</summary>
    public sealed class ToolInputRefDef
    {
        public string PropertyName { get; set; }
        public string DisplayName { get; set; }
        public Type ExpectedType { get; set; }
        public bool Optional { get; set; }
        public bool AcceptsCollection { get; set; }
        public PropertyInfo Property { get; set; }
    }

    /// <summary>工具元数据读取（带缓存）。</summary>
    public static class ToolMetadata
    {
        private static readonly Dictionary<Type, ToolOutputDef[]> _outputCache = new Dictionary<Type, ToolOutputDef[]>();
        private static readonly Dictionary<Type, ToolInputRefDef[]> _inputCache = new Dictionary<Type, ToolInputRefDef[]>();

        public static IReadOnlyList<ToolOutputDef> GetOutputs(Type toolType)
        {
            lock (_outputCache)
            {
                if (!_outputCache.TryGetValue(toolType, out ToolOutputDef[] defs))
                {
                    defs = toolType.GetCustomAttributes<ToolOutputAttribute>(inherit: true)
                        .Select(a => new ToolOutputDef
                        {
                            Name = a.Name,
                            Kind = a.Kind,
                            Type = a.Type,
                            ElementClrType = a.ElementClrType,
                            Members = a.Members ?? new string[0]
                        })
                        .ToArray();
                    _outputCache[toolType] = defs;
                }
                return defs;
            }
        }

        /// <summary>
        /// 工具实例的全部输出：类型上的静态声明，加上 <see cref="IDynamicOutputTool"/> 按当前配置给出的输出。
        /// 名称非法或与已有输出重名（不区分大小写）的动态输出不会出现在结果中，由 FlowValidator 报告。
        /// </summary>
        public static IReadOnlyList<ToolOutputDef> GetOutputs(ToolBase tool)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }
            IReadOnlyList<ToolOutputDef> staticOutputs = GetOutputs(tool.GetType());
            if (!(tool is IDynamicOutputTool dynamicTool))
            {
                return staticOutputs;
            }

            var outputs = new List<ToolOutputDef>(staticOutputs);
            var names = new HashSet<string>(staticOutputs.Select(o => o.Name), StringComparer.OrdinalIgnoreCase);
            foreach (ToolOutputDef def in dynamicTool.GetDynamicOutputs() ?? new ToolOutputDef[0])
            {
                if (def == null || !IsValidOutputName(def.Name) || !names.Add(def.Name))
                {
                    continue;
                }
                outputs.Add(new ToolOutputDef
                {
                    Name = def.Name,
                    Kind = def.Kind,
                    Type = def.Type,
                    ElementClrType = def.ElementClrType,
                    Members = def.Members ?? new string[0]
                });
            }
            return outputs;
        }

        /// <summary>
        /// 输出名是否可作为 模块名.变量名 中的变量名：非空，且不含空白和引用语法字符（. [ ] { }）。
        /// </summary>
        public static bool IsValidOutputName(string name)
        {
            if (string.IsNullOrEmpty(name))
            {
                return false;
            }
            foreach (char c in name)
            {
                if (char.IsWhiteSpace(c) || c == '.' || c == '[' || c == ']' || c == '{' || c == '}')
                {
                    return false;
                }
            }
            return true;
        }

        public static IReadOnlyList<ToolInputRefDef> GetInputRefs(Type toolType)
        {
            lock (_inputCache)
            {
                if (!_inputCache.TryGetValue(toolType, out ToolInputRefDef[] defs))
                {
                    defs = toolType.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                        .Select(p => new { Property = p, Attribute = p.GetCustomAttribute<InputRefAttribute>() })
                        .Where(x => x.Attribute != null)
                        .Select(x => new ToolInputRefDef
                        {
                            PropertyName = x.Property.Name,
                            DisplayName = x.Attribute.DisplayName,
                            ExpectedType = x.Attribute.ExpectedType,
                            Optional = x.Attribute.Optional,
                            AcceptsCollection = x.Attribute.AcceptsCollection,
                            Property = x.Property
                        })
                        .ToArray();
                    _inputCache[toolType] = defs;
                }
                return defs;
            }
        }
    }
}
