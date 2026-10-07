using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using VisionFlow.Tools;

namespace VisionFlow.WpfToolEditors.Controls
{
    /// <summary>
    /// metrology 测量（直线 / 矩形 / 圆 / 椭圆）的“高级参数”折叠区（MS-01），默认收起。
    /// 卡尺数大于 0 时按卡尺数布置并隐藏卡尺间距，否则按卡尺间距布置；默认值等于 HALCON 默认值。
    /// </summary>
    public sealed class MetrologyAdvancedExpander : Expander
    {
        private readonly TextBox _numMeasures = new TextBox();
        private readonly TextBox _measureDistance = new TextBox();
        private readonly TextBox _minScore = new TextBox();
        private readonly TextBox _numInstances = new TextBox();
        private readonly TextBox _distanceThreshold = new TextBox();
        private readonly ComboBox _interpolation = new ComboBox();
        private readonly TextBlock _measureDistanceLabel;

        public MetrologyAdvancedExpander()
        {
            Header = "高级参数";
            IsExpanded = false;
            Margin = new Thickness(0, 12, 0, 0);

            var panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            AddField(panel, "卡尺数（0 = 按间距）", _numMeasures, "卡尺数量；0 表示不设置，按卡尺间距布置");
            _measureDistanceLabel = AddField(panel, "卡尺间距", _measureDistance, "相邻卡尺中心的间距（像素），仅卡尺数为 0 时使用");
            AddField(panel, "最低得分", _minScore, "实例的最低得分（有效边缘点占比，0 ~ 1）");
            AddField(panel, "实例数", _numInstances, "最多查找的实例数，如两条平行直线设为 2");
            AddField(panel, "距离阈值", _distanceThreshold, "边缘点到拟合几何的最大距离（像素），超过视为离群点");
            _interpolation.ItemsSource = Enum.GetNames(typeof(MetrologyInterpolation));
            AddField(panel, "插值方式", _interpolation, "卡尺灰度插值方式");
            var hint = new TextBlock
            {
                Text = "默认值等于 HALCON 默认值，保持默认时结果与旧版一致。",
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0)
            };
            hint.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            panel.Children.Add(hint);
            Content = panel;
            _numMeasures.TextChanged += (s, e) => UpdateMeasureDistanceVisibility();
        }

        private static TextBlock AddField(Panel panel, string label, Control editor, string toolTip)
        {
            var text = new TextBlock { Text = label };
            text.SetResourceReference(StyleProperty, "EditorFieldLabelStyle");
            editor.ToolTip = toolTip;
            panel.Children.Add(text);
            panel.Children.Add(editor);
            return text;
        }

        private void UpdateMeasureDistanceVisibility()
        {
            bool byDistance = !int.TryParse(_numMeasures.Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0;
            Visibility visibility = byDistance ? Visibility.Visible : Visibility.Collapsed;
            _measureDistanceLabel.Visibility = visibility;
            _measureDistance.Visibility = visibility;
        }

        public void Load(MetrologyMeasureToolBase tool)
        {
            _numMeasures.Text = tool.NumMeasures.ToString(CultureInfo.InvariantCulture);
            _measureDistance.Text = Format(tool.MeasureDistance);
            _minScore.Text = Format(tool.MinScore);
            _numInstances.Text = tool.NumInstances.ToString(CultureInfo.InvariantCulture);
            _distanceThreshold.Text = Format(tool.DistanceThreshold);
            _interpolation.SelectedItem = tool.MeasureInterpolation.ToString();
            UpdateMeasureDistanceVisibility();
        }

        /// <summary>把界面值写入工具；格式错误时抛出 InvalidOperationException（中文说明）。</summary>
        public void Apply(MetrologyMeasureToolBase tool)
        {
            tool.NumMeasures = ParseInt(_numMeasures.Text, "卡尺数");
            tool.MeasureDistance = ParseDouble(_measureDistance.Text, "卡尺间距");
            tool.MinScore = ParseDouble(_minScore.Text, "最低得分");
            tool.NumInstances = ParseInt(_numInstances.Text, "实例数");
            tool.DistanceThreshold = ParseDouble(_distanceThreshold.Text, "距离阈值");
            tool.MeasureInterpolation = (MetrologyInterpolation)Enum.Parse(typeof(MetrologyInterpolation),
                (_interpolation.SelectedItem as string) ?? nameof(MetrologyInterpolation.nearest_neighbor));
        }

        private static string Format(double value)
        {
            return value.ToString("G", CultureInfo.InvariantCulture);
        }

        private static int ParseInt(string text, string name)
        {
            if (!int.TryParse((text ?? string.Empty).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
            {
                throw new InvalidOperationException(name + " 不是有效整数。");
            }
            return value;
        }

        private static double ParseDouble(string text, string name)
        {
            string trimmed = (text ?? string.Empty).Trim();
            if (!double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                && !double.TryParse(trimmed, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                throw new InvalidOperationException(name + " 不是有效数字。");
            }
            return value;
        }
    }
}
