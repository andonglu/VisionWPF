using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HalconDotNet;

namespace VisionFlow.Tools.Calibration
{
    /// <summary>相机标定中一张图像的结果。</summary>
    public sealed class CameraCalibrationImage
    {
        public string Name { get; set; }
        /// <summary>是否找到标定板（未找到的图像不参与标定，界面可移除后重算）。</summary>
        public bool Found { get; set; }
        /// <summary>找到时为该图像的反投影误差（像素，RMS）。</summary>
        public double? Error { get; set; }
        public int PointCount { get; set; }
        /// <summary>未找到时的原因（中文）。</summary>
        public string Message { get; set; }
    }

    /// <summary>一次相机标定的结果。</summary>
    public sealed class CameraCalibrationRun
    {
        /// <summary>可直接写入 .vfcal.json 的 Camera 段（位姿已按参考图像与厚度修正到测量平面）。</summary>
        public CameraCalibration Camera { get; set; }
        public List<CameraCalibrationImage> Images { get; set; } = new List<CameraCalibrationImage>();
        /// <summary>calibrate_cameras 返回的整体反投影误差（像素）。</summary>
        public double Error { get; set; }
        /// <summary>按逐张同样方法计算的全部标记点 RMS（像素，与 Error 定义略有不同，供对照）。</summary>
        public double PointRmsError { get; set; }
    }

    /// <summary>
    /// 相机标定（CB-04，UI 无关）：create_calib_data → set_calib_data_cam_param / set_calib_data_calib_object → 逐张 find_calib_object
    /// → calibrate_cameras → get_calib_data；参考图像的标定板位姿经 set_origin_pose(…, 0, 0, +厚度) 修正到测量平面
    /// （22.11 实测：标定板 z 轴背离相机，厚度以米计，见计划第 15 节）。
    /// </summary>
    public static class CameraCalibrator
    {
        /// <summary>本批支持的相机模型（其他类型明确报“暂不支持”）。</summary>
        public static readonly string[] SupportedCameraTypes = { "area_scan_division", "area_scan_polynomial" };

        /// <summary>
        /// 手拼初始参数元组（gen_cam_par_* 是 HDevelop 过程，.NET 中没有）：类型名在前，参数名与顺序取 <see cref="CameraCalibration.TryGetParameterNames"/>；
        /// 畸变系数初值为 0。焦距、像元宽高以米计；image_width / image_height 为整数。
        /// </summary>
        public static HTuple BuildStartParameters(string cameraType, double focus, double cellWidth, double cellHeight,
            double centerColumn, double centerRow, int imageWidth, int imageHeight)
        {
            if (!SupportedCameraTypes.Contains(cameraType) || !CameraCalibration.TryGetParameterNames(cameraType, out string[] names))
            {
                throw new NotSupportedException($"暂不支持的相机模型：{cameraType}（支持 {string.Join(" / ", SupportedCameraTypes)}）");
            }
            if (!(focus > 0) || !(cellWidth > 0) || !(cellHeight > 0) || imageWidth <= 0 || imageHeight <= 0)
            {
                throw new ArgumentException("初始参数无效：焦距、像元宽高、图像宽高都必须大于 0");
            }
            var tuple = new HTuple(cameraType);
            foreach (string name in names)
            {
                switch (name)
                {
                    case "focus": tuple = tuple.TupleConcat(focus); break;
                    case "sx": tuple = tuple.TupleConcat(cellWidth); break;
                    case "sy": tuple = tuple.TupleConcat(cellHeight); break;
                    case "cx": tuple = tuple.TupleConcat(centerColumn); break;
                    case "cy": tuple = tuple.TupleConcat(centerRow); break;
                    case "image_width": tuple = tuple.TupleConcat(imageWidth); break;
                    case "image_height": tuple = tuple.TupleConcat(imageHeight); break;
                    default: tuple = tuple.TupleConcat(0.0); break;
                }
            }
            return tuple;
        }

