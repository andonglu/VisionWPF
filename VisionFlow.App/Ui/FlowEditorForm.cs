using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Controls;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Validation;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    /// <summary>
    /// 流程编辑器：三列布局（工具箱 | 流程树+参数 | 图像+结果）。
    /// 引用参数一律下拉选择（候选项来自上游输出声明），不使用 PropertyGrid。
    /// 双击工具节点打开该工具的专用配置页（ToolEditPageRegistry 注册）。
    /// </summary>
    public sealed partial class FlowEditorForm : Form
    {
        private const string ToolboxDragFormat = "VisionFlow.ToolboxItemId";
        private const string FlowNodeDragFormat = "VisionFlow.FlowNode";
        private const string FlowNodeClipboardPrefix = "VisionFlow.Node.Json:";

        /// <summary>流程树中 IfElse / For 的分支伪节点标记。</summary>
        private sealed class BranchRef
        {
            public IfElseNode IfElse;
            public bool IsIf;
            public ForLoopNode Loop;

            public FlowNode ParentNode
            {
                get { return IfElse != null ? (FlowNode)IfElse : Loop; }
            }

            public IfBranch Branch
            {
                get { return IsIf ? IfBranch.If : IfBranch.Else; }
            }
        }

        private readonly FlowEditModel _model = new FlowEditModel();
        private readonly IVisionFlowRuntime _runtime = new VisionFlowRuntime();
        private readonly Dictionary<FlowNode, TreeNode> _nodeMap = new Dictionary<FlowNode, TreeNode>();
        private bool _buildingPanel;
        private IReadOnlyList<PluginLoadResult> _pluginLoadResults;

        private TreeView _toolboxTree;
        private TreeView _flowTree;
        private TableLayoutPanel _paramPanel;
        private HalconImageView _imageView;
        private ListView _varList;
        private DataGridView _logGrid;
        private HObject _inputImage;
        private string _inputImagePath;
        private FlowContext _lastRunContext;
        private CancellationTokenSource _runCts;
        private string _copiedNodeJson;
        private ToolStripButton _openImageButton;
        private ToolStripButton _saveFlowButton;
        private ToolStripButton _loadFlowButton;
        private ToolStripButton _validateButton;
        private ToolStripButton _runButton;
        private ToolStripButton _stopButton;
        private ToolStripButton _copyButton;
        private ToolStripButton _cutButton;
        private ToolStripButton _pasteButton;
        private ToolStripButton _deleteButton;
        private ToolStripButton _upButton;
        private ToolStripButton _downButton;
        private ToolStripLabel _runStatusLabel;

        public FlowEditorForm()
        {
            ToolboxRegistry.RegisterDefaults();

            // 注册内置工具专用配置页（插件编辑页由 VisionFlowPluginLoader 自动注册）
            ToolEditPageRegistry.Register(typeof(HalconModelMatchTool),
                (tool, ctx) => new FrmMatchToolEdit((HalconModelMatchTool)tool, ctx));
            ToolEditPageRegistry.Register(typeof(EllipseFollowMeasureTool),
                (tool, ctx) => new FrmMeasureToolEdit((EllipseFollowMeasureTool)tool, ctx));
            ToolEditPageRegistry.Register(typeof(LineFollowMeasureTool),
                (tool, ctx) => new FrmFollowMeasureToolEdit((LineFollowMeasureTool)tool, ctx));
            ToolEditPageRegistry.Register(typeof(OneDCaliperFollowMeasureTool),
                (tool, ctx) => new FrmFollowMeasureToolEdit((OneDCaliperFollowMeasureTool)tool, ctx));
            ToolEditPageRegistry.Register(typeof(ArcCaliperFollowMeasureTool),
                (tool, ctx) => new FrmFollowMeasureToolEdit((ArcCaliperFollowMeasureTool)tool, ctx));
            ToolEditPageRegistry.Register(typeof(RectangleFollowMeasureTool),
                (tool, ctx) => new FrmFollowMeasureToolEdit((RectangleFollowMeasureTool)tool, ctx));
            ToolEditPageRegistry.Register(typeof(CircleFollowMeasureTool),
                (tool, ctx) => new FrmFollowMeasureToolEdit((CircleFollowMeasureTool)tool, ctx));
            ToolEditPageRegistry.Register(typeof(LoadImageTool),
                (tool, ctx) => new FrmLoadImageToolEdit((LoadImageTool)tool));
            RegisterVisualPreviewToolEditors();
            _pluginLoadResults = VisionFlowPluginLoader.LoadFromDefaultDirectory();

            InitializeComponent();
            BuildToolbox();
            WritePluginLoadLog();
            RefreshFlowTree();
        }

        private static void RegisterVisualPreviewToolEditors()
        {
            ToolEditPageRegistry.Register(typeof(MeanImageTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(AddSubImageTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(DecomposeChannelsTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(Compose3ImageTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(TransColorSpaceTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(ThresholdTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(RegionProcessTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(RegionDifferenceTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(RegionUnion2Tool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(RegionShapeTransTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(RegionUnion1Tool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(MorphologyRectTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(MorphologyCircleTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(SelectRegionTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(RegionFeaturesTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(ContourCreateTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(SelectContourTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(ConcatXldTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(SegmentXldTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(XldFeaturesTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(FitLineTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(FitCircleTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
            ToolEditPageRegistry.Register(typeof(IntersectionLinesTool),
                (tool, ctx) => new FrmRegionToolEdit(tool, ctx));
        }

        // ---------------- 界面构建 ----------------

        private void BuildLayout()
        {
            var toolStrip = new ToolStrip
            {
                GripStyle = ToolStripGripStyle.Hidden,
                ImageScalingSize = new Size(28, 28)
            };
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
            toolStrip.Items.Add(_runStatusLabel);
            toolStrip.Dock = DockStyle.Top;
            Controls.Add(toolStrip);

            var main = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 3,
                RowCount = 1
            };
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 200));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
            main.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
            Controls.Add(main);
            main.BringToFront();
            toolStrip.BringToFront();

            // 左：工具箱
            var toolboxBox = new GroupBox { Text = "工具箱（双击或拖拽添加）", Dock = DockStyle.Fill };
            _toolboxTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false };
            _toolboxTree.NodeMouseDoubleClick += (s, e) => AddFromToolbox();
            _toolboxTree.ItemDrag += OnToolboxItemDrag;
            toolboxBox.Controls.Add(_toolboxTree);
            main.Controls.Add(toolboxBox, 0, 0);

            // 中：流程树 + 参数面板
            var middle = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 480
            };
            var flowBox = new GroupBox { Text = "流程（支持从工具箱拖入；双击工具打开配置页）", Dock = DockStyle.Fill };
            _flowTree = new TreeView { Dock = DockStyle.Fill, HideSelection = false, AllowDrop = true };
            _flowTree.AfterSelect += (s, e) => OnFlowNodeSelected();
            _flowTree.NodeMouseDoubleClick += (s, e) => OpenToolEditPage();
            _flowTree.NodeMouseClick += OnFlowTreeNodeMouseClick;
            _flowTree.KeyDown += OnFlowTreeKeyDown;
            _flowTree.ItemDrag += OnFlowItemDrag;
            _flowTree.DragEnter += OnFlowDragEnter;
            _flowTree.DragOver += OnFlowDragOver;
            _flowTree.DragDrop += OnFlowDragDrop;
            _flowTree.ContextMenuStrip = BuildFlowTreeMenu();
            flowBox.Controls.Add(_flowTree);
            middle.Panel1.Controls.Add(flowBox);

            var paramBox = new GroupBox { Text = "参数（引用项从上游输出中选择）", Dock = DockStyle.Fill };
            _paramPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Top,
                AutoSize = true,
                ColumnCount = 2,
                Padding = new Padding(6)
            };
            _paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
            _paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            paramBox.Controls.Add(_paramPanel);
            middle.Panel2.Controls.Add(paramBox);
            main.Controls.Add(middle, 1, 0);

            // 右：图像 + 结果
            var right = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Horizontal,
                SplitterDistance = 480
            };
            var imageBox = new GroupBox { Text = "图像（滚轮缩放，拖拽平移）", Dock = DockStyle.Fill };
            _imageView = new HalconImageView { Dock = DockStyle.Fill };
            imageBox.Controls.Add(_imageView);
            right.Panel1.Controls.Add(imageBox);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            var varPage = new TabPage("变量");
            _varList = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true };
            _varList.Columns.Add("模块", 90);
            _varList.Columns.Add("名称", 100);
            _varList.Columns.Add("形态", 50);
            _varList.Columns.Add("类型", 60);
            _varList.Columns.Add("值", 300);
            varPage.Controls.Add(_varList);
            tabs.TabPages.Add(varPage);

            var logPage = new TabPage("日志");
            _logGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                AllowUserToResizeRows = false,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
                ReadOnly = true,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect
            };
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
            logPage.Controls.Add(_logGrid);
            tabs.TabPages.Add(logPage);
            right.Panel2.Controls.Add(tabs);
            main.Controls.Add(right, 2, 0);
        }

        private ToolStripButton AddToolButton(ToolStrip toolStrip, string iconName, string tooltip, EventHandler click)
        {
            var button = new ToolStripButton
            {
                DisplayStyle = iconName == null ? ToolStripItemDisplayStyle.Text : ToolStripItemDisplayStyle.Image,
                Text = iconName == null ? tooltip : string.Empty,
                ToolTipText = tooltip,
                Image = iconName == null ? null : LoadToolbarIcon(iconName)
            };
            button.Click += click;
            toolStrip.Items.Add(button);
            return button;
        }

        private ContextMenuStrip BuildFlowTreeMenu()
        {
            var menu = new ContextMenuStrip();
            AddMenuItem(menu, "复制", "Ctrl+C", (s, e) => CopySelectedNode());
            AddMenuItem(menu, "剪切", "Ctrl+X", (s, e) => CutSelectedNode());
            AddMenuItem(menu, "粘贴", "Ctrl+V", (s, e) => PasteNode());
            menu.Items.Add(new ToolStripSeparator());
            AddMenuItem(menu, "删除", "Del", (s, e) => DeleteSelected());
            return menu;
        }

        private static void AddMenuItem(ContextMenuStrip menu, string text, string shortcut, EventHandler click)
        {
            var item = new ToolStripMenuItem(text);
            item.ShortcutKeyDisplayString = shortcut;
            item.Click += click;
            menu.Items.Add(item);
        }

        private static Image LoadToolbarIcon(string iconName)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", iconName);
            if (File.Exists(path))
            {
                return Image.FromFile(path);
            }
            string sourcePath = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "Resources", iconName);
            return File.Exists(sourcePath) ? Image.FromFile(sourcePath) : null;
        }

        private void BuildToolbox()
        {
            _toolboxTree.Nodes.Clear();
            foreach (IGrouping<string, ToolboxItem> group in ToolboxRegistry.Items
                         .OrderBy(i => i.Category)
                         .ThenBy(i => i.DisplayName)
                         .GroupBy(i => i.Category))
            {
                var categoryNode = new TreeNode(group.Key);
                foreach (ToolboxItem item in group)
                {
                    categoryNode.Nodes.Add(new TreeNode(item.DisplayName) { Tag = item.Id });
                }
                _toolboxTree.Nodes.Add(categoryNode);
            }
            _toolboxTree.ExpandAll();
        }

        private void WritePluginLoadLog()
        {
            if (_pluginLoadResults == null || _pluginLoadResults.Count == 0)
            {
                return;
            }

            int toolCount = _pluginLoadResults.Sum(r => r.ToolCount);
            int editorCount = _pluginLoadResults.Sum(r => r.EditorCount);
            AddUiLog(FlowLogLevel.Info, $"[插件] 已加载工具 {toolCount} 个，编辑页 {editorCount} 个");
            foreach (PluginLoadResult result in _pluginLoadResults.Where(r => r.Errors.Count > 0))
            {
                foreach (string error in result.Errors)
                {
                    AddUiLog(FlowLogLevel.Warning, $"[插件] {Path.GetFileName(result.AssemblyPath)}：{error}");
                }
            }
        }

        // ---------------- 流程树 ----------------

        private void RefreshFlowTree()
        {
            _flowTree.BeginUpdate();
            _flowTree.Nodes.Clear();
            _nodeMap.Clear();
            var rootNode = new TreeNode(TextOf(_model.Root)) { Tag = _model.Root };
            _nodeMap[_model.Root] = rootNode;
            BuildChildren(rootNode, _model.Root);
            _flowTree.Nodes.Add(rootNode);
            _flowTree.ExpandAll();
            _flowTree.EndUpdate();
        }

        private void BuildChildren(TreeNode parent, FlowNode node)
        {
            if (node is SequenceNode sequence)
            {
                foreach (FlowNode child in sequence.Children)
                {
                    AddFlowNode(parent, child);
                }
            }
            else if (node is IfElseNode ifElse)
            {
                var ifNode = new TreeNode("If 分支") { Tag = new BranchRef { IfElse = ifElse, IsIf = true } };
                parent.Nodes.Add(ifNode);
                foreach (FlowNode child in ifElse.IfBranch)
                {
                    AddFlowNode(ifNode, child);
                }
                var elseNode = new TreeNode("Else 分支") { Tag = new BranchRef { IfElse = ifElse, IsIf = false } };
                parent.Nodes.Add(elseNode);
                foreach (FlowNode child in ifElse.ElseBranch)
                {
                    AddFlowNode(elseNode, child);
                }
            }
            else if (node is ForLoopNode loop)
            {
                var bodyNode = new TreeNode("循环体") { Tag = new BranchRef { Loop = loop } };
                parent.Nodes.Add(bodyNode);
                foreach (FlowNode child in loop.Body)
                {
                    AddFlowNode(bodyNode, child);
                }
            }
        }

        private void AddFlowNode(TreeNode parent, FlowNode node)
        {
            var treeNode = new TreeNode(TextOf(node)) { Tag = node };
            _nodeMap[node] = treeNode;
            parent.Nodes.Add(treeNode);
            BuildChildren(treeNode, node);
        }

        private static string TextOf(FlowNode node)
        {
            if (node is ToolNode toolNode)
            {
                return $"工具: {toolNode.Tool.ModuleName}";
            }
            if (node is IfElseNode)
            {
                return $"IfElse: {node.Name}";
            }
            if (node is ForLoopNode loop)
            {
                return $"For({(loop.Mode == ForLoopMode.Count ? "次数" : "集合")}): {node.Name}";
            }
            return node.Name;
        }

        private void OnToolboxItemDrag(object sender, ItemDragEventArgs e)
        {
            if (!((e.Item as TreeNode)?.Tag is string toolboxId))
            {
                return;
            }

            var data = new DataObject();
            data.SetData(ToolboxDragFormat, toolboxId);
            _toolboxTree.DoDragDrop(data, DragDropEffects.Copy);
        }

        private void OnFlowItemDrag(object sender, ItemDragEventArgs e)
        {
            if (!((e.Item as TreeNode)?.Tag is FlowNode node) || node == _model.Root)
            {
                return;
            }

            var data = new DataObject();
            data.SetData(FlowNodeDragFormat, node);
            _flowTree.DoDragDrop(data, DragDropEffects.Move);
        }

        private void OnFlowTreeNodeMouseClick(object sender, TreeNodeMouseClickEventArgs e)
        {
            if (e.Button == MouseButtons.Right)
            {
                _flowTree.SelectedNode = e.Node;
            }
        }

        private void OnFlowDragEnter(object sender, DragEventArgs e)
        {
            e.Effect = GetDragEffect(e);
        }

        private void OnFlowDragOver(object sender, DragEventArgs e)
        {
            Point point = _flowTree.PointToClient(new Point(e.X, e.Y));
            TreeNode treeNode = _flowTree.GetNodeAt(point);
            if (treeNode != null)
            {
                _flowTree.SelectedNode = treeNode;
            }

            if (!TryGetDropLocation(treeNode, point, out FlowNode target, out IfBranch branch, out FlowInsertPosition position))
            {
                e.Effect = DragDropEffects.None;
                return;
            }

            if (e.Data.GetDataPresent(ToolboxDragFormat))
            {
                e.Effect = DragDropEffects.Copy;
                return;
            }

            FlowNode dragged = e.Data.GetData(FlowNodeDragFormat) as FlowNode;
            e.Effect = _model.CanMoveNode(dragged, target, branch, position)
                ? DragDropEffects.Move
                : DragDropEffects.None;
        }

        private void OnFlowDragDrop(object sender, DragEventArgs e)
        {
            Point point = _flowTree.PointToClient(new Point(e.X, e.Y));
            TreeNode treeNode = _flowTree.GetNodeAt(point);
            if (!TryGetDropLocation(treeNode, point, out FlowNode target, out IfBranch branch, out FlowInsertPosition position))
            {
                return;
            }

            FlowNode selectedNode = null;
            if (e.Data.GetDataPresent(ToolboxDragFormat))
            {
                string toolboxId = e.Data.GetData(ToolboxDragFormat) as string;
                if (string.IsNullOrEmpty(toolboxId))
                {
                    return;
                }
                selectedNode = _model.InsertToolboxNode(toolboxId, target, branch, position);
            }
            else if (e.Data.GetDataPresent(FlowNodeDragFormat))
            {
                FlowNode dragged = e.Data.GetData(FlowNodeDragFormat) as FlowNode;
                if (!_model.MoveNode(dragged, target, branch, position))
                {
                    return;
                }
                selectedNode = dragged;
            }

            RefreshFlowTree();
            if (selectedNode != null && _nodeMap.TryGetValue(selectedNode, out TreeNode newTreeNode))
            {
                _flowTree.SelectedNode = newTreeNode;
            }
        }

        private static DragDropEffects GetDragEffect(DragEventArgs e)
        {
            if (e.Data.GetDataPresent(ToolboxDragFormat))
            {
                return DragDropEffects.Copy;
            }
            if (e.Data.GetDataPresent(FlowNodeDragFormat))
            {
                return DragDropEffects.Move;
            }
            return DragDropEffects.None;
        }

        private bool TryGetDropLocation(TreeNode treeNode, Point clientPoint, out FlowNode target,
            out IfBranch branch, out FlowInsertPosition position)
        {
            target = null;
            branch = IfBranch.If;
            position = FlowInsertPosition.Into;

            if (treeNode == null)
            {
                return true;
            }

            if (treeNode.Tag is BranchRef branchRef)
            {
                target = branchRef.ParentNode;
                branch = branchRef.Branch;
                position = FlowInsertPosition.Into;
                return true;
            }

            if (!(treeNode.Tag is FlowNode node))
            {
                return false;
            }

            target = node;
            if (node == _model.Root)
            {
                position = FlowInsertPosition.Into;
                return true;
            }

            Rectangle bounds = treeNode.Bounds;
            bool isContainer = FlowEditModel.GetChildList(node) != null;
            if (!isContainer)
            {
                position = clientPoint.Y < bounds.Top + bounds.Height / 2
                    ? FlowInsertPosition.Above
                    : FlowInsertPosition.Below;
                return true;
            }

            int third = Math.Max(1, bounds.Height / 3);
            if (clientPoint.Y < bounds.Top + third)
            {
                position = FlowInsertPosition.Above;
            }
            else if (clientPoint.Y > bounds.Bottom - third)
            {
                position = FlowInsertPosition.Below;
            }
            else
            {
                position = FlowInsertPosition.Into;
            }
            return true;
        }

        // ---------------- 编辑操作 ----------------


        private void AddFromToolbox()
        {
            if (!(_toolboxTree.SelectedNode?.Tag is string toolboxId))
            {
                return;
            }

            FlowNode target = null;
            IfBranch branch = IfBranch.If;
            if (_flowTree.SelectedNode?.Tag is BranchRef branchRef)
            {
                target = branchRef.ParentNode;
                branch = branchRef.Branch;
            }
            else if (_flowTree.SelectedNode?.Tag is FlowNode selected && selected != _model.Root)
            {
                target = selected;
            }

            FlowNode node = _model.AddNode(toolboxId, target, branch);
            RefreshFlowTree();
            if (_nodeMap.TryGetValue(node, out TreeNode treeNode))
            {
                _flowTree.SelectedNode = treeNode;
            }
        }

        private void DeleteSelected()
        {
            if (_flowTree.SelectedNode?.Tag is FlowNode node && node != _model.Root)
            {
                _model.RemoveNode(node);
                RefreshFlowTree();
            }
        }

        private void CopySelectedNode()
        {
            if (!TryGetEditableSelectedNode(out FlowNode node))
            {
                return;
            }

            _copiedNodeJson = FlowSerializer.SaveNode(node);
            try
            {
                Clipboard.SetText(FlowNodeClipboardPrefix + _copiedNodeJson);
            }
            catch (ExternalException ex)
            {
                AddUiLog(FlowLogLevel.Warning, $"[复制节点] 系统剪贴板不可用，仅保留应用内复制：{ex.Message}");
            }
            AddUiLog(FlowLogLevel.Info, $"[复制节点] {node.Name}");
        }

        private void CutSelectedNode()
        {
            if (!TryGetEditableSelectedNode(out FlowNode node))
            {
                return;
            }

            _copiedNodeJson = FlowSerializer.SaveNode(node);
            try
            {
                Clipboard.SetText(FlowNodeClipboardPrefix + _copiedNodeJson);
            }
            catch (ExternalException ex)
            {
                AddUiLog(FlowLogLevel.Warning, $"[剪切节点] 系统剪贴板不可用，仅保留应用内剪切：{ex.Message}");
            }
            _model.RemoveNode(node);
            RefreshFlowTree();
            AddUiLog(FlowLogLevel.Info, $"[剪切节点] {node.Name}");
        }

        private void PasteNode()
        {
            string json = ReadCopiedNodeJson();
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            FlowNode node;
            try
            {
                node = FlowSerializer.LoadNode(json, regenerateIds: true);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"粘贴节点失败：{ex.Message}", "粘贴失败",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            MakePastedNodeNamesUnique(node);
            ResolvePasteLocation(out FlowNode target, out IfBranch branch, out FlowInsertPosition position);
            if (!_model.InsertExistingNode(node, target, branch, position))
            {
                MessageBox.Show(this, "当前位置不能粘贴节点。", "粘贴失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            RefreshFlowTree();
            if (_nodeMap.TryGetValue(node, out TreeNode treeNode))
            {
                _flowTree.SelectedNode = treeNode;
            }
            AddUiLog(FlowLogLevel.Info, $"[粘贴节点] {node.Name}");
        }

        private string ReadCopiedNodeJson()
        {
            if (!string.IsNullOrWhiteSpace(_copiedNodeJson))
            {
                return _copiedNodeJson;
            }
            try
            {
                if (Clipboard.ContainsText())
                {
                    string text = Clipboard.GetText();
                    if (text.StartsWith(FlowNodeClipboardPrefix, StringComparison.Ordinal))
                    {
                        return text.Substring(FlowNodeClipboardPrefix.Length);
                    }
                }
            }
            catch (ExternalException ex)
            {
                AddUiLog(FlowLogLevel.Warning, $"[粘贴节点] 系统剪贴板不可用：{ex.Message}");
                return null;
            }
            return null;
        }

        private bool TryGetEditableSelectedNode(out FlowNode node)
        {
            node = _flowTree.SelectedNode?.Tag as FlowNode;
            return node != null && node != _model.Root;
        }

        private void ResolvePasteLocation(out FlowNode target, out IfBranch branch, out FlowInsertPosition position)
        {
            target = null;
            branch = IfBranch.If;
            position = FlowInsertPosition.Into;

            object tag = _flowTree.SelectedNode?.Tag;
            if (tag is BranchRef branchRef)
            {
                target = branchRef.ParentNode;
                branch = branchRef.Branch;
                position = FlowInsertPosition.Into;
                return;
            }

            if (tag is FlowNode selected)
            {
                target = selected;
                position = selected == _model.Root || FlowEditModel.GetChildList(selected) != null
                    ? FlowInsertPosition.Into
                    : FlowInsertPosition.Below;
            }
        }

        private void MakePastedNodeNamesUnique(FlowNode pasted)
        {
            var renameMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(
                EnumerateFlowNodes(_model.Root)
                    .Select(NamespaceOf)
                    .Where(name => !string.IsNullOrWhiteSpace(name)),
                StringComparer.OrdinalIgnoreCase);

            foreach (FlowNode node in EnumerateFlowNodes(pasted))
            {
                string oldName = NamespaceOf(node);
                if (string.IsNullOrWhiteSpace(oldName))
                {
                    continue;
                }

                string newName = UniqueName(oldName, used);
                used.Add(newName);
                if (!string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
                {
                    SetNamespace(node, newName);
                    renameMap[oldName] = newName;
                }
            }

            foreach (KeyValuePair<string, string> pair in renameMap)
            {
                UpdateReferences(pasted, pair.Key, pair.Value);
            }
        }

        private static string UniqueName(string baseName, HashSet<string> used)
        {
            if (!used.Contains(baseName))
            {
                return baseName;
            }

            int index = 1;
            string name;
            do
            {
                name = $"{baseName}_{index}";
                index++;
            } while (used.Contains(name));
            return name;
        }

        private static string NamespaceOf(FlowNode node)
        {
            if (node is ToolNode toolNode)
            {
                return toolNode.Tool.ModuleName;
            }
            if (node is IfElseNode ifElse && ifElse.Outputs.Count > 0)
            {
                return ifElse.Name;
            }
            if (node is FlowOutputNode outputNode && outputNode.Outputs.Count > 0)
            {
                return outputNode.Name;
            }
            return null;
        }

        private static void SetNamespace(FlowNode node, string name)
        {
            if (node is ToolNode toolNode)
            {
                toolNode.Tool.ModuleName = name;
                toolNode.Name = name;
            }
            else if (node is IfElseNode || node is FlowOutputNode)
            {
                node.Name = name;
            }
        }

        private void OnFlowTreeKeyDown(object sender, KeyEventArgs e)
        {
            if (_runCts != null)
            {
                return;
            }

            if (e.Control && e.KeyCode == Keys.C)
            {
                CopySelectedNode();
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.X)
            {
                CutSelectedNode();
                e.SuppressKeyPress = true;
            }
            else if (e.Control && e.KeyCode == Keys.V)
            {
                PasteNode();
                e.SuppressKeyPress = true;
            }
            else if (e.KeyCode == Keys.Delete)
            {
                DeleteSelected();
                e.SuppressKeyPress = true;
            }
        }

        private void MoveSelected(int delta)
        {
            if (_flowTree.SelectedNode?.Tag is FlowNode node && _model.MoveNode(node, delta))
            {
                RefreshFlowTree();
                if (_nodeMap.TryGetValue(node, out TreeNode treeNode))
                {
                    _flowTree.SelectedNode = treeNode;
                }
            }
        }

        private void OpenToolEditPage()
        {
            if (!(_flowTree.SelectedNode?.Tag is ToolNode toolNode))
            {
                return;
            }
            string oldModuleName = toolNode.Tool.ModuleName;
            var context = new ToolEditContext
            {
                Root = _model.Root,
                Node = toolNode,
                InputImage = _inputImage,
                InputImagePath = _inputImagePath,
                LastRunContext = _lastRunContext
            };
            if (ToolEditPageRegistry.TryCreate(toolNode.Tool, context, out Form page))
            {
                page.ShowDialog(this);
                page.Dispose();
                if (!string.Equals(oldModuleName, toolNode.Tool.ModuleName, StringComparison.OrdinalIgnoreCase))
                {
                    RenameToolModule(toolNode, toolNode.Tool.ModuleName, oldModuleName, setToolName: false);
                }
                RefreshFlowTree(); // 模块名可能被修改
            }
            else
            {
                page = new FrmGenericToolEdit(toolNode.Tool, context);
                page.ShowDialog(this);
                page.Dispose();
                if (!string.Equals(oldModuleName, toolNode.Tool.ModuleName, StringComparison.OrdinalIgnoreCase))
                {
                    RenameToolModule(toolNode, toolNode.Tool.ModuleName, oldModuleName, setToolName: false);
                }
                RefreshFlowTree();
            }
        }

        // ---------------- 参数面板（选择式，不用 PropertyGrid） ----------------

        private void OnFlowNodeSelected()
        {
            object tag = _flowTree.SelectedNode?.Tag;
            FlowNode node = tag as FlowNode;
            FlowNode contextNode = node ?? (tag as BranchRef)?.ParentNode;

            _buildingPanel = true;
            _paramPanel.Controls.Clear();
            _paramPanel.RowCount = 1;
            try
            {
                if (node is ToolNode toolNode)
                {
                    BuildToolRefPanel(toolNode);
                }
                else if (contextNode is IfElseNode ifElse)
                {
                    BuildConditionPanel(ifElse);
                }
                else if (contextNode is ForLoopNode loop)
                {
                    BuildLoopPanel(loop);
                }
                else if (contextNode is FlowOutputNode outputNode)
                {
                    BuildFlowOutputPanel(outputNode);
                }
                else
                {
                    AddPanelRow("提示", new Label { Text = "选中工具或逻辑节点后在此设置参数", ForeColor = Color.Gray, Dock = DockStyle.Fill });
                }
            }
            finally
            {
                _buildingPanel = false;
            }
            DisplaySelectedResult();
        }

        /// <summary>工具节点：按 [InputRef] 声明生成"上游输出下拉选择"行。</summary>
        private void BuildToolRefPanel(ToolNode toolNode)
        {
            ToolBase tool = toolNode.Tool;
            var nameBox = new TextBox { Dock = DockStyle.Fill, Text = tool.ModuleName };
            VisionFlowUiStyle.ApplyEditor(nameBox);
            nameBox.Leave += (s, e) => CommitModuleName(toolNode, nameBox, tool.ModuleName);
            nameBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    CommitModuleName(toolNode, nameBox, tool.ModuleName);
                    e.SuppressKeyPress = true;
                }
            };
            AddPanelRow("模块名", nameBox);

            var inputRefs = ToolMetadata.GetInputRefs(tool.GetType());
            if (inputRefs.Count == 0)
            {
                AddPanelRow("提示", new Label { Text = "该工具没有引用参数；双击节点打开配置页设置其他参数", ForeColor = Color.Gray, Dock = DockStyle.Fill });
                return;
            }

            foreach (ToolInputRefDef def in inputRefs)
            {
                var combo = new ComboBox { Dock = DockStyle.Fill };
                VisionFlowUiStyle.ApplyEditor(combo);
                if (def.Optional)
                {
                    combo.Items.Add(string.Empty);
                }
                if (def.ExpectedType == typeof(HalconImage) && _inputImage != null && _inputImage.IsInitialized())
                {
                    combo.Items.Add("Input.Image");
                }
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_model.Root, toolNode, def.ExpectedType))
                {
                    combo.Items.Add(candidate.Path);
                }
                combo.Text = def.Property.GetValue(tool) as string ?? string.Empty;

                ToolInputRefDef captured = def;
                combo.SelectedIndexChanged += (s, e) => CommitRef(combo, tool, captured);
                combo.Leave += (s, e) => CommitRef(combo, tool, captured);
                AddPanelRow(def.DisplayName, combo);
            }
            AddPanelRow(string.Empty, new Label { Text = "双击节点可打开该工具的专用配置页", ForeColor = Color.Gray, Dock = DockStyle.Fill });
        }

        private void CommitModuleName(ToolNode toolNode, TextBox textBox, string oldName)
        {
            if (_buildingPanel)
            {
                return;
            }
            if (!RenameToolModule(toolNode, textBox.Text, oldName, setToolName: true))
            {
                textBox.Text = toolNode.Tool.ModuleName;
                return;
            }
            RefreshFlowTree();
            if (_nodeMap.TryGetValue(toolNode, out TreeNode treeNode))
            {
                _flowTree.SelectedNode = treeNode;
            }
        }

        private bool RenameToolModule(ToolNode toolNode, string newName, string oldName, bool setToolName)
        {
            newName = (newName ?? string.Empty).Trim();
            oldName = (oldName ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(newName))
            {
                MessageBox.Show(this, "模块名不能为空。", "命名错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return false;
            }
            if (string.Equals(oldName, newName, StringComparison.OrdinalIgnoreCase))
            {
                toolNode.Tool.ModuleName = newName;
                toolNode.Name = newName;
                return true;
            }
            if (HasModuleName(newName, toolNode))
            {
                MessageBox.Show(this, $"模块名 '{newName}' 已存在，请换一个名称。", "命名冲突",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                toolNode.Tool.ModuleName = oldName;
                toolNode.Name = oldName;
                return false;
            }

            if (setToolName)
            {
                toolNode.Tool.ModuleName = newName;
            }
            toolNode.Name = newName;
            UpdateReferences(_model.Root, oldName, newName);
            return true;
        }

        private bool HasModuleName(string name, ToolNode except)
        {
            foreach (FlowNode node in EnumerateFlowNodes(_model.Root))
            {
                if (node is ToolNode toolNode && toolNode != except
                    && string.Equals(toolNode.Tool.ModuleName, name, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        private void UpdateReferences(FlowNode node, string oldModuleName, string newModuleName)
        {
            if (node is ToolNode toolNode)
            {
                foreach (ToolInputRefDef def in ToolMetadata.GetInputRefs(toolNode.Tool.GetType()))
                {
                    string current = def.Property.GetValue(toolNode.Tool) as string;
                    string updated = ReplaceReferenceModule(current, oldModuleName, newModuleName);
                    if (!string.Equals(current, updated, StringComparison.Ordinal))
                    {
                        def.Property.SetValue(toolNode.Tool, updated);
                    }
                }
            }
            else if (node is IfElseNode ifElse)
            {
                if (ifElse.Condition != null)
                {
                    ifElse.Condition.Left = ReplaceOperandReference(ifElse.Condition.Left, oldModuleName, newModuleName);
                    ifElse.Condition.Right = ReplaceOperandReference(ifElse.Condition.Right, oldModuleName, newModuleName);
                }
                foreach (BranchOutputDef output in ifElse.Outputs)
                {
                    output.IfValue = ReplaceOperandReference(output.IfValue, oldModuleName, newModuleName);
                    output.ElseValue = ReplaceOperandReference(output.ElseValue, oldModuleName, newModuleName);
                }
            }
            else if (node is ForLoopNode loop)
            {
                loop.CountSource = ReplaceOperandReference(loop.CountSource, oldModuleName, newModuleName);
                loop.ItemsPath = ReplaceReferenceModule(loop.ItemsPath, oldModuleName, newModuleName);
            }
            else if (node is FlowOutputNode outputNode)
            {
                foreach (FlowOutputDef output in outputNode.Outputs)
                {
                    output.Value = ReplaceOperandReference(output.Value, oldModuleName, newModuleName);
                }
            }

            foreach (FlowNode child in EnumerateChildNodes(node))
            {
                UpdateReferences(child, oldModuleName, newModuleName);
            }
        }

        private static Operand ReplaceOperandReference(Operand operand, string oldModuleName, string newModuleName)
        {
            if (operand == null || operand.IsConstant)
            {
                return operand;
            }
            string updated = ReplaceReferenceModule(operand.Reference.ToString(), oldModuleName, newModuleName);
            return string.Equals(updated, operand.Reference.ToString(), StringComparison.Ordinal)
                ? operand
                : Operand.Ref(updated);
        }

        private static string ReplaceReferenceModule(string reference, string oldModuleName, string newModuleName)
        {
            if (string.IsNullOrWhiteSpace(reference) || string.IsNullOrWhiteSpace(oldModuleName))
            {
                return reference;
            }
            string prefix = oldModuleName + ".";
            return reference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? newModuleName + reference.Substring(oldModuleName.Length)
                : reference;
        }

        private static IEnumerable<FlowNode> EnumerateFlowNodes(FlowNode node)
        {
            yield return node;
            foreach (FlowNode child in EnumerateChildNodes(node))
            {
                foreach (FlowNode nested in EnumerateFlowNodes(child))
                {
                    yield return nested;
                }
            }
        }

        private static IEnumerable<FlowNode> EnumerateChildNodes(FlowNode node)
        {
            if (node is SequenceNode sequence)
            {
                return sequence.Children;
            }
            if (node is IfElseNode ifElse)
            {
                return ifElse.IfBranch.Concat(ifElse.ElseBranch);
            }
            if (node is ForLoopNode loop)
            {
                return loop.Body;
            }
            return Enumerable.Empty<FlowNode>();
        }

        private void CommitRef(ComboBox combo, ToolBase tool, ToolInputRefDef def)
        {
            if (!_buildingPanel)
            {
                def.Property.SetValue(tool, string.IsNullOrWhiteSpace(combo.Text) ? null : combo.Text.Trim());
            }
        }

        /// <summary>IfElse：条件编辑（操作数可下拉选择上游输出，也可手输常量）。</summary>
        private void BuildConditionPanel(IfElseNode ifElse)
        {
            List<RefCandidate> candidates = RefCandidateService.ForNode(_model.Root, ifElse)
                .Where(c => !c.IsCollection).ToList();

            ComboBox left = AddOperandRow("左操作数", candidates, ifElse.Condition?.Left);
            var op = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            VisionFlowUiStyle.ApplyEditor(op);
            op.Items.AddRange(new object[] { "==", "!=", ">", ">=", "<", "<=" });
            op.SelectedItem = OpToString(ifElse.Condition?.Operator ?? ComparisonOperator.Equal);
            AddPanelRow("运算符", op);
            ComboBox right = AddOperandRow("右操作数", candidates, ifElse.Condition?.Right);

            EventHandler apply = (s, e) =>
            {
                if (_buildingPanel || op.SelectedItem == null)
                {
                    return;
                }
                ifElse.Condition = new ComparisonCondition
                {
                    Left = ParseOperand(left.Text),
                    Operator = StringToOp(op.SelectedItem.ToString()),
                    Right = ParseOperand(right.Text)
                };
            };
            left.SelectedIndexChanged += apply;
            left.Leave += apply;
            op.SelectedIndexChanged += apply;
            right.SelectedIndexChanged += apply;
            right.Leave += apply;

            var outputButton = new Button { Text = $"编辑公共输出（{ifElse.Outputs.Count}）", Dock = DockStyle.Fill };
            VisionFlowUiStyle.ApplyButton(outputButton);
            outputButton.Click += (s, e) =>
            {
                OpenBranchOutputsEditor(ifElse);
                OnFlowNodeSelected();
            };
            AddPanelRow("公共输出", outputButton);
        }

        private ComboBox AddOperandRow(string label, List<RefCandidate> candidates, Operand current)
        {
            var combo = new ComboBox { Dock = DockStyle.Fill };
            VisionFlowUiStyle.ApplyEditor(combo);
            foreach (RefCandidate candidate in candidates)
            {
                combo.Items.Add("ref:" + candidate.Path);
            }
            if (current != null)
            {
                combo.Text = current.IsConstant ? current.ConstantValue?.ToString() : "ref:" + current.Reference;
            }
            AddPanelRow(label, combo);
            return combo;
        }

        /// <summary>For 循环：Each 模式选集合源；Count 模式选次数来源（引用或常量）。</summary>
        private void BuildLoopPanel(ForLoopNode loop)
        {
            if (loop.Mode == ForLoopMode.Each)
            {
                var combo = new ComboBox { Dock = DockStyle.Fill };
                VisionFlowUiStyle.ApplyEditor(combo);
                foreach (RefCandidate candidate in RefCandidateService.Collections(_model.Root, loop))
                {
                    combo.Items.Add(candidate.Path);
                }
                combo.Text = loop.ItemsPath ?? string.Empty;
                EventHandler commit = (s, e) =>
                {
                    if (!_buildingPanel)
                    {
                        loop.ItemsPath = combo.Text.Trim();
                    }
                };
                combo.SelectedIndexChanged += commit;
                combo.Leave += commit;
                AddPanelRow("循环源", combo);
            }
            else
            {
                List<RefCandidate> candidates = RefCandidateService.ForInput(_model.Root, loop, typeof(int));
                var combo = new ComboBox { Dock = DockStyle.Fill };
                VisionFlowUiStyle.ApplyEditor(combo);
                foreach (RefCandidate candidate in candidates)
                {
                    combo.Items.Add("ref:" + candidate.Path);
                }
                if (loop.CountSource != null)
                {
                    combo.Text = loop.CountSource.IsConstant
                        ? loop.CountSource.ConstantValue?.ToString()
                        : "ref:" + loop.CountSource.Reference;
                }
                EventHandler commit = (s, e) =>
                {
                    if (!_buildingPanel)
                    {
                        loop.CountSource = ParseOperand(combo.Text);
                    }
                };
                combo.SelectedIndexChanged += commit;
                combo.Leave += commit;
                AddPanelRow("次数", combo);
            }
        }

        private void BuildFlowOutputPanel(FlowOutputNode outputNode)
        {
            AddPanelRow("名称", new Label { Text = outputNode.Name, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft });
            var button = new Button { Text = $"编辑输出（{outputNode.Outputs.Count}）", Dock = DockStyle.Fill };
            VisionFlowUiStyle.ApplyButton(button);
            button.Click += (s, e) =>
            {
                OpenFlowOutputsEditor(outputNode);
                OnFlowNodeSelected();
            };
            AddPanelRow("输出", button);
            AddPanelRow("说明", new Label
            {
                Text = "建议把流程最终 Ok/Code/Message/坐标等结果统一配置在这里",
                ForeColor = Color.Gray,
                Dock = DockStyle.Fill
            });
        }

        private void AddPanelRow(string label, Control control)
        {
            int row = _paramPanel.RowCount++;
            _paramPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, VisionFlowUiStyle.EditorRowHeight));
            VisionFlowUiStyle.ApplyEditor(control);
            _paramPanel.Controls.Add(new Label
            {
                Text = label,
                Dock = DockStyle.Fill,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 8, 0)
            }, 0, row);
            _paramPanel.Controls.Add(control, 1, row);
        }

        /// <summary>"ref:模块.变量" 解析为变量引用，其余按 数字/布尔/字符串 常量解析。</summary>
        internal static Operand ParseOperand(string text)
        {
            text = (text ?? string.Empty).Trim();
            if (text.StartsWith("ref:", StringComparison.OrdinalIgnoreCase))
            {
                return Operand.Ref(text.Substring(4).Trim());
            }
            if (string.Equals(text, "null", StringComparison.OrdinalIgnoreCase))
            {
                return Operand.Const(null);
            }
            if (text == "\"\"")
            {
                return Operand.Const(string.Empty);
            }
            if (double.TryParse(text, out double d))
            {
                return Operand.Const(d);
            }
            if (bool.TryParse(text, out bool b))
            {
                return Operand.Const(b);
            }
            return Operand.Const(text);
        }

        private static string OpToString(ComparisonOperator op)
        {
            switch (op)
            {
                case ComparisonOperator.Equal: return "==";
                case ComparisonOperator.NotEqual: return "!=";
                case ComparisonOperator.Greater: return ">";
                case ComparisonOperator.GreaterOrEqual: return ">=";
                case ComparisonOperator.Less: return "<";
                default: return "<=";
            }
        }

        private static ComparisonOperator StringToOp(string text)
        {
            switch (text)
            {
                case "==": return ComparisonOperator.Equal;
                case "!=": return ComparisonOperator.NotEqual;
                case ">": return ComparisonOperator.Greater;
                case ">=": return ComparisonOperator.GreaterOrEqual;
                case "<": return ComparisonOperator.Less;
                default: return ComparisonOperator.LessOrEqual;
            }
        }

        // ---------------- 运行与结果 ----------------

        private async void RunFlow()
        {
            if (_runCts != null)
            {
                return;
            }

            FlowValidationResult validation = FlowValidator.Validate(_model.Root);
            if (!validation.IsValid)
            {
                ShowValidationResult(validation, "流程校验失败，已阻止运行。");
                return;
            }

            foreach (TreeNode node in _nodeMap.Values)
            {
                node.ForeColor = Color.Black;
            }

            _runCts = new CancellationTokenSource();
            SetRunningState(true);
            var progress = new Progress<FlowProgress>(OnFlowProgress);
            FlowRunResult result = null;
            try
            {
                var initialContext = new FlowContext();
                if (_inputImage != null && _inputImage.IsInitialized())
                {
                    initialContext.SetVariable(Variable.Object("Input", "Image", new HalconImage(_inputImage), 1));
                    initialContext.AddLog(FlowLogLevel.Info, string.IsNullOrEmpty(_inputImagePath)
                        ? "[输入] Input.Image"
                        : $"[输入] Input.Image = {_inputImagePath}");
                }
                result = await _runtime.RunAsync(_model.Root, initialContext, _runCts.Token, progress);
            }
            catch (OperationCanceledException)
            {
                _runStatusLabel.Text = "已取消";
            }
            catch (Exception ex)
            {
                _runStatusLabel.Text = "运行异常";
                MessageBox.Show($"流程运行异常：{ex.Message}", "运行结果", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            finally
            {
                _runCts?.Dispose();
                _runCts = null;
                SetRunningState(false);
            }

            if (result == null)
            {
                return;
            }

            FillVariables(result.Context);
            FillLog(result.Context);
            _lastRunContext = result.Context;
            DisplaySelectedResult();

            if (!result.IsSuccess && result.Status != NodeStatus.Skipped)
            {
                if (!string.IsNullOrWhiteSpace(result.FailedNodeId))
                {
                    SelectNodeById(result.FailedNodeId);
                }
                MessageBox.Show(
                    $"流程失败：{result.Message}\r\n错误码：{result.ErrorCode ?? "UNKNOWN"}\r\n节点：{result.FailedNodeName ?? "-"}",
                    "运行结果", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void StopFlow()
        {
            _runCts?.Cancel();
            _runStatusLabel.Text = "正在停止...";
            _stopButton.Enabled = false;
        }

        private void SetRunningState(bool running)
        {
            Cursor = running ? Cursors.WaitCursor : Cursors.Default;
            _openImageButton.Enabled = !running;
            _saveFlowButton.Enabled = !running;
            _loadFlowButton.Enabled = !running;
            _validateButton.Enabled = !running;
            _runButton.Enabled = !running;
            _stopButton.Enabled = running;
            _copyButton.Enabled = !running;
            _cutButton.Enabled = !running;
            _pasteButton.Enabled = !running;
            _deleteButton.Enabled = !running;
            _upButton.Enabled = !running;
            _downButton.Enabled = !running;
            _toolboxTree.Enabled = !running;
            _flowTree.Enabled = !running;
            _flowTree.AllowDrop = !running;
            _paramPanel.Enabled = !running;
            if (running)
            {
                _runStatusLabel.Text = "正在运行...";
            }
        }

        private void OnFlowProgress(FlowProgress progress)
        {
            if (!string.IsNullOrEmpty(progress.NodeId))
            {
                SelectNodeColor(progress.NodeId, ColorForProgress(progress.Kind));
            }
            switch (progress.Kind)
            {
                case FlowProgressKind.NodeStarted:
                    _runStatusLabel.Text = $"正在执行：{progress.NodeName}";
                    SelectNodeById(progress.NodeId);
                    break;
                case FlowProgressKind.NodeCompleted:
                    _runStatusLabel.Text = $"{progress.NodeName} 完成（{progress.Duration?.TotalMilliseconds:F0} ms）";
                    break;
                case FlowProgressKind.NodeFailed:
                    _runStatusLabel.Text = $"{progress.NodeName} 失败";
                    SelectNodeById(progress.NodeId);
                    break;
                case FlowProgressKind.FlowCompleted:
                    _runStatusLabel.Text = progress.Status == NodeStatus.Success
                        ? $"运行完成（{progress.Duration?.TotalMilliseconds:F0} ms）"
                        : $"运行失败（{progress.Duration?.TotalMilliseconds:F0} ms）";
                    break;
                case FlowProgressKind.FlowCancelled:
                    _runStatusLabel.Text = "已取消";
                    break;
            }
        }

        private void SelectNodeColor(string nodeId, Color color)
        {
            foreach (KeyValuePair<FlowNode, TreeNode> pair in _nodeMap)
            {
                if (pair.Key.Id == nodeId)
                {
                    pair.Value.ForeColor = color;
                    return;
                }
            }
        }

        private static Color ColorForProgress(FlowProgressKind kind)
        {
            switch (kind)
            {
                case FlowProgressKind.NodeStarted: return Color.RoyalBlue;
                case FlowProgressKind.NodeCompleted: return Color.DarkGreen;
                case FlowProgressKind.NodeFailed: return Color.Red;
                case FlowProgressKind.FlowCancelled: return Color.DarkOrange;
                default: return Color.Black;
            }
        }

        private void OpenInputImage()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                HOperatorSet.ReadImage(out HObject image, dialog.FileName);
                _inputImage?.Dispose();
                _inputImage = image;
                _inputImagePath = dialog.FileName;

                _imageView.ShowImage(_inputImage);
                _imageView.ClearOverlay();
                AddUiLog(FlowLogLevel.Info, $"[打开图像] {dialog.FileName}");
            }
        }

        private void SaveFlow()
        {
            using (var dialog = new SaveFileDialog
            {
                Filter = "VisionFlow 流程|*.vflow.json|JSON 文件|*.json|所有文件|*.*",
                FileName = "flow.vflow.json",
                DefaultExt = "vflow.json",
                AddExtension = true
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                string fileName = NormalizeFlowFileName(dialog.FileName);
                File.WriteAllText(fileName, FlowSerializer.Save(_model.Root));
                AddUiLog(FlowLogLevel.Info, $"[保存流程] {fileName}");
            }
        }

        private static string NormalizeFlowFileName(string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName))
            {
                return fileName;
            }
            if (fileName.EndsWith(".vflow.vflow.json", StringComparison.OrdinalIgnoreCase))
            {
                return fileName.Substring(0, fileName.Length - ".vflow.vflow.json".Length) + ".vflow.json";
            }
            if (fileName.EndsWith(".vflow", StringComparison.OrdinalIgnoreCase))
            {
                return fileName + ".json";
            }
            return fileName;
        }

        private void LoadFlow()
        {
            using (var dialog = new OpenFileDialog
            {
                Filter = "VisionFlow 流程|*.vflow.json;*.json|所有文件|*.*"
            })
            {
                if (dialog.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }
                SequenceNode root = FlowSerializer.Load(File.ReadAllText(dialog.FileName));
                _model.ReplaceRoot(root);
                RefreshFlowTree();
                AddUiLog(FlowLogLevel.Info, $"[加载流程] {dialog.FileName}");
                FlowValidationResult validation = FlowValidator.Validate(_model.Root);
                if (!validation.IsValid)
                {
                    ShowValidationResult(validation, "流程已加载，但校验发现问题。");
                }
            }
        }

        private void ShowValidation()
        {
            FlowValidationResult validation = FlowValidator.Validate(_model.Root);
            ShowValidationResult(validation, validation.IsValid ? "流程校验通过。" : "流程校验发现问题。");
        }

        private void ShowValidationResult(FlowValidationResult validation, string title)
        {
            FlowValidationIssue firstError = validation.Issues.FirstOrDefault(i => i.Severity == FlowValidationSeverity.Error);
            if (firstError != null)
            {
                SelectNodeById(firstError.NodeId);
            }

            string message = validation.Issues.Count == 0
                ? title
                : title + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, validation.Issues.Take(20).Select(i => i.ToString()));
            if (validation.Issues.Count > 20)
            {
                message += Environment.NewLine + $"... 还有 {validation.Issues.Count - 20} 条";
            }
            MessageBox.Show(message, "流程校验", MessageBoxButtons.OK,
                validation.IsValid ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }

        private void SelectNodeById(string nodeId)
        {
            if (string.IsNullOrEmpty(nodeId))
            {
                return;
            }
            foreach (KeyValuePair<FlowNode, TreeNode> pair in _nodeMap)
            {
                if (pair.Key.Id == nodeId)
                {
                    _flowTree.SelectedNode = pair.Value;
                    pair.Value.EnsureVisible();
                    return;
                }
            }
        }

        private void OpenBranchOutputsEditor(IfElseNode ifElse)
        {
            using (var form = new Form
            {
                Text = "IfElse 公共输出",
                StartPosition = FormStartPosition.CenterParent
            })
            {
                VisionFlowUiStyle.ApplyDialog(form, 1040, 620);
                var grid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    AllowUserToAddRows = true,
                    AllowUserToDeleteRows = true,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
                };
                VisionFlowUiStyle.ApplyDataGridView(grid);
                grid.Columns.Add("Name", "输出名");
                grid.Columns.Add(CreateValueColumn("IfValue", "If分支输出/常量", BuildBranchValueItems(ifElse, IfBranch.If)));
                grid.Columns.Add(CreateValueColumn("ElseValue", "Else分支输出/常量", BuildBranchValueItems(ifElse, IfBranch.Else)));
                grid.Columns.Add("Kind", "推断Kind");
                grid.Columns.Add("Type", "推断Type");
                grid.Columns.Add("ClrTypeName", "推断CLR类型");
                grid.Columns["Kind"].ReadOnly = true;
                grid.Columns["Type"].ReadOnly = true;
                grid.Columns["ClrTypeName"].ReadOnly = true;
                grid.DataError += (s, e) => { e.ThrowException = false; };

                foreach (BranchOutputDef output in ifElse.Outputs)
                {
                    EnsureComboItem(grid.Columns["IfValue"] as DataGridViewComboBoxColumn, OperandText(output.IfValue));
                    EnsureComboItem(grid.Columns["ElseValue"] as DataGridViewComboBoxColumn, OperandText(output.ElseValue));
                    grid.Rows.Add(output.Name, OperandText(output.IfValue), OperandText(output.ElseValue),
                        output.Kind.ToString(), output.Type.ToString(), output.ClrTypeName);
                }
                grid.CellValueChanged += (s, e) => RefreshOutputTypeCells(grid, ifElse, e.RowIndex);
                grid.CurrentCellDirtyStateChanged += (s, e) =>
                {
                    if (grid.IsCurrentCellDirty)
                    {
                        grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    }
                };
                grid.RowsAdded += (s, e) =>
                {
                    for (int i = e.RowIndex; i < e.RowIndex + e.RowCount; i++)
                    {
                        RefreshOutputTypeCells(grid, ifElse, i);
                    }
                };

                var hint = new TextBox
                {
                    Dock = DockStyle.Top,
                    Height = 80,
                    Multiline = true,
                    ReadOnly = true,
                    Text = BuildBranchOutputHint(ifElse)
                };

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = VisionFlowUiStyle.FooterHeight, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 16, 8) };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
                VisionFlowUiStyle.ApplyButton(ok);
                VisionFlowUiStyle.ApplyButton(cancel, false);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                form.Controls.Add(grid);
                form.Controls.Add(hint);
                form.Controls.Add(buttons);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                var outputs = new List<BranchOutputDef>();
                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (row.IsNewRow)
                    {
                        continue;
                    }
                    string name = CellText(row, 0);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    if (!TryInferBranchOutput(row, ifElse, out BranchOutputDef inferred, out string error))
                    {
                        MessageBox.Show(this, $"{name}：{error}", "公共输出配置错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    inferred.Name = name.Trim();
                    outputs.Add(inferred);
                }
                ifElse.Outputs.Clear();
                ifElse.Outputs.AddRange(outputs);
            }
        }

        private void OpenFlowOutputsEditor(FlowOutputNode outputNode)
        {
            using (var form = new Form
            {
                Text = "流程输出",
                StartPosition = FormStartPosition.CenterParent
            })
            {
                VisionFlowUiStyle.ApplyDialog(form, 1000, 560);
                var grid = new DataGridView
                {
                    Dock = DockStyle.Fill,
                    AllowUserToAddRows = true,
                    AllowUserToDeleteRows = true,
                    AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
                };
                VisionFlowUiStyle.ApplyDataGridView(grid);
                grid.Columns.Add("Name", "输出名");
                grid.Columns.Add(CreateValueColumn("Value", "取值(ref:...或常量)", BuildFlowOutputValueItems(outputNode)));
                grid.Columns.Add("Kind", "推断Kind");
                grid.Columns.Add("Type", "推断Type");
                grid.Columns.Add("ClrTypeName", "推断CLR类型");
                grid.Columns["Kind"].ReadOnly = true;
                grid.Columns["Type"].ReadOnly = true;
                grid.Columns["ClrTypeName"].ReadOnly = true;
                grid.DataError += (s, e) => { e.ThrowException = false; };

                foreach (FlowOutputDef output in outputNode.Outputs)
                {
                    EnsureComboItem(grid.Columns["Value"] as DataGridViewComboBoxColumn, OperandText(output.Value));
                    grid.Rows.Add(output.Name, OperandText(output.Value),
                        output.Kind.ToString(), output.Type.ToString(), output.ClrTypeName);
                }

                grid.CellValueChanged += (s, e) => RefreshFlowOutputTypeCells(grid, outputNode, e.RowIndex);
                grid.CurrentCellDirtyStateChanged += (s, e) =>
                {
                    if (grid.IsCurrentCellDirty)
                    {
                        grid.CommitEdit(DataGridViewDataErrorContexts.Commit);
                    }
                };

                var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = VisionFlowUiStyle.FooterHeight, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 16, 8) };
                var ok = new Button { Text = "确定", DialogResult = DialogResult.OK };
                var cancel = new Button { Text = "取消", DialogResult = DialogResult.Cancel };
                VisionFlowUiStyle.ApplyButton(ok);
                VisionFlowUiStyle.ApplyButton(cancel, false);
                buttons.Controls.Add(ok);
                buttons.Controls.Add(cancel);
                form.Controls.Add(grid);
                form.Controls.Add(buttons);
                form.AcceptButton = ok;
                form.CancelButton = cancel;

                if (form.ShowDialog(this) != DialogResult.OK)
                {
                    return;
                }

                var outputs = new List<FlowOutputDef>();
                foreach (DataGridViewRow row in grid.Rows)
                {
                    if (row.IsNewRow)
                    {
                        continue;
                    }
                    string name = CellText(row, 0);
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }
                    if (!TryInferFlowOutput(row, outputNode, out FlowOutputDef inferred, out string error))
                    {
                        MessageBox.Show(this, $"{name}：{error}", "流程输出配置错误",
                            MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        return;
                    }
                    inferred.Name = name.Trim();
                    outputs.Add(inferred);
                }

                outputNode.Outputs.Clear();
                outputNode.Outputs.AddRange(outputs);
            }
        }

        private List<string> BuildFlowOutputValueItems(FlowOutputNode outputNode)
        {
            var items = new List<string> { "null", "true", "false", "0", "\"\"" };
            foreach (RefCandidate candidate in RefCandidateService.ForNode(_model.Root, outputNode))
            {
                items.Add("ref:" + candidate.Path);
            }
            return items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void RefreshFlowOutputTypeCells(DataGridView grid, FlowOutputNode outputNode, int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count || grid.Rows[rowIndex].IsNewRow)
            {
                return;
            }
            if (TryInferFlowOutput(grid.Rows[rowIndex], outputNode, out FlowOutputDef output, out _))
            {
                grid.Rows[rowIndex].Cells["Kind"].Value = output.Kind.ToString();
                grid.Rows[rowIndex].Cells["Type"].Value = output.Type.ToString();
                grid.Rows[rowIndex].Cells["ClrTypeName"].Value = output.ClrTypeName;
            }
        }

        private bool TryInferFlowOutput(DataGridViewRow row, FlowOutputNode outputNode, out FlowOutputDef output, out string error)
        {
            output = new FlowOutputDef { Value = ParseOperand(CellText(row, "Value")) };
            error = null;

            ValueShape shape = ShapeOf(output.Value, RefCandidateService.ForNode(_model.Root, outputNode));
            if (shape.IsNull)
            {
                shape = new ValueShape { Kind = VariableKind.Object, Type = VariableType.Object, ClrType = typeof(object) };
            }
            output.Kind = shape.Kind;
            output.Type = shape.Type;
            output.ClrTypeName = shape.ClrType == null || shape.ClrType == typeof(object) ? null : shape.ClrType.AssemblyQualifiedName;
            return true;
        }

        private static DataGridViewComboBoxColumn CreateValueColumn(string name, string header, List<string> items)
        {
            var column = new DataGridViewComboBoxColumn
            {
                Name = name,
                HeaderText = header,
                FlatStyle = FlatStyle.Flat
            };
            foreach (string item in items)
            {
                column.Items.Add(item);
            }
            return column;
        }

        private static void EnsureComboItem(DataGridViewComboBoxColumn column, string value)
        {
            if (column == null || string.IsNullOrEmpty(value))
            {
                return;
            }
            foreach (object item in column.Items)
            {
                if (string.Equals(item?.ToString(), value, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }
            column.Items.Add(value);
        }

        private List<string> BuildBranchValueItems(IfElseNode ifElse, IfBranch branch)
        {
            var items = new List<string> { "null", "true", "false", "0", "\"\"" };
            foreach (RefCandidate candidate in RefCandidateService.ForBranchOutput(_model.Root, ifElse, branch))
            {
                items.Add("ref:" + candidate.Path);
            }
            return items.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        private void RefreshOutputTypeCells(DataGridView grid, IfElseNode ifElse, int rowIndex)
        {
            if (rowIndex < 0 || rowIndex >= grid.Rows.Count || grid.Rows[rowIndex].IsNewRow)
            {
                return;
            }
            if (TryInferBranchOutput(grid.Rows[rowIndex], ifElse, out BranchOutputDef output, out _))
            {
                grid.Rows[rowIndex].Cells["Kind"].Value = output.Kind.ToString();
                grid.Rows[rowIndex].Cells["Type"].Value = output.Type.ToString();
                grid.Rows[rowIndex].Cells["ClrTypeName"].Value = output.ClrTypeName;
            }
        }

        private bool TryInferBranchOutput(DataGridViewRow row, IfElseNode ifElse, out BranchOutputDef output, out string error)
        {
            output = new BranchOutputDef
            {
                IfValue = ParseOperand(CellText(row, "IfValue")),
                ElseValue = ParseOperand(CellText(row, "ElseValue"))
            };
            error = null;

            ValueShape ifShape = ShapeOf(output.IfValue, RefCandidateService.ForBranchOutput(_model.Root, ifElse, IfBranch.If));
            ValueShape elseShape = ShapeOf(output.ElseValue, RefCandidateService.ForBranchOutput(_model.Root, ifElse, IfBranch.Else));
            ValueShape shape = !ifShape.IsNull ? ifShape : elseShape;
            if (shape.IsNull)
            {
                shape = new ValueShape { Kind = VariableKind.Object, Type = VariableType.Object, ClrType = typeof(object) };
            }
            if (!elseShape.IsNull && !AreCompatible(shape, elseShape))
            {
                error = $"If取值类型是 {DescribeShape(shape)}，Else取值类型是 {DescribeShape(elseShape)}，两侧类型不一致。";
                return false;
            }
            if (!ifShape.IsNull && !AreCompatible(shape, ifShape))
            {
                error = $"If取值类型是 {DescribeShape(ifShape)}，Else取值类型是 {DescribeShape(shape)}，两侧类型不一致。";
                return false;
            }

            output.Kind = shape.Kind;
            output.Type = shape.Type;
            output.ClrTypeName = shape.ClrType == null || shape.ClrType == typeof(object) ? null : shape.ClrType.AssemblyQualifiedName;
            return true;
        }

        private ValueShape ShapeOf(Operand operand, List<RefCandidate> candidates)
        {
            if (operand == null || operand.IsConstant)
            {
                object value = operand?.ConstantValue;
                if (value == null)
                {
                    return new ValueShape { IsNull = true };
                }
                if (value is bool) return new ValueShape { Kind = VariableKind.Single, Type = VariableType.Bool, ClrType = typeof(bool) };
                if (value is string) return new ValueShape { Kind = VariableKind.Single, Type = VariableType.String, ClrType = typeof(string) };
                if (value is int) return new ValueShape { Kind = VariableKind.Single, Type = VariableType.Int, ClrType = typeof(int) };
                if (value is double) return new ValueShape { Kind = VariableKind.Single, Type = VariableType.Double, ClrType = typeof(double) };
                return new ValueShape { Kind = VariableKind.Object, Type = VariableType.Object, ClrType = value.GetType() };
            }

            string path = operand.Reference.ToString();
            RefCandidate candidate = candidates.FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase));
            if (candidate == null)
            {
                return new ValueShape { Kind = VariableKind.Object, Type = VariableType.Object, ClrType = typeof(object) };
            }
            return new ValueShape
            {
                Kind = candidate.IsCollection ? VariableKind.Array : (IsScalarType(candidate.ClrType) ? VariableKind.Single : VariableKind.Object),
                Type = VariableTypeOf(candidate.ClrType),
                ClrType = candidate.ClrType
            };
        }

        private static bool AreCompatible(ValueShape left, ValueShape right)
        {
            if (left.IsNull || right.IsNull)
            {
                return true;
            }
            if (left.Kind != right.Kind || left.Type != right.Type)
            {
                return false;
            }
            if (left.ClrType == null || right.ClrType == null || left.ClrType == typeof(object) || right.ClrType == typeof(object))
            {
                return true;
            }
            return left.ClrType == right.ClrType || left.ClrType.IsAssignableFrom(right.ClrType) || right.ClrType.IsAssignableFrom(left.ClrType);
        }

        private static bool IsScalarType(Type type)
        {
            return type == typeof(int) || type == typeof(double) || type == typeof(string) || type == typeof(bool);
        }

        private static VariableType VariableTypeOf(Type type)
        {
            if (type == typeof(int)) return VariableType.Int;
            if (type == typeof(double)) return VariableType.Double;
            if (type == typeof(string)) return VariableType.String;
            if (type == typeof(bool)) return VariableType.Bool;
            return VariableType.Object;
        }

        private static string DescribeShape(ValueShape shape)
        {
            return shape.IsNull ? "null" : $"{shape.Kind}/{shape.Type}/{shape.ClrType?.Name ?? "object"}";
        }

        private sealed class ValueShape
        {
            public bool IsNull;
            public VariableKind Kind;
            public VariableType Type;
            public Type ClrType;
        }

        private static string CellText(DataGridViewRow row, string name)
        {
            return row.Cells[name].Value?.ToString() ?? string.Empty;
        }

        private string BuildBranchOutputHint(IfElseNode ifElse)
        {
            string ifRefs = string.Join(", ", RefCandidateService.ForBranchOutput(_model.Root, ifElse, IfBranch.If).Select(c => c.Path).Take(12));
            string elseRefs = string.Join(", ", RefCandidateService.ForBranchOutput(_model.Root, ifElse, IfBranch.Else).Select(c => c.Path).Take(12));
            return "If 可选引用：" + ifRefs + Environment.NewLine + "Else 可选引用：" + elseRefs;
        }

        private static string OperandText(Operand operand)
        {
            if (operand == null)
            {
                return string.Empty;
            }
            return operand.IsConstant ? operand.ConstantValue?.ToString() ?? "null" : "ref:" + operand.Reference;
        }

        private static string CellText(DataGridViewRow row, int index)
        {
            return row.Cells[index].Value?.ToString() ?? string.Empty;
        }

        private static T ParseEnum<T>(string text, T fallback) where T : struct
        {
            if (!string.IsNullOrWhiteSpace(text) && Enum.TryParse(text, true, out T parsed))
            {
                return parsed;
            }
            return fallback;
        }

        private void FillVariables(FlowContext ctx)
        {
            _varList.Items.Clear();
            foreach (Variable v in ctx.GetAllVariables().OrderBy(v => v.ModuleName).ThenBy(v => v.Name))
            {
                var item = new ListViewItem(v.ModuleName);
                item.SubItems.Add(v.Name);
                item.SubItems.Add(v.Kind.ToString());
                item.SubItems.Add(v.Type.ToString());
                item.SubItems.Add(Summarize(v));
                _varList.Items.Add(item);
            }
        }

        private void FillLog(FlowContext ctx)
        {
            _logGrid.Rows.Clear();
            if (ctx.StructuredLogs.Count > 0)
            {
                foreach (FlowLogEntry entry in ctx.StructuredLogs)
                {
                    AddLogRow(entry);
                }
                return;
            }

            foreach (string line in ctx.Log)
            {
                AddUiLog(FlowLogLevel.Info, line);
            }
        }

        private void AddUiLog(FlowLogLevel level, string message)
        {
            AddLogRow(new FlowLogEntry(level, message));
        }

        private void AddLogRow(FlowLogEntry entry)
        {
            int rowIndex = _logGrid.Rows.Add(
                entry.Time.ToString("HH:mm:ss.fff"),
                entry.Level.ToString(),
                entry.NodeName ?? string.Empty,
                entry.Message ?? string.Empty,
                entry.Duration.HasValue ? $"{entry.Duration.Value.TotalMilliseconds:F0} ms" : string.Empty,
                entry.ErrorCode ?? string.Empty);

            if (entry.Level == FlowLogLevel.Error)
            {
                _logGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.DarkRed;
            }
            else if (entry.Level == FlowLogLevel.Warning)
            {
                _logGrid.Rows[rowIndex].DefaultCellStyle.ForeColor = Color.DarkOrange;
            }
        }

        private static string Summarize(Variable v)
        {
            switch (v.Kind)
            {
                case VariableKind.Single:
                    return v.Value?.ToString() ?? "null";
                case VariableKind.Array:
                    return $"{v.Type}[{v.Count}]";
                default:
                    return v.Value?.GetType().Name ?? "null";
            }
        }

        private void DisplaySelectedResult()
        {
            if (_imageView == null)
            {
                return;
            }

            if (_lastRunContext == null)
            {
                if (_inputImage != null && _inputImage.IsInitialized())
                {
                    _imageView.ShowImage(_inputImage);
                    _imageView.ClearOverlay();
                }
                return;
            }

            string moduleName = SelectedOutputModuleName();
            if (string.IsNullOrWhiteSpace(moduleName))
            {
                _imageView.ClearOverlay();
                return;
            }

            try
            {
                HObject overlay = BuildModuleOverlay(_lastRunContext, moduleName, out HObject imageToShow);
                try
                {
                    if (imageToShow != null && imageToShow.IsInitialized())
                    {
                        _imageView.ShowImage(imageToShow);
                    }
                    else if (_inputImage != null && _inputImage.IsInitialized())
                    {
                        _imageView.ShowImage(_inputImage);
                    }
                    _imageView.SetOverlay(overlay);
                }
                finally
                {
                    overlay?.Dispose();
                }
            }
            catch (Exception ex)
            {
                AddUiLog(FlowLogLevel.Error, "[显示] " + ex.Message);
            }
        }

        private string SelectedOutputModuleName()
        {
            object tag = _flowTree.SelectedNode?.Tag;
            if (tag is ToolNode toolNode)
            {
                return toolNode.Tool.ModuleName;
            }
            if (tag is IfElseNode ifElse)
            {
                return ifElse.Name;
            }
            if (tag is BranchRef branchRef && branchRef.ParentNode is IfElseNode parentIfElse)
            {
                return parentIfElse.Name;
            }
            return null;
        }

        private static HObject BuildModuleOverlay(FlowContext ctx, string moduleName, out HObject imageToShow)
        {
            imageToShow = null;
            HOperatorSet.GenEmptyObj(out HObject overlay);
            foreach (Variable v in ctx.GetAllVariables().Where(v => string.Equals(v.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase)))
            {
                if (v.Value is HalconImage image && imageToShow == null)
                {
                    imageToShow = image.Object;
                }
                else if (v.Value is HalconRegion region)
                {
                    AppendObject(ref overlay, region.Object);
                }
                else if (v.Value is HObject hObject)
                {
                    AppendObject(ref overlay, hObject);
                }
                else if (v.Value is List<HObject> objects)
                {
                    foreach (HObject obj in objects)
                    {
                        AppendObject(ref overlay, obj);
                    }
                }
                else if (v.Value is List<EllipseMeasureResult> ellipses)
                {
                    foreach (EllipseMeasureResult m in ellipses)
                    {
                        HOperatorSet.GenEllipseContourXld(out HObject ellipse,
                            m.Row, m.Column, m.Phi, m.Length1, m.Length2, 0, 6.28318, "positive", 1.5);
                        AppendAndDispose(ref overlay, ellipse);
                    }
                }
                else if (v.Value is List<LineMeasureResult> lines)
                {
                    foreach (LineMeasureResult line in lines)
                    {
                        HOperatorSet.GenRegionLine(out HObject lineObject, line.Row1, line.Column1, line.Row2, line.Column2);
                        AppendAndDispose(ref overlay, lineObject);
                    }
                }
                else if (v.Value is List<RectangleMeasureResult> rectangles)
                {
                    foreach (RectangleMeasureResult r in rectangles)
                    {
                        HOperatorSet.GenRectangle2(out HObject rectangle, r.Row, r.Column, r.Phi, r.Length1, r.Length2);
                        AppendAndDispose(ref overlay, rectangle);
                    }
                }
                else if (v.Value is List<CircleMeasureResult> circles)
                {
                    foreach (CircleMeasureResult c in circles)
                    {
                        HOperatorSet.GenCircle(out HObject circle, c.Row, c.Column, c.Radius);
                        AppendAndDispose(ref overlay, circle);
                    }
                }
            }
            return overlay;
        }

        private static void AppendObject(ref HObject target, HObject obj)
        {
            if (obj == null || !obj.IsInitialized())
            {
                return;
            }
            HOperatorSet.ConcatObj(target, obj, out HObject combined);
            target.Dispose();
            target = combined;
        }

        private static void AppendAndDispose(ref HObject target, HObject obj)
        {
            try
            {
                AppendObject(ref target, obj);
            }
            finally
            {
                obj?.Dispose();
            }
        }
    }
}
