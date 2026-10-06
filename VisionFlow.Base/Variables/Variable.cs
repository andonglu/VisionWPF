using System;
using System.Collections;
using System.Linq;

namespace VisionFlow.Variables
{
    /// <summary>
    /// 变量形态：单值 / 数组 / 复合对象。
    /// </summary>
    public enum VariableKind
    {
        Single,
        Array,
        Object
    }

    /// <summary>
    /// 变量类型。Array 形态时表示元素类型。
    /// </summary>
    public enum VariableType
    {
        Int,
        Double,
        String,
        Bool,
        Object
    }

    /// <summary>
    /// 工具输出的运行时变量。通过 模块名 + 变量名 在全局上下文中寻址。
    /// </summary>
    public sealed class Variable
    {
        public string ModuleName { get; private set; }
        public string Name { get; private set; }
        public VariableKind Kind { get; private set; }
        public VariableType Type { get; private set; }
        public object Value { get; private set; }
        public int Count { get; private set; }

        private Variable(string moduleName, string name, VariableKind kind, VariableType type, object value, int count)
        {
            ModuleName = moduleName;
            Name = name;
            Kind = kind;
            Type = type;
            Value = value;
            Count = count;
        }

        /// <summary>创建单值变量（单个数值、字符串、布尔等）。</summary>
        public static Variable Single<T>(string moduleName, string name, VariableType type, T value)
        {
            return new Variable(moduleName, name, VariableKind.Single, type, value, 1);
        }

        /// <summary>创建数组变量（数值数组或元素集合）。</summary>
        public static Variable Array<T>(string moduleName, string name, VariableType elementType, System.Collections.Generic.IEnumerable<T> values)
        {
            var array = values == null ? new T[0] : values.ToArray();
            return new Variable(moduleName, name, VariableKind.Array, elementType, array, array.Length);
        }

        /// <summary>创建复合对象变量（如整体匹配结果）。</summary>
        public static Variable Object<T>(string moduleName, string name, T value, int itemCount)
        {
            return new Variable(moduleName, name, VariableKind.Object, VariableType.Object, value, itemCount);
        }

        public T GetValue<T>()
        {
            return (T)Value;
        }

        internal Variable CopyForPreview(System.Collections.Generic.Dictionary<object, object> copies)
        {
            return new Variable(ModuleName, Name, Kind, Type,
                PreviewValueCopy.Copy(Value, copies), Count);
        }

        /// <summary>同一个值换一个地址（子流程把输出交给父流程、把父流程变量作为子流程输入时使用）。</summary>
        internal Variable WithName(string moduleName, string name)
        {
            return new Variable(moduleName, name, Kind, Type, Value, Count);
        }

        /// <summary>取数组变量的第 index 个元素。</summary>
        public object GetElement(int index)
        {
            if (Kind != VariableKind.Array || !(Value is IList list))
            {
                throw new InvalidOperationException($"变量 {ModuleName}.{Name} 不是数组，无法按下标取值");
            }
            if (index < 0 || index >= list.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(index), $"变量 {ModuleName}.{Name} 下标 {index} 越界（长度 {list.Count}）");
            }
            return list[index];
        }
    }
}
