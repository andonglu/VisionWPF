using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;
using VisionFlow.Ui;

namespace VisionFlow.LDWeldingPlugins
{
    partial class LDWeldingPluginEditor
    {
        private IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            TitlePanel = new Panel();
            LblTitle = new Label();
            BtnClose = new Button();
            ContentPanel = new Panel();
            MainPanel = new TableLayoutPanel();
            FooterPanel = new Panel();
            BtnCancel = new Button();
            BtnOK = new Button();
            TitlePanel.SuspendLayout();
            ContentPanel.SuspendLayout();
            FooterPanel.SuspendLayout();
            SuspendLayout();
            // 
            // TitlePanel
            // 
            TitlePanel.BackColor = VisionFlowUiStyle.Primary;
            TitlePanel.Controls.Add(LblTitle);
            TitlePanel.Controls.Add(BtnClose);
            TitlePanel.Dock = DockStyle.Top;
            TitlePanel.Location = new Point(0, 0);
            TitlePanel.Name = "TitlePanel";
            TitlePanel.Padding = new Padding(12, 0, 0, 0);
            TitlePanel.Size = new Size(820, 40);
            TitlePanel.TabIndex = 0;
            // 
            // LblTitle
            // 
            LblTitle.Dock = DockStyle.Fill;
            LblTitle.Font = VisionFlowUiStyle.TitleFont;
            LblTitle.ForeColor = Color.White;
            LblTitle.Location = new Point(12, 0);
            LblTitle.Name = "LblTitle";
            LblTitle.Size = new Size(764, 40);
            LblTitle.TabIndex = 0;
            LblTitle.Text = "LDWelding 插件配置";
            LblTitle.TextAlign = ContentAlignment.MiddleLeft;
            // 
            // BtnClose
            // 
            BtnClose.Dock = DockStyle.Right;
            BtnClose.FlatAppearance.BorderSize = 0;
            BtnClose.FlatAppearance.MouseOverBackColor = Color.FromArgb(83, 109, 254);
            BtnClose.FlatStyle = FlatStyle.Flat;
            BtnClose.ForeColor = Color.White;
            BtnClose.Location = new Point(776, 0);
            BtnClose.Name = "BtnClose";
            BtnClose.Size = new Size(44, 40);
            BtnClose.TabIndex = 1;
            BtnClose.Text = "×";
            BtnClose.UseVisualStyleBackColor = true;
            BtnClose.Click += BtnClose_Click;
            // 
            // ContentPanel
            // 
            ContentPanel.BackColor = VisionFlowUiStyle.PanelBackColor;
            ContentPanel.Controls.Add(MainPanel);
            ContentPanel.Dock = DockStyle.Fill;
            ContentPanel.Location = new Point(0, 40);
            ContentPanel.Name = "ContentPanel";
            ContentPanel.Padding = new Padding(12);
            ContentPanel.Size = new Size(820, 544);
            ContentPanel.TabIndex = 1;
            // 
            // MainPanel
            // 
            MainPanel.AutoScroll = true;
            MainPanel.BackColor = Color.White;
            MainPanel.ColumnCount = 2;
            MainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 130F));
            MainPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            MainPanel.Dock = DockStyle.Fill;
            MainPanel.Location = new Point(12, 12);
            MainPanel.Name = "MainPanel";
            MainPanel.Padding = new Padding(12);
            MainPanel.RowCount = 0;
            MainPanel.Size = new Size(796, 520);
            MainPanel.TabIndex = 0;
            // 
            // FooterPanel
            // 
            FooterPanel.BackColor = Color.WhiteSmoke;
            FooterPanel.Controls.Add(BtnCancel);
            FooterPanel.Controls.Add(BtnOK);
            FooterPanel.Dock = DockStyle.Bottom;
            FooterPanel.Location = new Point(0, 584);
            FooterPanel.Name = "FooterPanel";
            FooterPanel.Padding = new Padding(0, 8, 16, 8);
            FooterPanel.Size = new Size(820, 56);
            FooterPanel.TabIndex = 2;
            // 
            // BtnCancel
            // 
            BtnCancel.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            BtnCancel.DialogResult = DialogResult.Cancel;
            BtnCancel.Location = new Point(692, 11);
            BtnCancel.Name = "BtnCancel";
            BtnCancel.Size = new Size(104, 34);
            BtnCancel.TabIndex = 1;
            BtnCancel.Text = "取消";
            BtnCancel.UseVisualStyleBackColor = true;
            VisionFlowUiStyle.ApplyButton(BtnCancel, false);
            // 
            // BtnOK
            // 
            BtnOK.Anchor = AnchorStyles.Top | AnchorStyles.Right;
            BtnOK.DialogResult = DialogResult.OK;
            BtnOK.Location = new Point(574, 11);
            BtnOK.Name = "BtnOK";
            BtnOK.Size = new Size(104, 34);
            BtnOK.TabIndex = 0;
            BtnOK.Text = "确定";
            BtnOK.UseVisualStyleBackColor = false;
            VisionFlowUiStyle.ApplyButton(BtnOK);
            // 
            // LDWeldingPluginEditor
            // 
            AcceptButton = BtnOK;
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            CancelButton = BtnCancel;
            ClientSize = new Size(820, 640);
            Controls.Add(ContentPanel);
            Controls.Add(FooterPanel);
            Controls.Add(TitlePanel);
            Font = VisionFlowUiStyle.DefaultFont;
            MinimumSize = new Size(720, 520);
            Name = "LDWeldingPluginEditor";
            StartPosition = FormStartPosition.CenterParent;
            Text = "LDWelding 插件配置";
            TitlePanel.ResumeLayout(false);
            ContentPanel.ResumeLayout(false);
            FooterPanel.ResumeLayout(false);
            ResumeLayout(false);
        }

        #endregion

        private Panel TitlePanel;
        private Label LblTitle;
        private Button BtnClose;
        private Panel ContentPanel;
        private TableLayoutPanel MainPanel;
        private Panel FooterPanel;
        private Button BtnCancel;
        private Button BtnOK;
    }
}
