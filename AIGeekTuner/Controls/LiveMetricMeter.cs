using System;
using System.Windows;
using System.Windows.Media;

namespace AIGeekTuner.Controls
{
    /// <summary>
    /// V2-M4.5C Gate E/G：轻量实时量程条（track + fill + current/low/high marker）。
    /// 样式契约（纯蓝白主题，禁止渐变/发光/动画/红黄绿健康等级）：
    /// track = 白 ~15% 透明度；fill = 极浅青 ~80%；current = 白色圆点；
    /// low marker = 白 ~38%；high marker = 白 ~65%。
    /// 超出 visual scale 的值 clamp 到末端（数字由外层显示真实值）。
    /// </summary>
    public sealed class LiveMetricMeter : FrameworkElement
    {
        public static readonly DependencyProperty ScaleMinProperty =
            DependencyProperty.Register(nameof(ScaleMin), typeof(double), typeof(LiveMetricMeter),
                new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty ScaleMaxProperty =
            DependencyProperty.Register(nameof(ScaleMax), typeof(double), typeof(LiveMetricMeter),
                new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty CurrentProperty =
            DependencyProperty.Register(nameof(Current), typeof(double), typeof(LiveMetricMeter),
                new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty LowProperty =
            DependencyProperty.Register(nameof(Low), typeof(object), typeof(LiveMetricMeter),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public static readonly DependencyProperty HighProperty =
            DependencyProperty.Register(nameof(High), typeof(object), typeof(LiveMetricMeter),
                new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

        public double ScaleMin
        {
            get => (double)GetValue(ScaleMinProperty);
            set => SetValue(ScaleMinProperty, value);
        }

        public double ScaleMax
        {
            get => (double)GetValue(ScaleMaxProperty);
            set => SetValue(ScaleMaxProperty, value);
        }

        public double Current
        {
            get => (double)GetValue(CurrentProperty);
            set => SetValue(CurrentProperty, value);
        }

        /// <summary>object 承载 double?（XAML 绑定 nullable 值）。</summary>
        public object? Low
        {
            get => GetValue(LowProperty);
            set => SetValue(LowProperty, value);
        }

        public object? High
        {
            get => GetValue(HighProperty);
            set => SetValue(HighProperty, value);
        }

        private static readonly Brush TrackBrush = Freeze(
            new SolidColorBrush(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)));  // ~15%
        private static readonly Brush FillBrush = Freeze(
            new SolidColorBrush(Color.FromArgb(0xCC, 0xB9, 0xEC, 0xF7)));  // 浅青 ~80%
        private static readonly Brush CurrentBrush = Freeze(
            new SolidColorBrush(Colors.White));
        private static readonly Brush LowBrush = Freeze(
            new SolidColorBrush(Color.FromArgb(0x61, 0xFF, 0xFF, 0xFF)));  // ~38%
        private static readonly Brush HighBrush = Freeze(
            new SolidColorBrush(Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)));  // ~65%

        private static SolidColorBrush Freeze(SolidColorBrush brush)
        {
            brush.Freeze();
            return brush;
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 1 || height <= 1 || double.IsNaN(Current))
            {
                return;
            }

            var trackHeight = Math.Min(6, height);
            var trackY = (height - trackHeight) / 2;

            // track
            DrawRoundedBar(drawingContext, TrackBrush, 0, width, trackY, trackHeight);

            var geometry = Services.Telemetry.Presentation.LiveMetricMeterMath.Compute(
                ScaleMin, ScaleMax, Current, AsDouble(Low), AsDouble(High));

            // fill（0 → current）
            if (geometry.NormalizedFillEnd > 0.001)
            {
                DrawRoundedBar(
                    drawingContext, FillBrush, 0,
                    Math.Max(trackHeight, width * geometry.NormalizedFillEnd),
                    trackY, trackHeight);
            }

            // low marker（细、淡）
            if (geometry.NormalizedLow.HasValue)
            {
                DrawMarker(drawingContext, LowBrush, width * geometry.NormalizedLow.Value, trackY, trackHeight);
            }

            // high marker（细、稍亮）
            if (geometry.NormalizedHigh.HasValue)
            {
                DrawMarker(drawingContext, HighBrush, width * geometry.NormalizedHigh.Value, trackY, trackHeight);
            }

            // current marker（白色圆点）
            var cx = width * geometry.NormalizedCurrent;
            drawingContext.DrawEllipse(
                CurrentBrush, null,
                new Point(cx, trackY + trackHeight / 2), 3.2, 3.2);
        }

        private static double? AsDouble(object? value) => value switch
        {
            double d => d,
            null => null,
            _ => null,
        };

        private static void DrawRoundedBar(
            DrawingContext context, Brush brush, double x, double width, double y, double height)
        {
            var rect = new Rect(x, y, Math.Max(0, width), height);
            var radius = height / 2;
            var geometry = new RectangleGeometry(rect, radius, radius);
            geometry.Freeze();
            context.DrawGeometry(brush, null, geometry);
        }

        private static void DrawMarker(
            DrawingContext context, Brush brush, double centerX, double y, double height)
        {
            var rect = new Rect(centerX - 1.1, y - 2, 2.2, height + 4);
            var geometry = new RectangleGeometry(rect, 1, 1);
            geometry.Freeze();
            context.DrawGeometry(brush, null, geometry);
        }
    }
}
