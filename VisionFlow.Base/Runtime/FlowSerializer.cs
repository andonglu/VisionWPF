using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Nodes;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>
    /// 流程 JSON 保存/加载。保存的是声明和参数，不保存运行期变量值。
    ///
    /// 兼容性约定：
    /// - 文件携带 FormatVersion（当前为 1）；高于当前支持的版本明确失败，不静默降级。
    /// - 工具优先按显式注册的稳定标识（或 ToolboxToolAttribute.Id / 类型全名）解析，
    ///   解析失败再退回旧版 TypeName；历史类型名、程序集限定名可注册为别名。
    /// - 文件中出现当前工具类型不存在的参数时产生警告（Load/LoadNode 的 warnings 参数），不静默丢失。
    /// </summary>
    public static class FlowSerializer
    {
        /// <summary>当前流程文件格式版本。</summary>
        public const int CurrentFormatVersion = 1;

        private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
        {
            WriteIndented = true
        };

        private static readonly Dictionary<string, Type> _toolIdentities =
            new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<Type, string> _stableToolIds = new Dictionary<Type, string>();
        private static readonly HashSet<Assembly> _scannedAssemblies = new HashSet<Assembly>();

        public static string Save(SequenceNode root)
        {
            NodeDto dto = ToDto(root);
            dto.FormatVersion = CurrentFormatVersion;
            return JsonSerializer.Serialize(dto, Options);
        }

        public static SequenceNode Load(string json)
        {
            return Load(json, null);
        }

        /// <summary>加载流程。warnings 非空时收集兼容性警告（未知参数、枚举回退等）。</summary>
        public static SequenceNode Load(string json, IList<string> warnings)
        {
            NodeDto dto = JsonSerializer.Deserialize<NodeDto>(json, Options);
            if (dto == null)
            {
                throw new InvalidOperationException("流程文件为空或格式错误。");
            }
            CheckFormatVersion(dto);
            FlowNode node = FromDto(dto, warnings);
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
            NodeDto dto = ToDto(node);
            dto.FormatVersion = CurrentFormatVersion;
            return JsonSerializer.Serialize(dto, Options);
        }

        public static FlowNode LoadNode(string json, bool regenerateIds = false)
        {
            return LoadNode(json, regenerateIds, null);
        }

        /// <summary>加载单个节点（剪贴板路径）。warnings 非空时收集兼容性警告。</summary>
        public static FlowNode LoadNode(string json, bool regenerateIds, IList<string> warnings)
        {
            NodeDto dto = JsonSerializer.Deserialize<NodeDto>(json, Options);
            if (dto == null)
            {
                throw new InvalidOperationException("节点数据为空或格式错误。");
            }
            CheckFormatVersion(dto);
            FlowNode node = FromDto(dto, warnings);
            if (regenerateIds)
            {
                RegenerateIds(node);
            }
            return node;
        }

        /// <summary>注册工具类型的持久化身份（稳定 ID / 全名 / 程序集限定名），供按稳定 ID 加载。</summary>
        public static void RegisterToolType(Type toolType)
        {
            if (toolType == null || !typeof(ToolBase).IsAssignableFrom(toolType) || toolType.IsAbstract)
            {
                return;
            }
            RegisterToolIdentities(toolType, null, Array.Empty<string>());
        }

        /// <summary>
        /// 注册与 CLR 类型名无关的持久化 ID。类型或程序集迁移时，将旧 ToolId、FullName、
        /// AssemblyQualifiedName 作为 aliases 注册；身份冲突明确失败，不覆盖已有映射。
        /// </summary>
        public static void RegisterToolType(Type toolType, string stableId, params string[] aliases)
        {
            ArgumentNullException.ThrowIfNull(toolType);
            if (!typeof(ToolBase).IsAssignableFrom(toolType) || toolType.IsAbstract)
            {
                throw new ArgumentException("工具类型必须是非抽象的 ToolBase 派生类。", nameof(toolType));
            }
            ArgumentException.ThrowIfNullOrWhiteSpace(stableId);
            foreach (string alias in aliases ?? Array.Empty<string>())
            {
                if (string.IsNullOrWhiteSpace(alias))
                {
                    throw new ArgumentException("工具身份别名不能为空。", nameof(aliases));
                }
            }
            RegisterToolIdentities(toolType, stableId, aliases ?? Array.Empty<string>());
        }

        private static void RegisterToolIdentities(Type toolType, string explicitId, string[] aliases)
        {
            lock (_toolIdentities)
            {
                if (explicitId != null && _stableToolIds.TryGetValue(toolType, out string registeredId)
                    && !string.Equals(registeredId, explicitId, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"工具类型 '{toolType.FullName}' 已注册稳定标识 '{registeredId}'，不能改为 '{explicitId}'。");
                }
                var identities = new HashSet<string>(aliases, StringComparer.OrdinalIgnoreCase)
                {
                    explicitId ?? StableToolId(toolType),
                    DefaultToolId(toolType),
                    toolType.FullName,
                    toolType.AssemblyQualifiedName,
                    $"{toolType.FullName}, {toolType.Assembly.GetName().Name}"
                };
                foreach (string identity in identities)
                {
                    if (_toolIdentities.TryGetValue(identity, out Type registeredType) && registeredType != toolType)
                    {
                        throw new InvalidOperationException(
                            $"工具身份 '{identity}' 冲突：已注册 '{registeredType.AssemblyQualifiedName}'，" +
                            $"不能注册 '{toolType.AssemblyQualifiedName}'。");
                    }
                }
                foreach (string identity in identities)
                {
                    _toolIdentities[identity] = toolType;
                }
                if (explicitId != null)
                {
                    _stableToolIds[toolType] = explicitId;
                }
            }
        }

        private static void CheckFormatVersion(NodeDto dto)
        {
            if (dto.FormatVersion.HasValue && dto.FormatVersion.Value > CurrentFormatVersion)
            {
                throw new NotSupportedException(
                    $"流程文件版本 {dto.FormatVersion.Value} 高于当前支持的版本 {CurrentFormatVersion}，请使用更新版本的编辑器打开。");
            }
        }

        /// <summary>显式稳定 ID 优先；自动扫描和旧版注册不会将其还原成 CLR 类型名。</summary>
        private static string StableToolId(Type toolType)
        {
            lock (_toolIdentities)
            {
                return _stableToolIds.TryGetValue(toolType, out string stableId) ? stableId : DefaultToolId(toolType);
            }
        }

        private static string DefaultToolId(Type toolType)
        {
            var attribute = toolType.GetCustomAttribute<ToolboxToolAttribute>(inherit: false);
            return attribute != null && !string.IsNullOrWhiteSpace(attribute.Id)
                ? attribute.Id
                : toolType.FullName;
        }

        private static Type ResolveToolType(ToolDto dto)
        {
            EnsureToolIdentities();
            lock (_toolIdentities)
            {
                if (!string.IsNullOrWhiteSpace(dto.ToolId)
                    && _toolIdentities.TryGetValue(dto.ToolId, out Type byId))
                {
                    return byId;
                }
                if (!string.IsNullOrWhiteSpace(dto.TypeName)
                    && _toolIdentities.TryGetValue(dto.TypeName, out Type byName))
                {
                    return byName;
                }
            }
            if (!string.IsNullOrWhiteSpace(dto.TypeName))
            {
                try
                {
                    Type type = Type.GetType(dto.TypeName, throwOnError: true);
                    RegisterToolType(type);
                    return type;
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException(
                        $"无法加载工具（ToolId: {dto.ToolId ?? "-"}，TypeName: {dto.TypeName}）。" +
                        $"请确认对应工具库已加载或插件已注册。原因：{ex.Message}", ex);
                }
            }
            throw new InvalidOperationException($"工具节点缺少工具类型（ToolId: {dto.ToolId ?? "-"}）。");
        }

        /// <summary>按需扫描已加载程序集，建立稳定 ID → 类型的映射（宿主也可提前 RegisterToolType）。</summary>
        private static void EnsureToolIdentities()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                lock (_toolIdentities)
                {
                    if (assembly.IsDynamic || _scannedAssemblies.Contains(assembly))
                    {
                        continue;
                    }
                }
                Type[] types;
                try
                {
                    types = assembly.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(t => t != null).ToArray();
                }
                catch
                {
                    continue;
                }
                Type[] toolTypes = types.Where(t => !t.IsAbstract && typeof(ToolBase).IsAssignableFrom(t)).ToArray();
                // 仅加载 DLL 尚不保证模块初始化器已运行。先初始化工具模块，且不持有登记锁。
                foreach (Module module in toolTypes.Select(t => t.Module).Distinct())
                {
                    RuntimeHelpers.RunModuleConstructor(module.ModuleHandle);
                }
                foreach (Type type in toolTypes)
                {
                    RegisterToolType(type);
                }
                lock (_toolIdentities)
                {
                    _scannedAssemblies.Add(assembly);
                }
            }
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

        private static FlowNode FromDto(NodeDto dto, IList<string> warnings)
        {
            FlowNode node;
            switch (dto.Kind)
            {
                case "Sequence":
                    var sequence = new SequenceNode(dto.Name ?? "顺序");
                    AddChildren(sequence.Children, dto.Children, warnings);
                    node = sequence;
                    break;
                case "Tool":
                    node = new ToolNode(FromToolDto(dto.Tool, warnings), dto.Name);
                    break;
                case "IfElse":
                    var ifElse = new IfElseNode(dto.Name ?? "条件分支", FromConditionDto(dto.Condition, warnings));
                    AddChildren(ifElse.IfBranch, dto.IfBranch, warnings);
                    AddChildren(ifElse.ElseBranch, dto.ElseBranch, warnings);
                    foreach (BranchOutputDto output in dto.Outputs ?? new List<BranchOutputDto>())
                    {
                        ifElse.Outputs.Add(FromBranchOutputDto(output, warnings));
                    }
                    node = ifElse;
                    break;
                case "ForLoop":
                    if (dto.Loop == null)
                    {
                        throw new InvalidOperationException("ForLoop 节点缺少循环配置。");
                    }
                    ForLoopMode mode = ParseEnum<ForLoopMode>(dto.Loop.Mode, ForLoopMode.Count, warnings, "循环模式");
                    var loop = mode == ForLoopMode.Each
                        ? ForLoopNode.Each(dto.Name ?? "遍历循环", dto.Loop.ItemsPath)
                        : ForLoopNode.Count(dto.Name ?? "按次数循环", FromOperandDto(dto.Loop.CountSource));
                    AddChildren(loop.Body, dto.Children, warnings);
                    node = loop;
                    break;
                case "FlowOutput":
                    var outputNode = new FlowOutputNode(dto.Name ?? "流程输出");
                    foreach (FlowOutputDto output in dto.FlowOutputs ?? new List<FlowOutputDto>())
                    {
                        outputNode.Outputs.Add(FromFlowOutputDto(output, warnings));
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

        private static void AddChildren(IList<FlowNode> target, List<NodeDto> children, IList<string> warnings)
        {
            foreach (NodeDto child in children ?? new List<NodeDto>())
            {
                target.Add(FromDto(child, warnings));
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
            Type type = tool.GetType();
            RegisterToolType(type);
            var dto = new ToolDto
            {
                ToolId = StableToolId(type),
                TypeName = type.AssemblyQualifiedName,
                ModuleName = tool.ModuleName,
                Properties = new Dictionary<string, JsonElement>()
            };
            foreach (PropertyInfo property in SerializableProperties(type))
            {
                dto.Properties[property.Name] = JsonSerializer.SerializeToElement(property.GetValue(tool), property.PropertyType, Options);
            }
            return dto;
        }

        private static ToolBase FromToolDto(ToolDto dto, IList<string> warnings)
        {
            if (dto == null || (string.IsNullOrWhiteSpace(dto.ToolId) && string.IsNullOrWhiteSpace(dto.TypeName)))
            {
                throw new InvalidOperationException("工具节点缺少工具类型。");
            }
            Type type = ResolveToolType(dto);
            var tool = Activator.CreateInstance(type, dto.ModuleName) as ToolBase;
            if (tool == null)
            {
                throw new InvalidOperationException($"工具类型 {type.FullName} 不能通过 string moduleName 构造。");
            }

            List<PropertyInfo> known = SerializableProperties(type).ToList();
            if (dto.Properties != null)
            {
                foreach (string key in dto.Properties.Keys)
                {
                    if (!known.Any(p => p.Name == key))
                    {
                        warnings?.Add($"工具 '{dto.ModuleName}'（{type.Name}）的参数 '{key}' 在当前版本中不存在，已忽略。");
                    }
                }
            }
            foreach (PropertyInfo property in known)
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

        private static ComparisonCondition FromConditionDto(ConditionDto dto, IList<string> warnings)
        {
            if (dto == null)
            {
                return null;
            }
            return new ComparisonCondition
            {
                Left = FromOperandDto(dto.Left),
                Operator = ParseEnum<ComparisonOperator>(dto.Operator, ComparisonOperator.Equal, warnings, "条件运算符"),
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

        private static BranchOutputDef FromBranchOutputDto(BranchOutputDto dto, IList<string> warnings)
        {
            return new BranchOutputDef
            {
                Name = dto.Name,
                Kind = ParseEnum<VariableKind>(dto.Kind, VariableKind.Single, warnings, $"分支输出 '{dto.Name}' 的形态"),
                Type = ParseEnum<VariableType>(dto.Type, VariableType.Object, warnings, $"分支输出 '{dto.Name}' 的类型"),
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

        private static FlowOutputDef FromFlowOutputDto(FlowOutputDto dto, IList<string> warnings)
        {
            return new FlowOutputDef
            {
                Name = dto.Name,
                Kind = ParseEnum<VariableKind>(dto.Kind, VariableKind.Single, warnings, $"流程输出 '{dto.Name}' 的形态"),
                Type = ParseEnum<VariableType>(dto.Type, VariableType.Object, warnings, $"流程输出 '{dto.Name}' 的类型"),
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

        private static T ParseEnum<T>(string value, T fallback, IList<string> warnings, string what) where T : struct
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (Enum.TryParse(value, true, out T parsed))
                {
                    return parsed;
                }
                warnings?.Add($"{what} '{value}' 无法识别，已回退为 {fallback}。");
            }
            return fallback;
        }

        private sealed class NodeDto
        {
            public int? FormatVersion { get; set; }
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
            public string ToolId { get; set; }
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