        /// <summary>
        /// 执行相机标定。images 与 names 一一对应；未找到标定板的图像记入结果（Found = false）而不参与标定；
        /// referenceImage 为放在测量平面上的那张图像在 images 中的序号，plateThickness 为标定板厚度（米）。
        /// 标定板描述文件缺失、没有图像找到标定板、参考图像未找到标定板或 calibrate_cameras 失败时抛出带中文说明的异常。
        /// </summary>
        public static CameraCalibrationRun Calibrate(IReadOnlyList<HObject> images, IReadOnlyList<string> names, string plateDescription,
            HTuple startParameters, int referenceImage, double plateThickness)
        {
            if (images == null || images.Count == 0)
            {
                throw new InvalidOperationException("没有标定图像：请加入标定板图像（建议 10~20 张，覆盖视野不同位置与倾斜角度）");
            }
            if (string.IsNullOrWhiteSpace(plateDescription) || !File.Exists(plateDescription))
            {
                throw new FileNotFoundException($"标定板描述文件不存在：{plateDescription}", plateDescription);
            }
            string cameraType = startParameters != null && startParameters.Length > 0 && startParameters[0].Type == HTupleType.STRING ? startParameters[0].S : null;
            if (!SupportedCameraTypes.Contains(cameraType))
            {
                throw new NotSupportedException($"暂不支持的相机模型：{cameraType}（支持 {string.Join(" / ", SupportedCameraTypes)}）");
            }
            if (referenceImage < 0 || referenceImage >= images.Count)
            {
                throw new ArgumentOutOfRangeException(nameof(referenceImage), $"参考图像序号 {referenceImage} 超出范围（共 {images.Count} 张）");
            }
            if (!(plateThickness >= 0) || double.IsInfinity(plateThickness))
            {
                throw new ArgumentException("标定板厚度不能为负数", nameof(plateThickness));
            }
            CameraCalibration.TryGetParameterNames(cameraType, out string[] parameterNames);
            int expectedWidth = (int)Math.Round(startParameters[Array.IndexOf(parameterNames, "image_width") + 1].D);
            int expectedHeight = (int)Math.Round(startParameters[Array.IndexOf(parameterNames, "image_height") + 1].D);

            var run = new CameraCalibrationRun();
            var poseIndexOfImage = new int?[images.Count];
            HTuple calib = null;
            try
            {
                HOperatorSet.CreateCalibData("calibration_object", 1, 1, out calib);
                HOperatorSet.SetCalibDataCamParam(calib, 0, new HTuple(), startParameters);
                HOperatorSet.SetCalibDataCalibObject(calib, 0, plateDescription);
                int poseIndex = 0;
                for (int i = 0; i < images.Count; i++)
                {
                    var item = new CameraCalibrationImage { Name = names != null && i < names.Count ? names[i] : $"图像 {i + 1}" };
                    run.Images.Add(item);
                    HOperatorSet.GetImageSize(images[i], out HTuple width, out HTuple height);
                    if (width.I != expectedWidth || height.I != expectedHeight)
                    {
                        item.Message = $"图像尺寸 {width.I}×{height.I} 与初始参数 {expectedWidth}×{expectedHeight} 不一致";
                        continue;
                    }
                    try
                    {
                        // 位姿序号按找到的顺序连续编号（22.11 实测序号可有空位，连续编号便于对应）
                        HOperatorSet.FindCalibObject(images[i], calib, 0, 0, poseIndex, new HTuple(), new HTuple());
                        item.Found = true;
                        poseIndexOfImage[i] = poseIndex++;
                    }
                    catch (HalconException ex)
                    {
                        item.Message = ex.GetErrorCode() == 8397 ? "未找到标定板（标记分割失败）" : $"未找到标定板：{ex.Message}";
                    }
                }
                if (poseIndex == 0)
                {
                    throw new InvalidOperationException("没有图像找到标定板：请检查标定板描述文件与图像");
                }
                if (!poseIndexOfImage[referenceImage].HasValue)
                {
                    throw new InvalidOperationException($"参考图像（第 {referenceImage + 1} 张）没有找到标定板，请另选一张放在测量平面上的图像");
                }

                HTuple error;
                try
                {
                    HOperatorSet.CalibrateCameras(calib, out error);
                }
                catch (HalconException ex)
                {
                    throw new InvalidOperationException($"标定失败（{poseIndex} 张找到标定板）：{ex.Message}", ex);
                }
                run.Error = error.D;
                HOperatorSet.GetCalibData(calib, "camera", 0, "params", out HTuple cameraParameters);
                HOperatorSet.GetCalibData(calib, "calib_obj", 0, "x", out HTuple markX);
                HOperatorSet.GetCalibData(calib, "calib_obj", 0, "y", out HTuple markY);
                HOperatorSet.GetCalibData(calib, "calib_obj", 0, "z", out HTuple markZ);
                double sumAll = 0;
                int countAll = 0;
                for (int i = 0; i < images.Count; i++)
                {
                    if (!poseIndexOfImage[i].HasValue)
                    {
                        continue;
                    }
                    double sum = ImageSquaredError(calib, poseIndexOfImage[i].Value, cameraParameters, markX, markY, markZ, out int points);
                    run.Images[i].Error = Math.Sqrt(sum / points);
                    run.Images[i].PointCount = points;
                    sumAll += sum;
                    countAll += points;
                }
                run.PointRmsError = Math.Sqrt(sumAll / countAll);

                HOperatorSet.GetCalibData(calib, "calib_obj_pose", new HTuple(0, poseIndexOfImage[referenceImage].Value), "pose", out HTuple referencePose);
                HOperatorSet.SetOriginPose(referencePose, 0, 0, plateThickness, out HTuple planePose);
                CameraCalibration camera = CameraCalibration.FromHalcon(cameraParameters, planePose);
                camera.PlaneThickness = plateThickness;
                camera.RmsError = run.Error;
                camera.PlateDescription = Path.GetFileName(plateDescription);
                camera.ImageCount = poseIndex;
                run.Camera = camera;
                return run;
            }
            finally
            {
                if (calib != null)
                {
                    HOperatorSet.ClearCalibData(calib);
                }
            }
        }

