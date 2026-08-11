using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>流程 JSON 保存/加载。保存的是声明和参数，不保存运行期变量值。</summary>
    public static class FlowSerializer
    {
        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        public static string Save(SequenceNode root)
        {
            return JsonSerializer.Serialize(ToDto(root), Options);
        }

        public static SequenceNode Load(string json)
        {
            NodeDto dto = JsonSerializer.Deserialize<NodeDto>(json, Options);
            if (dto == null)
            {
                throw new InvalidOperationException("流程文件为空或格式错误。");
            }
            FlowNode node = FromDto(dto);
            if (node is SequenceNode sequence)
            {
                return sequence;
            }
            throw new InvalidOperationException("流程根节点必须是 Sequence。");
        }

        public static string SaveNode(FlowNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }
            return JsonSerializer.Serialize(ToDto(node), Options);
        }

        public static FlowNode LoadNode(string json, bool regenerateIds = false)
        {
            NodeDto dto = JsonSerializer.Deserialize<NodeDto>(json, Options);
            if (dto == null)
            {
                throw new InvalidOperationException("节点数据为空或格式错误。");
            }
            FlowNode node = FromDto(dto);
            if (regenerateIds)
            {
                RegenerateIds(node);
            }
            return node;
        }

        private static NodeDto ToDto(FlowNode node)
        {
            var dto = new NodeDto
            {
                Id = node.Id,
                Name = node.Name
            };

            if (node is SequenceNode sequence)
            {
                dto.Kind = "Sequence";
                dto.Children = sequence.Children.Select(ToDto).ToList();
            }
            else if (node is ToolNode toolNode)
            {
                dto.Kind = "Tool";
                dto.Tool = ToToolDto(toolNode.Tool);
            }
            else if (node is IfElseNode ifElse)
            {
                dto.Kind = "IfElse";
                dto.Condition = ToConditionDto(ifElse.Condition);
                dto.IfBranch = ifElse.IfBranch.Select(ToDto).ToList();
                dto.ElseBranch = ifElse.ElseBranch.Select(ToDto).ToList();
                dto.Outputs = ifElse.Outputs.Select(ToBranchOutputDto).ToList();
            }
            else if (node is ForLoopNode loop)
            {
                dto.Kind = "ForLoop";
                dto.Loop = new LoopDto
                {
                    Mode = loop.Mode.ToString(),
                    CountSource = ToOperandDto(loop.CountSource),
                    ItemsPath = loop.ItemsPath
                };
                dto.Children = loop.Body.Select(ToDto).ToList();
            }
            else if (node is FlowOutputNode outputNode)
            {
                dto.Kind = "FlowOutput";
                dto.FlowOutputs = outputNode.Outputs.Select(ToFlowOutputDto).ToList();
            }
            else
            {
                throw new NotSupportedException($"不支持保存节点类型 {node.GetType().FullName}");
            }

            return dto;
        }

        private static FlowNode FromDto(NodeDto dto)
        {
            FlowNode node;
            switch (dto.Kind)
            {
                case "Sequence":
                    var sequence = new SequenceNode(dto.Name ?? "顺序");
                    AddChildren(sequence.Children, dto.Children);
                    node = sequence;
                    break;
                case "Tool":
                    node = new ToolNode(FromToolDto(dto.Tool), dto.Name);
                    break;
                case "IfElse":
                    var ifElse = new IfElseNode(dto.Name ?? "条件分支", FromConditionDto(dto.Condition));
                    AddChildren(ifElse.IfBranch, dto.IfBranch);
                    AddChildren(ifElse.ElseBranch, dto.ElseBranch);
                    foreach (BranchOutputDto output in dto.Outputs ?? new List<BranchOutputDto>())
                    {
                        ifElse.Outputs.Add(FromBranchOutputDto(output));
                    }
                    node = ifElse;
                    break;
                case "ForLoop":
                    if (dto.Loop == null)
                    {
                        throw new InvalidOperationException("ForLoop 节点缺少循环配置。");
                    }
                    ForLoopMode mode = ParseEnum<ForLoopMode>(dto.Loop.Mode, ForLoopMode.Count);
                    var loop = mode == ForLoopMode.Each
                        ? ForLoopNode.Each(dto.Name ?? "遍历循环", dto.Loop.ItemsPath)
                        : ForLoopNode.Count(dto.Name ?? "按次数循环", FromOperandDto(dto.Loop.CountSource));
                    AddChildren(loop.Body, dto.Children);
                    node = loop;
                    break;
                case "FlowOutput":
                    var outputNode = new FlowOutputNode(dto.Name ?? "流程输出");
                    foreach (FlowOutputDto output in dto.FlowOutputs ?? new List<FlowOutputDto>())
                    {
                        outputNode.Outputs.Add(FromFlowOutputDto(output));
                    }
                    node = outputNode;
                    break;
                default:
                    throw new NotSupportedException($"不支持加载节点类型 {dto.Kind}");
            }

            if (!string.IsNullOrWhiteSpace(dto.Id))
            {
                node.Id = dto.Id;
            }
            return node;
        }

        private static void AddChildren(IList<FlowNode> target, List<NodeDto> children)
        {
            foreach (NodeDto child in children ?? new List<NodeDto>())
            {
                target.Add(FromDto(child));
            }
        }

        private static void RegenerateIds(FlowNode node)
        {
            node.Id = Guid.NewGuid().ToString("N");
            foreach (FlowNode child in EnumerateChildren(node))
            {
                RegenerateIds(child);
            }
        }

        private static IEnumerable<FlowNode> EnumerateChildren(FlowNode node)
        {
            if (node is SequenceNode sequence)
            {
                return sequence.Children;
            }
            if (node is IfElseNode ifElse)
            {
                return ifElse.IfBranch.Concat(ifElse.ElseBranch);
            }
            if (node is ForLoopNode loop)
            {
                return loop.Body;
            }
            return Enumerable.Empty<FlowNode>();
        }

        private static ToolDto ToToolDto(ToolBase tool)
        {
            var dto = new ToolDto
            {
                TypeName = tool.GetType().AssemblyQualifiedName,
                ModuleName = tool.ModuleName,
                Properties = new Dictionary<string, JsonElement>()
            };
            foreach (PropertyInfo property in SerializableProperties(tool.GetType()))
            {
                dto.Properties[property.Name] = JsonSerializer.SerializeToElement(property.GetValue(tool), property.PropertyType, Options);
            }
            return dto;
        }

        private static ToolBase FromToolDto(ToolDto dto)
        {
            if (dto == null || string.IsNullOrWhiteSpace(dto.TypeName))
            {
                throw new InvalidOperationException("工具节点缺少工具类型。");
            }
            Type type = Type.GetType(dto.TypeName, throwOnError: true);
            var tool = Activator.CreateInstance(type, dto.ModuleName) as ToolBase;
            if (tool == null)
            {
                throw new InvalidOperationException($"工具类型 {dto.TypeName} 不能通过 string moduleName 构造。");
            }

            foreach (PropertyInfo property in SerializableProperties(type))
            {
                if (dto.Properties != null && dto.Properties.TryGetValue(property.Name, out JsonElement element))
                {
                    property.SetValue(tool, ConvertElement(element, property.PropertyType));
                }
            }
            return tool;
        }

        private static IEnumerable<PropertyInfo> SerializableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(ToolBase.ModuleName)
                    && IsSerializablePropertyType(p.PropertyType));
        }

        private static bool IsSerializablePropertyType(Type type)
        {
            return type == typeof(string) || type == typeof(int) || type == typeof(double) || type == typeof(bool)
                || type == typeof(byte[]) || type.IsEnum;
        }

        private static object ConvertElement(JsonElement element, Type type)
        {
            if (element.ValueKind == JsonValueKind.Null)
            {
                return null;
            }
            if (type == typeof(string))
            {
                return element.GetString();
            }
            if (type == typeof(int))
            {
                return element.GetInt32();
            }
            if (type == typeof(double))
            {
                return element.GetDouble();
            }
            if (type == typeof(bool))
            {
                return element.GetBoolean();
            }
            if (type == typeof(byte[]))
            {
                return element.GetBytesFromBase64();
            }
            if (type.IsEnum)
            {
                if (element.ValueKind == JsonValueKind.Number)
                {
                    return Enum.ToObject(type, element.GetInt32());
                }
                return Enum.Parse(type, element.GetString(), ignoreCase: true);
            }
            throw new NotSupportedException($"不支持反序列化属性类型 {type.FullName}");
        }

        private static ConditionDto ToConditionDto(ComparisonCondition condition)
        {
            if (condition == null)
            {
                return null;
            }
            return new ConditionDto
            {
                Left = ToOperandDto(condition.Left),
                Operator = condition.Operator.ToString(),
                Right = ToOperandDto(condition.Right)
            };
        }

        private static ComparisonCondition FromConditionDto(ConditionDto dto)
        {
            if (dto == null)
            {
                return null;
            }
            return new ComparisonCondition
            {
                Left = FromOperandDto(dto.Left),
                Operator = ParseEnum<ComparisonOperator>(dto.Operator, ComparisonOperator.Equal),
                Right = FromOperandDto(dto.Right)
            };
        }

        private static BranchOutputDto ToBranchOutputDto(BranchOutputDef output)
        {
            return new BranchOutputDto
            {
                Name = output.Name,
                Kind = output.Kind.ToString(),
                Type = output.Type.ToString(),
                ClrTypeName = output.ClrTypeName,
                IfValue = ToOperandDto(output.IfValue),
                ElseValue = ToOperandDto(output.ElseValue)
            };
        }

        private static BranchOutputDef FromBranchOutputDto(BranchOutputDto dto)
        {
            return new BranchOutputDef
            {
                Name = dto.Name,
                Kind = ParseEnum<VariableKind>(dto.Kind, VariableKind.Single),
                Type = ParseEnum<VariableType>(dto.Type, VariableType.Object),
                ClrTypeName = dto.ClrTypeName,
                IfValue = FromOperandDto(dto.IfValue),
                ElseValue = FromOperandDto(dto.ElseValue)
            };
        }

        private static FlowOutputDto ToFlowOutputDto(FlowOutputDef output)
        {
            return new FlowOutputDto
            {
                Name = output.Name,
                Kind = output.Kind.ToString(),
                Type = output.Type.ToString(),
                ClrTypeName = output.ClrTypeName,
                Value = ToOperandDto(output.Value)
            };
        }

        private static FlowOutputDef FromFlowOutputDto(FlowOutputDto dto)
        {
            return new FlowOutputDef
            {
                Name = dto.Name,
                Kind = ParseEnum<VariableKind>(dto.Kind, VariableKind.Single),
                Type = ParseEnum<VariableType>(dto.Type, VariableType.Object),
                ClrTypeName = dto.ClrTypeName,
                Value = FromOperandDto(dto.Value)
            };
        }

        private static OperandDto ToOperandDto(Operand operand)
        {
            if (operand == null)
            {
                return null;
            }
            return new OperandDto
            {
                IsConstant = operand.IsConstant,
                Reference = operand.IsConstant ? null : operand.Reference.ToString(),
                Constant = operand.IsConstant ? operand.ConstantValue : null
            };
        }

        private static Operand FromOperandDto(OperandDto dto)
        {
            if (dto == null)
            {
                return null;
            }
            return dto.IsConstant ? Operand.Const(ConstantFromElement(dto.Constant)) : Operand.Ref(dto.Reference);
        }

        private static object ConstantFromElement(object value)
        {
            if (value is JsonElement element)
            {
                switch (element.ValueKind)
                {
                    case JsonValueKind.Number:
                        if (element.TryGetInt32(out int i))
                        {
                            return i;
                        }
                        return element.GetDouble();
                    case JsonValueKind.True:
                        return true;
                    case JsonValueKind.False:
                        return false;
                    case JsonValueKind.String:
                        return element.GetString();
                    case JsonValueKind.Null:
                    case JsonValueKind.Undefined:
                        return null;
                    default:
                        return element.ToString();
                }
            }
            return value;
        }

        private static T ParseEnum<T>(string value, T fallback) where T : struct
        {
            if (!string.IsNullOrWhiteSpace(value) && Enum.TryParse(value, true, out T parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private sealed class NodeDto
        {
            public string Id { get; set; }
            public string Kind { get; set; }
            public string Name { get; set; }
            public ToolDto Tool { get; set; }
            public ConditionDto Condition { get; set; }
            public LoopDto Loop { get; set; }
            public List<BranchOutputDto> Outputs { get; set; }
            public List<FlowOutputDto> FlowOutputs { get; set; }
            public List<NodeDto> Children { get; set; }
            public List<NodeDto> IfBranch { get; set; }
            public List<NodeDto> ElseBranch { get; set; }
        }

        private sealed class ToolDto
        {
            public string TypeName { get; set; }
            public string ModuleName { get; set; }
            public Dictionary<string, JsonElement> Properties { get; set; }
        }

        private sealed class ConditionDto
        {
            public OperandDto Left { get; set; }
            public string Operator { get; set; }
            public OperandDto Right { get; set; }
        }

        private sealed class LoopDto
        {
            public string Mode { get; set; }
            public OperandDto CountSource { get; set; }
            public string ItemsPath { get; set; }
        }

        private sealed class BranchOutputDto
        {
            public string Name { get; set; }
            public string Kind { get; set; }
            public string Type { get; set; }
            public string ClrTypeName { get; set; }
            public OperandDto IfValue { get; set; }
            public OperandDto ElseValue { get; set; }
        }

        private sealed class FlowOutputDto
        {
            public string Name { get; set; }
            public string Kind { get; set; }
            public string Type { get; set; }
            public string ClrTypeName { get; set; }
            public OperandDto Value { get; set; }
        }

        private sealed class OperandDto
        {
            public bool IsConstant { get; set; }
            public string Reference { get; set; }
            public object Constant { get; set; }
        }
    }
}
