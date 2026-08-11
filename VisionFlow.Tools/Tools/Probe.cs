using System;
using HalconDotNet;

namespace VisionFlow.Tools
{
    /// <summary>临时探针：对全部 10 个匹配位置 × 多个阈值做跟随测量，找稳健参数。</summary>
    public static class Probe
    {
        /// <summary>区域链路探针：对 razors1.png 试不同的 二值化→处理→筛选 组合，为 T13 选参数。</summary>
        public static void RegionProbe()
        {
            string imagePath = RepoPaths.Find("src/Image/razors1.png");
            HOperatorSet.ReadImage(out HObject image, imagePath);
            HOperatorSet.GetImageSize(image, out HTuple w, out HTuple h);
            Console.WriteLine($"图像尺寸 {w.I} x {h.I}");

            foreach (double[] range in new[]
            {
                new[] { 128.0, 255.0 },
                new[] { 0.0, 127.0 },
                new[] { 100.0, 200.0 },
                new[] { 0.0, 100.0 }
            })
            {
                HOperatorSet.Threshold(image, out HObject region, range[0], range[1]);
                HOperatorSet.Connection(region, out HObject connected);
                HOperatorSet.CountObj(connected, out HTuple count);
                Console.WriteLine($"--- 灰度 [{range[0]}, {range[1]}]：连通区域 {count.I} 个 ---");

                HOperatorSet.SelectShape(connected, out HObject selected, "area", "and", 100, 99999999);
                HOperatorSet.CountObj(selected, out HTuple selCount);
                HOperatorSet.AreaCenter(selected, out HTuple areas, out HTuple rows, out HTuple cols);
                Console.WriteLine($"  面积>=100 的有 {selCount.I} 个：");
                for (int i = 0; i < selCount.I && i < 12; i++)
                {
                    HOperatorSet.SelectObj(selected, out HObject one, i + 1);
                    HOperatorSet.OrientationRegion(one, out HTuple phi);
                    Console.WriteLine($"    #{i}: 面积={areas[i].D:F0}, 中心=({rows[i].D:F1}, {cols[i].D:F1}), 角度={phi.D:F3}");
                    one.Dispose();
                }

                region.Dispose();
                connected.Dispose();
                selected.Dispose();
            }

            image?.Dispose();
        }

        public static void Run()
        {
            string imagePath = RepoPaths.Find("src/Image/razors1.png");
            string modelPath = RepoPaths.Find("src/Image/temp.shm");

            HOperatorSet.ReadImage(out HObject image, imagePath);
            HOperatorSet.GetImageSize(image, out HTuple w, out HTuple h);
            HOperatorSet.ReadShapeModel(modelPath, out HTuple modelId);
            HOperatorSet.FindShapeModel(image, modelId, -0.39, 0.79, 0.5, 10, 0.5,
                "least_squares", 0, 0.9, out HTuple rows, out HTuple cols, out HTuple angles, out HTuple scores);

            foreach (double threshold in new[] { 10.0, 5.0, 2.0, 1.0 })
            {
                int ok = 0;
                Console.WriteLine($"--- Threshold={threshold} ---");
                for (int i = 0; i < rows.Length; i++)
                {
                    HOperatorSet.VectorAngleToRigid(99, 79, 0, rows[i], cols[i], angles[i], out HTuple hom);
                    HOperatorSet.AffineTransPoint2d(hom, 28.1559, 82.9631, out HTuple r, out HTuple c);
                    double phi = -Math.PI / 2 + angles[i].D;

                    HOperatorSet.CreateMetrologyModel(out HTuple metrology);
                    HOperatorSet.SetMetrologyModelImageSize(metrology, w, h);
                    HOperatorSet.AddMetrologyObjectEllipseMeasure(metrology, r, c, phi, 10, 4, 7, 2, 1, threshold,
                        new HTuple(), new HTuple(), out _);
                    HOperatorSet.ApplyMetrologyModel(image, metrology);
                    HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "all_param", out HTuple param);
                    if (param.Length >= 5)
                    {
                        ok++;
                        Console.WriteLine($"  #{i}: 中心=({param[0].D:F2}, {param[1].D:F2}), Len1={param[3].D:F2}, Len2={param[4].D:F2}");
                    }
                    else
                    {
                        Console.WriteLine($"  #{i}: 失败（跟随位置 {r.D:F1}, {c.D:F1}）");
                    }
                    HOperatorSet.ClearMetrologyModel(metrology);
                }
                Console.WriteLine($"  成功 {ok}/{rows.Length}");
            }

            HOperatorSet.ClearShapeModel(modelId);
            image?.Dispose();
        }
    }
}
