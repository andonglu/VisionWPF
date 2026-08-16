using System;
using System.Collections.Generic;
using System.IO;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>定位解决方案根目录下的文件（以 VisionFlow.slnx 为根标志），与程序运行目录无关。</summary>
    public static class RepoPaths
    {
        public static string Find(string relativePath)
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "VisionFlow.slnx")))
                {
                    string fullPath = Path.Combine(dir.FullName, relativePath);
                    if (File.Exists(fullPath))
                    {
                        return fullPath;
                    }
                    throw new FileNotFoundException($"仓库中不存在文件：{relativePath}", fullPath);
                }
                dir = dir.Parent;
            }
            throw new DirectoryNotFoundException("向上未找到解决方案根目录（缺少 VisionFlow.slnx）");
        }
    }

    /// <summary>
    /// 单个匹配结果：位姿、分数、匹配后轮廓，
    /// 以及<b>相对于基准模板的刚体变换矩阵</b>（HomMat，6 元素），供下游工具做跟随。
    /// </summary>
    public sealed class MatchResultItem
    {
        public int Index { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Angle { get; set; }
        public double Scale { get; set; } = 1;
        public double ScaleRow { get; set; } = 1;
        public double ScaleColumn { get; set; } = 1;
        public double Score { get; set; }
        /// <summary>匹配后的轮廓（已仿射变换到图像位置）。</summary>
        public HObject Contour { get; set; }
        public int ContourPointCount { get; set; }
        /// <summary>基准模板位姿 → 本匹配位姿 的变换矩阵（专门类型，非一般 HTuple）。</summary>
        public HomMat2D HomMat { get; set; }

        public override string ToString()
        {
            return $"#{Index} Row={Row:F2}, Col={Column:F2}, Angle={Angle:F4}, Scale={Scale:F3}, Score={Score:F3}, 轮廓点数={ContourPointCount}";
        }
    }

    public interface IHalconTemplateMatchTool
    {
        string ModuleName { get; set; }
        string ImagePath { get; set; }
        byte[] ShapeModelData { get; set; }
        double FindStartAngle { get; set; }
        double FindExtentAngle { get; set; }
        double MinScore { get; set; }
        int NumMatches { get; set; }
        double MaxOverlap { get; set; }
        string SubPixel { get; set; }
        int NumLevelsFind { get; set; }
        double Greediness { get; set; }
        double BaseRow { get; set; }
        double BaseColumn { get; set; }
        double BaseAngle { get; set; }
    }

    public abstract class HalconTemplateMatchToolBase : ToolBase, IHalconTemplateMatchTool
    {
        private double _findStartAngle = -0.39;
        private double _findExtentAngle = 0.79;

        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "Input.Image";
        public byte[] ShapeModelData { get; set; }
        public double FindStartAngle { get { return _findStartAngle; } set { _findStartAngle = value; } }
        public double FindExtentAngle { get { return _findExtentAngle; } set { _findExtentAngle = value; } }
        public double MinScore { get; set; } = 0.5;
        public int NumMatches { get; set; } = 10;
        public double MaxOverlap { get; set; } = 0.5;
        public string SubPixel { get; set; } = "least_squares";
        public int NumLevelsFind { get; set; }
        public double Greediness { get; set; } = 0.9;
        public double BaseRow { get; set; } = 99;
        public double BaseColumn { get; set; } = 79;
        public double BaseAngle { get; set; }

        protected HalconTemplateMatchToolBase(string moduleName) : base(moduleName)
        {
        }

        protected bool TryLoadImage(FlowContext ctx, out HObject image, out string error)
        {
            image = null;
            error = null;
            if (string.IsNullOrWhiteSpace(ImagePath))
            {
                error = "未配置输入图像";
                return false;
            }
            if (File.Exists(ImagePath))
            {
                HOperatorSet.ReadImage(out image, ImagePath);
                return true;
            }
            try
            {
                image = Input<HalconImage>(ctx, ImagePath).Object;
                return true;
            }
            catch (Exception ex)
            {
                error = $"输入图像引用无效：{ImagePath}，{ex.Message}";
                return false;
            }
        }

        protected void SetMatchOutputs(FlowContext ctx, HObject image, List<MatchResultItem> items,
            List<HomMat2D> homMats, List<HObject> contours, HObject resultContour)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "MatchCount", VariableType.Int, items.Count));
            SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, ToScores(items)));
            SetOutput(ctx, Variable.Array(ModuleName, "Items", VariableType.Object, items));
            SetOutput(ctx, Variable.Array(ModuleName, "HomMats", VariableType.Object, homMats));
            SetOutput(ctx, Variable.Object(ModuleName, "BestMatch", items[0], 1));
            SetOutput(ctx, Variable.Object(ModuleName, "BestHomMat", homMats[0], 1));
            SetOutput(ctx, Variable.Object(ModuleName, "Contours", contours, contours.Count));
            SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", resultContour, items.Count));
            SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(image), 1));
        }

        protected static double[] ToScores(List<MatchResultItem> items)
        {
            var scores = new double[items.Count];
            for (int i = 0; i < items.Count; i++)
            {
                scores[i] = items[i].Score;
            }
            return scores;
        }

        protected static int CountContourPoints(HObject contourObject)
        {
            HOperatorSet.CountObj(contourObject, out HTuple contourCount);
            int pointCount = 0;
            for (int k = 1; k <= contourCount.I; k++)
            {
                HOperatorSet.SelectObj(contourObject, out HObject single, k);
                HOperatorSet.GetContourXld(single, out HTuple rows, out _);
                pointCount += rows.Length;
                single.Dispose();
            }
            return pointCount;
        }
    }

    /// <summary>
    /// Halcon 模板匹配工具：读取图像与 temp.shm 形状模板，执行 find_shape_model。
    /// 基准模板位姿（BaseRow/BaseColumn/BaseAngle）是设置（示教）时确定的固定值，
    /// 整个流程无论运行多少次都不变；每个匹配结果都携带
    /// "固定基准位姿 → 本匹配位姿" 的变换矩阵 HomMat，供下游工具做跟随。
    /// 输出：
    ///   MatchCount(Int)     —— 匹配个数
    ///   Scores(Double[])    —— 各匹配分数
    ///   Items(MatchResultItem[]) —— 匹配结果集合（含轮廓与相对固定基准的变换矩阵），供 ForEach 遍历
    ///   Contours(对象)      —— 全部匹配轮廓列表
    ///   Image(对象)         —— 图像本身，供下游工具引用
    /// </summary>
    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Score", "HomMat" })]
    [ToolOutput("HomMats", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("BestMatch", VariableKind.Object, VariableType.Object, ElementClrType = typeof(MatchResultItem))]
    [ToolOutput("BestHomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("Contours", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<HObject>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class HalconModelMatchTool : ToolBase, IHalconTemplateMatchTool
    {
        private double _findStartAngle = -0.39;
        private double _findExtentAngle = 0.79;

        /// <summary>图像输入引用，如“Input.Image”或“图像加载1.Image”。兼容旧测试：也允许填写图像文件路径。</summary>
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; }
        /// <summary>兼容/导入用模型路径；正式运行优先使用 ShapeModelData。</summary>
        public string ModelPath { get; set; }
        /// <summary>内嵌 HALCON shape model 序列化数据，流程迁移时不依赖外部 .shm 文件。</summary>
        public byte[] ShapeModelData { get; set; }

        public double FindStartAngle
        {
            get { return _findStartAngle; }
            set { _findStartAngle = value; }
        }

        public double FindExtentAngle
        {
            get { return _findExtentAngle; }
            set { _findExtentAngle = value; }
        }

        public double AngleStart
        {
            get { return FindStartAngle; }
            set { FindStartAngle = value; }
        }

        public double AngleExtent
        {
            get { return FindExtentAngle; }
            set { FindExtentAngle = value; }
        }

        public double MinScore { get; set; } = 0.5;
        public int NumMatches { get; set; } = 10;
        public double MaxOverlap { get; set; } = 0.5;
        public string SubPixel { get; set; } = "least_squares";
        public int NumLevelsFind { get; set; }
        public double Greediness { get; set; } = 0.9;

        /// <summary>基准模板位姿：示教时设定的固定行坐标（不随运行改变）。</summary>
        public double BaseRow { get; set; } = 99;
        /// <summary>基准模板位姿：示教时设定的固定列坐标。</summary>
        public double BaseColumn { get; set; } = 79;
        /// <summary>基准模板位姿：示教时设定的固定角度（弧度）。</summary>
        public double BaseAngle { get; set; } = 0;

        public HalconModelMatchTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (!TryLoadModel(out HTuple modelId, out string modelError))
            {
                return NodeResult.Fail(modelError);
            }

            HObject image = null;
            bool ownsImage = false;
            HObject modelContours = null;
            try
            {
                if (!TryLoadImage(ctx, out image, out ownsImage, out string imageError))
                {
                    return NodeResult.Fail(imageError);
                }

                HOperatorSet.GetShapeModelContours(out modelContours, modelId, 1);
                HOperatorSet.FindShapeModel(image, modelId, FindStartAngle, FindExtentAngle, MinScore,
                    NumMatches, MaxOverlap, SubPixel, NumLevelsFind, Greediness,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scores);

                int count = rows.Length;
                if (count == 0)
                {
                    return NodeResult.Fail("未匹配到任何目标");
                }

                // 基准模板位姿：示教时设定的固定值，不随运行改变
                ctx.AddLog(FlowLogLevel.Info, $"[匹配] 固定基准位姿：Row={BaseRow:F2}, Col={BaseColumn:F2}, Angle={BaseAngle:F4}");

                var items = new List<MatchResultItem>();
                var contours = new List<HObject>();
                var homMats = new List<HomMat2D>();
                HOperatorSet.GenEmptyObj(out HObject resultContour);

                for (int i = 0; i < count; i++)
                {
                    // 匹配轮廓：模型轮廓以模型原点为基准，用 (0,0,0)→(Row,Column,Angle) 变换到图像位置
                    HOperatorSet.VectorAngleToRigid(0, 0, 0, rows[i], columns[i], angles[i], out HTuple contourMat);
                    HOperatorSet.AffineTransContourXld(modelContours, out HObject matchContour, contourMat);

                    HOperatorSet.CountObj(matchContour, out HTuple contourCount);
                    int pointCount = 0;
                    for (int k = 1; k <= contourCount.I; k++)
                    {
                        HOperatorSet.SelectObj(matchContour, out HObject single, k);
                        HOperatorSet.GetContourXld(single, out HTuple contRows, out _);
                        pointCount += contRows.Length;
                        single.Dispose();
                    }

                    // 相对固定基准位姿的变换矩阵：固定基准位姿 → 本匹配位姿（专门类型 HomMat2D）
                    HomMat2D followMat = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle,
                        rows[i].D, columns[i].D, angles[i].D);
                    homMats.Add(followMat);

                    var item = new MatchResultItem
                    {
                        Index = i,
                        Row = rows[i].D,
                        Column = columns[i].D,
                        Angle = angles[i].D,
                        Score = scores[i].D,
                        Contour = matchContour,
                        ContourPointCount = pointCount,
                        HomMat = followMat
                    };
                    items.Add(item);
                    contours.Add(matchContour);
                    HOperatorSet.ConcatObj(resultContour, matchContour, out resultContour);
                    ctx.AddLog(FlowLogLevel.Info, $"[匹配] {item}");
                }

                SetOutput(ctx, Variable.Single(ModuleName, "MatchCount", VariableType.Int, count));
                SetOutput(ctx, Variable.Array(ModuleName, "Scores", VariableType.Double, scores.DArr));
                SetOutput(ctx, Variable.Array(ModuleName, "Items", VariableType.Object, items));
                SetOutput(ctx, Variable.Array(ModuleName, "HomMats", VariableType.Object, homMats));
                SetOutput(ctx, Variable.Object(ModuleName, "BestMatch", items[0], 1));
                SetOutput(ctx, Variable.Object(ModuleName, "BestHomMat", homMats[0], 1));
                SetOutput(ctx, Variable.Object(ModuleName, "Contours", contours, contours.Count));
                SetOutput(ctx, Variable.Object(ModuleName, "ResultContour", resultContour, count));
                SetOutput(ctx, Variable.Object(ModuleName, "Image", new HalconImage(image), 1));
                return NodeResult.Ok;
            }
            catch (Exception ex)
            {
                return NodeResult.Fail($"{ModuleName} 模板匹配失败：{ex.Message}");
            }
            finally
            {
                modelContours?.Dispose();
                HOperatorSet.ClearShapeModel(modelId);
                // image 会作为输出变量传给下游工具，不能在这里释放。
            }
        }

        public void ImportModelFile(string modelPath)
        {
            ShapeModelData = ShapeModelSerialization.ReadShapeModelFileAsBytes(modelPath);
            ModelPath = modelPath;
        }

        public void SetSerializedModel(byte[] modelData)
        {
            ShapeModelData = modelData;
        }

        private bool TryLoadModel(out HTuple modelId, out string error)
        {
            modelId = null;
            error = null;
            if (ShapeModelData != null && ShapeModelData.Length > 0)
            {
                modelId = ShapeModelSerialization.Deserialize(ShapeModelData);
                return true;
            }
            if (!string.IsNullOrEmpty(ModelPath) && File.Exists(ModelPath))
            {
                HOperatorSet.ReadShapeModel(ModelPath, out modelId);
                return true;
            }
            error = "模板未创建或模型文件不存在";
            return false;
        }

        private bool TryLoadImage(FlowContext ctx, out HObject image, out bool ownsImage, out string error)
        {
            image = null;
            ownsImage = false;
            error = null;

            if (string.IsNullOrWhiteSpace(ImagePath))
            {
                error = "未配置输入图像";
                return false;
            }

            if (File.Exists(ImagePath))
            {
                HOperatorSet.ReadImage(out image, ImagePath);
                ownsImage = true;
                return true;
            }

            try
            {
                image = Input<HalconImage>(ctx, ImagePath).Object;
                return true;
            }
            catch (Exception ex)
            {
                error = $"输入图像引用无效：{ImagePath}，{ex.Message}";
                return false;
            }
        }
    }

    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Score", "HomMat" })]
    [ToolOutput("HomMats", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("BestMatch", VariableKind.Object, VariableType.Object, ElementClrType = typeof(MatchResultItem))]
    [ToolOutput("BestHomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("Contours", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<HObject>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class HalconGrayMatchTool : HalconTemplateMatchToolBase
    {
        public double TeachAngleStart { get; set; } = -0.39;
        public double TeachAngleExtent { get; set; } = 0.79;

        public HalconGrayMatchTool(string moduleName) : base(moduleName)
        {
            SubPixel = "true";
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (ShapeModelData == null || ShapeModelData.Length == 0)
            {
                return NodeResult.Fail("灰度模板未创建");
            }

            HObject image = null;
            HObject modelRegion = null;
            HObject modelContour = null;
            HObject resultContour = null;
            HTuple modelId = null;
            try
            {
                if (!TryLoadImage(ctx, out image, out string imageError))
                {
                    return NodeResult.Fail(imageError);
                }

                modelId = ShapeModelSerialization.DeserializeNcc(ShapeModelData);
                HOperatorSet.GetNccModelRegion(out modelRegion, modelId);
                HOperatorSet.GenContourRegionXld(modelRegion, out modelContour, "border");
                HOperatorSet.FindNccModel(image, modelId, FindStartAngle, FindExtentAngle, MinScore,
                    NumMatches, MaxOverlap, string.IsNullOrWhiteSpace(SubPixel) ? "true" : SubPixel, NumLevelsFind,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scores);

                if (rows.Length == 0)
                {
                    return NodeResult.Fail("灰度匹配未匹配到任何目标");
                }

                var items = new List<MatchResultItem>();
                var contours = new List<HObject>();
                var homMats = new List<HomMat2D>();
                HOperatorSet.GenEmptyObj(out resultContour);
                for (int i = 0; i < rows.Length; i++)
                {
                    HOperatorSet.VectorAngleToRigid(0, 0, 0, rows[i], columns[i], angles[i], out HTuple contourMat);
                    HOperatorSet.AffineTransContourXld(modelContour, out HObject matchContour, contourMat);
                    HOperatorSet.ConcatObj(resultContour, matchContour, out HObject combined);
                    resultContour.Dispose();
                    resultContour = combined;

                    HomMat2D followMat = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle, rows[i].D, columns[i].D, angles[i].D);
                    homMats.Add(followMat);
                    var item = new MatchResultItem
                    {
                        Index = i,
                        Row = rows[i].D,
                        Column = columns[i].D,
                        Angle = angles[i].D,
                        Score = scores[i].D,
                        Contour = matchContour,
                        ContourPointCount = CountContourPoints(matchContour),
                        HomMat = followMat
                    };
                    items.Add(item);
                    contours.Add(matchContour);
                    ctx.AddLog(FlowLogLevel.Info, $"[灰度匹配] {item}");
                }

                SetMatchOutputs(ctx, image, items, homMats, contours, resultContour);
                return NodeResult.Ok;
            }
            catch (Exception ex)
            {
                return NodeResult.Fail($"{ModuleName} 灰度匹配失败：{ex.Message}");
            }
            finally
            {
                modelRegion?.Dispose();
                modelContour?.Dispose();
                if (modelId != null)
                {
                    HOperatorSet.ClearNccModel(modelId);
                }
            }
        }
    }

    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Angle", "Scale", "Score", "HomMat" })]
    [ToolOutput("HomMats", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("BestMatch", VariableKind.Object, VariableType.Object, ElementClrType = typeof(MatchResultItem))]
    [ToolOutput("BestHomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("Contours", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<HObject>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class HalconScaledShapeMatchTool : HalconTemplateMatchToolBase
    {
        public double ScaleMin { get; set; } = 0.9;
        public double ScaleMax { get; set; } = 1.1;

        public HalconScaledShapeMatchTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (ShapeModelData == null || ShapeModelData.Length == 0)
            {
                return NodeResult.Fail("缩放形状模板未创建");
            }

            HObject image = null;
            HObject modelContours = null;
            HObject resultContour = null;
            HTuple modelId = null;
            try
            {
                if (!TryLoadImage(ctx, out image, out string imageError))
                {
                    return NodeResult.Fail(imageError);
                }

                modelId = ShapeModelSerialization.Deserialize(ShapeModelData);
                HOperatorSet.GetShapeModelContours(out modelContours, modelId, 1);
                HOperatorSet.FindScaledShapeModel(image, modelId, FindStartAngle, FindExtentAngle,
                    ScaleMin, ScaleMax, MinScore, NumMatches, MaxOverlap, SubPixel, NumLevelsFind, Greediness,
                    out HTuple rows, out HTuple columns, out HTuple angles, out HTuple scales, out HTuple scores);
                if (rows.Length == 0)
                {
                    return NodeResult.Fail("缩放形状匹配未匹配到任何目标");
                }

                var items = new List<MatchResultItem>();
                var contours = new List<HObject>();
                var homMats = new List<HomMat2D>();
                HOperatorSet.GenEmptyObj(out resultContour);
                for (int i = 0; i < rows.Length; i++)
                {
                    HomMat2D followMat = HomMat2D.FromScaledPose(rows[i].D, columns[i].D, angles[i].D, scales[i].D);
                    HOperatorSet.AffineTransContourXld(modelContours, out HObject matchContour, followMat.Data);
                    HOperatorSet.ConcatObj(resultContour, matchContour, out HObject combined);
                    resultContour.Dispose();
                    resultContour = combined;
                    homMats.Add(followMat);
                    var item = new MatchResultItem
                    {
                        Index = i,
                        Row = rows[i].D,
                        Column = columns[i].D,
                        Angle = angles[i].D,
                        Scale = scales[i].D,
                        ScaleRow = scales[i].D,
                        ScaleColumn = scales[i].D,
                        Score = scores[i].D,
                        Contour = matchContour,
                        ContourPointCount = CountContourPoints(matchContour),
                        HomMat = followMat
                    };
                    items.Add(item);
                    contours.Add(matchContour);
                    ctx.AddLog(FlowLogLevel.Info, $"[缩放形状匹配] {item}");
                }

                SetMatchOutputs(ctx, image, items, homMats, contours, resultContour);
                return NodeResult.Ok;
            }
            catch (Exception ex)
            {
                return NodeResult.Fail($"{ModuleName} 缩放形状匹配失败：{ex.Message}");
            }
            finally
            {
                modelContours?.Dispose();
                if (modelId != null)
                {
                    HOperatorSet.ClearShapeModel(modelId);
                }
            }
        }
    }

    [ToolOutput("MatchCount", VariableKind.Single, VariableType.Int)]
    [ToolOutput("Scores", VariableKind.Array, VariableType.Double, ElementClrType = typeof(double))]
    [ToolOutput("Items", VariableKind.Array, VariableType.Object, ElementClrType = typeof(MatchResultItem),
        Members = new[] { "Row", "Column", "Score", "HomMat" })]
    [ToolOutput("HomMats", VariableKind.Array, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("BestMatch", VariableKind.Object, VariableType.Object, ElementClrType = typeof(MatchResultItem))]
    [ToolOutput("BestHomMat", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HomMat2D))]
    [ToolOutput("Contours", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<HObject>))]
    [ToolOutput("ResultContour", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HObject))]
    [ToolOutput("Image", VariableKind.Object, VariableType.Object, ElementClrType = typeof(HalconImage))]
    public sealed class HalconLocalDeformableMatchTool : HalconTemplateMatchToolBase
    {
        public double ScaleRowMin { get; set; } = 0.9;
        public double ScaleRowMax { get; set; } = 1.1;
        public double ScaleColumnMin { get; set; } = 0.9;
        public double ScaleColumnMax { get; set; } = 1.1;
        public string DeformationSmoothness { get; set; } = "11";

        public HalconLocalDeformableMatchTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            if (ShapeModelData == null || ShapeModelData.Length == 0)
            {
                return NodeResult.Fail("局部变形模板未创建");
            }

            HObject image = null;
            HObject imageRectified = null;
            HObject vectorField = null;
            HObject deformedContours = null;
            HObject resultContour = null;
            HTuple modelId = null;
            try
            {
                if (!TryLoadImage(ctx, out image, out string imageError))
                {
                    return NodeResult.Fail(imageError);
                }

                modelId = ShapeModelSerialization.DeserializeDeformable(ShapeModelData);
                HTuple genNames = new HTuple();
                HTuple genValues = new HTuple();
                if (!string.IsNullOrWhiteSpace(DeformationSmoothness))
                {
                    genNames = genNames.TupleConcat("deformation_smoothness");
                    genValues = genValues.TupleConcat(DeformationSmoothness);
                }
                HOperatorSet.FindLocalDeformableModel(image, out imageRectified, out vectorField, out deformedContours,
                    modelId, FindStartAngle, FindExtentAngle, ScaleRowMin, ScaleRowMax, ScaleColumnMin, ScaleColumnMax,
                    MinScore, NumMatches, MaxOverlap, NumLevelsFind, Greediness, "deformed_contours", genNames, genValues,
                    out HTuple scores, out HTuple rows, out HTuple columns);
                if (rows.Length == 0)
                {
                    return NodeResult.Fail("局部变形匹配未匹配到任何目标");
                }

                var items = new List<MatchResultItem>();
                var contours = new List<HObject>();
                var homMats = new List<HomMat2D>();
                resultContour = deformedContours.Clone();
                HOperatorSet.CountObj(deformedContours, out HTuple contourCount);
                for (int i = 0; i < rows.Length; i++)
                {
                    HObject contour = null;
                    if (i < contourCount.I)
                    {
                        HOperatorSet.SelectObj(deformedContours, out contour, i + 1);
                    }
                    else
                    {
                        HOperatorSet.GenEmptyObj(out contour);
                    }
                    HomMat2D followMat = HomMat2D.FromPoses(BaseRow, BaseColumn, BaseAngle, rows[i].D, columns[i].D, 0);
                    homMats.Add(followMat);
                    var item = new MatchResultItem
                    {
                        Index = i,
                        Row = rows[i].D,
                        Column = columns[i].D,
                        Angle = 0,
                        Score = scores[i].D,
                        Contour = contour,
                        ContourPointCount = CountContourPoints(contour),
                        HomMat = followMat
                    };
                    items.Add(item);
                    contours.Add(contour);
                    ctx.AddLog(FlowLogLevel.Info, $"[局部变形匹配] {item}");
                }

                SetMatchOutputs(ctx, image, items, homMats, contours, resultContour);
                return NodeResult.Ok;
            }
            catch (Exception ex)
            {
                return NodeResult.Fail($"{ModuleName} 局部变形匹配失败：{ex.Message}");
            }
            finally
            {
                imageRectified?.Dispose();
                vectorField?.Dispose();
                deformedContours?.Dispose();
                if (modelId != null)
                {
                    HOperatorSet.ClearDeformableModel(modelId);
                }
            }
        }
    }

    /// <summary>单次椭圆测量结果。</summary>
    public sealed class EllipseMeasureResult
    {
        public int Index { get; set; }
        public double Row { get; set; }
        public double Column { get; set; }
        public double Phi { get; set; }
        public double Length1 { get; set; }
        public double Length2 { get; set; }
        /// <summary>true = 按输入矩阵做了跟随变换；false = 固定位置测量。</summary>
        public bool Followed { get; set; }

        public override string ToString()
        {
            return $"#{(Followed ? Index.ToString() : "固定")} 中心=({Row:F2}, {Column:F2}), Phi={Phi:F4}, Len1={Length1:F2}, Len2={Length2:F2}";
        }
    }

    /// <summary>
    /// 椭圆测量工具：只持有自己的初始测量位置（图像坐标），与匹配工具完全解耦。
    /// MatrixPath 配置了变换矩阵引用时：按矩阵对初始位置做仿射变换后测量（跟随模式）；
    /// 未配置或引用不可解析时：直接在初始位置测量（固定模式）。
    /// 测量结果累积到 "模块名.Results" 变量（List&lt;EllipseMeasureResult&gt;），循环结束后即为全部结果。
    /// </summary>
    [ToolOutput("Row", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Column", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Phi", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length1", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Length2", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Results", VariableKind.Object, VariableType.Object, ElementClrType = typeof(List<EllipseMeasureResult>))]
    public sealed class EllipseFollowMeasureTool : ToolBase
    {
        // 初始测量位置（图像坐标系下的椭圆定义）
        public double EllipseRow { get; set; } = 28.1559;
        public double EllipseColumn { get; set; } = 82.9631;
        public double EllipseAngle { get; set; } = -Math.PI / 2; // rad(-90)
        public double EllipseLength1 { get; set; } = 10;
        public double EllipseLength2 { get; set; } = 4;

        // metrology 测量参数（MeasureLength1/2、Sigma 与 temp.hdev 一致；
        // hdev 的 Threshold=20 在该图像上过严会导致测量失败，经全位置扫描验证 Threshold=1 时 10/10 全部测到且数值稳定）
        public double MeasureLength1 { get; set; } = 7;
        public double MeasureLength2 { get; set; } = 2;
        public double MeasureSigma { get; set; } = 1;
        public double MeasureThreshold { get; set; } = 1;

        /// <summary>图像变量引用（专用类型 HalconImage，区域等其它 HObject 不会混入候选）。</summary>
        [InputRef("图像", typeof(HalconImage))]
        public string ImagePath { get; set; } = "模板匹配.Image";

        /// <summary>
        /// 变换矩阵变量引用（可选），如 "Loop.Current.HomMat" 或 "匹配2.Items[3].HomMat"。
        /// 期望类型是专门的 HomMat2D（不是一般 HTuple）；为空或解析失败时按固定位置测量。
        /// </summary>
        [InputRef("变换矩阵", typeof(HomMat2D), Optional = true)]
        public string MatrixPath { get; set; }

        /// <summary>结果序号来源（可选，仅用于标记结果属于哪次迭代）。</summary>
        [InputRef("结果序号", typeof(int), Optional = true)]
        public string IndexPath { get; set; }

        public EllipseFollowMeasureTool(string moduleName) : base(moduleName)
        {
        }

        public override NodeResult Run(FlowContext ctx)
        {
            HObject image = Input<HalconImage>(ctx, ImagePath).Object;

            double row = EllipseRow;
            double column = EllipseColumn;
            double phi = EllipseAngle;
            bool followed = false;
            int index = -1;

            if (!string.IsNullOrEmpty(IndexPath))
            {
                index = VariableReference.Parse(IndexPath).Resolve<int>(ctx);
            }

            // 可选的变换矩阵：有则跟随，无则固定
            HomMat2D matrix = null;
            if (!string.IsNullOrEmpty(MatrixPath))
            {
                try
                {
                    matrix = VariableReference.Parse(MatrixPath).Resolve<HomMat2D>(ctx);
                }
                catch (Exception ex)
                {
                    ctx.AddLog(FlowLogLevel.Warning, $"[测量] 矩阵引用 '{MatrixPath}' 解析失败（{ex.Message}），按固定位置测量");
                }
            }

            if (matrix != null)
            {
                // 用矩阵把初始测量位姿（中心 + 方向角）变换到当前图像
                matrix.TransformPose(EllipseRow, EllipseColumn, EllipseAngle, out row, out column, out phi);
                followed = true;
            }

            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CreateMetrologyModel(out HTuple metrology);
            try
            {
                HOperatorSet.SetMetrologyModelImageSize(metrology, width, height);
                HOperatorSet.AddMetrologyObjectEllipseMeasure(metrology,
                    row, column, phi, EllipseLength1, EllipseLength2,
                    MeasureLength1, MeasureLength2, MeasureSigma, MeasureThreshold,
                    new HTuple(), new HTuple(), out HTuple _);
                HOperatorSet.ApplyMetrologyModel(image, metrology);
                HOperatorSet.GetMetrologyObjectResult(metrology, 0, "all", "result_type", "all_param", out HTuple param);

                if (param.Length < 5)
                {
                    return NodeResult.Fail($"椭圆测量失败（位置 {row:F1}, {column:F1}）：未找到足够的边缘点");
                }

                var measure = new EllipseMeasureResult
                {
                    Index = index,
                    Row = param[0].D,
                    Column = param[1].D,
                    Phi = param[2].D,
                    Length1 = param[3].D,
                    Length2 = param[4].D,
                    Followed = followed
                };

                // 累积写入结果列表（跨循环迭代共享）
                List<EllipseMeasureResult> results;
                if (ctx.TryGetVariable(ModuleName, "Results", out Variable existing))
                {
                    results = existing.GetValue<List<EllipseMeasureResult>>();
                }
                else
                {
                    results = new List<EllipseMeasureResult>();
                    SetOutput(ctx, Variable.Object(ModuleName, "Results", results, 0));
                }
                results.Add(measure);

                // 单次测量结果同时以单值输出，方便下游直接引用
                SetOutput(ctx, Variable.Single(ModuleName, "Row", VariableType.Double, measure.Row));
                SetOutput(ctx, Variable.Single(ModuleName, "Column", VariableType.Double, measure.Column));
                SetOutput(ctx, Variable.Single(ModuleName, "Phi", VariableType.Double, measure.Phi));
                SetOutput(ctx, Variable.Single(ModuleName, "Length1", VariableType.Double, measure.Length1));
                SetOutput(ctx, Variable.Single(ModuleName, "Length2", VariableType.Double, measure.Length2));

                ctx.AddLog(FlowLogLevel.Info, $"[测量]{(followed ? "跟随" : "固定")} {measure}");
                return NodeResult.Ok;
            }
            finally
            {
                HOperatorSet.ClearMetrologyModel(metrology);
            }
        }
    }
}
