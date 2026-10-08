using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using VisionFlow.Core;
using VisionFlow.Tools.Calibration;
using VisionFlow.Variables;

namespace VisionFlow.Tools
{
    /// <summary>纠偏方式（按数字保存，追加在末尾）。</summary>
    public enum AlignmentMode
    {
        /// <summary>只补偿平移：DeltaAngle = 0。</summary>
        TranslationOnly,
        /// <summary>先绕旋转中心转回角度差，再补偿剩余平移（吸嘴、旋转台）。</summary>
        RotateAroundCenter
    }

    /// <summary>相机安装方式（按数字保存，追加在末尾）；决定平移补偿的符号。</summary>
    public enum CameraMounting
    {
        /// <summary>相机固定，看轴带动的工件。</summary>
        Fixed,
        /// <summary>相机装在轴上，看固定的目标。</summary>
        OnAxis
    }

    /// <summary>
    /// 纠偏计算（CB-07）：由当前定位位姿与示教基准位姿计算平台补偿量。符号约定逐字按计划第 6 节 CB-07“符号约定”：
    /// 全部在物理坐标系中计算（X = WorldRow、Y = WorldColumn，即标定矩阵的 Qx / Qy）；
    /// P / θ 与 B / θb 为当前位姿与基准位姿经 Affine2D 换算的物理坐标与 WorldAngle（与坐标转换 CB-05 同一换算路径），
    /// dθ = θ − θb 折算到 (−π, π]，正值为物理系中“从 +X 转向 +Y”；R(α)·(x, y) = (x·cos α − y·sin α, x·sin α + y·cos α)。
    /// 输出为平台补偿量（把当前件移回基准的运动，先绕 C 转 DeltaAngle 再平移）：
    /// Fixed + RotateAroundCenter：DeltaAngle = −dθ，(DeltaX, DeltaY) = B − R(C, −dθ)·P；
    /// Fixed + TranslationOnly：DeltaAngle = 0，(DeltaX, DeltaY) = B − P；
    /// OnAxis + TranslationOnly：DeltaAngle = 0，(DeltaX, DeltaY) = P − B；OnAxis + RotateAroundCenter 不支持（校验与运行都拒绝）。
    /// AngleDifference 总是输出 dθ。当前位姿含 NaN（如未找到目标）时全部输出 NaN、Valid = false，节点不失败。
    /// </summary>
    [ToolOutput("DeltaX", VariableKind.Single, VariableType.Double)]
    [ToolOutput("DeltaY", VariableKind.Single, VariableType.Double)]
    [ToolOutput("DeltaAngle", VariableKind.Single, VariableType.Double)]
    [ToolOutput("AngleDifference", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldX", VariableKind.Single, VariableType.Double)]
    [ToolOutput("WorldY", VariableKind.Single, VariableType.Double)]
    [ToolOutput("Valid", VariableKind.Single, VariableType.Bool)]
    public sealed class AlignmentOffsetTool : ToolBase, IToolConfigurationCheck, IToolParameterVisibility, IToolResourceLifecycle
    {
        /// <summary>OnAxis + RotateAroundCenter 的拒绝说明（计划第 6 节：不自行发明公式）。</summary>
        public const string OnAxisRotationMessage =
            "相机随轴旋转后平移补偿取决于轴叠放方式，第二批不支持，请改用 TranslationOnly 或由上层按机构计算";

        [InputRef("Row", typeof(double))]
        public string RowPath { get; set; }

        [InputRef("Column", typeof(double))]
        public string ColumnPath { get; set; }

        /// <summary>当前位姿角度（弧度，图像角度约定，如匹配 Angle）；RotateAroundCenter 必填，TranslationOnly 可选。</summary>
        [InputRef("角度", typeof(double), Optional = true)]
        public string AnglePath { get; set; }

        /// <summary>标定来源，默认 File。</summary>
        public CalibrationSource CalibrationSource { get; set; } = CalibrationSource.File;
        /// <summary>标定文件（需带 Affine2D 段；RotateAroundCenter 还需 RotationCenter 段）。</summary>
        public string CalibrationFile { get; set; }
        /// <summary>内嵌的 .vfcal.json 内容（CalibrationSource = Embedded 时使用）。</summary>
        public string CalibrationData { get; set; }
        /// <summary>基准位姿（图像坐标；角度为弧度，图像角度约定），默认 0。</summary>
        public double BaseRow { get; set; }
        public double BaseColumn { get; set; }
        public double BaseAngle { get; set; }
        /// <summary>纠偏方式，默认 RotateAroundCenter。</summary>
        public AlignmentMode Mode { get; set; } = AlignmentMode.RotateAroundCenter;
        /// <summary>相机安装方式，默认 Fixed。</summary>
        public CameraMounting CameraMounting { get; set; } = CameraMounting.Fixed;
        /// <summary>DeltaAngle / AngleDifference 的单位，默认弧度。</summary>
        public AngleUnit AngleUnit { get; set; } = AngleUnit.Radian;

        private readonly CalibrationSourceCache _calibrationCache = new CalibrationSourceCache();

        public AlignmentOffsetTool(string moduleName) : base(moduleName)
        {
        }

        /// <summary>改用标定文件：来源切到文件并清空内嵌数据（二者互斥）。</summary>
        public void UseCalibrationFile(string path)
        {
            CalibrationSource = CalibrationSource.File;
            CalibrationFile = path;
            CalibrationData = null;
        }

        /// <summary>改用内嵌标定数据：来源切到内嵌并清空标定文件路径（二者互斥）。</summary>
        public void UseEmbeddedCalibration(string json)
        {
            CalibrationSource = CalibrationSource.Embedded;
            CalibrationData = json;
            CalibrationFile = null;
        }

        public bool IsParameterVisible(string propertyName)
        {
            switch (propertyName)
            {
                case nameof(CalibrationFile):
                    return CalibrationSource == CalibrationSource.File;
                case nameof(CalibrationData):
                    // 内嵌数据是整段 JSON，只在编辑窗口中生成与查看
                    return false;
                default:
                    return true;
            }
        }

        public IEnumerable<ToolConfigurationIssue> CheckConfiguration()
        {
            List<ToolConfigurationIssue> issues = StructuralIssues();
            if (issues.Count > 0 || !CalibrationSourceCache.IsReadable(CalibrationSource, CalibrationFile))
            {
                return issues;
            }
            try
            {
                CheckSections(LoadCalibration(), out string sectionError);
                if (sectionError != null)
                {
                    issues.Add(new ToolConfigurationIssue(SourceParameter, sectionError));
                }
            }
            catch (Exception ex) when (CalibrationSourceCache.IsLoadException(ex))
            {
                issues.Add(new ToolConfigurationIssue(SourceParameter, ex.Message));
            }
            return issues;
        }

        /// <summary>不需要读取标定内容就能判断的配置问题，运行时同样拒绝。</summary>
        private List<ToolConfigurationIssue> StructuralIssues()
        {
            var issues = new List<ToolConfigurationIssue>();
            if (CameraMounting == CameraMounting.OnAxis && Mode == AlignmentMode.RotateAroundCenter)
            {
                issues.Add(new ToolConfigurationIssue(nameof(Mode), OnAxisRotationMessage));
            }
            if (Mode == AlignmentMode.RotateAroundCenter && string.IsNullOrWhiteSpace(AnglePath))
            {
                issues.Add(new ToolConfigurationIssue(nameof(AnglePath), "RotateAroundCenter 方式需要当前位姿的角度输入（如匹配 Angle）"));
            }
            CalibrationSourceCache.AddExclusivityIssues(issues, CalibrationSource, CalibrationFile, CalibrationData,
                "请在编辑窗口中把标定文件内嵌到工具");
            return issues;
        }

        private string SourceName => CalibrationSourceCache.SourceName(CalibrationSource, CalibrationFile);

        private string SourceParameter => CalibrationSource == CalibrationSource.Embedded ? nameof(CalibrationData) : nameof(CalibrationFile);

        /// <summary>标定内容是否带当前方式需要的段；不符时给出“来源 + 实际类型（含哪些段）+ 期望段”，校验、运行、预热措辞相同。</summary>
        private void CheckSections(CalibrationResult calibration, out string error)
        {
            error = null;
            if (calibration.Affine2D == null)
            {
                error = $"{SourceName} 的类型为 {calibration.Kind}（包含 {calibration.SectionsText()}），纠偏计算需要 Affine2D 数据";
            }
            else if (Mode == AlignmentMode.RotateAroundCenter && calibration.RotationCenter == null)
            {
                error = calibration.IsLegacyTuple
                    ? $"{SourceName} 是旧版 write_tuple 矩阵文件（只有 Affine2D 矩阵），RotateAroundCenter 方式需要 Affine2D + RotationCenter 数据"
                    : $"{SourceName} 的类型为 {calibration.Kind}（包含 {calibration.SectionsText()}），RotateAroundCenter 方式需要 Affine2D + RotationCenter 数据";
            }
        }

        private CalibrationResult LoadCalibration()
        {
            return _calibrationCache.Load(CalibrationSource, CalibrationFile, CalibrationData);
        }

        public override NodeResult Run(FlowContext ctx)
        {
            ToolConfigurationIssue issue = StructuralIssues().FirstOrDefault();
            if (issue != null)
            {
                return NodeResult.Fail($"{ModuleName} {issue.Parameter}：{issue.Message}");
            }
            bool hasAngle = !string.IsNullOrWhiteSpace(AnglePath);
            double row, column, angle = double.NaN;
            try
            {
                row = ReadSingle(ctx, RowPath, "Row");
                column = ReadSingle(ctx, ColumnPath, "Column");
                if (hasAngle)
                {
                    angle = ReadSingle(ctx, AnglePath, "角度");
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException)
            {
                return NodeResult.Fail($"{ModuleName} 输入引用无效：{ex.Message}");
            }

            if (!TryLoadForRun(out CalibrationResult calibration, out string error))
            {
                return NodeResult.Fail(error);
            }

            if (double.IsNaN(row) || double.IsNaN(column) || (hasAngle && double.IsNaN(angle)))
            {
                WriteOutputs(ctx, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, double.NaN, false);
                ctx.AddLog(FlowLogLevel.Warning, $"[纠偏计算] {ModuleName} 当前位姿含 NaN（如未找到目标），输出 NaN、Valid = false");
                return NodeResult.Ok;
            }

            double[] matrix = calibration.Affine2D.HomMat2D;
            double px, py, bx, by, dTheta = double.NaN;
            if (hasAngle)
            {
                CalibrationService.TransformPose(matrix, row, column, angle, out px, out py, out double theta);
                CalibrationService.TransformPose(matrix, BaseRow, BaseColumn, BaseAngle, out bx, out by, out double thetaBase);
                dTheta = CalibrationService.WrapAngle(theta - thetaBase);
            }
            else
            {
                CalibrationService.TransformPoint(matrix, row, column, out px, out py);
                CalibrationService.TransformPoint(matrix, BaseRow, BaseColumn, out bx, out by);
            }

            double deltaX, deltaY, deltaAngle;
            if (Mode == AlignmentMode.RotateAroundCenter)
            {
                // 仅 Fixed（OnAxis + RotateAroundCenter 已在前面拒绝）：先绕 C 转 −dθ 得 P'，(DeltaX, DeltaY) = B − P'
                RotationCenterCalibration center = calibration.RotationCenter;
                double cx, cy;
                if (center.X.HasValue && center.Y.HasValue)
                {
                    cx = center.X.Value;
                    cy = center.Y.Value;
                }
                else
                {
                    CalibrationService.TransformPoint(matrix, center.Row, center.Column, out cx, out cy);
                }
                CalibrationService.RotateAbout(cx, cy, -dTheta, px, py, out double rotatedX, out double rotatedY);
                deltaX = bx - rotatedX;
                deltaY = by - rotatedY;
                deltaAngle = CalibrationService.WrapAngle(-dTheta);
            }
            else if (CameraMounting == CameraMounting.Fixed)
            {
                deltaX = bx - px;
                deltaY = by - py;
                deltaAngle = 0;
            }
            else
            {
                deltaX = px - bx;
                deltaY = py - by;
                deltaAngle = 0;
            }

            WriteOutputs(ctx, deltaX, deltaY, ToUnit(deltaAngle), ToUnit(dTheta), px, py, true);
            ctx.AddLog(FlowLogLevel.Info, string.Format(CultureInfo.InvariantCulture,
                "[纠偏计算] {0}/{1}：DeltaX={2:F4}, DeltaY={3:F4}, DeltaAngle={4:F4}{5}, AngleDifference={6:F4}{5}",
                CameraMounting, Mode, deltaX, deltaY, ToUnit(deltaAngle), AngleUnit == AngleUnit.Degree ? "°" : " rad", ToUnit(dTheta)));
            return NodeResult.Ok;
        }

        private double ToUnit(double radians)
        {
            double value = AngleUnit == AngleUnit.Degree ? AngleMath.ToDegrees(radians) : radians;
            // −0（如 dθ = 0 时的 −dθ）按 0 输出，避免显示成“-0”
            return value == 0 ? 0 : value;
        }

        private void WriteOutputs(FlowContext ctx, double deltaX, double deltaY, double deltaAngle, double angleDifference, double worldX, double worldY, bool valid)
        {
            SetOutput(ctx, Variable.Single(ModuleName, "DeltaX", VariableType.Double, deltaX));
            SetOutput(ctx, Variable.Single(ModuleName, "DeltaY", VariableType.Double, deltaY));
            SetOutput(ctx, Variable.Single(ModuleName, "DeltaAngle", VariableType.Double, deltaAngle));
            SetOutput(ctx, Variable.Single(ModuleName, "AngleDifference", VariableType.Double, angleDifference));
            SetOutput(ctx, Variable.Single(ModuleName, "WorldX", VariableType.Double, worldX));
            SetOutput(ctx, Variable.Single(ModuleName, "WorldY", VariableType.Double, worldY));
            SetOutput(ctx, Variable.Single(ModuleName, "Valid", VariableType.Bool, valid));
        }

        /// <summary>读取单值数值引用；数组明确拒绝（当前位姿必须是单值）。</summary>
        private static double ReadSingle(FlowContext ctx, string path, string what)
        {
            List<double> values = RegionDistanceTool.ReadNumbers(ctx, path, what + " ");
            if (values.Count != 1)
            {
                throw new InvalidOperationException($"{what}（{path}）需要单值，当前为 {values.Count} 个值；多个目标请放在 For 循环中逐个计算");
            }
            return values[0];
        }

        /// <summary>运行时读取标定来源；缺失、格式错误或缺段时返回 false 与中文错误（与校验、预热措辞相同）。</summary>
        private bool TryLoadForRun(out CalibrationResult calibration, out string error)
        {
            calibration = null;
            error = null;
            if (CalibrationSource == CalibrationSource.File)
            {
                if (string.IsNullOrWhiteSpace(CalibrationFile))
                {
                    error = $"{ModuleName} 未配置 CalibrationFile";
                    return false;
                }
                if (!System.IO.File.Exists(CalibrationFile))
                {
                    error = $"{ModuleName} 标定文件不存在：{CalibrationFile}";
                    return false;
                }
            }
            try
            {
                calibration = LoadCalibration();
            }
            catch (Exception ex) when (CalibrationSourceCache.IsLoadException(ex))
            {
                error = $"{ModuleName} {ex.Message}";
                return false;
            }
            CheckSections(calibration, out error);
            if (error != null)
            {
                error = $"{ModuleName} {error}";
                return false;
            }
            return true;
        }

        /// <summary>预热：读取标定来源；文件缺失、格式错误、缺段时抛出（文件缺失不在校验阶段报，沿用批一约定）。</summary>
        public void Prepare()
        {
            if (CalibrationSource == CalibrationSource.File)
            {
                if (string.IsNullOrWhiteSpace(CalibrationFile))
                {
                    return;
                }
                if (!System.IO.File.Exists(CalibrationFile))
                {
                    throw new System.IO.FileNotFoundException($"标定文件不存在：{CalibrationFile}", CalibrationFile);
                }
            }
            else if (string.IsNullOrWhiteSpace(CalibrationData))
            {
                return;
            }
            CheckSections(LoadCalibration(), out string error);
            if (error != null)
            {
                throw new InvalidOperationException(error);
            }
        }

        public void ReleaseResources()
        {
            _calibrationCache.Release();
        }
    }
}
