using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;

namespace TrimFetch.Helpers;

internal static class ContourPathBuilder
{
    public static PathGeometry CreateRoundedRectContour(double width, double height, double cornerRadius, double strokeThickness)
    {
        var inset = strokeThickness / 2;
        var maxRadius = Math.Max(0, Math.Min(width, height) / 2 - inset);
        var radius = Math.Clamp(cornerRadius, 0, maxRadius);
        var left = inset;
        var top = inset;
        var right = width - inset;
        var bottom = height - inset;
        var start = new Point(width / 2, top);

        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = true,
        };

        if (radius <= 0)
        {
            figure.Segments.Add(new LineSegment { Point = new Point(right, top) });
            figure.Segments.Add(new LineSegment { Point = new Point(right, bottom) });
            figure.Segments.Add(new LineSegment { Point = new Point(left, bottom) });
            figure.Segments.Add(new LineSegment { Point = new Point(left, top) });
        }
        else
        {
            figure.Segments.Add(new LineSegment { Point = new Point(right - radius, top) });
            figure.Segments.Add(CreateArc(new Point(right, top + radius), radius));
            figure.Segments.Add(new LineSegment { Point = new Point(right, bottom - radius) });
            figure.Segments.Add(CreateArc(new Point(right - radius, bottom), radius));
            figure.Segments.Add(new LineSegment { Point = new Point(left + radius, bottom) });
            figure.Segments.Add(CreateArc(new Point(left, bottom - radius), radius));
            figure.Segments.Add(new LineSegment { Point = new Point(left, top + radius) });
            figure.Segments.Add(CreateArc(new Point(left + radius, top), radius));
        }

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    public static void ApplyProgressStroke(Microsoft.UI.Xaml.Shapes.Path path, double pathLength, double progress, double strokeThickness)
    {
        var visible = Math.Max(0, pathLength * Math.Clamp(progress, 0, 1));
        path.StrokeThickness = strokeThickness;
        path.StrokeDashArray = [pathLength, pathLength];
        path.StrokeDashOffset = pathLength - visible;
    }

    public static double MeasurePathLength(PathGeometry geometry)
    {
        var length = 0d;
        foreach (var figure in geometry.Figures)
        {
            var current = figure.StartPoint;
            foreach (var segment in figure.Segments)
            {
                length += segment switch
                {
                    LineSegment line => Distance(current, line.Point),
                    ArcSegment arc => ArcLength(current, arc),
                    _ => 0,
                };

                current = segment switch
                {
                    LineSegment line => line.Point,
                    ArcSegment arc => arc.Point,
                    _ => current,
                };
            }

            if (figure.IsClosed)
            {
                length += Distance(current, figure.StartPoint);
            }
        }

        return length;
    }

    private static double ArcLength(Point from, ArcSegment arc)
    {
        var radius = (arc.Size.Width + arc.Size.Height) / 2;
        if (radius <= 0)
        {
            return Distance(from, arc.Point);
        }

        var chord = Distance(from, arc.Point);
        if (chord <= 0)
        {
            return 0;
        }

        var ratio = Math.Clamp(chord / (2 * radius), -1, 1);
        return radius * 2 * Math.Asin(ratio);
    }

    private static double Distance(Point a, Point b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt((dx * dx) + (dy * dy));
    }

    private static ArcSegment CreateArc(Point point, double radius) =>
        new()
        {
            Point = point,
            Size = new Size(radius, radius),
            SweepDirection = SweepDirection.Clockwise,
            IsLargeArc = false,
        };
}
