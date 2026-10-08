using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using HalconDotNet;
using Microsoft.Win32;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Tools;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.WpfToolEditors.Editors
{
    public partial class WpfGenericToolEditWindow : Window
    {
        private sealed class FieldBinding
        {
            public PropertyInfo Property { get; set; }
            public Control Editor { get; set; }
            public Type PropertyType { get; set; }
            public bool IsInputRef { get; set; }
            /// <summary>该字段在面板中的全部元素（标签与编辑控件），用于按方式显示/隐藏。</summary>
            public List<UIElement> Elements { get; set; } = new List<UIElement>();
        }

        private sealed class ViewTarget
        {
            public string Name { get; set; }
            public Variable Variable { get; set; }

            public override string ToString()
            {
                return Name;
            }
        }

        private sealed class StatRow
        {
            public string Name { get; set; }
            public string Value { get; set; }
        }

        private readonly ToolBase _tool;
        private readonly ToolEditContext _context;
        private readonly List<FieldBinding> _bindings = new List<FieldBinding>();
        private FlowContext _previewContext;

        public WpfGenericToolEditWindow(ToolBase tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();

            InitializeComponent();
            Title = "工具编辑 - " + _tool.ModuleName;
            ModuleNameText.Text = _tool.ModuleName;
            BuildInputFields();
            BuildScalarFields();
            BuildToolActions();
            HookParameterVisibility();
            RunPreview();
        }

        /// <summary>按工具声明只显示当前方式用到的参数；切换枚举参数时立即刷新（编辑的是工作副本，可直接写回）。</summary>
        private void HookParameterVisibility()
        {
            if (!(_tool is IToolParameterVisibility visibility))
            {
                return;
            }
            foreach (FieldBinding binding in _bindings.Where(b => b.PropertyType.IsEnum && b.Editor is ComboBox))
            {
                FieldBinding current = binding;
                ((ComboBox)current.Editor).SelectionChanged += (s, e) =>
                {
                    if (((ComboBox)current.Editor).SelectedItem is string name && Enum.IsDefined(current.PropertyType, name))
                    {
                        current.Property.SetValue(_tool, Enum.Parse(current.PropertyType, name));
                        UpdateParameterVisibility(visibility);
                    }
                };
            }
            UpdateParameterVisibility(visibility);
        }

        private void UpdateParameterVisibility(IToolParameterVisibility visibility)
        {
            foreach (FieldBinding binding in _bindings)
            {
                Visibility state = visibility.IsParameterVisible(binding.Property.Name) ? Visibility.Visible : Visibility.Collapsed;
                foreach (UIElement element in binding.Elements)
                {
                    element.Visibility = state;
                }
            }
        }

        private void BuildInputFields()
        {
            IReadOnlyList<ToolInputRefDef> inputs = ToolMetadata.GetInputRefs(_tool.GetType());
            if (inputs.Count == 0)
            {
                InputPanel.Children.Add(CreateInfo("无输入引用"));
                return;
            }

            foreach (ToolInputRefDef input in inputs)
            {
                int start = InputPanel.Children.Count;
                ComboBox combo = CreateCombo(input.DisplayName);
                if (input.Optional)
                {
                    combo.Items.Add(string.Empty);
                }
                if (input.ExpectedType == typeof(HalconImage) && _context.InputImage != null && _context.InputImage.IsInitialized())
                {
                    combo.Items.Add("Input.Image");
                }
                if (_context.Root != null && _context.Node != null)
                {
                    foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, input.ExpectedType, input.AcceptsCollection))
                    {
                        if (!combo.Items.Contains(candidate.Path))
                        {
                            combo.Items.Add(candidate.Path);
                        }
                    }
                }
                combo.Text = input.Property.GetValue(_tool) as string ?? string.Empty;
                _bindings.Add(new FieldBinding
                {
                    Property = input.Property,
                    Editor = combo,
                    PropertyType = typeof(string),
                    IsInputRef = true,
                    Elements = InputPanel.Children.Cast<UIElement>().Skip(start).ToList()
                });
            }
        }

        private void BuildScalarFields()
        {
            IReadOnlyList<ToolInputRefDef> inputs = ToolMetadata.GetInputRefs(_tool.GetType());
            bool any = false;
            foreach (PropertyInfo property in SerializableProperties(_tool.GetType()))
            {
                if (inputs.Any(i => i.PropertyName == property.Name))
                {
                    continue;
                }
                any = true;
                int start = ScalarPanel.Children.Count;
                int bindingCount = _bindings.Count;
                AddScalarField(property);
                if (_bindings.Count > bindingCount)
                {
                    _bindings[_bindings.Count - 1].Elements = ScalarPanel.Children.Cast<UIElement>().Skip(start).ToList();
                }
            }

            if (!any)
            {
                ScalarPanel.Children.Add(CreateInfo("无可编辑标量参数"));
            }
        }

        private void AddScalarField(PropertyInfo property)
        {
            Type type = property.PropertyType;
            if (type == typeof(bool))
            {
                var check = new CheckBox
                {
                    Content = property.Name,
                    IsChecked = (bool)property.GetValue(_tool),
                    Margin = new Thickness(0, 0, 0, 16)
                };
                ScalarPanel.Children.Add(check);
                _bindings.Add(new FieldBinding { Property = property, Editor = check, PropertyType = type });
                return;
            }

            if (type.IsEnum)
            {
                ComboBox combo = CreateCombo(property.Name);
                foreach (string name in Enum.GetNames(type))
                {
                    combo.Items.Add(name);
                }
                combo.Text = property.GetValue(_tool).ToString();
                _bindings.Add(new FieldBinding { Property = property, Editor = combo, PropertyType = type });
                return;
            }

            if (type == typeof(string) && IsFilePathProperty(property))
            {
                AddFilePathField(property);
                return;
            }

            TextBox text = CreateTextBox(property.Name, Convert.ToString(property.GetValue(_tool), CultureInfo.CurrentCulture));
            _bindings.Add(new FieldBinding { Property = property, Editor = text, PropertyType = type });
        }

        private void BuildToolActions()
        {
            if (_tool is RegionPoseTool)
            {
                var button = new Button
                {
                    Content = "使用当前输入 Region 设为基准",
                    Margin = new Thickness(0, 0, 0, 16)
                };
                button.Click += SetRegionPoseBase_Click;
                ScalarPanel.Children.Add(button);
            }
        }

        private void AddFilePathField(PropertyInfo property)
        {
            AddLabel(property.Name, ScalarPanel);
            var grid = new Grid { Margin = new Thickness(0, 0, 0, 16) };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
            var text = new TextBox { Text = property.GetValue(_tool) as string ?? string.Empty, Margin = new Thickness(0) };
            var browse = new Button { Content = "...", MinWidth = 32, Width = 32, Padding = new Thickness(0), Margin = new Thickness(8, 0, 0, 0) };
            browse.Click += (s, e) => BrowsePath(text);
            Grid.SetColumn(text, 0);
            Grid.SetColumn(browse, 1);
            grid.Children.Add(text);
            grid.Children.Add(browse);
            ScalarPanel.Children.Add(grid);
            _bindings.Add(new FieldBinding { Property = property, Editor = text, PropertyType = typeof(string) });
        }

        private ComboBox CreateCombo(string label)
        {
            StackPanel target = IsBuildingInputs(label) ? InputPanel : ScalarPanel;
            AddLabel(label, target);
            var combo = new ComboBox { IsEditable = true };
            target.Children.Add(combo);
            return combo;
        }

        private bool IsBuildingInputs(string label)
        {
            return ToolMetadata.GetInputRefs(_tool.GetType()).Any(i => i.DisplayName == label);
        }

        private TextBox CreateTextBox(string label, string value)
        {
            AddLabel(label, ScalarPanel);
            var text = new TextBox { Text = value ?? string.Empty };
            ScalarPanel.Children.Add(text);
            return text;
        }

        private static TextBlock CreateInfo(string text)
        {
            return new TextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 16)
            };
        }

        private static void AddLabel(string label, Panel target)
        {
            if (target == null)
            {
                return;
            }
            var textBlock = new TextBlock
            {
                Tag = label,
                Text = label,
                Margin = new Thickness(0, 0, 0, 8)
            };
            textBlock.SetResourceReference(TextBlock.ForegroundProperty, "TextFillColorSecondaryBrush");
            target.Children.Add(textBlock);
        }

        private void BrowsePath(TextBox text)
        {
            var dialog = new OpenFileDialog
            {
                Filter = "图像文件|*.png;*.bmp;*.jpg;*.jpeg;*.tif;*.tiff|HALCON 对象|*.hobj|所有文件|*.*"
            };
            if (dialog.ShowDialog(this) == true)
            {
                text.Text = dialog.FileName;
            }
        }

        private void RunPreview_Click(object sender, RoutedEventArgs e)
        {
            RunPreview();
        }

        private void SetRegionPoseBase_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitFields();
                if (!(_tool is RegionPoseTool poseTool))
                {
                    return;
                }

                using FlowContext ctx = BuildPreviewContext();
                HalconRegion region = VariableReference.Parse(poseTool.RegionPath).Resolve<HalconRegion>(ctx);
                poseTool.SetBaseFromRegion(region.Object);
                SyncFieldsFromTool();
                RunPreview();
                StatusText.Text = $"已设置基准：Row={poseTool.BaseRow:F3}, Column={poseTool.BaseColumn:F3}, Angle={poseTool.BaseAngle:F5}";
            }
            catch (Exception ex)
            {
                StatusText.Text = "设置基准失败：" + ex.Message;
            }
        }

        private void Fit_Click(object sender, RoutedEventArgs e)
        {
            PreviewImageView.FitToWindow();
        }

        private void ViewTargetCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            ShowSelectedTarget();
        }

        private void RunPreview()
        {
            FlowContext previousPreview = _previewContext;
            try
            {
                CommitFields();
                _previewContext = BuildPreviewContext();
                NodeResult result = _tool.Run(_previewContext);
                RefreshViewTargets(result.IsSuccess);
                StatusText.Text = result.IsSuccess ? "预览完成。" : "预览失败：" + result.Message;
            }
            catch (Exception ex)
            {
                if (!ReferenceEquals(_previewContext, previousPreview))
                {
                    _previewContext?.Dispose();
                }
                _previewContext = BuildPreviewContextWithoutThrow();
                RefreshViewTargets(false);
                StatusText.Text = "预览失败：" + ex.Message;
            }
            finally
            {
                if (!ReferenceEquals(_previewContext, previousPreview))
                {
                    previousPreview?.Dispose();
                }
            }
        }

        protected override void OnClosed(EventArgs e)
        {
            _previewContext?.Dispose();
            _previewContext = null;
            base.OnClosed(e);
        }

        private FlowContext BuildPreviewContext()
        {
            // 派生上下文：共享主窗口变量（借用），释放时只回收本窗口新写入的输出（VF-04）
            FlowContext ctx = (_context.LastRunContext ?? new FlowContext()).CreatePreviewContext();
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ctx.SetVariable(Variable.Object("Input", "Image", new HalconImage(_context.InputImage), 1));
            }
            return ctx;
        }

        private FlowContext BuildPreviewContextWithoutThrow()
        {
            try
            {
                return BuildPreviewContext();
            }
            catch
            {
                return new FlowContext().CreatePreviewContext();
            }
        }

        private void RefreshViewTargets(bool includeOutput)
        {
            ViewTargetCombo.Items.Clear();
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                ViewTargetCombo.Items.Add(new ViewTarget { Name = "Input.Image", Variable = Variable.Object("Input", "Image", new HalconImage(_context.InputImage), 1) });
            }
            if (_previewContext != null)
            {
                foreach (Variable variable in _previewContext.GetAllVariables())
                {
                    bool isCurrentToolOutput = string.Equals(variable.ModuleName, _tool.ModuleName, StringComparison.OrdinalIgnoreCase);
                    if (includeOutput || !isCurrentToolOutput)
                    {
                        ViewTargetCombo.Items.Add(new ViewTarget { Name = variable.ModuleName + "." + variable.Name, Variable = variable });
                    }
                }
            }
            if (ViewTargetCombo.Items.Count > 0)
            {
                // 预览成功后默认查看当前工具的输出（优先图像 / 区域 / 轮廓），而不是上下文中最后一个变量：
                // 预览上下文派生自上次运行，工具不是流程最后一个节点时，最后一个变量属于下游节点
                List<ViewTarget> targets = ViewTargetCombo.Items.Cast<ViewTarget>().ToList();
                List<ViewTarget> own = includeOutput
                    ? targets.Where(t => string.Equals(t.Variable?.ModuleName, _tool.ModuleName, StringComparison.OrdinalIgnoreCase)).ToList()
                    : new List<ViewTarget>();
                ViewTarget preferred = own.LastOrDefault(t => IsDisplayable(t.Variable?.Value)) ?? own.LastOrDefault();
                ViewTargetCombo.SelectedIndex = preferred != null ? targets.IndexOf(preferred) : ViewTargetCombo.Items.Count - 1;
            }
        }

        private static bool IsDisplayable(object value)
        {
            return value is HalconImage || value is HalconRegion || value is HalconXld || value is HObject;
        }

        private void ShowSelectedTarget()
        {
            if (!(ViewTargetCombo.SelectedItem is ViewTarget target) || target.Variable == null)
            {
                return;
            }

            object value = target.Variable.Value;
            if (value is HalconImage image)
            {
                PreviewImageView.ShowImage(image.Object);
                PreviewImageView.ClearOverlay();
            }
            else if (value is HalconRegion region)
            {
                ShowBaseImage();
                PreviewImageView.SetOverlay(region.Object);
            }
            else if (value is HalconXld xld)
            {
                ShowBaseImage();
                PreviewImageView.SetOverlay(xld.Object);
            }
            else if (value is HObject hObject)
            {
                ShowBaseImage();
                PreviewImageView.SetOverlay(hObject);
            }

            StatsGrid.ItemsSource = new[]
            {
                new StatRow { Name = "路径", Value = target.Name },
                new StatRow { Name = "类型", Value = target.Variable.Kind + "/" + target.Variable.Type },
                new StatRow { Name = "数量", Value = target.Variable.Count.ToString(CultureInfo.CurrentCulture) },
                new StatRow { Name = "值", Value = value?.ToString() ?? "null" }
            };
        }

        private void ShowBaseImage()
        {
            Variable imageVar = _previewContext?.GetAllVariables()
                .FirstOrDefault(v => v.Value is HalconImage);
            if (imageVar?.Value is HalconImage image)
            {
                PreviewImageView.ShowImage(image.Object);
            }
            else if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                PreviewImageView.ShowImage(_context.InputImage);
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                CommitFields();
                DialogResult = true;
                Close();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, "参数保存失败：" + ex.Message, "工具编辑", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
            Close();
        }

        private void CommitFields()
        {
            object[] values = _bindings.Select(ReadValue).ToArray();
            _tool.ModuleName = (ModuleNameText.Text ?? string.Empty).Trim();
            for (int i = 0; i < _bindings.Count; i++)
            {
                _bindings[i].Property.SetValue(_tool, values[i]);
            }
        }

        private void SyncFieldsFromTool()
        {
            ModuleNameText.Text = _tool.ModuleName;
            foreach (FieldBinding binding in _bindings)
            {
                object value = binding.Property.GetValue(_tool);
                if (binding.Editor is TextBox text)
                {
                    text.Text = Convert.ToString(value, CultureInfo.CurrentCulture);
                }
                else if (binding.Editor is ComboBox combo)
                {
                    combo.Text = Convert.ToString(value, CultureInfo.CurrentCulture);
                }
                else if (binding.Editor is CheckBox check && value is bool boolValue)
                {
                    check.IsChecked = boolValue;
                }
            }
        }

        private static object ReadValue(FieldBinding binding)
        {
            if (binding.Editor is TextBox text)
            {
                return ConvertText(text.Text, binding.PropertyType);
            }
            if (binding.Editor is ComboBox combo)
            {
                return binding.PropertyType.IsEnum
                    ? Enum.Parse(binding.PropertyType, combo.Text)
                    : ConvertText(combo.Text, binding.PropertyType);
            }
            if (binding.Editor is CheckBox check)
            {
                return check.IsChecked == true;
            }
            throw new NotSupportedException("不支持的编辑控件：" + binding.Editor.GetType().Name);
        }

        private static object ConvertText(string value, Type type)
        {
            if (type == typeof(string))
            {
                return value;
            }
            if (type == typeof(int))
            {
                return int.Parse(value, CultureInfo.CurrentCulture);
            }
            if (type == typeof(double))
            {
                return double.Parse(value, CultureInfo.CurrentCulture);
            }
            if (type == typeof(bool))
            {
                return bool.Parse(value);
            }
            throw new NotSupportedException("不支持编辑属性类型 " + type.Name);
        }

        private static IEnumerable<PropertyInfo> SerializableProperties(Type type)
        {
            return type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.CanRead && p.CanWrite && p.Name != nameof(ToolBase.ModuleName)
                    && (p.PropertyType == typeof(string)
                        || p.PropertyType == typeof(int)
                        || p.PropertyType == typeof(double)
                        || p.PropertyType == typeof(bool)
                        || p.PropertyType.IsEnum));
        }

        private static bool IsFilePathProperty(PropertyInfo property)
        {
            return property.Name.IndexOf("Path", StringComparison.OrdinalIgnoreCase) >= 0
                || property.Name.IndexOf("File", StringComparison.OrdinalIgnoreCase) >= 0;
        }
    }
}
