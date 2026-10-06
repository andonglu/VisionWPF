using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Conditions;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Engine;
using VisionFlow.Nodes;
using VisionFlow.Runtime;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Validation;
using VisionFlow.Variables;
using VisionFlow.WpfApp.Ui;
using VisionFlow.WpfApp.Services;
using VisionFlow.WpfToolEditors;
using VisionFlow.WpfToolEditors.Controls;
using VisionFlow.WpfToolEditors.Editors;

namespace VisionFlow.WpfApp
{
    public partial class MainWindow : Window
    {
        private sealed class BranchRef
        {
            public IfElseNode Node { get; set; }
            public IfBranch Branch { get; set; }
        }

        private sealed class VariableRow
        {
            public string Path { get; set; }
            public string Type { get; set; }
            public int Count { get; set; }
            public string Value { get; set; }
        }

        private sealed class LogRow
        {
            public string Level { get; set; }
            public string NodeName { get; set; }
            public string Message { get; set; }
        }

        private sealed class DisplayOutputRow
        {
            public string Path { get; set; }
            public Variable Variable { get; set; }

            public override string ToString()
            {
                return Path;
            }
        }

        private readonly FlowEditModel _model = new FlowEditModel();
        private readonly IVisionFlowRuntime _runtime = new VisionFlowRuntime();
        private readonly EditorRunSession _runSession;
        private readonly FlowDocumentController _document;
        private readonly DisplaySettingsStore _displaySettings;
        private readonly Dictionary<string, TreeViewItem> _flowItemsByNodeId = new Dictionary<string, TreeViewItem>();
        private readonly List<PluginLoadResult> _pluginLoadResults = new List<PluginLoadResult>();
        private ParameterPanelBuilder _panelBuilder;
        private HObject _inputImage;
        private string _inputImagePath;
        private bool _closed;
        /// <summary>当前调试暂停节点 Id 与被高亮的树节点（调试会话用）。</summary>
        private string _pausedNodeId;
        private TreeViewItem _pausedItem;
        private string _overlayDrawMode = "margin";
        private double _overlayFillOpacity = 0.35;
        private double _overlayLineWidth = 2.0;

        /// <summary>是否正在运行。运行期间结构编辑、参数修改、流程切换与换图统一锁定。</summary>
        private bool IsRunning
        {
            get { return _runSession.IsRunning; }
        }

        /// <summary>运行期间的编辑守卫：返回 false 表示操作被锁定。</summary>
        private bool EnsureEditable(string action)
        {
            if (!IsRunning)
            {
                return true;
            }
            SetStatus($"正在运行，{action}已锁定；请停止或等待运行结束。");
            return false;
        }

        public MainWindow()
        {
            InitializeComponent();
            _runSession = new EditorRunSession(_runtime);
            _runSession.RunningChanged += SetRunningState;
            _runSession.Progressed += OnProgress;
            _document = new FlowDocumentController(_model, ShowSaveFlowDialog, ConfirmCloseWithDialog, ShowWarning);
            _document.Saved += path =>
            {
                UpdateWindowTitle();
                SetStatus("已保存流程：" + path);
            };
            _displaySettings = new DisplaySettingsStore(ShowWarning);
            // 结构编辑（增删移动节点）置脏后刷新标题脏标记
            _model.StructureChanged += (s, e) => UpdateWindowTitle();
            _panelBuilder = new ParameterPanelBuilder(
                ParameterPanel,
                () => _model.Root,
                () => _inputImage,
                EnsureEditable,
                MarkDirtyFromUi,
                ShowWarning,
                RefreshFlowTree,
                SetStatus,
                OpenToolEditor);
            LoadDisplaySettings();
            ApplyDisplaySettings();
            BootstrapEditor();
            BuildToolbox();
            RefreshFlowTree();
            UpdateWindowTitle();
            WriteStartupHint();
        }

        private void BootstrapEditor()
        {
            ToolboxRegistry.RegisterDefaults();
            // 平台相关编辑器注册与工具执行注册分离：WPF 侧编辑器由 WpfToolEditorRegistry 接收
            VisionFlowPluginLoader.EditorRegistrars.Add(WpfToolEditorRegistry.RegisterEditor);
            _pluginLoadResults.AddRange(VisionFlowPluginLoader.LoadFromDefaultDirectory());
        }

