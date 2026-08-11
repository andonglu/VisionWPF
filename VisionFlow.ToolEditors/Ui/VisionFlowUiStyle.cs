using System.Drawing;
using System.Windows.Forms;

namespace VisionFlow.Ui
{
    /// <summary>VisionFlow 窗体设计标准：统一尺寸、间距、字体、颜色与控件最小高度。</summary>
    public static class VisionFlowUiStyle
    {
        public static readonly Font DefaultFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Regular, GraphicsUnit.Point, 134);
        public static readonly Font TitleFont = new Font("Microsoft YaHei UI", 10.5F, FontStyle.Bold, GraphicsUnit.Point, 134);
        public static readonly Font GroupTitleFont = new Font("Microsoft YaHei UI", 9F, FontStyle.Bold, GraphicsUnit.Point, 134);

        public static readonly Color Primary = Color.FromArgb(48, 63, 159);
        public static readonly Color PrimaryLight = Color.FromArgb(235, 243, 255);
        public static readonly Color PanelBackColor = Color.FromArgb(248, 250, 252);
        public static readonly Color BorderColor = Color.FromArgb(180, 205, 235);
        public static readonly Color TextColor = Color.FromArgb(64, 64, 64);
        public static readonly Color SelectionBackColor = Color.LightSkyBlue;

        public const int FormMinWidth = 1366;
        public const int FormMinHeight = 768;
        public const int MainToolboxWidth = 260;
        public const int MainFlowPercent = 42;
        public const int MainImagePercent = 58;
        public const int HeaderHeight = 40;
        public const int FooterHeight = 56;
        public const int EditorLabelWidth = 130;
        public const int EditorRowHeight = 42;
        public const int EditorButtonWidth = 104;
        public const int EditorButtonHeight = 34;
        public const int ControlHeight = 30;
        public const int Padding = 10;

        public static void ApplyForm(Form form)
        {
            form.Font = DefaultFont;
            form.BackColor = PanelBackColor;
            form.AutoScaleMode = AutoScaleMode.Font;
            form.MinimumSize = new Size(FormMinWidth, FormMinHeight);
            form.StartPosition = FormStartPosition.CenterScreen;
        }

        public static void ApplyDialog(Form form, int width = 820, int height = 640)
        {
            form.Font = DefaultFont;
            form.BackColor = PanelBackColor;
            form.AutoScaleMode = AutoScaleMode.Font;
            form.ClientSize = new Size(width, height);
            form.MinimumSize = new Size(720, 520);
            form.StartPosition = FormStartPosition.CenterParent;
        }

        public static void ApplyGroupBox(GroupBox groupBox)
        {
            groupBox.Font = GroupTitleFont;
            groupBox.Padding = new Padding(Padding);
            groupBox.BackColor = PanelBackColor;
        }

        public static void ApplyButton(Button button, bool primary = true)
        {
            button.Width = EditorButtonWidth;
            button.Height = EditorButtonHeight;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderSize = 0;
            button.BackColor = primary ? Primary : Color.White;
            button.ForeColor = primary ? Color.White : TextColor;
            button.Margin = new Padding(8, 8, 0, 8);
        }

        public static void ApplyEditor(Control control)
        {
            control.MinimumSize = new Size(0, ControlHeight);
            control.Margin = new Padding(4, 6, 4, 6);
            control.Font = DefaultFont;
        }

        public static void ApplyDataGridView(DataGridView grid)
        {
            grid.BackgroundColor = Color.White;
            grid.BorderStyle = BorderStyle.FixedSingle;
            grid.EnableHeadersVisualStyles = false;
            grid.GridColor = BorderColor;
            grid.RowTemplate.Height = 28;
            grid.ColumnHeadersHeight = 32;
            grid.ColumnHeadersDefaultCellStyle.BackColor = PrimaryLight;
            grid.ColumnHeadersDefaultCellStyle.ForeColor = TextColor;
            grid.ColumnHeadersDefaultCellStyle.Alignment = DataGridViewContentAlignment.MiddleCenter;
            grid.DefaultCellStyle.SelectionBackColor = SelectionBackColor;
            grid.DefaultCellStyle.SelectionForeColor = TextColor;
        }
    }
}
