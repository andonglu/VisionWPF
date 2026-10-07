using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace VisionFlow.WpfToolEditors.Controls
{
    public partial class NumericInputControl : UserControl
    {
        private bool _updatingText;

        public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
            nameof(Value), typeof(double), typeof(NumericInputControl),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

        public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
            nameof(Minimum), typeof(double), typeof(NumericInputControl), new PropertyMetadata(double.NegativeInfinity));

        public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
            nameof(Maximum), typeof(double), typeof(NumericInputControl), new PropertyMetadata(double.PositiveInfinity));

        public static readonly DependencyProperty IncrementProperty = DependencyProperty.Register(
            nameof(Increment), typeof(double), typeof(NumericInputControl), new PropertyMetadata(1.0));

        public static readonly DependencyProperty DecimalPlacesProperty = DependencyProperty.Register(
            nameof(DecimalPlaces), typeof(int), typeof(NumericInputControl), new PropertyMetadata(2));

        public event RoutedPropertyChangedEventHandler<double> ValueChanged;

        public NumericInputControl()
        {
            InitializeComponent();
            UpdateText();
        }

        public double Value
        {
            get => (double)GetValue(ValueProperty);
            set => SetValue(ValueProperty, Coerce(value));
        }

        public double Minimum
        {
            get => (double)GetValue(MinimumProperty);
            set => SetValue(MinimumProperty, value);
        }

        public double Maximum
        {
            get => (double)GetValue(MaximumProperty);
            set => SetValue(MaximumProperty, value);
        }

        public double Increment
        {
            get => (double)GetValue(IncrementProperty);
            set => SetValue(IncrementProperty, value);
        }

        public int DecimalPlaces
        {
            get => (int)GetValue(DecimalPlacesProperty);
            set => SetValue(DecimalPlacesProperty, value);
        }

        private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (NumericInputControl)d;
            control.UpdateText();
            control.ValueChanged?.Invoke(control, new RoutedPropertyChangedEventArgs<double>((double)e.OldValue, (double)e.NewValue));
        }

        private void Increase_Click(object sender, RoutedEventArgs e)
        {
            Value += Increment;
        }

        private void Decrease_Click(object sender, RoutedEventArgs e)
        {
            Value -= Increment;
        }

        private void ValueText_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_updatingText)
            {
                return;
            }
            if (double.TryParse(ValueText.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
                || double.TryParse(ValueText.Text, NumberStyles.Float, CultureInfo.CurrentCulture, out value))
            {
                Value = value;
            }
        }

        private void ValueText_LostFocus(object sender, RoutedEventArgs e)
        {
            UpdateText();
        }

        private void ValueText_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            e.Handled = !IsNumericText(ValueText.Text, ValueText.SelectionStart, ValueText.SelectionLength, e.Text);
        }

        private static bool IsNumericText(string current, int selectionStart, int selectionLength, string input)
        {
            string text = current.Remove(selectionStart, selectionLength).Insert(selectionStart, input);
            return string.IsNullOrWhiteSpace(text)
                || text == "-"
                || text == "."
                || text == "-."
                || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)
                || double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out _);
        }

        private double Coerce(double value)
        {
            if (value < Minimum) return Minimum;
            if (value > Maximum) return Maximum;
            return value;
        }

        private void UpdateText()
        {
            if (ValueText == null)
            {
                return;
            }
            _updatingText = true;
            try
            {
                ValueText.Text = FormatValue(Value, DecimalPlaces);
            }
            finally
            {
                _updatingText = false;
            }
        }

        /// <summary>
        /// 按 DecimalPlaces 位小数显示；值的精度超过该位数时补足有效小数（最多 6 位），
        /// 避免把输入的 0.02 显示成 0。精度不超过 DecimalPlaces 的值显示与原来完全相同。
        /// </summary>
        public static string FormatValue(double value, int decimalPlaces)
        {
            int places = Math.Max(0, decimalPlaces);
            string text = value.ToString("F" + places, CultureInfo.InvariantCulture);
            if (places >= 6 || double.IsNaN(value) || double.IsInfinity(value)
                || double.Parse(text, CultureInfo.InvariantCulture) == value)
            {
                return text;
            }
            return value.ToString("0." + new string('0', places) + new string('#', 6 - places), CultureInfo.InvariantCulture);
        }
    }
}