        private void BuildToolbox()
        {
            ToolboxPanel.Children.Clear();
            foreach (IGrouping<string, ToolboxItem> group in ToolboxRegistry.Items.GroupBy(i => i.Category).OrderBy(g => g.Key))
            {
                var grid = new UniformGrid
                {
                    Columns = 3,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                foreach (ToolboxItem item in group.OrderBy(i => i.DisplayName))
                {
                    grid.Children.Add(CreateToolButton(item));
                }

                var expander = new Expander
                {
                    Header = group.Key,
                    Content = grid,
                    Style = (Style)FindResource("ToolboxExpanderStyle")
                };
                ToolboxPanel.Children.Add(expander);
            }
        }

        private Button CreateToolButton(ToolboxItem item)
        {
            // 图标资源由 tools/svg_to_tool_icons.py 从 SVG 生成，key 为 ToolIcon.{工具ID}
            object content = TryFindResource("ToolIcon." + item.Id) ?? TryFindResource("ToolIcon._default");
            var button = new Button
            {
                Content = content,
                Tag = item,
                ToolTip = "单击或双击添加：" + item.DisplayName,
                Style = (Style)FindResource("ToolTileButtonStyle")
            };
            button.PreviewMouseLeftButtonDown += (s, e) =>
            {
                if (e.ClickCount == 1)
                {
                    AddToolboxNode(item.Id);
                    e.Handled = true;
                }
            };
            return button;
        }

        private void RefreshFlowTree()
        {
            _flowItemsByNodeId.Clear();
            FlowTree.Items.Clear();
            TreeViewItem root = CreateFlowItem(_model.Root);
            root.IsExpanded = true;
            FlowTree.Items.Add(root);
            _panelBuilder.Build(GetSelectedFlowNode());
            // 调试暂停中重建了树（理论上运行期已锁定编辑），补回暂停节点高亮
            if (_pausedNodeId != null)
            {
                string nodeId = _pausedNodeId;
                ClearPausedNodeHighlight();
                HighlightPausedNode(nodeId);
            }
        }

        private TreeViewItem CreateFlowItem(FlowNode node)
        {
            var item = new TreeViewItem { Header = GetNodeHeader(node), Tag = node, IsExpanded = true };
            _flowItemsByNodeId[node.Id] = item;

            if (node is SequenceNode sequence)
            {
                foreach (FlowNode child in sequence.Children)
                {
                    item.Items.Add(CreateFlowItem(child));
                }
            }
            else if (node is IfElseNode ifElse)
            {
                var ifItem = new TreeViewItem { Header = "If 分支", Tag = new BranchRef { Node = ifElse, Branch = IfBranch.If }, IsExpanded = true };
                foreach (FlowNode child in ifElse.IfBranch)
                {
                    ifItem.Items.Add(CreateFlowItem(child));
                }
                var elseItem = new TreeViewItem { Header = "Else 分支", Tag = new BranchRef { Node = ifElse, Branch = IfBranch.Else }, IsExpanded = true };
                foreach (FlowNode child in ifElse.ElseBranch)
                {
                    elseItem.Items.Add(CreateFlowItem(child));
                }
                item.Items.Add(ifItem);
                item.Items.Add(elseItem);
            }
            else if (node is ForLoopNode loop)
            {
                foreach (FlowNode child in loop.Body)
                {
                    item.Items.Add(CreateFlowItem(child));
                }
            }

            return item;
        }

        private static string GetNodeHeader(FlowNode node)
        {
            if (node is SequenceNode)
            {
                return "顺序: " + node.Name;
            }
            if (node is ToolNode toolNode)
            {
                return "工具: " + toolNode.Tool.ModuleName;
            }
            if (node is IfElseNode)
            {
                return "IfElse: " + node.Name;
            }
            if (node is ForLoopNode loop)
            {
                return "For(" + (loop.Mode == ForLoopMode.Count ? "次数" : "集合") + "): " + node.Name;
            }
            if (node is FlowOutputNode)
            {
                return "输出: " + node.Name;
            }
            return node.Name;
        }

        /// <summary>参数面板/编辑器成功提交后标记文档已修改，并刷新标题脏标记。</summary>
        private void MarkDirtyFromUi()
        {
            _model.MarkDirty();
            UpdateWindowTitle();
        }

        private void OpenToolEditor(ToolNode node)
        {
            if (!EnsureEditable("打开工具编辑器"))
            {
                return;
            }
            try
            {
                using var transaction = new ToolEditTransaction(node, new ToolEditContext
                {
                    Root = _model.Root,
                    Node = node,
                    InputImage = _inputImage,
                    InputImagePath = _inputImagePath,
                    LastRunContext = _runSession.LastRunContext
                });

                Window window = WpfToolEditorRouter.Create(transaction.WorkingCopy, transaction.Context);
                window.Owner = this;
                if (window.ShowDialog() == true)
                {
                    transaction.Commit();
                    if (transaction.HasCommittedChanges)
                    {
                        node.Name = node.Tool.ModuleName;
                        MarkDirtyFromUi();
                        RefreshFlowTree();
                        // 新配置（如重新示教的模板）立即预热，问题在编辑时就能发现
                        ReportPrepareIssues(FlowResources.Prepare(node), "工具资源预热");
                    }
                }
            }
            catch (Exception ex) when (ToolEditTransaction.IsConfigurationException(ex))
            {
                ShowWarning("工具编辑", "工具参数未保存：" + ex.GetBaseException().Message);
            }
        }

        private void AddToolboxNode(string toolboxId)
        {
            if (!EnsureEditable("添加节点"))
            {
                return;
            }
            FlowNode selected = GetSelectedFlowNode();
            IfBranch branch = IfBranch.If;
            object tag = (FlowTree.SelectedItem as TreeViewItem)?.Tag;
            if (tag is BranchRef branchRef)
            {
                selected = branchRef.Node;
                branch = branchRef.Branch;
            }

            FlowNode added = _model.AddNode(toolboxId, selected, branch);
            NodeNaming.EnsureUniqueNewNodeName(_model.Root, added);
            RefreshFlowTree();
            SelectNode(added.Id);
            SetStatus("已添加节点：" + added.Name);
        }

        private void FlowTree_SelectedItemChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
        {
            _panelBuilder.Build(GetSelectedFlowNode());
        }

        private void FlowTree_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!EnsureEditable("打开工具编辑器"))
            {
                return;
            }
            if (GetSelectedFlowNode() is ToolNode node)
            {
                OpenToolEditor(node);
            }
        }

