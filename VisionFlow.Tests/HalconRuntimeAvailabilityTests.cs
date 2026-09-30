using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tests;

/// <summary>
/// HALCON 原生运行库冒烟测试（VF-11 建议 6）：真实图像/资源类测试依赖本机
/// HALCON 运行库与授权，此类用于把"环境缺失"与"算法回归"区分开。
/// 每个原生用例独立检查环境，不依赖另一个用例的执行顺序或筛选范围。
/// </summary>
public class HalconRuntimeAvailabilityTests
{
    /// <summary>探测 HALCON 原生运行库与授权是否可用。</summary>
    public static bool IsAvailable(out string reason)
    {
        try
        {
            HOperatorSet.GenImageConst(out HObject image, "byte", 4, 4);
            using (image)
            {
                HOperatorSet.CountChannels(image, out HTuple channels);
                channels.Dispose();
            }
            reason = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException
            or BadImageFormatException or TypeInitializationException or HalconException)
        {
            reason = ex.GetBaseException().Message;
            return false;
        }
    }

    internal static void RequireAvailable()
    {
        Assert.True(IsAvailable(out string reason), "HALCON 原生运行库或授权不可用：" + reason);
    }

    [Fact]
    public void HalconRuntime_IsAvailable()
    {
        RequireAvailable();
    }

    [Fact]
    public void OwnedWrapper_Dispose_ReleasesNativeObject()
    {
        RequireAvailable();

        HOperatorSet.GenImageConst(out HObject image, "byte", 8, 8);
        using var wrapper = HalconImage.Owned(image);
        Assert.True(wrapper.Object.IsInitialized());

        wrapper.Dispose();
        wrapper.Dispose(); // 幂等

        Assert.False(wrapper.Object.IsInitialized());
    }
}
