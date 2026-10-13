using System.Collections.Generic;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace DualNet.App.Controls;

/// <summary>Tiny sparkline graph showing the last ~90 seconds of download speed.</summary>
public sealed class SpeedGraph : Avalonia.Controls.Control
{
    private readonly double[] _samples = new double[90];
    private int _head;
    private int _filled;

    public void Push(double bytesPerSecond)
    {
        _samples[_head] = bytesPerSecond;
        _head = (_head + 1) % _samples.Length;
        _filled = Math.Min(_filled + 1, _samples.Length);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        var bounds = new Rect(0, 0, Bounds.Width, Bounds.Height);
        if (bounds.Width < 4 || bounds.Height < 4 || _filled == 0)
            return;

        double max = 1;
        for (int i = 0; i < _filled; i++)
            max = Math.Max(max, _samples[i]);

        double stepX = bounds.Width / (_samples.Length - 1);
        var points = new List<Point>(_filled);

        // oldest -> newest, right-aligned
        for (int i = 0; i < _filled; i++)
        {
            int idx = (_head - _filled + i + _samples.Length * 2) % _samples.Length;
            double x = bounds.Width - (_filled - 1 - i) * stepX;
            double y = bounds.Height - 2 - (_samples[idx] / max) * (bounds.Height - 6);
            points.Add(new Point(x, y));
        }

        var geometry = new StreamGeometry();
        using (var g = geometry.Open())
        {
            g.BeginFigure(new Point(points[0].X, bounds.Height), isFilled: true);
            g.LineTo(points[0]);
            for (int i = 1; i < points.Count; i++)
                g.LineTo(points[i]);
            g.LineTo(new Point(points[^1].X, bounds.Height));
            g.EndFigure(true);
        }

        var fillBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#6622D3EE"), 0),
                new GradientStop(Color.Parse("#0022D3EE"), 1)
            }
        };
        context.DrawGeometry(fillBrush, null, geometry);

        var pen = new Pen(new SolidColorBrush(Color.Parse("#22D3EE")), 1.6,
            lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round);
        for (int i = 1; i < points.Count; i++)
            context.DrawLine(pen, points[i - 1], points[i]);
    }
}
