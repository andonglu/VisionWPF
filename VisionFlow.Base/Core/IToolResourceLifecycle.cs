namespace VisionFlow.Core
{
    /// <summary>
    /// 可选的工具资源生命周期：预热与释放运行期缓存（如 HALCON 模型句柄、标定矩阵）。
    /// 流程文件只保存可序列化参数；资源默认在首次运行时按需加载，
    /// 宿主可在加载/切换流程后调用 <see cref="Prepare"/> 提前加载并尽早发现问题，
    /// 在流程被替换、节点删除或程序退出时调用 <see cref="ReleaseResources"/> 确定性释放。
    /// 两个方法都必须可重复调用、并与 Run 线程安全；释放后再次运行会重新按需加载。
    /// </summary>
    public interface IToolResourceLifecycle
    {
        /// <summary>
        /// 预先加载运行所需资源。参数尚未配置完整（如模板未创建）时直接返回，由运行时报告；
        /// 已配置但加载失败（数据损坏、文件不存在等）时抛出异常。
        /// </summary>
        void Prepare();

        /// <summary>释放已缓存的资源。</summary>
        void ReleaseResources();
    }
}
