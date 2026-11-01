using System;
using System.Collections.Generic;
using System.Text.Json;

namespace VisionFlow.Core
{
    /// <summary>
    /// 占位工具：流程文件中的工具类型在当前环境未安装/未登记时加载使用。
    /// 原样保留 ToolId、模块名与全部原始参数，保存时按原样写回，装回工具库后数据不丢。
    /// 运行时会抛出明确异常，由运行错误通道呈现。
    /// </summary>
    public sealed class UnknownTool : ToolBase
    {
        /// <summary>原流程文件中的稳定 ToolId（未登记时可能与 CLR 类型名相同）。</summary>
        public string StoredToolId { get; }

        /// <summary>原流程文件中的全部参数，按 JSON 原样保留。</summary>
        public IReadOnlyDictionary<string, JsonElement> RawProperties { get; }

        public UnknownTool(string moduleName, string storedToolId, IReadOnlyDictionary<string, JsonElement> rawProperties)
            : base(moduleName)
        {
            StoredToolId = string.IsNullOrWhiteSpace(storedToolId) ? "unknown" : storedToolId;
            RawProperties = rawProperties ?? new Dictionary<string, JsonElement>();
        }

        public override NodeResult Run(FlowContext ctx)
        {
            throw new InvalidOperationException(
                $"工具 '{ModuleName}' 的类型 '{StoredToolId}' 在当前环境未安装或尚未登记，无法执行。");
        }
    }
}
