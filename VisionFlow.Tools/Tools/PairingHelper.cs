using System;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 两组对象的配对规则（数组逐元素运算、区域距离等共用）：
    /// - 两侧个数相等：第 i 个与第 i 个配对；
    /// - 一侧只有 1 个：该对象与另一侧每个对象分别配对（一对多）；
    /// - 两侧个数不等且都不为 1：无法配对，报错。
    /// 任一侧为空时配对数为 0（1 对 0 也为 0）。
    /// </summary>
    public static class PairingHelper
    {
        /// <summary>计算配对数；无法配对时返回 false 并给出中文错误。</summary>
        public static bool TryGetPairCount(int leftCount, int rightCount, out int pairCount, out string error)
        {
            if (leftCount < 0 || rightCount < 0)
            {
                throw new ArgumentOutOfRangeException(leftCount < 0 ? nameof(leftCount) : nameof(rightCount));
            }
            if (leftCount != rightCount && leftCount != 1 && rightCount != 1)
            {
                pairCount = 0;
                error = $"两组对象个数不一致（{leftCount} 对 {rightCount}），无法逐一配对";
                return false;
            }
            pairCount = leftCount == 0 || rightCount == 0 ? 0 : Math.Max(leftCount, rightCount);
            error = null;
            return true;
        }

        /// <summary>第 pairIndex 个配对在某一侧的对象序号（该侧只有 1 个时总是 0）。</summary>
        public static int SideIndex(int pairIndex, int sideCount)
        {
            return sideCount == 1 ? 0 : pairIndex;
        }
    }
}
