using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace AudioBookPlayer.Controls
{
    /// <summary>
    /// 自绘调色盘（HSV）。
    ///
    /// 上面是"饱和度 × 明度"方块（底色跟着色相走），下面是色相带，再下面是预览 + 十六进制。
    /// 点住拖动即可取色；<see cref="Color"/> 是双向依赖属性，直接绑到 ViewModel 上就行。
    ///
    /// 之所以自己画：Windows 自带的颜色对话框是 90 年代的 WinForms 弹窗，和这套界面完全不搭。
    /// </summary>
    public partial class ColorPickerPanel : UserControl
    {
        public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(
            nameof(Color),
            typeof(Color),
            typeof(ColorPickerPanel),
            new FrameworkPropertyMetadata(
                Colors.White,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault,
                OnColorChanged));

        private double _hue;
        private double _saturation = 1;
        private double _value = 1;

        /// <summary>拖动过程中避免"外部改色 → 重算 HSV → 又改色"的回环。</summary>
        private bool _updating;

        public ColorPickerPanel()
        {
            InitializeComponent();

            // 布局定下来 / 尺寸变化都要重画：弹窗首次打开、换缩放、换显示器都可能重排
            Loaded += (_, _) => UpdateVisuals();
            SizeChanged += (_, _) => UpdateVisuals();
        }

        /// <summary>当前颜色（双向）。</summary>
        public Color Color
        {
            get => (Color)GetValue(ColorProperty);
            set => SetValue(ColorProperty, value);
        }

        private static void OnColorChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ColorPickerPanel panel)
            {
                panel.SyncFromColor((Color)e.NewValue);
            }
        }

        private void SyncFromColor(Color color)
        {
            if (_updating)
            {
                return;
            }

            _updating = true;
            try
            {
                var (h, s, v) = RgbToHsv(color);
                _hue = h;
                _saturation = s;
                _value = v;
                UpdateVisuals();
            }
            finally
            {
                _updating = false;
            }
        }

        private void UpdateVisuals()
        {
            // 还没量出尺寸就没法算位置；一旦有了尺寸（含离屏渲染）就画
            if (SvCanvas.ActualWidth <= 0 || HueCanvas.ActualWidth <= 0)
            {
                return;
            }

            HueLayer.Fill = new SolidColorBrush(HsvToRgb(_hue, 1, 1));

            // 取色点：x = 饱和度，y = 1 - 明度
            var x = _saturation * SvCanvas.ActualWidth;
            var y = (1 - _value) * SvCanvas.ActualHeight;
            Canvas.SetLeft(SvThumb, Clamp(x - (SvThumb.Width / 2), 0, Math.Max(0, SvCanvas.ActualWidth - SvThumb.Width)));
            Canvas.SetTop(SvThumb, Clamp(y - (SvThumb.Height / 2), 0, Math.Max(0, SvCanvas.ActualHeight - SvThumb.Height)));

            // 色相滑块
            var hueX = (_hue / 360.0) * HueCanvas.ActualWidth;
            Canvas.SetLeft(HueThumb, Clamp(hueX - (HueThumb.Width / 2), 0, Math.Max(0, HueCanvas.ActualWidth - HueThumb.Width)));
            Canvas.SetTop(HueThumb, 1);
            HueThumb.Fill = new SolidColorBrush(HsvToRgb(_hue, _saturation <= 0 ? 1 : _saturation, _value <= 0 ? 1 : _value));

            var current = HsvToRgb(_hue, _saturation, _value);
            PreviewBorder.Background = new SolidColorBrush(current);

            var hex = $"#{current.R:X2}{current.G:X2}{current.B:X2}";
            if (!HexBox.IsKeyboardFocusWithin && HexBox.Text != hex)
            {
                HexBox.Text = hex;
            }
        }

        private void ApplyHsv()
        {
            var color = HsvToRgb(_hue, _saturation, _value);

            _updating = true;
            try
            {
                Color = color;
            }
            finally
            {
                _updating = false;
            }

            UpdateVisuals();
        }

        // ---------------- 饱和度 / 明度方块 ----------------

        private void OnSvMouseDown(object sender, MouseButtonEventArgs e)
        {
            SvCanvas.CaptureMouse();
            UpdateSvFromPoint(e.GetPosition(SvCanvas));
        }

        private void OnSvMouseMove(object sender, MouseEventArgs e)
        {
            if (SvCanvas.IsMouseCaptured)
            {
                UpdateSvFromPoint(e.GetPosition(SvCanvas));
            }
        }

        private void OnSvMouseUp(object sender, MouseButtonEventArgs e) => SvCanvas.ReleaseMouseCapture();

        private void UpdateSvFromPoint(Point point)
        {
            var width = Math.Max(1, SvCanvas.ActualWidth);
            var height = Math.Max(1, SvCanvas.ActualHeight);

            _saturation = Clamp(point.X / width, 0, 1);
            _value = 1 - Clamp(point.Y / height, 0, 1);
            ApplyHsv();
        }

        // ---------------- 色相带 ----------------

        private void OnHueMouseDown(object sender, MouseButtonEventArgs e)
        {
            HueCanvas.CaptureMouse();
            UpdateHueFromPoint(e.GetPosition(HueCanvas));
        }

        private void OnHueMouseMove(object sender, MouseEventArgs e)
        {
            if (HueCanvas.IsMouseCaptured)
            {
                UpdateHueFromPoint(e.GetPosition(HueCanvas));
            }
        }

        private void OnHueMouseUp(object sender, MouseButtonEventArgs e) => HueCanvas.ReleaseMouseCapture();

        private void UpdateHueFromPoint(Point point)
        {
            var width = Math.Max(1, HueCanvas.ActualWidth);
            _hue = Clamp(point.X / width, 0, 1) * 360.0;
            ApplyHsv();
        }

        // ---------------- 十六进制输入 ----------------

        private void OnHexKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter)
            {
                return;
            }

            CommitHex();
            e.Handled = true;
        }

        private void OnHexCommit(object sender, KeyboardFocusChangedEventArgs e) => CommitHex();

        private void CommitHex()
        {
            try
            {
                var text = HexBox.Text.Trim();
                if (!text.StartsWith('#'))
                {
                    text = "#" + text;
                }

                var parsed = (Color)ColorConverter.ConvertFromString(text)!;
                SyncFromColor(parsed);
                ApplyHsv();
            }
            catch (Exception)
            {
                // 输入不合法就还原显示
                UpdateVisuals();
            }
        }

        // ---------------- 颜色换算 ----------------

        private static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;

        private static (double Hue, double Saturation, double Value) RgbToHsv(Color color)
        {
            double r = color.R / 255.0, g = color.G / 255.0, b = color.B / 255.0;
            var max = Math.Max(r, Math.Max(g, b));
            var min = Math.Min(r, Math.Min(g, b));
            var delta = max - min;

            double hue;
            if (delta < 0.00001)
            {
                hue = 0;
            }
            else if (max == r)
            {
                hue = 60 * (((g - b) / delta) % 6);
            }
            else if (max == g)
            {
                hue = 60 * (((b - r) / delta) + 2);
            }
            else
            {
                hue = 60 * (((r - g) / delta) + 4);
            }

            if (hue < 0)
            {
                hue += 360;
            }

            var saturation = max <= 0 ? 0 : delta / max;
            return (hue, saturation, max);
        }

        private static Color HsvToRgb(double hue, double saturation, double value)
        {
            hue = ((hue % 360) + 360) % 360;
            saturation = Clamp(saturation, 0, 1);
            value = Clamp(value, 0, 1);

            var c = value * saturation;
            var x = c * (1 - Math.Abs(((hue / 60) % 2) - 1));
            var m = value - c;

            double r, g, b;
            if (hue < 60) { r = c; g = x; b = 0; }
            else if (hue < 120) { r = x; g = c; b = 0; }
            else if (hue < 180) { r = 0; g = c; b = x; }
            else if (hue < 240) { r = 0; g = x; b = c; }
            else if (hue < 300) { r = x; g = 0; b = c; }
            else { r = c; g = 0; b = x; }

            return Color.FromRgb(
                (byte)Math.Round((r + m) * 255),
                (byte)Math.Round((g + m) * 255),
                (byte)Math.Round((b + m) * 255));
        }
    }
}
