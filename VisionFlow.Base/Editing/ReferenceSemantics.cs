using System;
using System.Collections;
using System.Linq;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>引用静态语义检查的结果。</summary>
    public sealed class RefTypeCheckResult
    {
        public bool Success { get; private set; }
        /// <summary>推导出的值 CLR 类型；无法静态确定时为 typeof(object)。数组整体引用时为元素类型的数组。</summary>
        public Type ClrType { get; private set; }
        /// <summary>引用结果是否为数组，包括数组类型的成员。</summary>
        public bool IsCollectionValue { get; set; }
        public string Error { get; private set; }

        public static RefTypeCheckResult Ok(Type clrType)
        {
            return new RefTypeCheckResult
            {
                Success = true,
                ClrType = clrType ?? typeof(object),
                IsCollectionValue = clrType?.IsArray == true
            };
        }

        public static RefTypeCheckResult Fail(string error)
        {
            return new RefTypeCheckResult { Success = false, Error = error };
        }
    }

    /// <summary>
    /// 引用静态语义检查：与 <see cref="VariableReference"/> 运行时解析使用同一套规则，
    /// 按“输出声明 → 数组下标 → 成员链”逐段推导类型，而不是与候选字符串做精确匹配。
    ///
    /// 错误分类：
    /// - 静态可判断错误（变量不存在、非数组使用下标、已知类型上不存在成员、
    ///   循环变量使用环境不符）在此直接报出；
    /// - 数组实际长度不足、值为 null、object 类型上的成员链等留给运行期明确报错。
    /// </summary>
    public static class ReferenceSemantics
    {
        public static RefTypeCheckResult Check(VariableReference reference, RefScope scope)
        {
            if (reference == null)
            {
                return RefTypeCheckResult.Fail("引用为空");
            }

            Type current;
            if (reference.ModuleName == "Loop")
            {
                if (scope.LoopMode == RefLoopMode.None)
                {
                    return RefTypeCheckResult.Fail(
                        $"引用 '{reference}' 使用了循环变量，但当前节点不在任何循环内");
                }
                if (reference.ElementIndex.HasValue)
                {
                    return RefTypeCheckResult.Fail($"循环变量 'Loop.{reference.VarName}' 不支持下标");
                }
                switch (reference.VarName)
                {
                    case "Index":
                    case "Count":
                        current = typeof(int);
                        break;
                    case "Current":
                        if (scope.LoopMode == RefLoopMode.Count)
                        {
                            return RefTypeCheckResult.Fail(
                                $"引用 '{reference}'：按次数循环不提供 Loop.Current，仅支持 Loop.Index / Loop.Count");
                        }
                        current = scope.LoopCurrentElementType ?? typeof(object);
                        break;
                    default:
                        return RefTypeCheckResult.Fail(
                            $"未知的循环变量 'Loop.{reference.VarName}'，仅支持 Index / Count / Current");
                }
            }
            else
            {
                string basePath = reference.ModuleName + "." + reference.VarName;
                RefCandidate candidate = scope.Candidates.FirstOrDefault(c => SamePath(c.Path, basePath));
                if (candidate == null)
                {
                    return RefTypeCheckResult.Fail(
                        $"引用 '{reference}' 不存在、顺序不合法，或不在当前作用域内" +
                        "（If/Else 分支与循环体的内部输出对外不可见）");
                }
                if (reference.ElementIndex.HasValue && !candidate.IsCollection)
                {
                    return RefTypeCheckResult.Fail(
                        $"引用 '{reference}' 使用了下标，但 '{basePath}' 不是数组输出");
                }

                current = candidate.ClrType ?? typeof(object);
                if (candidate.IsCollection && !reference.ElementIndex.HasValue)
                {
                    // 整体引用数组变量：成员链（如 .Count）作用于数组本身
                    current = current.MakeArrayType();
                }

                return WalkMembers(reference, current);
            }

            return WalkMembers(reference, current);
        }

        private static RefTypeCheckResult WalkMembers(VariableReference reference, Type startType)
        {
            Type current = startType ?? typeof(object);
            foreach (string member in reference.MemberPath)
            {
                if (current == typeof(object))
                {
                    // 类型未知：成员是否存在留待运行期验证
                    continue;
                }
                if (member == "Count" && typeof(ICollection).IsAssignableFrom(current))
                {
                    current = typeof(int);
                    continue;
                }
                var property = current.GetProperty(member);
                if (property != null)
                {
                    current = property.PropertyType;
                    continue;
                }
                var field = current.GetField(member);
                if (field != null)
                {
                    current = field.FieldType;
                    continue;
                }
                return RefTypeCheckResult.Fail(
                    $"引用 '{reference}'：类型 {current.Name} 上不存在成员 '{member}'");
            }
            return RefTypeCheckResult.Ok(current);
        }

        private static bool SamePath(string left, string right)
        {
            return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
        }
    }
}
