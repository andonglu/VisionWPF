using System;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Runtime;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    /// <summary>
    /// 编辑窗口“执行测试”的公共流程：在工具的一次性配置副本上应用界面参数并运行，
    /// 编辑窗口持有的工具实例不会被修改（参数只在确认时写入）。
    /// 结果位于派生的预览上下文（借用上次运行的变量，Input.Image 取主窗口当前图像），由调用方 Dispose；
    /// 副本运行中缓存的资源（如模型句柄）在返回前释放。
    /// </summary>
    public sealed class ToolTestRun : IDisposable
    {
        private ToolTestRun(FlowContext context, NodeResult result, string moduleName)
        {
            Context = context;
            Result = result;
            ModuleName = moduleName;
        }

        /// <summary>预览上下文，结果展示结束后随 Dispose 释放。</summary>
        public FlowContext Context { get; private set; }
        public NodeResult Result { get; private set; }
        /// <summary>副本运行时的模块名（界面上可能已改名），用于读取输出变量。</summary>
        public string ModuleName { get; private set; }

        /// <param name="tool">编辑窗口持有的工具，只读取其配置。</param>
        /// <param name="applyEdits">把界面上的参数写入副本；解析失败可直接抛出异常。</param>
        public static ToolTestRun Run(ToolBase tool, Action<ToolBase> applyEdits, FlowContext lastRunContext, HObject inputImage)
        {
            if (tool == null)
            {
                throw new ArgumentNullException(nameof(tool));
            }
            ToolBase copy = ToolEditTransaction.CopyConfiguration(tool);
            FlowContext ctx = null;
            try
            {
                applyEdits?.Invoke(copy);
                ctx = (lastRunContext ?? new FlowContext()).CreatePreviewContext();
                if (inputImage != null && inputImage.IsInitialized())
                {
                    ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(inputImage), 1));
                }
                NodeResult result = copy.Run(ctx);
                var run = new ToolTestRun(ctx, result, copy.ModuleName);
                ctx = null;
                return run;
            }
            finally
            {
                ctx?.Dispose();
                FlowResources.Release(copy);
            }
        }

        public void Dispose()
        {
            Context?.Dispose();
            Context = null;
        }
    }
}