        private FlowNode GetSelectedFlowNode()
        {
            object tag = (FlowTree.SelectedItem as TreeViewItem)?.Tag;
            if (tag is FlowNode node)
            {
                return node;
            }
            if (tag is BranchRef branchRef)
            {
                return branchRef.Node;
            }
            return _model.Root;
        }

        private void MoveUp_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(-1);
        }

        private void MoveDown_Click(object sender, RoutedEventArgs e)
        {
            MoveSelected(1);
        }

        private void MoveSelected(int delta)
        {
            if (!EnsureEditable("移动节点"))
            {
                return;
            }
            FlowNode node = GetSelectedFlowNode();
            if (_model.MoveNode(node, delta))
            {
                RefreshFlowTree();
                SelectNode(node.Id);
            }
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureEditable("删除节点"))
            {
                return;
            }
            FlowNode node = GetSelectedFlowNode();
            if (node == _model.Root)
            {
                return;
            }
            if (_model.RemoveNode(node))
            {
                RefreshFlowTree();
            }
        }

        private void OpenImage_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureEditable("换图"))
            {
                return;
            }
            var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            HOperatorSet.ReadImage(out HObject image, dialog.FileName);
            _inputImage?.Dispose();
            _inputImage = image;
            _inputImagePath = dialog.FileName;
            ImageView.ShowImage(_inputImage);
            ImageView.ClearOverlay();
            OutputDisplayCombo.SelectedItem = null;
            SetStatus("已打开图像：" + dialog.FileName);
        }

        private void NewFlow_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureEditable("新建流程"))
            {
                return;
            }
            if (!_document.ConfirmClose())
            {
                return;
            }

            _document.New();
            // 切换了文档：上次运行结果不再属于当前文档，回收其 HALCON 资源（VF-04）
            DiscardLastRunResult();
            VariablesGrid.ItemsSource = null;
            LogGrid.ItemsSource = null;
            OutputDisplayCombo.ItemsSource = null;
            OutputDisplayCombo.SelectedItem = null;
            ImageView.ClearOverlay();
            RefreshFlowTree();
            UpdateWindowTitle();
            SetStatus("已新建未命名流程，可直接添加工具。");
        }

        private void SaveFlow_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentFlow(saveAs: false);
        }

        private void SaveFlowAs_Click(object sender, RoutedEventArgs e)
        {
            SaveCurrentFlow(saveAs: true);
        }

        private void LoadFlow_Click(object sender, RoutedEventArgs e)
        {
            if (!EnsureEditable("打开流程"))
            {
                return;
            }
            if (!_document.ConfirmClose())
            {
                return;
            }

            var dialog = new OpenFileDialog
            {
                Filter = "VisionFlow 流程|*.vflow.json;*.json|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            FlowLoadResult load = _document.Load(dialog.FileName);
            // 打开的是另一个流程：上次运行结果不再属于当前文档，回收其 HALCON 资源（VF-04）
            DiscardLastRunResult();
            VariablesGrid.ItemsSource = null;
            LogGrid.ItemsSource = null;
            OutputDisplayCombo.ItemsSource = null;
            OutputDisplayCombo.SelectedItem = null;
            ImageView.ClearOverlay();
            RefreshFlowTree();
            UpdateWindowTitle();
            SetStatus("已加载流程：" + dialog.FileName);
            PrepareFlowInBackground(_model.Root);

            if (load.Warnings.Count > 0)
            {
                ShowWarning("兼容性提示",
                    "流程已加载，但存在以下兼容性问题：\r\n" + string.Join("\r\n", load.Warnings.Take(10)));
            }

            if (!load.Validation.IsValid)
            {
                ShowValidationResult(load.Validation, "流程已加载，但校验发现问题。");
            }
        }

        private bool SaveCurrentFlow(bool saveAs)
        {
            if (!EnsureEditable("保存流程"))
            {
                return false;
            }
            return _document.Save(saveAs);
        }

        private string ShowSaveFlowDialog(string currentPath)
        {
            var dialog = new SaveFileDialog
            {
                Filter = "VisionFlow 流程|*.vflow.json|JSON 文件|*.json|所有文件|*.*",
                FileName = string.IsNullOrWhiteSpace(currentPath) ? "flow.vflow.json" : Path.GetFileName(currentPath),
                DefaultExt = "vflow.json",
                AddExtension = true
            };
            if (!string.IsNullOrWhiteSpace(currentPath))
            {
                dialog.InitialDirectory = Path.GetDirectoryName(currentPath);
            }
            return dialog.ShowDialog(this) == true ? FlowDocumentController.NormalizeFlowFileName(dialog.FileName) : null;
        }

        /// <summary>未保存修改的关闭确认对话框（WF-10）：是=保存，否=放弃，取消=中止。</summary>
        private ConfirmCloseChoice ConfirmCloseWithDialog()
        {
            MessageBoxResult choice = MessageBox.Show(this,
                "当前流程有未保存的修改。是否先保存？",
                "关闭当前流程",
                MessageBoxButton.YesNoCancel,
                MessageBoxImage.Question);
            switch (choice)
            {
                case MessageBoxResult.Yes:
                    return ConfirmCloseChoice.Save;
                case MessageBoxResult.No:
                    return ConfirmCloseChoice.Discard;
                default:
                    return ConfirmCloseChoice.Cancel;
            }
        }

        private void Validate_Click(object sender, RoutedEventArgs e)
        {
            FlowValidationResult validation = FlowValidator.Validate(_model.Root);
            ShowValidationResult(validation, validation.IsValid ? "流程校验通过。" : "流程校验发现问题。");
        }

        private async void Run_Click(object sender, RoutedEventArgs e)
        {
            await RunFlowAsync();
        }

        private async void Debug_Click(object sender, RoutedEventArgs e)
        {
            // 暂停中再次点击“调试”充当单步（步入）；未运行时启动调试会话
            if (_runSession.IsPaused)
            {
                _runSession.Step();
                return;
            }
            await DebugFlowAsync();
        }

        private void StepOver_Click(object sender, RoutedEventArgs e)
        {
            _runSession.StepOver();
        }

        private void Continue_Click(object sender, RoutedEventArgs e)
        {
            _runSession.Continue();
        }

        private Task RunFlowAsync()
        {
            return RunFlowCoreAsync(startDebugging: false);
        }

        private Task DebugFlowAsync()
        {
            return RunFlowCoreAsync(startDebugging: true);
        }

        private async Task RunFlowCoreAsync(bool startDebugging)
        {
            if (_runSession.IsRunning)
            {
                return;
            }

            FlowValidationResult validation = FlowValidator.Validate(_model.Root);
            if (!validation.IsValid)
            {
                ShowValidationResult(validation, startDebugging ? "流程校验失败，已阻止调试。" : "流程校验失败，已阻止运行。");
                return;
            }

            EditorRunOutcome outcome = startDebugging
                ? await _runSession.StartDebugAsync(_model.Root, _inputImage, _inputImagePath)
                : await _runSession.RunAsync(_model.Root, _inputImage, _inputImagePath);
            switch (outcome.Kind)
            {
                case EditorRunOutcomeKind.AlreadyRunning:
                    return;
                case EditorRunOutcomeKind.Cancelled:
                    SetStatus("已取消");
                    return;
                case EditorRunOutcomeKind.Error:
                    ShowWarning("运行异常", "流程运行异常：" + outcome.Error.Message);
                    return;
            }

            FlowRunResult result = outcome.Result;
            FillVariables(result.Context);
            FillLog(result.Context);
            FillDisplayOutputs(result.Context);
            DisplaySelectedOutput();
            if (!result.IsSuccess && result.Status != NodeStatus.Skipped)
            {
                if (!string.IsNullOrWhiteSpace(result.FailedNodeId))
                {
                    SelectNode(result.FailedNodeId);
                }
                ShowWarning("运行结果", $"流程失败：{result.Message}\r\n错误码：{result.ErrorCode ?? "UNKNOWN"}\r\n节点：{result.FailedNodeName ?? "-"}");
            }
        }

        private void Stop_Click(object sender, RoutedEventArgs e)
        {
            _runSession.Stop();
            SetStatus("正在停止...");
            StopButton.IsEnabled = false;
        }

        /// <summary>运行期间禁止关闭窗口，避免后台执行访问已释放的界面与资源。</summary>
        protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
        {
            if (IsRunning)
            {
                e.Cancel = true;
                SetStatus("正在运行，请先停止或等待运行结束再关闭窗口。");
                return;
            }
            // 有未保存修改时先询问，取消则中止关窗
            if (!_document.ConfirmClose())
            {
                e.Cancel = true;
                return;
            }
            base.OnClosing(e);
        }

        protected override void OnClosed(EventArgs e)
        {
            _closed = true;
            DiscardLastRunResult();
            FlowResources.Release(_model.Root);
            _inputImage?.Dispose();
            _inputImage = null;
            base.OnClosed(e);
        }

        /// <summary>
        /// 加载流程后在后台预热工具资源（模型句柄、标定等），避免首次运行的加载延迟。
        /// 预热与运行共用工具内部锁，期间开始运行是安全的；预热完成前已切换文档或关闭窗口时，立即释放刚加载的资源。
        /// </summary>
        private async void PrepareFlowInBackground(SequenceNode root)
        {
            List<ToolNode> toolNodes = FlowResources.EnumerateToolNodes(root).ToList();
            List<ToolBase> snapshotTools = toolNodes.Select(n => n.Tool).ToList();
            FlowPrepareResult result;
            try
            {
                result = await Task.Run(() => FlowResources.Prepare(toolNodes));
            }
            catch (Exception ex)
            {
                SetStatus("流程资源预热失败：" + ex.GetBaseException().Message);
                return;
            }
            if (_closed || _model.Root != root)
            {
                FlowResources.Release(root);
                foreach (ToolBase tool in snapshotTools)
                {
                    FlowResources.Release(tool);
                }
                return;
            }
            // 预热期间被删除或被替换配置的工具可能刚被重新加载，补释放
            var currentTools = new HashSet<ToolBase>(FlowResources.EnumerateToolNodes(root).Select(n => n.Tool));
            foreach (ToolBase tool in snapshotTools.Where(t => !currentTools.Contains(t)))
            {
                FlowResources.Release(tool);
            }
            if (result.PreparedCount > 0 && result.IsSuccess)
            {
                SetStatus($"流程资源已预热：{result.PreparedCount} 个工具，耗时 {result.Duration.TotalMilliseconds:F0} ms");
            }
            ReportPrepareIssues(result, "流程资源预热");
        }

        private void ReportPrepareIssues(FlowPrepareResult result, string title)
        {
            if (result.IsSuccess)
            {
                return;
            }
            ShowWarning(title, "以下工具的资源加载失败，运行到这些节点时会报错：\r\n"
                + string.Join("\r\n", result.Issues.Take(10).Select(i => i.ToString())));
        }

        /// <summary>丢弃上次运行结果并回收其 HALCON 资源（VF-04：旧结果替换后资源可回收）。</summary>
        private void DiscardLastRunResult()
        {
            _runSession.DiscardLastRunResult();
        }

        private void OutputDisplayCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            DisplaySelectedOutput();
        }

        private void ShowInputImage_Click(object sender, RoutedEventArgs e)
        {
            OutputDisplayCombo.SelectedItem = null;
            DisplayCurrentImage();
            SetStatus("正在显示输入图像。");
        }

        private void DisplaySettings_Click(object sender, RoutedEventArgs e)
        {
            var drawMode = new ComboBox { IsEditable = false, Margin = new Thickness(0, 0, 0, 16) };
            drawMode.Items.Add("margin");
            drawMode.Items.Add("fill");
            drawMode.Text = _overlayDrawMode;

            var opacity = new Slider { Minimum = 0.05, Maximum = 1.0, Value = _overlayFillOpacity, TickFrequency = 0.05, IsSnapToTickEnabled = false };
            var opacityValue = new TextBlock { Text = DisplaySettingsStore.FormatPercent(opacity.Value), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            opacity.ValueChanged += (s, args) => opacityValue.Text = DisplaySettingsStore.FormatPercent(opacity.Value);

            var lineWidth = new Slider { Minimum = 1, Maximum = 8, Value = _overlayLineWidth, TickFrequency = 0.5, IsSnapToTickEnabled = false };
            var lineWidthValue = new TextBlock { Text = lineWidth.Value.ToString("F1", CultureInfo.CurrentCulture), Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            lineWidth.ValueChanged += (s, args) => lineWidthValue.Text = lineWidth.Value.ToString("F1", CultureInfo.CurrentCulture);

            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "叠加显示方式", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(drawMode);
            panel.Children.Add(new TextBlock { Text = "Fill 透明度", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8) });
            panel.Children.Add(new DockPanel { LastChildFill = true, Children = { opacityValue, opacity } });
            panel.Children.Add(new TextBlock { Text = "线宽", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 16, 0, 8) });
            panel.Children.Add(new DockPanel { LastChildFill = true, Children = { lineWidthValue, lineWidth } });

            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
            var ok = new Button { Content = "确定", MinWidth = 96, Style = (Style)FindResource("AccentButtonStyle") };
            var cancel = new Button { Content = "取消", MinWidth = 96, Margin = new Thickness(8, 0, 0, 0) };
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            panel.Children.Add(buttons);

            var window = new Window
            {
                Title = "系统显示设置",
                Owner = this,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Width = 360,
                Height = 300,
                ResizeMode = ResizeMode.NoResize,
                Content = panel
            };
            ok.Click += (s, args) =>
            {
                _overlayDrawMode = drawMode.Text;
                _overlayFillOpacity = opacity.Value;
                _overlayLineWidth = lineWidth.Value;
                ApplyDisplaySettings();
                SaveDisplaySettings();
                DisplaySelectedOutput();
                window.DialogResult = true;
            };
            cancel.Click += (s, args) => window.DialogResult = false;
            window.ShowDialog();
        }

        private void ApplyDisplaySettings()
        {
            if (ImageView == null)
            {
                return;
            }
            ImageView.OverlayDrawMode = _overlayDrawMode;
            ImageView.OverlayFillOpacity = _overlayFillOpacity;
            ImageView.OverlayLineWidth = _overlayLineWidth;
        }

        private void LoadDisplaySettings()
        {
            DisplaySettingsModel settings = _displaySettings.Load();
            _overlayDrawMode = settings.OverlayDrawMode;
            _overlayFillOpacity = settings.OverlayFillOpacity;
            _overlayLineWidth = settings.OverlayLineWidth;
        }

        private void SaveDisplaySettings()
        {
            _displaySettings.Save(new DisplaySettingsModel
            {
                OverlayDrawMode = _overlayDrawMode,
                OverlayFillOpacity = _overlayFillOpacity,
                OverlayLineWidth = _overlayLineWidth
            });
        }

        private void OnProgress(FlowProgress progress)
        {
            if (!string.IsNullOrWhiteSpace(progress.NodeId))
            {
                SelectNode(progress.NodeId);
            }

            switch (progress.Kind)
            {
                case FlowProgressKind.NodeStarted:
                    SetStatus("正在执行：" + progress.NodeName);
                    break;
                case FlowProgressKind.NodeCompleted:
                    SetStatus($"{progress.NodeName} 完成（{progress.Duration?.TotalMilliseconds:F0} ms）");
                    break;
                case FlowProgressKind.NodeFailed:
                    SetStatus(progress.NodeName + " 失败");
                    break;
                case FlowProgressKind.FlowCompleted:
                    SetStatus(progress.Status == NodeStatus.Success
                        ? $"运行完成（{progress.Duration?.TotalMilliseconds:F0} ms）"
                        : $"运行失败（{progress.Duration?.TotalMilliseconds:F0} ms）");
                    ClearPausedNodeHighlight();
                    break;
                case FlowProgressKind.FlowCancelled:
                    SetStatus("已取消");
                    ClearPausedNodeHighlight();
                    break;
                case FlowProgressKind.DebugPaused:
                    OnDebugPaused(progress);
                    break;
                case FlowProgressKind.DebugResumed:
                    OnDebugResumed(progress);
                    break;
            }
        }

        /// <summary>调试暂停：高亮当前节点、刷新变量与日志面板，让用户逐节点观察现场。</summary>
        private void OnDebugPaused(FlowProgress progress)
        {
            SetStatus("已暂停：" + progress.NodeName);
            HighlightPausedNode(progress.NodeId);
            RefreshPausedPanels();
            UpdateDebugButtons();
        }

        private void OnDebugResumed(FlowProgress progress)
        {
            SetStatus("继续执行：" + progress.NodeName);
            ClearPausedNodeHighlight();
            UpdateDebugButtons();
        }

        /// <summary>暂停期间引擎线程被阻塞，实时上下文稳定可读；复用运行完成后的填充逻辑。</summary>
        private void RefreshPausedPanels()
        {
            FlowContext context = _runSession.ActiveContext;
            if (context == null || !_runSession.IsPaused)
            {
                return;
            }
            try
            {
                FillVariables(context);
                FillLog(context);
            }
            catch (InvalidOperationException)
            {
                // 恰好在恢复瞬间读取失败时放弃本次刷新，下一次暂停再刷
            }
        }

        /// <summary>高亮流程树中的“当前暂停节点”（琥珀底，区别于选中高亮与失败定位）。</summary>
        private void HighlightPausedNode(string nodeId)
        {
            ClearPausedNodeHighlight();
            _pausedNodeId = nodeId;
            if (string.IsNullOrWhiteSpace(nodeId) || !_flowItemsByNodeId.TryGetValue(nodeId, out TreeViewItem item))
            {
                return;
            }
            _pausedItem = item;
            item.Background = (Brush)FindResource("SystemFillColorCautionBrush");
            item.Foreground = Brushes.White;
        }

        private void ClearPausedNodeHighlight()
        {
            _pausedNodeId = null;
            if (_pausedItem != null)
            {
                _pausedItem.ClearValue(Control.BackgroundProperty);
                _pausedItem.ClearValue(Control.ForegroundProperty);
                _pausedItem = null;
            }
        }

        /// <summary>调试按钮启用状态：未运行全禁（仅“调试”可启动）；运行中非暂停只许停止；暂停中允许单步/逐过程/继续/停止。</summary>
        private void UpdateDebugButtons()
        {
            bool paused = _runSession.IsPaused;
            if (!IsRunning)
            {
                DebugButton.IsEnabled = true;
                StepOverButton.IsEnabled = false;
                ContinueButton.IsEnabled = false;
            }
            else
            {
                // 暂停中“调试”按钮充当单步（步入）；运行中未暂停时三个步进按钮都不可用
                DebugButton.IsEnabled = paused;
                StepOverButton.IsEnabled = paused;
                ContinueButton.IsEnabled = paused;
            }
            Cursor = IsRunning && !paused ? Cursors.Wait : Cursors.Arrow;
        }

        private void FillVariables(FlowContext context)
        {
            VariablesGrid.ItemsSource = context.GetAllVariables()
                .OrderBy(v => v.ModuleName)
                .ThenBy(v => v.Name)
                .Select(v => new VariableRow
                {
                    Path = v.ModuleName + "." + v.Name,
                    Type = v.Kind + "/" + v.Type,
                    Count = v.Count,
                    Value = FormatValue(v.Value)
                })
                .ToList();
        }

        private void FillLog(FlowContext context)
        {
            LogGrid.ItemsSource = context.StructuredLogs.Select(l => new LogRow
            {
                Level = l.Level.ToString(),
                NodeName = l.NodeName,
                Message = l.Message
            }).ToList();
        }

        private void FillDisplayOutputs(FlowContext context)
        {
            var rows = context.GetAllVariables()
                .Where(DisplayOverlayBuilder.IsDisplayableVariable)
                .OrderBy(v => string.Equals(v.ModuleName, "Input", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(v => v.ModuleName)
                .ThenBy(v => v.Name)
                .Select(v => new DisplayOutputRow
                {
                    Path = v.ModuleName + "." + v.Name,
                    Variable = v
                })
                .ToList();
            OutputDisplayCombo.ItemsSource = rows;
            OutputDisplayCombo.SelectedItem = rows.LastOrDefault(r => !string.Equals(r.Variable.ModuleName, "Input", StringComparison.OrdinalIgnoreCase))
                ?? rows.FirstOrDefault();
        }

        private void DisplayCurrentImage()
        {
            if (_inputImage != null && _inputImage.IsInitialized())
            {
                ImageView.ShowImage(_inputImage);
                ImageView.ClearOverlay();
            }
        }

        private void DisplaySelectedOutput()
        {
            FlowContext lastRunContext = _runSession.LastRunContext;
            if (lastRunContext == null || !(OutputDisplayCombo.SelectedItem is DisplayOutputRow row))
            {
                DisplayCurrentImage();
                return;
            }

            HObject overlay = null;
            try
            {
                HObject imageToShow = DisplayOverlayBuilder.ResolveDisplayBaseImage(lastRunContext, row.Variable);
                if (imageToShow != null && imageToShow.IsInitialized())
                {
                    ImageView.ShowImage(imageToShow);
                }
                else
                {
                    DisplayCurrentImage();
                }

                overlay = DisplayOverlayBuilder.BuildVariableOverlay(row.Variable);
                if (overlay != null && overlay.IsInitialized())
                {
                    ImageView.SetOverlay(overlay);
                }
                else
                {
                    ImageView.ClearOverlay();
                }
                SetStatus("正在显示：" + row.Path);
            }
            catch (Exception ex)
            {
                ShowWarning("显示失败", ex.Message);
            }
            finally
            {
                overlay?.Dispose();
            }
        }

        private void SetRunningState(bool running)
        {
            // 统一的运行会话状态：锁定结构编辑、参数修改、流程切换、换图与保存，保留停止和只读查看
            StopButton.IsEnabled = running;
            RunButton.IsEnabled = !running;
            NewFlowButton.IsEnabled = !running;
            LoadFlowButton.IsEnabled = !running;
            SaveFlowButton.IsEnabled = !running;
            SaveFlowAsButton.IsEnabled = !running;
            OpenImageButton.IsEnabled = !running;
            ValidateButton.IsEnabled = !running;
            MoveUpButton.IsEnabled = !running;
            MoveDownButton.IsEnabled = !running;
            DeleteButton.IsEnabled = !running;
            ToolboxPanel.IsEnabled = !running;
            ParameterPanel.IsEnabled = !running;
            if (!running)
            {
                // 运行结束：清除可能残留的暂停高亮
                ClearPausedNodeHighlight();
            }
            UpdateDebugButtons();
            if (running)
            {
                SetStatus("正在运行...");
            }
        }

        private void ShowValidationResult(FlowValidationResult validation, string title)
        {
            FlowValidationIssue firstError = validation.Issues.FirstOrDefault(i => i.Severity == FlowValidationSeverity.Error);
            if (!string.IsNullOrWhiteSpace(firstError?.NodeId))
            {
                SelectNode(firstError.NodeId);
            }

            string message = validation.Issues.Count == 0
                ? title
                : title + Environment.NewLine + Environment.NewLine
                    + string.Join(Environment.NewLine, validation.Issues.Take(20).Select(i => i.ToString()));
            if (validation.Issues.Count > 20)
            {
                message += Environment.NewLine + "... 还有 " + (validation.Issues.Count - 20) + " 条";
            }
            MessageBox.Show(this, message, "流程校验", MessageBoxButton.OK,
                validation.IsValid ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }

        private void SelectNode(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId) || !_flowItemsByNodeId.TryGetValue(nodeId, out TreeViewItem item))
            {
                return;
            }
            item.IsSelected = true;
            item.BringIntoView();
        }

        private void SetStatus(string text)
        {
            StatusText.Text = text;
            TitleStatusText.Text = text;
        }

        private void UpdateWindowTitle()
        {
            string name = string.IsNullOrWhiteSpace(_document.CurrentFlowPath)
                ? "未命名流程"
                : Path.GetFileName(_document.CurrentFlowPath);
            // 有未保存修改时标题加 * 前缀
            Title = "VisionFlow WPF 编辑器 - " + (_model.IsDirty ? "*" : string.Empty) + name;
        }

        private void ShowWarning(string title, string message)
        {
            MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        private void WriteStartupHint()
        {
            int loadedTools = _pluginLoadResults.Sum(r => r.ToolCount);
            int loadedEditors = _pluginLoadResults.Sum(r => r.EditorCount);
            int failed = _pluginLoadResults.Sum(r => r.Errors.Count);
            int editorDiagnostics = WpfToolEditorRegistry.Diagnostics.Count;
            SetStatus("WPF 版编辑器已启用：左侧工具箱，中间流程和参数，右侧图像/变量/日志。"
                + (loadedTools > 0 ? $" 已加载插件工具 {loadedTools} 个。" : string.Empty)
                + (loadedEditors > 0 ? $" 插件编辑器 {loadedEditors} 个。" : string.Empty)
                + (failed > 0 ? $" 插件加载错误 {failed} 条。" : string.Empty)
                + (editorDiagnostics > 0 ? $" 编辑器注册提示 {editorDiagnostics} 条。" : string.Empty));
        }

        private static string FormatValue(object value)
        {
            if (value == null)
            {
                return "null";
            }
            if (value is Array array)
            {
                return "Array[" + array.Length + "]";
            }
            return value.ToString();
        }
    }
}
