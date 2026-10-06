using System;
using System.Collections.Generic;
using System.Linq;
using VisionFlow.Variables;

namespace VisionFlow.Editing
{
    /// <summary>
    /// 一个可声明的外部输入：上层宿主在运行前注入流程上下文的变量。
    /// 声明提供给编辑器（候选下拉）、声明级校验和运行前的必填检查，三处共用同一来源。
    /// </summary>
    public sealed class ExternalInputDef
    {
        /// <summary>引用路径，如 "Input.Image"、"Input.PartId"。</summary>
        public string Path { get; private set; }
        /// <summary>值的 CLR 类型（变量内容应为该类型）。</summary>
        public Type ClrType { get; private set; }
        /// <summary>是否必填：必填输入缺失时运行明确失败。</summary>
        public bool Required { get; private set; }
        /// <summary>用途说明（编辑器提示用）。</summary>
        public string Description { get; set; }

        public string ModuleName { get; private set; }
        public string VarName { get; private set; }

        public ExternalInputDef(string path, Type clrType, bool required = false, string description = null)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                throw new ArgumentException("外部输入路径不能为空", nameof(path));
            }
            var reference = VariableReference.Parse(path);
            if (reference.ElementIndex.HasValue || reference.MemberPath.Length > 0)
            {
                throw new ArgumentException($"外部输入路径 '{path}' 只能是 模块名.变量名 形式", nameof(path));
            }
            Path = path;
            ClrType = clrType ?? typeof(object);
            Required = required;
            Description = description;
            ModuleName = reference.ModuleName;
            VarName = reference.VarName;
        }
    }

    /// <summary>
    /// 外部输入注册表。默认注册 Input.Image；上层设备程序可在启动时
    /// 注册额外输入（编号、阈值、坐标等），编辑器与校验器随即识别。
    /// </summary>
    public static class ExternalInputRegistry
    {
        private static readonly Dictionary<string, ExternalInputDef> _items =
            new Dictionary<string, ExternalInputDef>(StringComparer.OrdinalIgnoreCase);

        [ThreadStatic]
        private static List<ExternalInputDef> _scoped;

        static ExternalInputRegistry()
        {
            ResetToDefaults();
        }

        /// <summary>全部外部输入：全局注册项，加上当前线程临时声明的输入（见 <see cref="BeginScope"/>）。</summary>
        public static IReadOnlyList<ExternalInputDef> Items
        {
            get
            {
                List<ExternalInputDef> items;
                lock (_items)
                {
                    items = _items.Values.ToList();
                }
                if (_scoped != null)
                {
                    foreach (ExternalInputDef def in _scoped)
                    {
                        items.RemoveAll(i => string.Equals(i.Path, def.Path, StringComparison.OrdinalIgnoreCase));
                        items.Add(def);
                    }
                }
                return items;
            }
        }

        /// <summary>
        /// 在当前线程临时追加外部输入（如校验子流程时声明父流程映射进来的输入），释放返回值即撤销。
        /// 不影响其他线程与全局注册。
        /// </summary>
        public static IDisposable BeginScope(IEnumerable<ExternalInputDef> inputs)
        {
            List<ExternalInputDef> previous = _scoped;
            var scoped = previous == null ? new List<ExternalInputDef>() : new List<ExternalInputDef>(previous);
            scoped.AddRange(inputs ?? Enumerable.Empty<ExternalInputDef>());
            _scoped = scoped;
            return new ScopeHandle(previous);
        }

        private sealed class ScopeHandle : IDisposable
        {
            private readonly List<ExternalInputDef> _previous;
            private bool _disposed;

            public ScopeHandle(List<ExternalInputDef> previous)
            {
                _previous = previous;
            }

            public void Dispose()
            {
                if (!_disposed)
                {
                    _scoped = _previous;
                    _disposed = true;
                }
            }
        }

        /// <summary>注册（或按路径覆盖）一个外部输入。</summary>
        public static void Register(ExternalInputDef def)
        {
            if (def == null)
            {
                throw new ArgumentNullException(nameof(def));
            }
            lock (_items)
            {
                _items[def.Path] = def;
            }
        }

        /// <summary>移除一个外部输入（主要用于测试）。</summary>
        public static bool Unregister(string path)
        {
            lock (_items)
            {
                return !string.IsNullOrWhiteSpace(path) && _items.Remove(path);
            }
        }

        /// <summary>恢复默认注册（仅 Input.Image），主要用于测试。</summary>
        public static void ResetToDefaults()
        {
            lock (_items)
            {
                _items.Clear();
                _items["Input.Image"] = new ExternalInputDef("Input.Image", typeof(HalconImage),
                    required: false, description: "上层注入的输入图像");
            }
        }
    }
}
