using System.Windows;
using System.Windows.Media;

namespace AIGeekTuner.Controls
{
    /// <summary>
    /// 极简趋势线（§31）：Polyline 渲染最近 N 个值。处理 0/1 点、等值、极小尺寸，
    /// 不做除零（§32）。缺口处理：第一版仅绘制最近连续有效段（§33 折中）。
    /// </summary>
    public sealed class Sparkline : FrameworkElement
    {
        public static readonly DependencyProperty PointsProperty =
            DependencyProperty.Register(
                nameof(Points),
                typeof(IReadOnlyList<double>),
                typeof(Sparkline),
                new FrameworkPropertyMetadata(
                    Array.Empty<double>(),
                    FrameworkPropertyMetadataOptions.AffectsRender));

        public IReadOnlyList<double> Points
        {
            get => (IReadOnlyList<double>)GetValue(PointsProperty);
            set => SetValue(PointsProperty, value);
        }

        static Sparkline()
        {
            // 主题样式不需要；默认前景由使用方通过 RenderSetting 注入太重，直接固定 Accent 色。
        }

        protected override void OnRender(DrawingContext drawingContext)
        {
            base.OnRender(drawingContext);

            var width = ActualWidth;
            var height = ActualHeight;
            if (width <= 1 || height <= 1)
            {
                return;
            }

            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0x67, 0xD6, 0xED)), 1.2);
            pen.Freeze();

            var points = Points;
            if (points is null || points.Count == 0)
            {
                return;
            }

            if (points.Count == 1)
            {
                var y = height / 2;
                drawingContext.DrawEllipse(
                    new SolidColorBrush(Color.FromRgb(0x67, 0xD6, 0xED)), null,
                    new Point(width / 2, y), 1.5, 1.5);
                return;
            }

            double min = double.MaxValue, max = double.MinValue;
            foreach (var v in points)
            {
                if (v < min) min = v;
                if (v > max) max = v;
            }

            var range = max - min;
            if (range < 1e-9) range = 1; // 等值：画在中线

            StreamGeometry geometry = new();
            using (var ctx = geometry.Open())
            {
                for (var i = 0; i < points.Count; i++)
                {
                    var x = points.Count == 1 ? 0 : i * width / (points.Count - 1);
                    var norm = (points[i] - min) / range;      // 0..1
                    var y = height - (norm * (height - 4)) - 2; // 上下留 2px
                    var pt = new Point(x, y);
                    if (i == 0) ctx.BeginFigure(pt, false, false);
                    else ctx.LineTo(pt, true, false);
                }
            }

            geometry.Freeze();
            drawingContext.DrawGeometry(null, pen, geometry);
        }
    }
}
