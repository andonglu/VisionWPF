using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Controls;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Variables;

namespace VisionFlow.Ui
{
    public sealed class FrmRegionToolEdit : Form
    {
        private readonly ToolBase _tool;
        private readonly ToolEditContext _context;
        private readonly TableLayoutPanel _paramPanel;
        private readonly ComboBox _viewTarget;
        private readonly HalconImageView _preview;
        private readonly Label _status;
        private readonly DataGridView _statsGrid;
        private FlowContext _previewContext;

        public FrmRegionToolEdit(ToolBase tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();

            VisionFlowUiStyle.ApplyDialog(this, 1040, 680);
            Text = $"视觉处理配置 - {_tool.ModuleName}";

            var root = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3,
                Padding = new Padding(VisionFlowUiStyle.Padding)
            };
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, VisionFlowUiStyle.HeaderHeight));
            root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            root.RowStyles.Add(new RowStyle(SizeType.Absolute, VisionFlowUiStyle.FooterHeight));
            Controls.Add(root);

            root.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = Text,
                Font = VisionFlowUiStyle.TitleFont,
                ForeColor = VisionFlowUiStyle.Primary,
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            var split = new SplitContainer
            {
                Dock = DockStyle.Fill,
                SplitterDistance = 320,
                FixedPanel = FixedPanel.Panel1
            };
            root.Controls.Add(split, 0, 1);

            _paramPanel = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                AutoScroll = true,
                ColumnCount = 2,
                RowCount = 0,
                BackColor = Color.White
            };
            _paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, VisionFlowUiStyle.EditorLabelWidth));
            _paramPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            split.Panel1.Controls.Add(_paramPanel);

            var previewRoot = new TableLayoutPanel
            {
                Dock = DockStyle.Fill,
                ColumnCount = 1,
                RowCount = 3
            };
            previewRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
            previewRoot.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewRoot.RowStyles.Add(new RowStyle(SizeType.Absolute, 126));
            split.Panel2.Controls.Add(previewRoot);

            var viewBar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4 };
            viewBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
            viewBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            viewBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            viewBar.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 92));
            previewRoot.Controls.Add(viewBar, 0, 0);

            viewBar.Controls.Add(new Label
            {
                Dock = DockStyle.Fill,
                Text = "查看内容",
                TextAlign = ContentAlignment.MiddleLeft
            }, 0, 0);

            _viewTarget = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
            VisionFlowUiStyle.ApplyEditor(_viewTarget);
            _viewTarget.SelectedIndexChanged += (s, e) => ShowSelectedTarget();
            viewBar.Controls.Add(_viewTarget, 1, 0);

            var run = new Button { Text = "运行预览" };
            VisionFlowUiStyle.ApplyButton(run);
            run.Click += (s, e) => RunPreview();
            viewBar.Controls.Add(run, 2, 0);

            var fit = new Button { Text = "适应" };
            VisionFlowUiStyle.ApplyButton(fit, primary: false);
            fit.Click += (s, e) => _preview.FitToWindow();
            viewBar.Controls.Add(fit, 3, 0);

            _preview = new HalconImageView { Dock = DockStyle.Fill };
            previewRoot.Controls.Add(_preview, 0, 1);

            var bottom = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
            bottom.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
            bottom.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            previewRoot.Controls.Add(bottom, 0, 2);

            _status = new Label
            {
                Dock = DockStyle.Fill,
                Padding = new Padding(4, 0, 0, 0),
                TextAlign = ContentAlignment.MiddleLeft,
                ForeColor = VisionFlowUiStyle.TextColor
            };
            bottom.Controls.Add(_status, 0, 0);

            _statsGrid = new DataGridView
            {
                Dock = DockStyle.Fill,
                ReadOnly = true,
                AllowUserToAddRows = false,
                AllowUserToDeleteRows = false,
                RowHeadersVisible = false,
                SelectionMode = DataGridViewSelectionMode.FullRowSelect,
                AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill
            };
            VisionFlowUiStyle.ApplyDataGridView(_statsGrid);
            _statsGrid.Columns.Add("Name", "项目");
            _statsGrid.Columns.Add("Value", "值");
            bottom.Controls.Add(_statsGrid, 0, 1);

            var footer = new FlowLayoutPanel
            {
                Dock = DockStyle.Fill,
                FlowDirection = FlowDirection.RightToLeft
            };
            var close = new Button { Text = "关闭" };
            VisionFlowUiStyle.ApplyButton(close);
            close.Click += (s, e) => Close();
            footer.Controls.Add(close);
            root.Controls.Add(footer, 0, 2);

            BuildRows();
            RunPreview();
        }

        private void BuildRows()
        {
            AddHeader("基础");
            AddTextRow("模块名", _tool.ModuleName, value => _tool.ModuleName = value.Trim());

            AddHeader("输入引用");
            IReadOnlyList<ToolInputRefDef> inputRefs = ToolMetadata.GetInputRefs(_tool.GetType());
            if (inputRefs.Count == 0)
            {
                AddInfo("无输入引用");
            }
            foreach (ToolInputRefDef input in inputRefs)
            {
                AddInputRefRow(input);
            }

            AddHeader("参数");
            bool hasScalar = false;
            foreach (PropertyInfo property in SerializableProperties(_tool.GetType()))
            {
                if (inputRefs.Any(r => r.PropertyName == property.Name))
                {
                    continue;
                }

                hasScalar = true;
                AddScalarRow(property);
            }
            if (!hasScalar)
            {
                AddInfo("无可配置标量参数");
            }
        }

        private void RunPreview()
        {
            try
            {
                _previewContext = BuildPreviewContext();
                NodeResult result = _tool.Run(_previewContext);
                RefreshViewTargets(includeOutput: result.IsSuccess);
                if (!result.IsSuccess)
                {
                    _status.Text = $"预览运行失败：{result.Message}";
                }
            }
            catch (Exception ex)
            {
                _previewContext = BuildPreviewContextWithoutThrow();
                RefreshViewTargets(includeOutput: false);
                _status.Text = "预览运行失败：" + ex.Message;
            }
        }

        private FlowContext BuildPreviewContext()
        {
            var ctx = new FlowContext();
            if (_context.LastRunContext != null)
            {
                foreach (Variable variable in _context.LastRunContext.GetAllVariables())
                {
                    ctx.SetVariable(variable);
                }
            }
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
                return new FlowContext();
            }
        }

        private void RefreshViewTargets(bool includeOutput)
        {
            string previous = (_viewTarget.SelectedItem as PreviewItem)?.Key;
            _viewTarget.Items.Clear();

            foreach (ToolInputRefDef input in ToolMetadata.GetInputRefs(_tool.GetType()).Where(i => i.ExpectedType == typeof(HalconImage)))
            {
                string path = input.Property.GetValue(_tool) as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _viewTarget.Items.Add(new PreviewItem("image:" + input.PropertyName, $"输入图像 - {input.DisplayName}", path, PreviewKind.InputImage));
                }
            }

            foreach (ToolInputRefDef input in ToolMetadata.GetInputRefs(_tool.GetType()).Where(i => i.ExpectedType == typeof(HalconRegion)))
            {
                string path = input.Property.GetValue(_tool) as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _viewTarget.Items.Add(new PreviewItem("region:" + input.PropertyName, $"输入Region - {input.DisplayName}", path, PreviewKind.InputRegion));
                }
            }

            foreach (ToolInputRefDef input in ToolMetadata.GetInputRefs(_tool.GetType()).Where(i => i.ExpectedType == typeof(HalconXld)))
            {
                string path = input.Property.GetValue(_tool) as string;
                if (!string.IsNullOrWhiteSpace(path))
                {
                    _viewTarget.Items.Add(new PreviewItem("xld:" + input.PropertyName, $"输入XLD - {input.DisplayName}", path, PreviewKind.InputXld));
                }
            }

            if (includeOutput && TryGetOutputImage(_previewContext, out _))
            {
                _viewTarget.Items.Add(new PreviewItem("output-image", "输出图像", null, PreviewKind.OutputImage));
            }

            if (includeOutput && TryGetOutputRegion(_previewContext, out _))
            {
                _viewTarget.Items.Add(new PreviewItem("output-region", "输出Region", null, PreviewKind.OutputRegion));
            }

            if (includeOutput && TryGetOutputXld(_previewContext, out _))
            {
                _viewTarget.Items.Add(new PreviewItem("output-xld", "输出XLD", null, PreviewKind.OutputXld));
            }

            if (_viewTarget.Items.Count == 0)
            {
                _preview.ClearImage();
                _statsGrid.Rows.Clear();
                return;
            }

            int selected = FindPreferredSelection(previous);
            if (selected < 0)
            {
                selected = 0;
            }
            _viewTarget.SelectedIndex = selected;
        }

        private int FindPreferredSelection(string previous)
        {
            if (!string.IsNullOrWhiteSpace(previous))
            {
                for (int i = 0; i < _viewTarget.Items.Count; i++)
                {
                    if (((PreviewItem)_viewTarget.Items[i]).Key == previous)
                    {
                        return i;
                    }
                }
            }

            for (int i = 0; i < _viewTarget.Items.Count; i++)
            {
                PreviewKind kind = ((PreviewItem)_viewTarget.Items[i]).Kind;
                if (kind == PreviewKind.OutputRegion || kind == PreviewKind.OutputXld || kind == PreviewKind.OutputImage)
                {
                    return i;
                }
            }
            return -1;
        }

        private void ShowSelectedTarget()
        {
            if (!(_viewTarget.SelectedItem is PreviewItem item))
            {
                return;
            }

            try
            {
                if (item.Kind == PreviewKind.InputImage)
                {
                    HObject image = VariableReference.Parse(item.Path).Resolve<HalconImage>(_previewContext).Object;
                    ShowImage(image);
                    FillImageStats(image);
                    _status.Text = $"正在查看：{item.Text}";
                    return;
                }

                if (item.Kind == PreviewKind.OutputImage)
                {
                    HObject image = TryGetOutputImage(_previewContext, out HObject outputImage) ? outputImage : null;
                    ShowImage(image);
                    FillImageStats(image);
                    _status.Text = $"正在查看：{item.Text}";
                    return;
                }

                if (item.Kind == PreviewKind.InputXld || item.Kind == PreviewKind.OutputXld)
                {
                    HObject xld = item.Kind == PreviewKind.OutputXld
                        ? TryGetOutputXld(_previewContext, out HObject outputXld) ? outputXld : null
                        : VariableReference.Parse(item.Path).Resolve<HalconXld>(_previewContext).Object;
                    if (xld == null || !xld.IsInitialized())
                    {
                        ClearPreview($"{item.Text} 为空");
                        return;
                    }
                    ShowOverlayObject(xld);
                    FillXldStats(xld);
                    _status.Text = $"正在查看：{item.Text}";
                    return;
                }

                HObject region = item.Kind == PreviewKind.OutputRegion
                    ? TryGetOutputRegion(_previewContext, out HObject output) ? output : null
                    : VariableReference.Parse(item.Path).Resolve<HalconRegion>(_previewContext).Object;

                if (region == null || !region.IsInitialized())
                {
                    ClearPreview($"{item.Text} 为空");
                    return;
                }

                ShowRegion(region);
                FillStats(region);
                _status.Text = $"正在查看：{item.Text}";
            }
            catch (Exception ex)
            {
                ClearPreview("显示失败：" + ex.Message);
            }
        }

        private bool TryGetOutputRegion(FlowContext ctx, out HObject region)
        {
            region = null;
            if (ctx == null)
            {
                return false;
            }

            Variable variable;
            if (ctx.TryGetVariable(_tool.ModuleName, "Region", out variable) && variable.Value is HalconRegion output)
            {
                region = output.Object;
                return true;
            }

            foreach (Variable v in ctx.GetAllVariables().Where(v => string.Equals(v.ModuleName, _tool.ModuleName, StringComparison.OrdinalIgnoreCase)))
            {
                if (v.Value is HalconRegion anyRegion)
                {
                    region = anyRegion.Object;
                    return true;
                }
            }
            return false;
        }

        private bool TryGetOutputImage(FlowContext ctx, out HObject image)
        {
            image = null;
            if (ctx == null)
            {
                return false;
            }

            foreach (Variable v in ctx.GetAllVariables().Where(v => string.Equals(v.ModuleName, _tool.ModuleName, StringComparison.OrdinalIgnoreCase)))
            {
                if (v.Value is HalconImage halconImage)
                {
                    image = halconImage.Object;
                    return true;
                }
            }
            return false;
        }

        private bool TryGetOutputXld(FlowContext ctx, out HObject xld)
        {
            xld = null;
            if (ctx == null)
            {
                return false;
            }

            Variable variable;
            if (ctx.TryGetVariable(_tool.ModuleName, "Xld", out variable) && variable.Value is HalconXld output)
            {
                xld = output.Object;
                return true;
            }

            foreach (Variable v in ctx.GetAllVariables().Where(v => string.Equals(v.ModuleName, _tool.ModuleName, StringComparison.OrdinalIgnoreCase)))
            {
                if (v.Value is HalconXld anyXld)
                {
                    xld = anyXld.Object;
                    return true;
                }
            }
            return false;
        }

        private void ShowRegion(HObject region)
        {
            ShowOverlayObject(region);
        }

        private void ShowOverlayObject(HObject overlay)
        {
            HObject image = ResolveBackgroundImage();
            HObject previewImage = null;
            HObject previewOverlay = null;
            bool disposeImage = false;
            bool disposeOverlay = false;
            try
            {
                if (image != null && image.IsInitialized())
                {
                    previewImage = image;
                    previewOverlay = overlay;
                }
                else
                {
                    CreateObjectPreview(overlay, out previewImage, out previewOverlay);
                    disposeImage = true;
                    disposeOverlay = true;
                }

                _preview.ShowImage(previewImage);
                _preview.SetOverlay(previewOverlay);
            }
            finally
            {
                if (disposeImage)
                {
                    previewImage?.Dispose();
                }
                if (disposeOverlay)
                {
                    previewOverlay?.Dispose();
                }
            }
        }

        private void ShowImage(HObject image)
        {
            if (image == null || !image.IsInitialized())
            {
                ClearPreview("图像为空");
                return;
            }
            _preview.ShowImage(image);
            _preview.ClearOverlay();
        }

        private void ClearPreview(string message)
        {
            _preview.ClearImage();
            _statsGrid.Rows.Clear();
            _status.Text = message;
        }

        private HObject ResolveBackgroundImage()
        {
            if (_context.InputImage != null && _context.InputImage.IsInitialized())
            {
                return _context.InputImage;
            }

            if (_previewContext != null
                && _previewContext.TryGetVariable("Input", "Image", out Variable variable)
                && variable.Value is HalconImage image)
            {
                return image.Object;
            }
            return null;
        }

        private static void CreateObjectPreview(HObject obj, out HObject image, out HObject movedObject)
        {
            try
            {
                CreateRegionPreview(obj, out image, out movedObject);
            }
            catch (HalconException)
            {
                CreateXldPreview(obj, out image, out movedObject);
            }
        }

        private static void CreateRegionPreview(HObject region, out HObject image, out HObject movedRegion)
        {
            HOperatorSet.Union1(region, out HObject union);
            try
            {
                HOperatorSet.SmallestRectangle1(union, out HTuple row1, out HTuple col1, out HTuple row2, out HTuple col2);
                int width = Math.Max(32, (int)Math.Ceiling(col2.D - col1.D + 21));
                int height = Math.Max(32, (int)Math.Ceiling(row2.D - row1.D + 21));
                HOperatorSet.MoveRegion(region, out movedRegion, -row1.D + 10, -col1.D + 10);
                HOperatorSet.GenImageConst(out HObject empty, "byte", width, height);
                try
                {
                    HOperatorSet.PaintRegion(movedRegion, empty, out image, 80, "fill");
                }
                finally
                {
                    empty.Dispose();
                }
            }
            finally
            {
                union.Dispose();
            }
        }

        private static void CreateXldPreview(HObject xld, out HObject image, out HObject movedXld)
        {
            HOperatorSet.SmallestRectangle1Xld(xld, out HTuple row1, out HTuple col1, out HTuple row2, out HTuple col2);
            int width = Math.Max(32, (int)Math.Ceiling(col2.D - col1.D + 21));
            int height = Math.Max(32, (int)Math.Ceiling(row2.D - row1.D + 21));
            HOperatorSet.HomMat2dIdentity(out HTuple homMat);
            HOperatorSet.HomMat2dTranslate(homMat, -row1.D + 10, -col1.D + 10, out HTuple movedMat);
            HOperatorSet.AffineTransContourXld(xld, out movedXld, movedMat);
            HOperatorSet.GenImageConst(out image, "byte", width, height);
        }

        private void FillStats(HObject region)
        {
            _statsGrid.Rows.Clear();
            HOperatorSet.CountObj(region, out HTuple count);
            AddStat("区域数量", count.I.ToString());
            HOperatorSet.AreaCenter(region, out HTuple area, out HTuple row, out HTuple column);
            AddStat("总面积", SumTuple(area).ToString("F0"));
            if (area.Length > 0)
            {
                AddStat("首区域面积", area[0].D.ToString("F0"));
                AddStat("首区域中心Row", row[0].D.ToString("F2"));
                AddStat("首区域中心Column", column[0].D.ToString("F2"));
            }
        }

        private void FillImageStats(HObject image)
        {
            _statsGrid.Rows.Clear();
            HOperatorSet.GetImageSize(image, out HTuple width, out HTuple height);
            HOperatorSet.CountChannels(image, out HTuple channels);
            AddStat("图像宽度", width.I.ToString());
            AddStat("图像高度", height.I.ToString());
            AddStat("通道数", channels.I.ToString());
        }

        private void FillXldStats(HObject xld)
        {
            _statsGrid.Rows.Clear();
            HOperatorSet.CountObj(xld, out HTuple count);
            AddStat("轮廓数量", count.I.ToString());
            HOperatorSet.LengthXld(xld, out HTuple length);
            AddStat("总长度", SumTuple(length).ToString("F2"));
            if (length.Length > 0)
            {
                AddStat("首轮廓长度", length[0].D.ToString("F2"));
            }
        }

        private static double SumTuple(HTuple tuple)
        {
            double sum = 0;
            for (int i = 0; i < tuple.Length; i++)
            {
                sum += tuple[i].D;
            }
            return sum;
        }

        private void AddStat(string name, string value)
        {
            _statsGrid.Rows.Add(name, value);
        }

        private void AddInputRefRow(ToolInputRefDef input)
        {
            var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDown };
            VisionFlowUiStyle.ApplyEditor(combo);
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
                foreach (RefCandidate candidate in RefCandidateService.ForInput(_context.Root, _context.Node, input.ExpectedType))
                {
                    if (!combo.Items.Contains(candidate.Path))
                    {
                        combo.Items.Add(candidate.Path);
                    }
                }
            }

            combo.Text = input.Property.GetValue(_tool) as string ?? string.Empty;
            combo.SelectedIndexChanged += (s, e) => input.Property.SetValue(_tool, combo.Text);
            combo.Leave += (s, e) => input.Property.SetValue(_tool, combo.Text);
            AddRow(input.DisplayName, combo);
        }

        private void AddScalarRow(PropertyInfo property)
        {
            Type type = property.PropertyType;
            if (type == typeof(bool))
            {
                var check = new CheckBox { Dock = DockStyle.Left, Checked = (bool)property.GetValue(_tool) };
                VisionFlowUiStyle.ApplyEditor(check);
                check.CheckedChanged += (s, e) => property.SetValue(_tool, check.Checked);
                AddRow(property.Name, check);
                return;
            }

            if (type.IsEnum)
            {
                var combo = new ComboBox { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
                VisionFlowUiStyle.ApplyEditor(combo);
                combo.Items.AddRange(Enum.GetNames(type));
                combo.SelectedItem = property.GetValue(_tool).ToString();
                combo.SelectedIndexChanged += (s, e) => property.SetValue(_tool, Enum.Parse(type, combo.Text));
                AddRow(property.Name, combo);
                return;
            }

            string text = Convert.ToString(property.GetValue(_tool));
            AddTextRow(property.Name, text, value => property.SetValue(_tool, ConvertText(value, type)));
        }

        private void AddTextRow(string label, string value, Action<string> commit)
        {
            var textBox = new TextBox { Dock = DockStyle.Fill, Text = value ?? string.Empty };
            VisionFlowUiStyle.ApplyEditor(textBox);
            textBox.Leave += (s, e) => CommitText(textBox, commit);
            textBox.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Enter)
                {
                    CommitText(textBox, commit);
                    e.SuppressKeyPress = true;
                }
            };
            AddRow(label, textBox);
        }

        private void CommitText(TextBox textBox, Action<string> commit)
        {
            try
            {
                commit(textBox.Text);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "参数错误", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private static object ConvertText(string value, Type type)
        {
            if (type == typeof(string))
            {
                return value;
            }
            if (type == typeof(int))
            {
                return int.Parse(value);
            }
            if (type == typeof(double))
            {
                return double.Parse(value);
            }
            throw new NotSupportedException($"不支持编辑属性类型 {type.Name}");
        }

        private void AddHeader(string text)
        {
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = text,
                Font = VisionFlowUiStyle.GroupTitleFont,
                ForeColor = VisionFlowUiStyle.Primary,
                Padding = new Padding(4, 10, 4, 4),
                TextAlign = ContentAlignment.MiddleLeft
            };
            AddControl(label, 0, 2);
        }

        private void AddInfo(string text)
        {
            AddRow(string.Empty, new Label { Dock = DockStyle.Fill, Text = text, ForeColor = Color.Gray });
        }

        private void AddRow(string labelText, Control editor)
        {
            var label = new Label
            {
                Dock = DockStyle.Fill,
                Text = labelText,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(4, 0, 4, 0)
            };
            AddControl(label, 0, 1);
            AddControl(editor, 1, 1);
        }

        private void AddControl(Control control, int column, int columnSpan)
        {
            int row = _paramPanel.RowCount++;
            _paramPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, VisionFlowUiStyle.EditorRowHeight));
            _paramPanel.Controls.Add(control, column, row);
            if (columnSpan > 1)
            {
                _paramPanel.SetColumnSpan(control, columnSpan);
            }
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

        private sealed class PreviewItem
        {
            public string Key { get; }
            public string Text { get; }
            public string Path { get; }
            public PreviewKind Kind { get; }

            public PreviewItem(string key, string text, string path, PreviewKind kind)
            {
                Key = key;
                Text = text;
                Path = path;
                Kind = kind;
            }

            public override string ToString()
            {
                return Text;
            }
        }

        private enum PreviewKind
        {
            InputImage,
            InputRegion,
            InputXld,
            OutputImage,
            OutputRegion,
            OutputXld
        }
    }
}
