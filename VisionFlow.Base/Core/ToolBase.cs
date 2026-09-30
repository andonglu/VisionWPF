using VisionFlow.Variables;

namespace VisionFlow.Core
{
    /// <summary>
    /// 工具抽象基类。工具是流程中的最小执行单元：
    /// 通过 Input 读取上游变量（变量引用），通过 SetOutput 把结果写入全局上下文。
    /// 一个工具可以输出多个变量（单值、数组、对象任意组合）。
    /// </summary>
    public abstract class ToolBase
    {
        /// <summary>模块名：该工具所有输出变量的命名空间（可在编辑器中修改）。</summary>
        public string ModuleName { get; set; }

        protected ToolBase(string moduleName)
        {
            ModuleName = moduleName;
        }

        public abstract NodeResult Run(FlowContext ctx);

        /// <summary>读取一个输入引用。</summary>
        protected T Input<T>(FlowContext ctx, string path)
        {
            return VariableReference.Parse(path).Resolve<T>(ctx);
        }

        /// <summary>
        /// 写入一个输出变量。输出归本次运行所有（VF-04）：值中的 HALCON 包装对象
        /// 会被标记为拥有（上下文 Dispose 时回收）。直接透传上游输入对象时请改用
        /// <see cref="SetBorrowedOutput"/>，避免调用方/上游的对象被提前释放。
        /// </summary>
        protected void SetOutput(FlowContext ctx, Variable variable)
        {
            ctx.SetOwnedVariable(variable);
        }

        /// <summary>写入借用语义的输出（如直接透传的上游输入对象），上下文释放时不处置。</summary>
        protected void SetBorrowedOutput(FlowContext ctx, Variable variable)
        {
            ctx.SetVariable(variable);
        }
    }
}
