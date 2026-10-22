using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace SnapHook.Services;

/// <summary>
/// A point of light travelling around the edge of a rounded rectangle, with a
/// soft trail settling behind it.
///
/// Modelled on the StarBorder component: rather than a border that switches on,
/// an accent orbits the outline, so the control reads as powered rather than as
/// merely toggled. The light is placed by sampling the outline, which keeps its
/// speed even over straight edges and corners alike.
/// </summary>
public sealed class StarBorderEffect
{
    private const double TrailScale = 2.6;

    private readonly Canvas _canvas;
    private readonly Rectangle _head;
    private readonly Rectangle _trail;
    private readonly List<(double X, double Y)> _path = [];
    private readonly Storyboard _storyboard = new();

    private StarBorderEffect(Canvas canvas, Rectangle head, Rectangle trail)
    {
        _canvas = canvas;
        _head = head;
        _trail = trail;
    }

    /// <summary>
    /// Lays the moving light over a host border.
    ///
    /// The light lives in a canvas that covers the host and ignores the mouse, so
    /// it never intercepts a click meant for the switch underneath.
    /// </summary>
    public static StarBorderEffect Attach(Border host)
    {
        var canvas = new Canvas
        {
            IsHitTestVisible = false,
            ClipToBounds = false,
            Visibility = Visibility.Collapsed
        };

        var trail = MakeDot(3.4, 0.30);
        var head = MakeDot(2.0, 1.0);

        canvas.Children.Add(trail);
        canvas.Children.Add(head);

        // A grid rather than a raw child, so the canvas can sit on top of the
        // host's own background instead of replacing it.
        if (host.Child is Grid grid)
        {
            grid.Children.Add(canvas);
        }

        var effect = new StarBorderEffect(canvas, head, trail);
        host.SizeChanged += (_, _) => effect.BuildPath(host.ActualWidth, host.ActualHeight);
        return effect;
    }

    private static Rectangle MakeDot(double size, double opacity) => new()
    {
        Width = size,
        Height = size,
        Fill = new SolidColorBrush(Colors.White) { Opacity = opacity },
        RadiusX = size / 2,
        RadiusY = size / 2
    };

    /// <summary>
    /// Samples the outline into a list of points, once per size change.
    ///
    /// Walking a list of points is far cheaper per frame than solving the shape
    /// analytically, and it makes the speed even everywhere by construction.
    /// </summary>
    private void BuildPath(double width, double height)
    {
        const double radius = 8;

        _path.Clear();

        if (width <= 2 || height <= 2)
        {
            return;
        }

        // One sample every few pixels: fine enough to look smooth, coarse enough
        // that the list stays short.
        const double step = 3;

        void Line(double x1, double y1, double x2, double y2)
        {
            var length = Math.Sqrt((x2 - x1) * (x2 - x1) + (y2 - y1) * (y2 - y1));
            var count = Math.Max(1, (int)(length / step));
            for (var i = 0; i < count; i++)
            {
                var t = i / (double)count;
                _path.Add((x1 + (x2 - x1) * t, y1 + (y2 - y1) * t));
            }
        }

        void Arc(double cx, double cy, double from, double to)
        {
            var count = Math.Max(2, (int)(Math.PI * radius / 2 / step));
            for (var i = 0; i < count; i++)
            {
                var angle = from + (to - from) * (i / (double)count);
                _path.Add((cx + Math.Cos(angle) * radius, cy + Math.Sin(angle) * radius));
            }
        }

        var right = width - radius;
        var bottom = height - radius;

        Line(radius, 0, right, 0);
        Arc(right, radius, -Math.PI / 2, 0);
        Line(width, radius, width, bottom);
        Arc(right, bottom, 0, Math.PI / 2);
        Line(right, height, radius, height);
        Arc(radius, bottom, Math.PI / 2, Math.PI);
        Line(0, bottom, 0, radius);
        Arc(radius, radius, Math.PI, Math.PI * 1.5);
    }

    /// <summary>
    /// Sets the light moving. Does nothing when the system asks for reduced
    /// motion, where a static accent is the right answer.
    /// </summary>
    public void Start(TimeSpan lap)
    {
        if (Motion.ReduceMotion || _path.Count == 0)
        {
            return;
        }

        // Already running, so nothing to do.
        //
        // This used to build a new timer on every call without stopping the old
        // one. Start is reached from the settings pass, which runs on every change,
        // so the timers piled up: each one ticking sixty times a second forever,
        // each holding its own closure. That is what made the interface slow down
        // over a session and only recover after a restart.
        if (_ticker is not null)
        {
            return;
        }

        _canvas.Visibility = Visibility.Visible;

        _ticker = new System.Windows.Threading.DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            System.Windows.Threading.DispatcherPriority.Render,
            (_, _) => Step(lap),
            _canvas.Dispatcher);

        _started = DateTime.UtcNow;
        _ticker.Start();
    }

    private System.Windows.Threading.DispatcherTimer? _ticker;
    private DateTime _started;

    private void Step(TimeSpan lap)
    {
        if (_path.Count == 0)
        {
            return;
        }

        var progress = (DateTime.UtcNow - _started).TotalSeconds / lap.TotalSeconds % 1.0;
        var head = (int)(progress * _path.Count);

        Place(_head, _path[head % _path.Count]);

        // The trail sits behind the head along the path, so it always lags rather
        // than overtaking on a short lap.
        var trail = (head - (int)(_path.Count * 0.06) + _path.Count) % _path.Count;
        Place(_trail, _path[trail]);
    }

    private void Place(FrameworkElement dot, (double X, double Y) point)
    {
        Canvas.SetLeft(dot, point.X - dot.Width / 2);
        Canvas.SetTop(dot, point.Y - dot.Height / 2);
    }

    public void Stop()
    {
        _ticker?.Stop();
        _ticker = null;
        _canvas.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Releases the timer for good, for when the host is going away.
    /// </summary>
    public void Dispose() => Stop();
}
