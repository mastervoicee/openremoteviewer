using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using WpfPoint = System.Windows.Point;
using WpfColor = System.Windows.Media.Color;

namespace OpenRemoteViewer;

/// <summary>
/// Draws freehand strokes on a Canvas. Every stroke disappears 3 seconds
/// after the last point was added to it.
/// Used both on the client (overlay window) and on the admin (preview above the image).
/// </summary>
public sealed class StrokePainter
{
    private const int MaxPointsPerStroke = 4000;
    private static readonly TimeSpan HoldTime = TimeSpan.FromSeconds(3);
    private static readonly Duration FadeTime = new(TimeSpan.FromSeconds(0.45));

    private readonly Canvas _canvas;
    private readonly Dictionary<int, Stroke> _strokes = [];

    private sealed class Stroke
    {
        public required Polyline Line;
        public required DispatcherTimer Timer;
    }

    public StrokePainter(Canvas canvas) => _canvas = canvas;

    /// <summary>
    /// Add a point to a stroke. <paramref name="isStart"/> begins a new stroke with this id.
    /// Coordinates are in the canvas' own units.
    /// </summary>
    public void AddPoint(int strokeId, bool isStart, double x, double y)
    {
        if (isStart)
            RemoveStroke(strokeId);

        if (!_strokes.TryGetValue(strokeId, out var stroke))
        {
            stroke = CreateStroke(strokeId);
            _strokes[strokeId] = stroke;
            _canvas.Children.Add(stroke.Line);
        }

        if (stroke.Line.Points.Count < MaxPointsPerStroke)
            stroke.Line.Points.Add(new WpfPoint(x, y));

        // каждая новая точка отодвигает исчезновение — линия живёт 3 сек после последнего движения
        stroke.Timer.Stop();
        stroke.Timer.Start();
    }

    public void Clear()
    {
        foreach (var id in _strokes.Keys.ToList())
            RemoveStroke(id);
    }

    private Stroke CreateStroke(int strokeId)
    {
        var line = new Polyline
        {
            Stroke = new SolidColorBrush(WpfColor.FromRgb(0xFF, 0x2D, 0x2D)),
            StrokeThickness = 4,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            IsHitTestVisible = false,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 6,
                ShadowDepth = 0,
                Opacity = 0.85
            }
        };

        var timer = new DispatcherTimer { Interval = HoldTime };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            FadeOut(strokeId);
        };

        return new Stroke { Line = line, Timer = timer };
    }

    private void FadeOut(int strokeId)
    {
        if (!_strokes.TryGetValue(strokeId, out var stroke)) return;

        var anim = new DoubleAnimation(1, 0, FadeTime);
        anim.Completed += (_, _) => RemoveStroke(strokeId);
        stroke.Line.BeginAnimation(UIElement.OpacityProperty, anim);
    }

    private void RemoveStroke(int strokeId)
    {
        if (!_strokes.Remove(strokeId, out var stroke)) return;
        stroke.Timer.Stop();
        stroke.Line.BeginAnimation(UIElement.OpacityProperty, null);
        _canvas.Children.Remove(stroke.Line);
    }
}
