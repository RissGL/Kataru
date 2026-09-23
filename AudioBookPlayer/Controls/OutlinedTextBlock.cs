using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace AudioBookPlayer.Controls
{
    /// <summary>
    /// 带描边的文字控件。
    ///
    /// 实现方式：把文本转成几何图形，用一支圆角连接的 Pen 描边、再用 Fill 填充。
    /// 相比"叠 4 个偏移 TextBlock"的做法，描边更均匀、字距不会变，也不会有重影。
    /// 不使用任何 Shader / 第三方库。
    /// </summary>
    public sealed class OutlinedTextBlock : FrameworkElement
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
            nameof(Text),
            typeof(string),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                string.Empty,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
            nameof(Fill),
            typeof(Brush),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                Brushes.White,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(
            nameof(Stroke),
            typeof(Brush),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                Brushes.Black,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty StrokeThicknessProperty = DependencyProperty.Register(
            nameof(StrokeThickness),
            typeof(double),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                3.5,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
            nameof(FontFamily),
            typeof(FontFamily),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                SystemFonts.MessageFontFamily,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
            nameof(FontSize),
            typeof(double),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                32.0,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
            nameof(FontWeight),
            typeof(FontWeight),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                FontWeights.Normal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty FontStyleProperty = DependencyProperty.Register(
            nameof(FontStyle),
            typeof(FontStyle),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                FontStyles.Normal,
                FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        public static readonly DependencyProperty TextAlignmentProperty = DependencyProperty.Register(
            nameof(TextAlignment),
            typeof(System.Windows.TextAlignment),
            typeof(OutlinedTextBlock),
            new FrameworkPropertyMetadata(
                System.Windows.TextAlignment.Center,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnVisualChanged));

        private FormattedText? _formattedText;
        private Geometry? _geometry;
        private bool _dirty = true;

        public string Text
        {
            get => (string)GetValue(TextProperty);
            set => SetValue(TextProperty, value);
        }

        public Brush Fill
        {
            get => (Brush)GetValue(FillProperty);
            set => SetValue(FillProperty, value);
        }

        public Brush Stroke
        {
            get => (Brush)GetValue(StrokeProperty);
            set => SetValue(StrokeProperty, value);
        }

        public double StrokeThickness
        {
            get => (double)GetValue(StrokeThicknessProperty);
            set => SetValue(StrokeThicknessProperty, value);
        }

        public FontFamily FontFamily
        {
            get => (FontFamily)GetValue(FontFamilyProperty);
            set => SetValue(FontFamilyProperty, value);
        }

        public double FontSize
        {
            get => (double)GetValue(FontSizeProperty);
            set => SetValue(FontSizeProperty, value);
        }

        public FontWeight FontWeight
        {
            get => (FontWeight)GetValue(FontWeightProperty);
            set => SetValue(FontWeightProperty, value);
        }

        public FontStyle FontStyle
        {
            get => (FontStyle)GetValue(FontStyleProperty);
            set => SetValue(FontStyleProperty, value);
        }

        public TextAlignment TextAlignment
        {
            get => (TextAlignment)GetValue(TextAlignmentProperty);
            set => SetValue(TextAlignmentProperty, value);
        }

        protected override Size MeasureOverride(Size availableSize)
        {
            var text = BuildFormattedText(double.IsInfinity(availableSize.Width) ? double.PositiveInfinity : availableSize.Width);            if (text == null)
            {
                return new Size(0, 0);
            }

            var width = double.IsInfinity(availableSize.Width)
                ? text.Width
                : Math.Min(availableSize.Width, text.Width);

            return new Size(width + StrokeThickness, text.Height + StrokeThickness);
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var size = RenderSize;
            if (size.Width <= 1 || size.Height <= 1)
            {
                return;
            }

            var geometry = GetGeometry(size.Width);
            if (geometry == null)
            {
                return;
            }

            var text = _formattedText;
            var thickness = Math.Max(0, StrokeThickness);
            var pad = thickness / 2.0;

            // 水平居中：FormattedText.TextAlignment = Center + MaxTextWidth = 可用宽度
            // 垂直居中：按文本块整体高度做偏移，单行 / 双行都会稳稳居中
            var offsetY = (size.Height - (text?.Height ?? 0)) / 2.0;
            if (offsetY < pad)
            {
                offsetY = pad;
            }

            var transform = new TranslateTransform(pad, offsetY);
            var pen = thickness > 0 && Stroke != null
                ? CreatePen(Stroke, thickness)
                : null;

            drawingContext.PushTransform(transform);
            drawingContext.DrawGeometry(Fill ?? Brushes.White, pen, geometry);
            drawingContext.Pop();
        }

        protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
        {
            base.OnRenderSizeChanged(sizeInfo);
            _dirty = true; // 宽度变化会影响自动换行
        }

        private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var control = (OutlinedTextBlock)d;
            control._dirty = true;
            control.InvalidateVisual();
        }

        private static Pen CreatePen(Brush brush, double thickness)
        {
            var pen = new Pen(brush, thickness)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                MiterLimit = 1.0,
            };

            pen.Freeze();
            return pen;
        }

        private Geometry? GetGeometry(double availableWidth)
        {
            if (_dirty || _formattedText == null || _geometry == null)
            {
                _formattedText = BuildFormattedText(availableWidth);
                _geometry = _formattedText?.BuildGeometry(new Point(0, 0));
                _dirty = false;
            }

            return _geometry;
        }

        private FormattedText? BuildFormattedText(double availableWidth)
        {
            var value = Text;
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            var dpi = VisualTreeHelper.GetDpi(this);
            var typeface = new Typeface(FontFamily, FontStyle, FontWeight, FontStretches.Normal);

            var formatted = new FormattedText(
                value,
                CultureInfo.CurrentUICulture,
                FlowDirection.LeftToRight,
                typeface,
                FontSize <= 0 ? 1 : FontSize,
                Fill ?? Brushes.White,
                dpi.PixelsPerDip)
            {
                TextAlignment = TextAlignment,
            };

            if (!double.IsInfinity(availableWidth) && availableWidth > 0)
            {
                formatted.MaxTextWidth = availableWidth;
            }

            return formatted;
        }
    }
}
