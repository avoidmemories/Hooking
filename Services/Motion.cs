using System.Windows;
using System.Windows.Media.Animation;

namespace SnapHook.Services;

/// <summary>
/// Every duration and easing curve in the interface, in one place.
///
/// Keeping them together is what makes the motion feel consistent: a hover and a
/// slide are tuned against each other rather than invented per call site. Nothing
/// here exceeds 250 ms, so the interface always answers within a frame or two of
/// a glance.
///
/// When the system asks for reduced motion, <see cref="Move"/> turns off anything
/// that displaces or scales an element and shortens what remains, so only colour
/// and opacity change and they do so almost at once.
/// </summary>
public static class Motion
{
    /// <summary>Hover and other immediate feedback.</summary>
    public const int Fast = 120;

    /// <summary>The default for a change of state.</summary>
    public const int Normal = 180;

    /// <summary>Transitions between whole screens or selections.</summary>
    public const int Slow = 220;

    /// <summary>How long a chip takes to shrink away when removed.</summary>
    public const int Exit = 150;

    /// <summary>Stagger between successive items appearing.</summary>
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(30);

    private static bool? _reduceMotion;

    /// <summary>
    /// True when movement should be suppressed.
    ///
    /// Read once from the system setting. WPF exposes it through
    /// ClientAreaAnimation, which the accessibility options turn off when the
    /// user asks for less motion.
    /// </summary>
    public static bool ReduceMotion => _reduceMotion ??= Detect();

    /// <summary>
    /// Duration to use for anything that moves. Collapses to a brief fade when
    /// reduced motion is on, so the change is still perceivable without travel.
    /// </summary>
    public static int Move(int milliseconds) => ReduceMotion ? Math.Min(milliseconds, 100) : milliseconds;

    /// <summary>
    /// Duration for a pure colour or opacity change, which stays meaningful even
    /// when motion is suppressed.
    /// </summary>
    public static int Fade(int milliseconds) => ReduceMotion ? Math.Min(milliseconds, 100) : milliseconds;

    public static DoubleAnimation Value(double to, int milliseconds, EasingMode mode = EasingMode.EaseOut) =>
        new(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = Ease(mode)
        };

    public static DoubleAnimation Value(double from, double to, int milliseconds, EasingMode mode = EasingMode.EaseOut) =>
        new(from, to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = Ease(mode)
        };

    public static ThicknessAnimation Thickness(Thickness to, int milliseconds, EasingMode mode = EasingMode.EaseInOut) =>
        new(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = Ease(mode),
            FillBehavior = FillBehavior.HoldEnd
        };

    /// <summary>
    /// Animates a margin from a known starting value.
    ///
    /// The starting value must be given explicitly: an animation that only names a
    /// target takes its start from the property, which for a margin that has just
    /// been cleared is the value the last animation ended on rather than where the
    /// element actually is.
    /// </summary>
    public static ThicknessAnimation Thickness(
        double fromLeft, Thickness to, int milliseconds, EasingMode mode = EasingMode.EaseInOut) =>
        new(new Thickness(fromLeft, to.Top, to.Right, to.Bottom), to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = Ease(mode),
            FillBehavior = FillBehavior.HoldEnd
        };

    public static ColorAnimation Color(System.Windows.Media.Color to, int milliseconds, EasingMode mode = EasingMode.EaseOut) =>
        new(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = Ease(mode),
            FillBehavior = FillBehavior.HoldEnd
        };

    /// <summary>
    /// Ease out for anything appearing, ease in-out for anything moving from one
    /// settled state to another. No easing here overshoots: a value that springs
    /// past its target reads as a bounce, which is explicitly not wanted.
    /// </summary>
    public static IEasingFunction Ease(EasingMode mode) => new CubicEase { EasingMode = mode };

    private static bool Detect()
    {
        try
        {
            // ClientAreaAnimation is off when the user disables animations in the
            // accessibility settings, which is the desktop equivalent of the
            // prefers-reduced-motion media query on the web.
            return !SystemParameters.ClientAreaAnimation;
        }
        catch
        {
            return false;
        }
    }
}
