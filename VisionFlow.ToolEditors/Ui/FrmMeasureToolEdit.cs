using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    /// <summary>
    /// 椭圆测量工具配置页（示例）：标量参数用输入控件；
    /// 引用参数（图像/变换矩阵/结果序号）用下拉选择，候选项来自上游输出。
    /// </summary>
    public sealed class FrmMeasureToolEdit : Form
    {
        private readonly EllipseFollowMeasureTool _tool;

        private TextBox _moduleName;
        private NumericUpDown _ellipseRow;
        private NumericUpDown _ellipseColumn;
        private NumericUpDown _ellipseAngle;
        private NumericUpDown _length1;
        private NumericUpDown _length2;
        private NumericUpDown _measureLength1;
        private NumericUpDown _measureSigma;
        private NumericUpDown _measureThreshold;
        private ComboBox _imagePath;
        private ComboBox _matrixPath;
        private ComboBox _indexPath;

        public FrmMeasureToolEdit(EllipseFollowMeasureTool tool, ToolEditContext context)
        {
            _tool = tool;
            Text = $"椭圆测量 - {tool.ModuleName}";
            Width = 480;
            Height = 480;
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;

            var layout = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 2,
                RowCount = 13,
                Padding = new Padding(10)
            };
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 100));
            layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            Controls.Add(layout);

            int row = 0;
            _moduleName = AddTextRow(layout, row++, "模块名", tool.ModuleName);

            // 引用参数：下拉选择上游输出（可选的允许留空）
            _imagePath = AddRefRow(layout, row++, "图像",
                RefCandidateService.ForInput(context.Root, context.Node, typeof(HObject)),
                tool.ImagePath, optional: false);
            _matrixPath = AddRefRow(layout, row++, "变换矩阵",
                RefCandidateService.ForInput(context.Root, context.Node, typeof(HomMat2D)),
                tool.MatrixPath, optional: true);
            _indexPath = AddRefRow(layout, row++, "结果序号",
                RefCandidateService.ForInput(context.Root, context.Node, typeof(int)),
                tool.IndexPath, optional: true);

            _ellipseRow = AddNumRow(layout, row++, "椭圆行", tool.EllipseRow, 4);
            _ellipseColumn = AddNumRow(layout, row++, "椭圆列", tool.EllipseColumn, 4);
            _ellipseAngle = AddNumRow(layout, row++, "椭圆角度", tool.EllipseAngle, 4);
            _length1 = AddNumRow(layout, row++, "半轴长1", tool.EllipseLength1, 2);
            _length2 = AddNumRow(layout, row++, "半轴长2", tool.EllipseLength2, 2);
            _measureLength1 = AddNumRow(layout, row++, "测量长度", tool.MeasureLength1, 1);
            _measureSigma = AddNumRow(layout, row++, "平滑Sigma", tool.MeasureSigma, 1);
            _measureThreshold = AddNumRow(layout, row++, "边缘阈值", tool.MeasureThreshold, 1);

            var okButton = new Button { Text = "确定", DialogResult = DialogResult.OK, Dock = DockStyle.Fill };
            layout.Controls.Add(okButton, 1, row);
            AcceptButton = okButton;
        }

        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (DialogResult == DialogResult.OK)
            {
                _tool.ModuleName = _moduleName.Text.Trim();
                _tool.ImagePath = _imagePath.Text.Trim();
                _tool.MatrixPath = _matrixPath.Text.Trim();
                _tool.IndexPath = _indexPath.Text.Trim();
                _tool.EllipseRow = (double)_ellipseRow.Value;
                _tool.EllipseColumn = (double)_ellipseColumn.Value;
                _tool.EllipseAngle = (double)_ellipseAngle.Value;
                _tool.EllipseLength1 = (double)_length1.Value;
                _tool.EllipseLength2 = (double)_length2.Value;
                _tool.MeasureLength1 = (double)_measureLength1.Value;
                _tool.MeasureSigma = (double)_measureSigma.Value;
                _tool.MeasureThreshold = (double)_measureThreshold.Value;
            }
            base.OnClosing(e);
        }

        private static TextBox AddTextRow(TableLayoutPanel layout, int row, string label, string value)
        {
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var textBox = new TextBox { Text = value, Dock = DockStyle.Fill };
            layout.Controls.Add(textBox, 1, row);
            return textBox;
        }

        private static ComboBox AddRefRow(TableLayoutPanel layout, int row, string label,
            List<RefCandidate> candidates, string currentValue, bool optional)
        {
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var combo = new ComboBox { Dock = DockStyle.Fill };
            if (optional)
            {
                combo.Items.Add(string.Empty);
            }
            foreach (RefCandidate candidate in candidates)
            {
                combo.Items.Add(candidate.Path);
            }
            combo.Text = currentValue ?? string.Empty;
            layout.Controls.Add(combo, 1, row);
            return combo;
        }

        private static NumericUpDown AddNumRow(TableLayoutPanel layout, int row, string label, double value, int decimals)
        {
            layout.Controls.Add(new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft }, 0, row);
            var num = new NumericUpDown
            {
                Dock = DockStyle.Fill,
                Minimum = -100000,
                Maximum = 100000,
                DecimalPlaces = decimals,
                Increment = decimals >= 4 ? 0.0001m : 1m,
                Value = (decimal)value
            };
            layout.Controls.Add(num, 1, row);
            return num;
        }
    }
}