        /// <summary>
        /// 一张图像的反投影平方误差之和：观测点与“标定后位姿 + 相机参数”投影的标记点之差（get_calib_data_observ_points
        /// 返回的位姿是初始估计，须用 calib_obj_pose 的标定后位姿，见计划第 15 节）。
        /// </summary>
        private static double ImageSquaredError(HTuple calib, int poseIndex, HTuple cameraParameters, HTuple markX, HTuple markY, HTuple markZ, out int points)
        {
            HOperatorSet.GetCalibDataObservPoints(calib, 0, 0, poseIndex, out HTuple rows, out HTuple columns, out HTuple indices, out HTuple _);
            HOperatorSet.GetCalibData(calib, "calib_obj_pose", new HTuple(0, poseIndex), "pose", out HTuple pose);
            HOperatorSet.PoseToHomMat3d(pose, out HTuple homMat3D);
            HOperatorSet.AffineTransPoint3d(homMat3D, markX.TupleSelect(indices), markY.TupleSelect(indices), markZ.TupleSelect(indices), out HTuple x, out HTuple y, out HTuple z);
            HOperatorSet.Project3dPoint(x, y, z, cameraParameters, out HTuple projectedRows, out HTuple projectedColumns);
            double sum = 0;
            for (int k = 0; k < rows.Length; k++)
            {
                sum += Math.Pow(projectedRows[k].D - rows[k].D, 2) + Math.Pow(projectedColumns[k].D - columns[k].D, 2);
            }
            points = rows.Length;
            return sum;
        }

        /// <summary>标定板描述文件的默认目录（%HALCONROOT%\calib）；没有环境变量时返回 null。</summary>
        public static string DefaultPlateDirectory()
        {
            string root = Environment.GetEnvironmentVariable("HALCONROOT");
            return string.IsNullOrWhiteSpace(root) ? null : Path.Combine(root, "calib");
        }

        /// <summary>界面显示用：米 → 毫米文本。</summary>
        public static string Millimeters(double meters)
        {
            return (meters * 1000).ToString("G6", CultureInfo.CurrentCulture);
        }
    }
}
