using System;

namespace VisionFlow.Core
{
    /// <summary>
    /// 标记可自动加入工具箱的工具类型。插件 DLL 中的 ToolBase 派生类添加该特性后，
    /// 编辑器启动时会自动发现并注册。
    /// </summary>
    [AttributeUsage(AttributeTargets.Class, Inherited = false)]
    public sealed class ToolboxToolAttribute : Attribute
    {
        public string Id { get; set; }
        public string Category { get; private set; }
        public string DisplayName { get; private set; }
        public string DefaultModuleName { get; set; }
        public int Order { get; set; }

        public ToolboxToolAttribute(string category, string displayName)
        {
            Category = category;
            DisplayName = displayName;
            DefaultModuleName = displayName;
        }
    }
}
