using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>
    /// 描述子匹配（非标定，find_uncalib_descriptor_model）：基于纹理特征点，可应对透视变形、
    /// 部分遮挡与大角度旋转，适合印刷图案、包装面等纹理丰富的目标（对应 HALCON 示例 locate_cookie_box_multiple_models）。
    ///
    /// 模型的保存方式：描述子模型序列化后可达数十 MB，不适合内嵌在流程文件中，
    /// 因此流程只保存模板裁剪图（TemplateData）、模板在参考图中的位置与训练参数；
    /// 模型在预热或首次运行时按模板重建（随机种子固定，结果可复现），并缓存到本机
    /// %LocalAppData%\VisionFlow\DescriptorModelCache（键为模板、参数与 HALCON 版本的哈希），之后的加载只需几十毫秒。
    ///
    /// 输出与其他匹配工具一致；每个结果的 HomMat 为“参考图 → 当前图”的仿射近似（由模板四角的投影拟合），
    /// 可直接供测量、手动 Region 跟随；完整的投影矩阵在 Items[i].ProjectiveHomMat，
    /// 结果轮廓为模板矩形四角经投影变换后的四边形。Angle 为平面内转角（弧度，HALCON 约定）。
    /// </summary>
    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Found", VariableKind.Single, VariableType.Bool)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Scale", "Score", "HomMat" })]
    [ToolOutput("HomMats", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("BestMatch", VariableKind.Object, VariableType.Object, ElementClrType = typeof(MatchResultItem))]
    [ToolOutput("BestHomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("Contours", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconXld))]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Angle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Score", VariableKind.Single, VariableType.Double)]
    public sealed class DescriptorMatchTool : HalconMatchToolBase
    {
        /// <summary>模板裁剪图（serialize_image 序列化，含 ROI 外的边距），由 <see cref="CreateTemplate"/> 生成。</summary>
        public byte[] TemplateData { get; set; }
        /// <summary>模板矩形在参考图中的位置（整数像素，含端点）。</summary>
        public int TemplateRow1 { get; set; }
        public int TemplateColumn1 { get; set; }
        public int TemplateRow2 { get; set; }
        public int TemplateColumn2 { get; set; }
        /// <summary>裁剪图左上角在参考图中的位置。裁剪图包含 ROI 外一圈边距，训练时把定义域限定回 ROI，与 reduce_domain 语义一致。</summary>
        public int TemplateCropRow { get; set; }
        public int TemplateCropColumn { get; set; }

        /// <summary>特征点检测器：harris_binomial / harris / lepetit。</summary>
        public string DetectorType { get; set; } = "harris_binomial";
        public int Depth { get; set; } = 11;
        public int NumberFerns { get; set; } = 30;
        public int PatchSize { get; set; } = 17;
        public double MinScale { get; set; } = 0.4;
        public double MaxScale { get; set; } = 1.2;
        /// <summary>训练随机种子（固定后模型可复现）。</summary>
        public int Seed { get; set; } = 42;
        /// <summary>得分类型：num_points（匹配点数）/ inlier_ratio（内点比例）。</summary>
        public string ScoreType { get; set; } = "num_points";
        /// <summary>是否使用本机模型文件缓存（关闭后每个工具实例都在预热/首次运行时重新训练）。</summary>
        public bool UseModelFileCache { get; set; } = true;

        public DescriptorMatchTool(string moduleName) : base(moduleName)
        {
            MinScore = 0.25;
            NumMatches = 1;
        }

        protected override string MatchLabel
        {
            get { return "描述子匹配"; }
        }

        protected override string MissingModelMessage
        {
            get { return "描述子模板未创建，请在编辑器中打开参考图像、框选模板区域后点击“创建模型”"; }
        }

        protected override object CurrentModelKey
        {
            get
            {
                return TemplateData != null && TemplateData.Length > 0
                    ? new ModelKey(TemplateData, TrainingSignature())
                    : null;
            }
        }

        /// <summary>缓存键：模板数组按引用比较，训练参数按值比较。</summary>
        private sealed record ModelKey(byte[] Template, string Parameters);

        private string TrainingSignature()
        {
            return string.Join("|", DetectorType, Depth, NumberFerns, PatchSize,
                MinScale.ToString("R", CultureInfo.InvariantCulture),
                MaxScale.ToString("R", CultureInfo.InvariantCulture), Seed,
                TemplateRow1, TemplateColumn1, TemplateRow2, TemplateColumn2, TemplateCropRow, TemplateCropColumn);
        }

        /// <summary>从参考图像裁剪模板（矩形 ROI，参考图坐标）。不训练模型；训练在预热或首次运行时进行。</summary>
        public void CreateTemplate(HObject referenceImage, double row1, double column1, double row2, double column2)
        {
            if (referenceImage == null || !referenceImage.IsInitialized())
            {
                throw new InvalidOperationException("请先加载参考图像");
            }
            int r1 = (int)Math.Round(Math.Min(row1, row2));
            int c1 = (int)Math.Round(Math.Min(column1, column2));
            int r2 = (int)Math.Round(Math.Max(row1, row2));
            int c2 = (int)Math.Round(Math.Max(column1, column2));
            HOperatorSet.GetImageSize(referenceImage, out HTuple width, out HTuple height);
            r1 = Math.Max(0, r1);
            c1 = Math.Max(0, c1);
            r2 = Math.Min(height.I - 1, r2);
            c2 = Math.Min(width.I - 1, c2);
            int minSize = Math.Max(PatchSize * 2, 32);
            if (r2 - r1 + 1 < minSize || c2 - c1 + 1 < minSize)
            {
                throw new InvalidOperationException($"模板区域过小（至少 {minSize}×{minSize} 像素）");
            }
            // 保留 ROI 外的边距，让边缘附近的特征检测看到真实的周边灰度
            int margin = Math.Max(PatchSize * 2, 32);
            int cropRow1 = Math.Max(0, r1 - margin);
            int cropColumn1 = Math.Max(0, c1 - margin);
            int cropRow2 = Math.Min(height.I - 1, r2 + margin);
            int cropColumn2 = Math.Min(width.I - 1, c2 + margin);
            HOperatorSet.CropRectangle1(referenceImage, out HObject template, cropRow1, cropColumn1, cropRow2, cropColumn2);
            try
            {
                TemplateData = HalconImageSerialization.Serialize(template);
            }
            finally
            {
                template.Dispose();
            }
            TemplateRow1 = r1;
            TemplateColumn1 = c1;
            TemplateRow2 = r2;
            TemplateColumn2 = c2;
            TemplateCropRow = cropRow1;
            TemplateCropColumn = cropColumn1;
        }

        protected override HTuple LoadModel()
        {
            string cacheFile = UseModelFileCache ? CacheFilePath() : null;
            if (cacheFile != null && File.Exists(cacheFile))
            {
                try
                {
                    HOperatorSet.ReadDescriptorModel(cacheFile, out HTuple cached);
                    return cached;
                }
                catch (HalconException ex)
                {
                    Trace.TraceWarning($"描述子模型缓存损坏，重新训练：{cacheFile}，{ex.Message}");
                    TryDelete(cacheFile);
                }
            }

            HObject crop = HalconImageSerialization.Deserialize(TemplateData);
            HTuple model;
            try
            {
                HOperatorSet.GenRectangle1(out HObject domain,
                    TemplateRow1 - TemplateCropRow, TemplateColumn1 - TemplateCropColumn,
                    TemplateRow2 - TemplateCropRow, TemplateColumn2 - TemplateCropColumn);
                HOperatorSet.ReduceDomain(crop, domain, out HObject template);
                domain.Dispose();
                try
                {
                    HOperatorSet.CreateUncalibDescriptorModel(template, DetectorType, new HTuple(), new HTuple(),
                        new HTuple("depth", "number_ferns", "patch_size", "min_scale", "max_scale"),
                        new HTuple(Depth, NumberFerns, PatchSize).TupleConcat(new HTuple(MinScale, MaxScale)),
                        Seed, out model);
                }
                finally
                {
                    template.Dispose();
                }
            }
            finally
            {
                crop.Dispose();
            }

            if (cacheFile != null)
            {
                TryWriteCache(model, cacheFile);
            }
            return model;
        }

        protected override void ClearModel(HTuple model)
        {
            HOperatorSet.ClearDescriptorModel(model);
        }

        protected override void FindMatches(FlowContext ctx, HObject image, HTuple model, List<MatchResultItem> items)
        {
            HOperatorSet.FindUncalibDescriptorModel(image, model, new HTuple(), new HTuple(), new HTuple(), new HTuple(),
                MinScore, NumMatches, ScoreType, out HTuple homMats, out HTuple scores);

            // 模型坐标以模板（裁剪图全域）的重心为原点；参考图坐标 = 模型坐标 + 模板重心在参考图中的位置
            double centerRow = TemplateRow1 + (TemplateRow2 - TemplateRow1) / 2.0;
            double centerColumn = TemplateColumn1 + (TemplateColumn2 - TemplateColumn1) / 2.0;
            var referenceRows = new HTuple((double)TemplateRow1, TemplateRow1, TemplateRow2, TemplateRow2);
            var referenceColumns = new HTuple((double)TemplateColumn1, TemplateColumn2, TemplateColumn2, TemplateColumn1);
            HTuple modelRows = referenceRows.TupleSub(centerRow);
            HTuple modelColumns = referenceColumns.TupleSub(centerColumn);

            for (int i = 0; i < scores.Length; i++)
            {
                HTuple projective = homMats.TupleSelectRange(i * 9, i * 9 + 8);
                HOperatorSet.ProjectiveTransPixel(projective, modelRows, modelColumns, out HTuple rows, out HTuple columns);
                HOperatorSet.ProjectiveTransPixel(projective, 0.0, 0.0, out HTuple row, out HTuple column);
                HOperatorSet.GenContourPolygonXld(out HObject contour,
                    rows.TupleConcat(rows[0]), columns.TupleConcat(columns[0]));
                HOperatorSet.VectorToHomMat2d(referenceRows, referenceColumns, rows, columns, out HTuple affine);
                var followMat = new HomMat2D(affine);
                MatchResultItem item = CreateItem(ctx, i, row.D, column.D, followMat.RotationAngle,
                    followMat.ScaleFactor, scores[i].D, contour, followMat);
                item.ProjectiveHomMat = projective.DArr;
                items.Add(item);
            }
        }

        private string CacheFilePath()
        {
            HOperatorSet.GetSystem("version", out HTuple version);
            // 序列化字节在不同进程间不完全相同，缓存键必须按像素内容计算
            byte[] content = HalconImageSerialization.ContentHash(TemplateData);
            byte[] signature = Encoding.UTF8.GetBytes(TrainingSignature() + "|" + version.S);
            byte[] hash = SHA256.HashData(content.Concat(signature).ToArray());
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "VisionFlow", "DescriptorModelCache");
            return Path.Combine(directory, Convert.ToHexString(hash) + ".dsm");
        }

        private static void TryWriteCache(HTuple model, string cacheFile)
        {
            string temp = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cacheFile));
                HOperatorSet.WriteDescriptorModel(model, temp);
                File.Move(temp, cacheFile, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is HalconException)
            {
                Trace.TraceWarning($"描述子模型缓存写入失败（不影响运行）：{ex.Message}");
                TryDelete(temp);
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException)
            {
                Trace.TraceWarning($"删除文件失败：{path}，{ex.Message}");
            }
        }
    }

    /// <summary>HALCON 图像与字节数组互转（serialize_image，无损）。</summary>
    public static class HalconImageSerialization
    {
        /// <summary>
        /// 按像素内容计算 SHA-256（各通道的类型、尺寸与像素数据）。serialize_image 的字节在不同进程间不完全相同，
        /// 不能直接作为跨进程的缓存键。
        /// </summary>
        public static byte[] ContentHash(byte[] serializedImage)
        {
            HObject image = Deserialize(serializedImage);
            try
            {
                using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                HOperatorSet.CountChannels(image, out HTuple channels);
                for (int channel = 1; channel <= channels.I; channel++)
                {
                    HOperatorSet.AccessChannel(image, out HObject single, channel);
                    try
                    {
                        HOperatorSet.GetImagePointer1(single, out HTuple pointer, out HTuple type, out HTuple width, out HTuple height);
                        int bytesPerPixel = BytesPerPixel(type.S);
                        var pixels = new byte[(long)width.I * height.I * bytesPerPixel];
                        Marshal.Copy(new IntPtr(pointer.L), pixels, 0, pixels.Length);
                        sha.AppendData(Encoding.UTF8.GetBytes($"{type.S}|{width.I}|{height.I}|"));
                        sha.AppendData(pixels);
                    }
                    finally
                    {
                        single.Dispose();
                    }
                }
                return sha.GetHashAndReset();
            }
            finally
            {
                image.Dispose();
            }
        }

        private static int BytesPerPixel(string type)
        {
            switch (type)
            {
                case "byte":
                case "int1":
                    return 1;
                case "uint2":
                case "int2":
                    return 2;
                case "int4":
                case "real":
                    return 4;
                case "int8":
                    return 8;
                default:
                    throw new NotSupportedException("不支持的模板图像类型：" + type);
            }
        }

        public static byte[] Serialize(HObject image)
        {
            HOperatorSet.SerializeImage(image, out HTuple item);
            try
            {
                HOperatorSet.GetSerializedItemPtr(item, out HTuple pointer, out HTuple size);
                var bytes = new byte[size.L];
                Marshal.Copy(new IntPtr(pointer.L), bytes, 0, bytes.Length);
                return bytes;
            }
            finally
            {
                HOperatorSet.ClearSerializedItem(item);
            }
        }

        public static HObject Deserialize(byte[] bytes)
        {
            if (bytes == null || bytes.Length == 0)
            {
                throw new InvalidOperationException("图像数据为空");
            }
            IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
            HTuple item = null;
            try
            {
                Marshal.Copy(bytes, 0, pointer, bytes.Length);
                HOperatorSet.CreateSerializedItemPtr(new HTuple(pointer.ToInt64()), bytes.Length, "true", out item);
                HOperatorSet.DeserializeImage(out HObject image, item);
                return image;
            }
            finally
            {
                if (item != null)
                {
                    HOperatorSet.ClearSerializedItem(item);
                }
                Marshal.FreeHGlobal(pointer);
            }
        }
    }
}
