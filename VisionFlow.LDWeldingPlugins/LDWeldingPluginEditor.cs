using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Reflection;
using System.Windows.Forms;
using HalconDotNet;
using VisionFlow.Core;
using VisionFlow.Editing;
using VisionFlow.Ui;
using VisionFlow.Variables;

namespace VisionFlow.LDWeldingPlugins
{
    [ToolEditor(typeof(LDMeanImageTool))]
    [ToolEditor(typeof(LDAddSubImageTool))]
    [ToolEditor(typeof(LDDecomposeChannelsTool))]
    [ToolEditor(typeof(LDCompose3ImageTool))]
    [ToolEditor(typeof(LDTransColorSpaceTool))]
    [ToolEditor(typeof(LDRegionDifferenceTool))]
    [ToolEditor(typeof(LDRegionUnion2Tool))]
    [ToolEditor(typeof(LDShapeTransTool))]
    [ToolEditor(typeof(LDRegionUnion1Tool))]
    [ToolEditor(typeof(LDMorphologyRectTool))]
    [ToolEditor(typeof(LDMorphologyCircleTool))]
    [ToolEditor(typeof(LDRegionFeaturesTool))]
    [ToolEditor(typeof(LDContourCreateTool))]
    [ToolEditor(typeof(LDSelectContourTool))]
    [ToolEditor(typeof(LDConcatXldTool))]
    [ToolEditor(typeof(LDSegmentXldTool))]
    [ToolEditor(typeof(LDXldFeaturesTool))]
    [ToolEditor(typeof(LDFitLineTool))]
    [ToolEditor(typeof(LDFitCircleTool))]
    [ToolEditor(typeof(LDIntersectionLinesTool))]
    [ToolEditor(typeof(LDAffinePointTool))]
    [ToolEditor(typeof(LDBarcode1DTool))]
    public sealed partial class LDWeldingPluginEditor : Form
    {
        private readonly ToolBase _tool;
        private readonly ToolEditContext _context;

        public LDWeldingPluginEditor(ToolBase tool, ToolEditContext context)
        {
            _tool = tool ?? throw new ArgumentNullException(nameof(tool));
            _context = context ?? new ToolEditContext();

            InitializeComponent();
            Text = $"LDWelding 插件配置 - {_tool.ModuleName}";
            LblTitle.Text = Text;
            BuildRows();
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
                Font = new Font(Font, FontStyle.Bold),
                ForeColor = VisionFlowUiStyle.Primary,
                Padding = new Padding(0, 10, 0, 4),
                TextAlign = ContentAlignment.MiddleLeft
            };
            AddControl(label, 0, 2);
        }

        private void AddInfo(string text)
        {
            AddRow("", new Label { Dock = DockStyle.Fill, Text = text, ForeColor = Color.Gray });
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
            int row = MainPanel.RowCount++;
            MainPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, VisionFlowUiStyle.EditorRowHeight));
            MainPanel.Controls.Add(control, column, row);
            if (columnSpan > 1)
            {
                MainPanel.SetColumnSpan(control, columnSpan);
            }
        }

        private void BtnClose_Click(object sender, EventArgs e)
        {
            DialogResult = DialogResult.Cancel;
            Close();
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
    }
}
