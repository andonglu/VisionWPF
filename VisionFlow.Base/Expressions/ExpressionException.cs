using System;

namespace VisionFlow.Expressions
{
    /// <summary>表达式解析或求值错误。Position 为出错位置（从 0 开始的字符下标），无法定位时为 -1。</summary>
    public sealed class ExpressionException : Exception
    {
        public int Position { get; private set; }

        /// <summary>不含位置前缀的错误说明。</summary>
        public string Detail { get; private set; }

        public ExpressionException(string detail, int position, Exception innerException = null)
            : base(position >= 0 ? $"第 {position + 1} 个字符处：{detail}" : detail, innerException)
        {
            Detail = detail;
            Position = position;
        }
    }
}
