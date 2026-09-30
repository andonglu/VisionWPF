using System;
using System.IO;
using System.Linq;
using System.Reflection;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Nodes;

namespace VisionFlow.Ui
{
    /// <summary>工具编辑和预览只操作配置副本；确认时发布重新校验后的配置。</summary>
    public sealed class ToolEditTransaction
    {
        private readonly ToolNode _target;
        private bool _committed;

        public ToolBase WorkingCopy { get; private set; }
        public ToolEditContext Context { get; private set; }
        public bool HasCommittedChanges { get; private set; }

        public ToolEditTransaction(ToolNode target, ToolEditContext context = null)
        {
            _target = target ?? throw new ArgumentNullException(nameof(target));
            WorkingCopy = CopyConfiguration(target.Tool);
            var editNode = new ToolNode(WorkingCopy, target.Name) { Id = target.Id };
            Context = new ToolEditContext
            {
                Node = editNode,
                Root = CopyScope(context?.Root, target, editNode),
                InputImage = context?.InputImage,
                InputImagePath = context?.InputImagePath,
                LastRunContext = context?.LastRunContext
            };
        }

        public static bool IsConfigurationException(Exception error)
        {
            return error is ArgumentException || error is FormatException || error is OverflowException
                || error is InvalidCastException || error is InvalidOperationException
                || error is NotSupportedException || error is TargetInvocationException
                || error is IOException || error is UnauthorizedAccessException
                || error is HalconException;
        }

        public void Commit()
        {
            if (_committed)
            {
                throw new InvalidOperationException("工具参数已经提交。");
            }

            // 在新实例上执行全部属性校验，原工具不参与任何可能失败的 setter。
            ToolBase configuration = CopyConfiguration(WorkingCopy);
            HasCommittedChanges = !SameConfiguration(_target.Tool, configuration);
            if (HasCommittedChanges)
            {
                _target.ReplaceTool(configuration);
            }
            _committed = true;
        }

        public static ToolBase CopyConfiguration(ToolBase source)
        {
            if (source == null)
            {
                throw new ArgumentNullException(nameof(source));
            }

            Type type = source.GetType();
            ConstructorInfo constructor = type.GetConstructor(new[] { typeof(string) });
            if (constructor == null)
            {
                throw new NotSupportedException($"工具 {type.FullName} 缺少 string moduleName 构造函数，无法安全编辑。");
            }

            PropertyInfo[] properties = ConfigurationProperties(type);
            object[] values = properties.Select(p => CopyValue(p.GetValue(source))).ToArray();
            var copy = (ToolBase)constructor.Invoke(new object[] { source.ModuleName });
            copy.ModuleName = source.ModuleName;
            for (int i = 0; i < properties.Length; i++)
            {
                properties[i].SetValue(copy, values[i]);
            }
            return copy;
        }

        private static PropertyInfo[] ConfigurationProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetMethod?.IsPublic == true && p.SetMethod?.IsPublic == true
                    && p.GetIndexParameters().Length == 0 && p.Name != nameof(ToolBase.ModuleName)
                    && (p.PropertyType == typeof(string) || p.PropertyType == typeof(int)
                        || p.PropertyType == typeof(double) || p.PropertyType == typeof(bool)
                        || p.PropertyType == typeof(byte[]) || p.PropertyType.IsEnum))
                .ToArray();
        }

        private static bool SameConfiguration(ToolBase left, ToolBase right)
        {
            if (left.GetType() != right.GetType()
                || !string.Equals(left.ModuleName, right.ModuleName, StringComparison.Ordinal))
            {
                return false;
            }
            foreach (PropertyInfo property in ConfigurationProperties(left.GetType()))
            {
                object before = property.GetValue(left);
                object after = property.GetValue(right);
                bool equal = before is byte[] first && after is byte[] second
                    ? first.SequenceEqual(second)
                    : Equals(before, after);
                if (!equal)
                {
                    return false;
                }
            }
            return true;
        }

        private static object CopyValue(object value)
        {
            return value is byte[] bytes ? bytes.Clone() : value;
        }

        // 仅复制目标的容器路径，不序列化整棵流程；保留原有上游声明与嵌套作用域。
        private static FlowNode CopyScope(FlowNode root, ToolNode target, ToolNode replacement)
        {
            if (root == target)
            {
                return replacement;
            }
            if (root is SequenceNode sequence)
            {
                FlowNode[] children = sequence.Children.Select(n => CopyScope(n, target, replacement)).ToArray();
                if (children.Where((n, i) => n != sequence.Children[i]).Any())
                {
                    var copy = new SequenceNode(sequence.Name) { Id = sequence.Id };
                    copy.Children.AddRange(children);
                    return copy;
                }
            }
            else if (root is ForLoopNode loop)
            {
                FlowNode[] children = loop.Body.Select(n => CopyScope(n, target, replacement)).ToArray();
                if (children.Where((n, i) => n != loop.Body[i]).Any())
                {
                    ForLoopNode copy = loop.Mode == ForLoopMode.Each
                        ? ForLoopNode.Each(loop.Name, loop.ItemsPath)
                        : ForLoopNode.Count(loop.Name, loop.CountSource);
                    copy.Id = loop.Id;
                    copy.Body.AddRange(children);
                    return copy;
                }
            }
            else if (root is IfElseNode branch)
            {
                FlowNode[] ifChildren = branch.IfBranch.Select(n => CopyScope(n, target, replacement)).ToArray();
                FlowNode[] elseChildren = branch.ElseBranch.Select(n => CopyScope(n, target, replacement)).ToArray();
                if (ifChildren.Where((n, i) => n != branch.IfBranch[i]).Any()
                    || elseChildren.Where((n, i) => n != branch.ElseBranch[i]).Any())
                {
                    var copy = new IfElseNode(branch.Name, branch.Condition) { Id = branch.Id };
                    copy.IfBranch.AddRange(ifChildren);
                    copy.ElseBranch.AddRange(elseChildren);
                    copy.Outputs.AddRange(branch.Outputs);
                    return copy;
                }
            }
            return root;
        }
    }
}
