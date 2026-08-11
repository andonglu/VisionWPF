using System.ComponentModel;
using System.Drawing;
using System.Windows.Forms;

namespace VisionFlow.Ui
{
    partial class FlowEditorForm
    {
        private IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }
            _inputImage?.Dispose();
            _runCts?.Dispose();
            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            components = new Container();
            var toolStrip = new ToolStrip();
            var mainLayout = new TableLayoutPanel();
            var toolboxBox = new GroupBox();
            var middleSplit = new SplitContainer();
            var flowBox = new GroupBox();
            var paramBox = new GroupBox();
            var rightSplit = new SplitContainer();
            var imageBox = new GroupBox();
            var tabs = new TabControl();
            var varPage = new TabPage();
            var logPage = new TabPage();
            _toolboxTree = new TreeView();
            _flowTree = new TreeView();
            _paramPanel = new TableLayoutPanel();
            _imageView = new VisionFlow.Controls.HalconImageView();
            _varList = new ListView();
            _logGrid = new DataGridView();
            ((ISupportInitialize)_logGrid).BeginInit();
            toolStrip.SuspendLayout();
            mainLayout.SuspendLayout();
            toolboxBox.SuspendLayout();
            ((ISupportInitialize)middleSplit).BeginInit();
            middleSplit.Panel1.SuspendLayout();
            middleSplit.Panel2.SuspendLayout();
            middleSplit.SuspendLayout();
            flowBox.SuspendLayout();
            paramBox.SuspendLayout();
            ((ISupportInitialize)rightSplit).BeginInit();
            rightSplit.Panel1.SuspendLayout();
            rightSplit.Panel2.SuspendLayout();
            rightSplit.SuspendLayout();
            imageBox.SuspendLayout();
            tabs.SuspendLayout();
            varPage.SuspendLayout();
            logPage.SuspendLayout();
            SuspendLayout();
            // 
            // toolStrip
            // 
            toolStrip.BackColor = Color.White;
            toolStrip.GripStyle = ToolStripGripStyle.Hidden;
            toolStrip.ImageScalingSize = new Size(28, 28);
            toolStrip.Padding = new Padding(8, 4, 8, 4);
            toolStrip.Dock = DockStyle.Top;
            toolStrip.Name = "toolStrip";
            toolStrip.Size = new Size(1600, 44);
            toolStrip.TabIndex = 0;
            _openImageButton = AddToolButton(toolStrip, "open.png", "打开图像", (s, e) => OpenInputImage());
            _saveFlowButton = AddToolButton(toolStrip, "save.png", "保存流程", (s, e) => SaveFlow());
            _loadFlowButton = AddToolButton(toolStrip, "open.png", "加载流程", (s, e) => LoadFlow());
            _validateButton = AddToolButton(toolStrip, null, "校验", (s, e) => ShowValidation());
            _runButton = AddToolButton(toolStrip, "run.png", "运行", (s, e) => RunFlow());
            _stopButton = AddToolButton(toolStrip, "stop.png", "停止", (s, e) => StopFlow());
            _stopButton.Enabled = false;
            toolStrip.Items.Add(new ToolStripSeparator());
            _copyButton = AddToolButton(toolStrip, null, "复制", (s, e) => CopySelectedNode());
            _cutButton = AddToolButton(toolStrip, null, "剪切", (s, e) => CutSelectedNode());
            _pasteButton = AddToolButton(toolStrip, null, "粘贴", (s, e) => PasteNode());
            _deleteButton = AddToolButton(toolStrip, "delete.png", "删除节点", (s, e) => DeleteSelected());
            _upButton = AddToolButton(toolStrip, "up.png", "上移节点", (s, e) => MoveSelected(-1));
            _downButton = AddToolButton(toolStrip, "down.png", "下移节点", (s, e) => MoveSelected(1));
            toolStrip.Items.Add(new ToolStripSeparator());
            _runStatusLabel = new ToolStripLabel("就绪");
            _runStatusLabel.Margin = new Padding(12, 1, 0, 2);
            toolStrip.Items.Add(_runStatusLabel);
            // 
            // mainLayout
            // 
            mainLayout.ColumnCount = 3;
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, VisionFlowUiStyle.MainToolboxWidth));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, VisionFlowUiStyle.MainFlowPercent));
            mainLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, VisionFlowUiStyle.MainImagePercent));
            mainLayout.Controls.Add(toolboxBox, 0, 0);
            mainLayout.Controls.Add(middleSplit, 1, 0);
            mainLayout.Controls.Add(rightSplit, 2, 0);
            mainLayout.Dock = DockStyle.Fill;
            mainLayout.Location = new Point(0, 44);
            mainLayout.Name = "mainLayout";
            mainLayout.Padding = new Padding(10);
            mainLayout.RowCount = 1;
            mainLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            mainLayout.Size = new Size(1600, 876);
            mainLayout.TabIndex = 1;
            // 
            // toolboxBox
            // 
            toolboxBox.Controls.Add(_toolboxTree);
            toolboxBox.Dock = DockStyle.Fill;
            toolboxBox.Name = "toolboxBox";
            toolboxBox.Text = "工具箱（双击或拖拽添加）";
            VisionFlowUiStyle.ApplyGroupBox(toolboxBox);
            // 
            // _toolboxTree
            // 
            _toolboxTree.Dock = DockStyle.Fill;
            _toolboxTree.HideSelection = false;
            _toolboxTree.ItemHeight = 26;
            _toolboxTree.Name = "_toolboxTree";
            _toolboxTree.NodeMouseDoubleClick += (s, e) => AddFromToolbox();
            _toolboxTree.ItemDrag += OnToolboxItemDrag;
            // 
            // middleSplit
            // 
            middleSplit.Dock = DockStyle.Fill;
            middleSplit.FixedPanel = FixedPanel.None;
            middleSplit.Location = new Point(273, 13);
            middleSplit.Name = "middleSplit";
            middleSplit.Orientation = Orientation.Horizontal;
            middleSplit.Panel1.Controls.Add(flowBox);
            middleSplit.Panel2.Controls.Add(paramBox);
            middleSplit.Panel1MinSize = 280;
            middleSplit.Panel2MinSize = 220;
            middleSplit.Size = new Size(548, 850);
            middleSplit.SplitterDistance = 520;
            middleSplit.SplitterWidth = 8;
            middleSplit.TabIndex = 1;
            // 
            // flowBox
            // 
            flowBox.Controls.Add(_flowTree);
            flowBox.Dock = DockStyle.Fill;
            flowBox.Name = "flowBox";
            flowBox.Text = "流程（支持拖入；双击工具打开配置页）";
            VisionFlowUiStyle.ApplyGroupBox(flowBox);
            // 
            // _flowTree
            // 
            _flowTree.AllowDrop = true;
            _flowTree.Dock = DockStyle.Fill;
            _flowTree.HideSelection = false;
            _flowTree.ItemHeight = 26;
            _flowTree.Name = "_flowTree";
            _flowTree.AfterSelect += (s, e) => OnFlowNodeSelected();
            _flowTree.NodeMouseDoubleClick += (s, e) => OpenToolEditPage();
            _flowTree.NodeMouseClick += OnFlowTreeNodeMouseClick;
            _flowTree.KeyDown += OnFlowTreeKeyDown;
            _flowTree.ItemDrag += OnFlowItemDrag;
            _flowTree.DragEnter += OnFlowDragEnter;
            _flowTree.DragOver += OnFlowDragOver;
            _flowTree.DragDrop += OnFlowDragDrop;
            _flowTree.ContextMenuStrip = BuildFlowTreeMenu();
            // 
            // paramBox
            // 
            paramBox.Controls.Add(_paramPanel);
            paramBox.Dock = DockStyle.Fill;
            paramBox.Name = "paramBox";
            paramBox.Text = "参数（引用项从上游输出中选择）";
            VisionFlowUiStyle.ApplyGroupBox(paramBox);
            // 
            // _paramPanel
            // 
            _paramPanel.AutoScroll = true;
            _paramPanel.AutoSize = true;
            _paramPanel.ColumnCount = 2;
            _paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, VisionFlowUiStyle.EditorLabelWidth));
            _paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            _paramPanel.Dock = DockStyle.Top;
            _paramPanel.Name = "_paramPanel";
            _paramPanel.Padding = new Padding(8);
            _paramPanel.RowCount = 1;
            // 
            // rightSplit
            // 
            rightSplit.Dock = DockStyle.Fill;
            rightSplit.Location = new Point(827, 13);
            rightSplit.Name = "rightSplit";
            rightSplit.Orientation = Orientation.Horizontal;
            rightSplit.Panel1.Controls.Add(imageBox);
            rightSplit.Panel2.Controls.Add(tabs);
            rightSplit.Panel1MinSize = 320;
            rightSplit.Panel2MinSize = 220;
            rightSplit.Size = new Size(760, 850);
            rightSplit.SplitterDistance = 540;
            rightSplit.SplitterWidth = 8;
            rightSplit.TabIndex = 2;
            // 
            // imageBox
            // 
            imageBox.Controls.Add(_imageView);
            imageBox.Dock = DockStyle.Fill;
            imageBox.Name = "imageBox";
            imageBox.Text = "图像（滚轮缩放，拖拽平移）";
            VisionFlowUiStyle.ApplyGroupBox(imageBox);
            // 
            // _imageView
            // 
            _imageView.Dock = DockStyle.Fill;
            _imageView.Name = "_imageView";
            // 
            // tabs
            // 
            tabs.Controls.Add(varPage);
            tabs.Controls.Add(logPage);
            tabs.Dock = DockStyle.Fill;
            tabs.Name = "tabs";
            tabs.SelectedIndex = 0;
            tabs.TabIndex = 0;
            // 
            // varPage
            // 
            varPage.Controls.Add(_varList);
            varPage.Location = new Point(4, 26);
            varPage.Name = "varPage";
            varPage.Padding = new Padding(4);
            varPage.Text = "变量";
            varPage.UseVisualStyleBackColor = true;
            // 
            // _varList
            // 
            _varList.Columns.Add("模块", 120);
            _varList.Columns.Add("名称", 130);
            _varList.Columns.Add("形态", 70);
            _varList.Columns.Add("类型", 80);
            _varList.Columns.Add("值", 420);
            _varList.Dock = DockStyle.Fill;
            _varList.FullRowSelect = true;
            _varList.GridLines = true;
            _varList.HideSelection = false;
            _varList.Name = "_varList";
            _varList.View = View.Details;
            // 
            // logPage
            // 
            logPage.Controls.Add(_logGrid);
            logPage.Location = new Point(4, 26);
            logPage.Name = "logPage";
            logPage.Padding = new Padding(4);
            logPage.Text = "日志";
            logPage.UseVisualStyleBackColor = true;
            // 
            // _logGrid
            // 
            _logGrid.AllowUserToAddRows = false;
            _logGrid.AllowUserToDeleteRows = false;
            _logGrid.AllowUserToResizeRows = false;
            _logGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
            _logGrid.Dock = DockStyle.Fill;
            _logGrid.Name = "_logGrid";
            _logGrid.ReadOnly = true;
            _logGrid.RowHeadersVisible = false;
            _logGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
            _logGrid.Columns.Add("Time", "时间");
            _logGrid.Columns.Add("Level", "级别");
            _logGrid.Columns.Add("Node", "节点");
            _logGrid.Columns.Add("Message", "消息");
            _logGrid.Columns.Add("Duration", "耗时");
            _logGrid.Columns.Add("ErrorCode", "错误码");
            _logGrid.Columns["Time"].FillWeight = 70;
            _logGrid.Columns["Level"].FillWeight = 45;
            _logGrid.Columns["Node"].FillWeight = 90;
            _logGrid.Columns["Message"].FillWeight = 260;
            _logGrid.Columns["Duration"].FillWeight = 55;
            _logGrid.Columns["ErrorCode"].FillWeight = 80;
            VisionFlowUiStyle.ApplyDataGridView(_logGrid);
            // 
            // FlowEditorForm
            // 
            AutoScaleDimensions = new SizeF(7F, 17F);
            AutoScaleMode = AutoScaleMode.Font;
            BackColor = VisionFlowUiStyle.PanelBackColor;
            ClientSize = new Size(1600, 920);
            Controls.Add(mainLayout);
            Controls.Add(toolStrip);
            Font = VisionFlowUiStyle.DefaultFont;
            MinimumSize = new Size(VisionFlowUiStyle.FormMinWidth, VisionFlowUiStyle.FormMinHeight);
            Name = "FlowEditorForm";
            StartPosition = FormStartPosition.CenterScreen;
            Text = "VisionFlow 流程编辑器";
            toolStrip.ResumeLayout(false);
            toolStrip.PerformLayout();
            mainLayout.ResumeLayout(false);
            toolboxBox.ResumeLayout(false);
            middleSplit.Panel1.ResumeLayout(false);
            middleSplit.Panel2.ResumeLayout(false);
            ((ISupportInitialize)middleSplit).EndInit();
            middleSplit.ResumeLayout(false);
            flowBox.ResumeLayout(false);
            paramBox.ResumeLayout(false);
            paramBox.PerformLayout();
            rightSplit.Panel1.ResumeLayout(false);
            rightSplit.Panel2.ResumeLayout(false);
            ((ISupportInitialize)rightSplit).EndInit();
            rightSplit.ResumeLayout(false);
            imageBox.ResumeLayout(false);
            tabs.ResumeLayout(false);
            varPage.ResumeLayout(false);
            logPage.ResumeLayout(false);
            ((ISupportInitialize)_logGrid).EndInit();
            ResumeLayout(false);
            PerformLayout();
        }

        #endregion
    }
}
