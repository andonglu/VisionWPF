using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Tools.Calibration;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    /// <summary>
    /// 纠偏计算编辑窗口（CB-07）：当前位姿引用、标定来源（文件 / 内嵌，互斥）、纠偏方式与基准位姿；
    /// “用当前结果设为基准”从上次运行结果读取当前位姿引用的值；执行测试经 ToolTestRun，确定时才写回工具。
    /// </summary>
    public partial class WpfAlignmentOffsetToolEditWindow : Window
    {
        private sealed class Choice<T>
        {
            public Choice(T value, string label)
            {
                Value = value;
                Label = label;
            }

            public T Value { get; private set; }
            public string Label { get; private set; }

            public override string ToString()
            {
                return Label;
            }
        }

        private sealed class OutputRow
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        private static readonly Choice<CalibrationSource>[] SourceChoices =
        {
            new Choice<CalibrationSource>(CalibrationSource.File, "标定文件"),
            new Choice<CalibrationSource>(CalibrationSource.Embedded, "内嵌在工具中")
        };

        private static readonly Choice<AlignmentMode>[] ModeChoices =
        {
            new Choice<AlignmentMode>(AlignmentMode.TranslationOnly, "TranslationOnly（只补偿平移）"),
            new Choice<AlignmentMode>(AlignmentMode.RotateAroundCenter, "RotateAroundCenter（先绕旋转中心转回，再平移）")
        };

        private static readonly Choice<CameraMounting>[] MountingChoices =
        {
            new Choice<CameraMounting>(CameraMounting.Fixed, "Fixed（相机固定，看轴带动的工件）"),
            new Choice<CameraMounting>(CameraMounting.OnAxis, "OnAxis（相机装在轴上，看固定目标）")
        };

        private static readonly Choice<AngleUnit>[] UnitChoices =
        {
            new Choice<AngleUnit>(AngleUnit.Radian, "弧度"),
            new Choice<AngleUnit>(AngleUnit.Degree, "度")
        };

        private static readonly string[] OutputNames = { "DeltaX", "DeltaY", "DeltaAngle", "AngleDifference", "WorldX", "WorldY", "Valid" };

        private readonly AlignmentOffsetTool _tool;
        private readonly ToolEditContext _context;
        /// <summary>窗口内待提交的标定来源（文件 / 内嵌，互斥），确定时写回工具。</summary>
        private readonly AlignmentOffsetTool _calibrationState = new AlignmentOffsetTool("标定");
        private bool _loading;

        public WpfAlignmentOffsetToolEditWindow(AlignmentOffsetTool tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();
            InitializeComponent();
            Title = "纠偏计算 - " + tool.ModuleName;
            _loading = true;
            InitializeOptions();
            LoadFromTool();
            _loading = false;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            UpdateModeHint();
        }

        private CalibrationSource SelectedSource => (SourceCombo.SelectedItem as Choice<CalibrationSource>)?.Value ?? CalibrationSource.File;
        private AlignmentMode SelectedMode => (ModeCombo.SelectedItem as Choice<AlignmentMode>)?.Value ?? AlignmentMode.RotateAroundCenter;
        private CameraMounting SelectedMounting => (MountingCombo.SelectedItem as Choice<CameraMounting>)?.Value ?? CameraMounting.Fixed;
        private AngleUnit SelectedUnit => (AngleUnitCombo.SelectedItem as Choice<AngleUnit>)?.Value ?? AngleUnit.Radian;

        private void InitializeOptions()
        {
            SourceCombo.ItemsSource = SourceChoices;
            ModeCombo.ItemsSource = ModeChoices;
            MountingCombo.ItemsSource = MountingChoices;
            AngleUnitCombo.ItemsSource = UnitChoices;
            AnglePathCombo.Items.Add(string.Empty);
            if (_context.Root != null && _context.Node != null)
            {
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, typeof(double)))
                {
                    RowPathCombo.Items.Add(candidate.Path);
                    ColumnPathCombo.Items.Add(candidate.Path);
                    AnglePathCombo.Items.Add(candidate.Path);
                }
            }
        }

        private void LoadFromTool()
        {
            ModuleNameText.Text = _tool.ModuleName;
            RowPathCombo.Text = _tool.RowPath ?? string.Empty;
            ColumnPathCombo.Text = _tool.ColumnPath ?? string.Empty;
            AnglePathCombo.Text = _tool.AnglePath ?? string.Empty;
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == _tool.CalibrationSource);
            ModeCombo.SelectedItem = ModeChoices.First(c => c.Value == _tool.Mode);
            MountingCombo.SelectedItem = MountingChoices.First(c => c.Value == _tool.CameraMounting);
            AngleUnitCombo.SelectedItem = UnitChoices.First(c => c.Value == _tool.AngleUnit);
            _calibrationState.CalibrationSource = _tool.CalibrationSource;
            _calibrationState.CalibrationFile = _tool.CalibrationFile;
            _calibrationState.CalibrationData = _tool.CalibrationData;
            CalibrationFileText.Text = _tool.CalibrationFile ?? string.Empty;
            BaseRowText.Text = Format(_tool.BaseRow);
            BaseColumnText.Text = Format(_tool.BaseColumn);
            BaseAngleText.Text = Format(_tool.BaseAngle);
        }

        // ======================= 标定来源 =======================

        private void SourceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading || SelectedSource == _calibrationState.CalibrationSource)
            {
                return;
            }
            if (SelectedSource == CalibrationSource.File)
            {
                _calibrationState.UseCalibrationFile(NullIfEmpty(CalibrationFileText.Text));
                SetStatus("已切换为标定文件，内嵌标定数据已清空。");
            }
            else
            {
                _calibrationState.UseEmbeddedCalibration(null);
                CalibrationFileText.Text = string.Empty;
                SetStatus("已切换为内嵌：标定文件路径已清空，请选择标定文件后“把标定文件内嵌到工具”。");
            }
            UpdateSourcePanels();
            UpdateCalibrationInfo();
        }

        private void CalibrationFileText_LostFocus(object sender, RoutedEventArgs e)
        {
            string path = NullIfEmpty(CalibrationFileText.Text);
            if (_calibrationState.CalibrationSource == CalibrationSource.File && !string.Equals(path, _calibrationState.CalibrationFile, StringComparison.Ordinal))
            {
                _calibrationState.UseCalibrationFile(path);
                UpdateCalibrationInfo();
            }
        }

        private void BrowseCalibrationFile_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new OpenFileDialog
            {
                Title = "选择标定文件",
                Filter = "标定文件 (*.vfcal.json;*.tup;*.dat)|*.vfcal.json;*.tup;*.dat|所有文件 (*.*)|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }
            _loading = true;
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == CalibrationSource.File);
            _loading = false;
            _calibrationState.UseCalibrationFile(dialog.FileName);
            CalibrationFileText.Text = dialog.FileName;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            SetStatus("已选择标定文件：" + dialog.FileName);
        }

        /// <summary>把当前标定文件的内容内嵌到工具（来源改为内嵌，文件路径清空）。</summary>
        private void EmbedFile_Click(object sender, RoutedEventArgs e)
        {
            string path = _calibrationState.CalibrationSource == CalibrationSource.File ? _calibrationState.CalibrationFile : null;
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                ShowError("请先选择存在的标定文件，再内嵌到工具。");
                return;
            }
            try
            {
                CalibrationResult calibration = CalibrationService.Load(path);
                if (calibration.IsLegacyTuple)
                {
                    ShowError("旧版 write_tuple 矩阵文件不能内嵌：请先在“图像坐标转世界坐标”中重新标定并保存为 .vfcal.json。");
                    return;
                }
                _calibrationState.UseEmbeddedCalibration(CalibrationService.ToJson(calibration));
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException)
            {
                ShowError("读取标定文件失败：" + ex.Message);
                return;
            }
            _loading = true;
            SourceCombo.SelectedItem = SourceChoices.First(c => c.Value == CalibrationSource.Embedded);
            _loading = false;
            CalibrationFileText.Text = string.Empty;
            UpdateSourcePanels();
            UpdateCalibrationInfo();
            SetStatus("已把标定文件内嵌到工具（标定文件路径已清空），确定后生效。");
        }

        private void UpdateSourcePanels()
        {
            bool file = _calibrationState.CalibrationSource == CalibrationSource.File;
            CalibrationFileLabel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            CalibrationFilePanel.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
            EmbedFileButton.Visibility = file ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>摘要：标定来源包含哪些段；缺段时直接给出与流程校验相同的说明。</summary>
        private void UpdateCalibrationInfo()
        {
            _calibrationState.Mode = SelectedMode;
            _calibrationState.CameraMounting = CameraMounting.Fixed;
            _calibrationState.AnglePath = "角度";
            string origin = _calibrationState.CalibrationSource == CalibrationSource.Embedded ? "内嵌" : "文件";
            try
            {
                CalibrationResult calibration;
                if (_calibrationState.CalibrationSource == CalibrationSource.Embedded)
                {
                    if (string.IsNullOrWhiteSpace(_calibrationState.CalibrationData))
                    {
                        CalibrationInfoText.Text = "标定：内嵌，尚无标定数据";
                        return;
                    }
                    calibration = CalibrationService.Parse(_calibrationState.CalibrationData, "内嵌标定数据");
                }
                else
                {
                    if (string.IsNullOrWhiteSpace(_calibrationState.CalibrationFile))
                    {
                        CalibrationInfoText.Text = "标定：未配置标定文件";
                        return;
                    }
                    calibration = CalibrationService.Load(_calibrationState.CalibrationFile);
                }
                string text = calibration.IsLegacyTuple
                    ? "标定：旧版 write_tuple 矩阵文件（Affine2D）"
                    : $"标定：{calibration.Kind}（包含 {calibration.SectionsText()}），单位 {calibration.Unit ?? "未填"}";
                if (calibration.RotationCenter != null && calibration.RotationCenter.X.HasValue)
                {
                    text += string.Format(CultureInfo.CurrentCulture, "，旋转中心物理坐标 ({0:F4}, {1:F4})", calibration.RotationCenter.X.Value, calibration.RotationCenter.Y.Value);
                }
                string issue = _calibrationState.CheckConfiguration().FirstOrDefault(i => i.Parameter == "CalibrationFile" || i.Parameter == "CalibrationData")?.Message;
                CalibrationInfoText.Text = text + "（" + origin + "）" + (issue == null ? string.Empty : "；" + issue);
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is UnauthorizedAccessException || ex is ArgumentException)
            {
                CalibrationInfoText.Text = "标定：" + ex.Message;
            }
        }

        private void Option_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
            {
                return;
            }
            UpdateModeHint();
            UpdateCalibrationInfo();
        }

        private void UpdateModeHint()
        {
            string hint = SelectedMounting == CameraMounting.OnAxis && SelectedMode == AlignmentMode.RotateAroundCenter
                ? AlignmentOffsetTool.OnAxisRotationMessage
                : null;
            ModeHintText.Text = hint ?? string.Empty;
            ModeHintText.Visibility = hint == null ? Visibility.Collapsed : Visibility.Visible;
        }

        // ======================= 基准位姿与测试 =======================

        /// <summary>从上次运行结果读取当前位姿引用（Row / Column / 角度）的值，写入基准位姿。</summary>
        private void SetBaseFromLastRun_Click(object sender, RoutedEventArgs e)
        {
            if (_context.LastRunContext == null)
            {
                ShowError("还没有运行结果：请先运行一次流程，再用当前结果设为基准。");
                return;
            }
            try
            {
                double row = ReadSingle(RowPathCombo.Text, "Row");
                double column = ReadSingle(ColumnPathCombo.Text, "Column");
                bool hasAngle = !string.IsNullOrWhiteSpace(AnglePathCombo.Text);
                double angle = hasAngle ? ReadSingle(AnglePathCombo.Text, "角度") : 0;
                BaseRowText.Text = Format(row);
                BaseColumnText.Text = Format(column);
                if (hasAngle)
                {
                    BaseAngleText.Text = Format(angle);
                }
                SetStatus(hasAngle
                    ? $"已用上次运行结果设为基准：({Format(row)}, {Format(column)})，角度 {Format(angle)} 弧度。"
                    : $"已用上次运行结果设为基准：({Format(row)}, {Format(column)})（未配置角度输入，基准角度不变）。");
            }
            catch (InvalidOperationException ex)
            {
                ShowError(ex.Message);
            }
        }

        private double ReadSingle(string pathText, string what)
        {
            string path = (pathText ?? string.Empty).Trim();
            if (path.Length == 0)
            {
                throw new InvalidOperationException($"请先配置 {what} 引用");
            }
            object value;
            try
            {
                value = VariableReference.Parse(path).Resolve(_context.LastRunContext);
            }
            catch (Exception ex) when (ex is InvalidOperationException || ex is KeyNotFoundException || ex is InvalidCastException || ex is FormatException || ex is ArgumentException)
            {
                throw new InvalidOperationException($"{what} {path} 无法读取：{ex.Message}");
            }
            if (value is IEnumerable sequence && !(value is string))
            {
                throw new InvalidOperationException($"{what} {path} 是数组（{sequence.Cast<object>().Count()} 个值），基准位姿要求单值：请引用具体元素，如 {path}[0]");
            }
            try
            {
                double number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
                if (double.IsNaN(number))
                {
                    throw new InvalidOperationException($"{what} {path} 上次运行的值为 NaN（如未找到目标），不能设为基准");
                }
                return number;
            }
            catch (Exception ex) when (ex is FormatException || ex is InvalidCastException || ex is OverflowException)
            {
                throw new InvalidOperationException($"{what} {path} 不是数值");
            }
        }

        private void TestRun_Click(object sender, RoutedEventArgs e)
        {
            ToolTestRun run = null;
            try
            {
                run = ToolTestRun.Run(_tool, copy => ApplyTo((AlignmentOffsetTool)copy), _context.LastRunContext, _context.InputImage);
                if (!run.Result.IsSuccess)
                {
                    ResultGrid.ItemsSource = null;
                    ResultSummaryText.Text = "运行失败：" + run.Result.Message;
                    SetStatus("执行测试：" + run.Result.Message);
                    return;
                }
                FlowContext ctx = run.Context;
                string module = run.ModuleName;
                ResultGrid.ItemsSource = OutputNames.Select(name => new OutputRow
                {
                    Name = name,
                    Value = FormatOutput(ctx.GetVariable(module, name).Value)
                }).ToList();
                bool valid = (bool)ctx.GetVariable(module, "Valid").Value;
                ResultSummaryText.Text = valid
                    ? string.Format(CultureInfo.CurrentCulture, "补偿 DeltaX {0:F4}，DeltaY {1:F4}，DeltaAngle {2:F4}（{3}）",
                        ctx.GetVariable(module, "DeltaX").Value, ctx.GetVariable(module, "DeltaY").Value, ctx.GetVariable(module, "DeltaAngle").Value,
                        SelectedUnit == AngleUnit.Degree ? "度" : "弧度")
                    : "当前位姿无效（NaN），Valid = false";
                SetStatus("执行测试完成：" + ResultSummaryText.Text);
            }
            catch (Exception ex)
            {
                ShowError("执行测试失败：" + ex.Message);
            }
            finally
            {
                run?.Dispose();
            }
        }

        private static string FormatOutput(object value)
        {
            return value is double d ? d.ToString("R", CultureInfo.InvariantCulture) : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                ApplyTo(_tool);
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                ShowError("参数保存失败：" + ex.Message);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        /// <summary>界面参数写入 target（确定时为窗口持有的工具，执行测试时为一次性副本）。</summary>
        private void ApplyTo(AlignmentOffsetTool target)
        {
            CalibrationFileText_LostFocus(this, null);
            target.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            target.RowPath = NullIfEmpty(RowPathCombo.Text);
            target.ColumnPath = NullIfEmpty(ColumnPathCombo.Text);
            target.AnglePath = NullIfEmpty(AnglePathCombo.Text);
            target.CalibrationSource = _calibrationState.CalibrationSource;
            target.CalibrationFile = _calibrationState.CalibrationFile;
            target.CalibrationData = _calibrationState.CalibrationData;
            target.Mode = SelectedMode;
            target.CameraMounting = SelectedMounting;
            target.AngleUnit = SelectedUnit;
            target.BaseRow = ParseDouble(BaseRowText.Text, "BaseRow");
            target.BaseColumn = ParseDouble(BaseColumnText.Text, "BaseColumn");
            target.BaseAngle = ParseDouble(BaseAngleText.Text, "BaseAngle");
        }

        private static string NullIfEmpty(string text)
        {
            string trimmed = (text ?? string.Empty).Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }

        private static double ParseDouble(string text, string name)
        {
            if (double.TryParse((text ?? string.Empty).Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                return value;
            }
            throw new FormatException($"{name} 不是有效数字。");
        }

        private static string Format(double value)
        {
            return value.ToString("R", CultureInfo.InvariantCulture);
        }

        private void SetStatus(string message)
        {
            StatusText.Text = message;
        }

        private void ShowError(string message)
        {
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(message);
        }
    }
}
