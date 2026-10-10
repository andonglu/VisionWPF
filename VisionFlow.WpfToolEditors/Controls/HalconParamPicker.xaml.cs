using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using VisionFlow.Tools.Halcon;

namespace VisionFlow.WpfToolEditors.Controls
{
    /// <summary>
    /// HALCON 模型参数候选选择器（可复用）：参数名下拉（名称 — 中文说明前段）+ 说明 + 候选值下拉 / 数值输入 + “添加”。
    /// 通过 <see cref="ParameterAdded"/> 产出“名称=值”字符串，不依赖具体工具；数据源为 <see cref="HalconParamCatalog"/> 的 scope。
    /// </summary>
    public partial class HalconParamPicker : UserControl
    {
        /// <summary>参数目录 scope（如 "datacode2d:QR Code"、"barcode1d"）；变化时刷新参数名下拉。</summary>
        public static readonly DependencyProperty ScopeProperty =
            DependencyProperty.Register(nameof(Scope), typeof(string), typeof(HalconParamPicker),
                new PropertyMetadata(null, OnScopeChanged));

        /// <summary>“添加”后产出 名称=值；同名参数重复添加时先移除旧条目（列表内参数唯一）。</summary>
        public event Action<string> ParameterAdded;

        private sealed class ParamItem
        {
            public HalconParamEntry Entry { get; }
            public string Display { get; }

            public ParamItem(HalconParamEntry entry)
            {
                Entry = entry;
                string summary = entry.Description ?? string.Empty;
                int cut = summary.IndexOfAny(new[] { '。', '；', ';', ':' });
                if (cut > 0)
                {
                    summary = summary.Substring(0, cut);
                }
                if (summary.Length > 30)
                {
                    summary = summary.Substring(0, 30) + "…";
                }
                Display = summary.Length > 0 ? entry.Name + " — " + summary : entry.Name;
            }

            public override string ToString()
            {
                return Display;
            }
        }

        public HalconParamPicker()
        {
            InitializeComponent();
        }

        public string Scope
        {
            get { return (string)GetValue(ScopeProperty); }
            set { SetValue(ScopeProperty, value); }
        }

        private static void OnScopeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            ((HalconParamPicker)d).Reload();
        }

        private void Reload()
        {
            ParamNameCombo.Items.Clear();
            DescriptionText.Text = string.Empty;
            ValueCombo.Visibility = Visibility.Collapsed;
            ValueText.Visibility = Visibility.Collapsed;
            IReadOnlyList<HalconParamEntry> entries = HalconParamCatalog.GetEntries(Scope);
            foreach (HalconParamEntry entry in entries)
            {
                ParamNameCombo.Items.Add(new ParamItem(entry));
            }
            if (ParamNameCombo.Items.Count > 0)
            {
                ParamNameCombo.SelectedIndex = 0;
            }
        }

        private void ParamNameCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (!(ParamNameCombo.SelectedItem is ParamItem item))
            {
                return;
            }
            HalconParamEntry entry = item.Entry;
            DescriptionText.Text = entry.Description;
            if (entry.Values.Length > 0)
            {
                ValueCombo.Items.Clear();
                foreach (string value in entry.Values)
                {
                    ValueCombo.Items.Add(value);
                }
                int defaultIndex = string.IsNullOrEmpty(entry.Default)
                    ? -1
                    : Enumerable.Range(0, entry.Values.Length).FirstOrDefault(i => entry.Values[i] == entry.Default, -1);
                ValueCombo.SelectedIndex = defaultIndex >= 0 ? defaultIndex : 0;
                ValueCombo.Visibility = Visibility.Visible;
                ValueText.Visibility = Visibility.Collapsed;
            }
            else
            {
                ValueCombo.Visibility = Visibility.Collapsed;
                ValueText.Text = entry.Default ?? string.Empty;
                ValueText.Visibility = Visibility.Visible;
            }
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            if (!(ParamNameCombo.SelectedItem is ParamItem item))
            {
                return;
            }
            string value = item.Entry.Values.Length > 0
                ? ValueCombo.SelectedItem as string
                : (ValueText.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(value))
            {
                return;
            }
            ParameterAdded?.Invoke(item.Entry.Name + "=" + value);
        }
    }
}
