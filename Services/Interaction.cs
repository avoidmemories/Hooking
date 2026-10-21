using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Hooking.Services;

/// <summary>
/// Click and hover feedback shared by the whole interface.
///
/// These are done in code rather than in the control templates because a name
/// declared inside a property element (<c>Border.RenderTransform</c> and the
/// like) does not join the template name scope, so XAML storyboards cannot
/// target it. Attaching the behaviour here keeps every button consistent.
/// </summary>
public static class Interaction
{
    /// <summary>
    /// Adds the press-shrink and hover-lift feedback to a button. Safe to call
    /// more than once per element.
    /// </summary>
    public static void Attach(Button button, double hoverScale = 1.05, double pressScale = 0.94)
    {
        if (button.GetValue(AttachedProperty) is true)
        {
            return;
        }

        button.SetValue(AttachedProperty, true);

        var transform = new ScaleTransform(1, 1);
        button.RenderTransformOrigin = new Point(0.5, 0.5);
        button.RenderTransform = transform;

        button.MouseEnter += (_, _) => Scale(transform, hoverScale, 170, EasingMode.EaseOut);
        button.MouseLeave += (_, _) => Scale(transform, 1, 220, EasingMode.EaseOut);
        button.PreviewMouseLeftButtonDown += (_, _) => Scale(transform, pressScale, 85, EasingMode.EaseOut);
        button.PreviewMouseLeftButtonUp += (_, _) => Scale(transform, button.IsMouseOver ? hoverScale : 1, 150, EasingMode.EaseOut);
    }

    /// <summary>
    /// Adds a small horizontal nudge on hover. Used by the sidebar entries so
    /// the list feels responsive without moving anything else.
    /// </summary>
    public static void AttachSlide(FrameworkElement element, double distance = 3)
    {
        if (element.GetValue(SlideProperty) is true)
        {
            return;
        }

        element.SetValue(SlideProperty, true);

        var transform = new TranslateTransform(0, 0);
        element.RenderTransform = transform;

        element.MouseEnter += (_, _) => Slide(transform, distance, 180, EasingMode.EaseOut);
        element.MouseLeave += (_, _) => Slide(transform, 0, 220, EasingMode.EaseOut);
    }

    /// <summary>
    /// Walks the visual tree and attaches button feedback to everything that
    /// looks like a button, so newly created chips animate too.
    /// </summary>
    public static void AttachRecursive(DependencyObject root)
    {
        if (root is Button button)
        {
            Attach(button);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            AttachRecursive(VisualTreeHelper.GetChild(root, index));
        }
    }

    public static void Scale(ScaleTransform transform, double to, int milliseconds, EasingMode easing)
    {
        var animation = new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = new CubicEase { EasingMode = easing }
        };

        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
    }

    private static void Slide(TranslateTransform transform, double to, int milliseconds, EasingMode easing)
    {
        transform.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(to, TimeSpan.FromMilliseconds(milliseconds))
            {
                EasingFunction = new CubicEase { EasingMode = easing }
            });
    }

    private static readonly DependencyProperty AttachedProperty =
        DependencyProperty.RegisterAttached("Attached", typeof(bool), typeof(Interaction), new PropertyMetadata(false));


    /// <summary>
    /// Gives a button a slight inward squeeze while it is held.
    ///
    /// Small on purpose: 2.5% is enough to be felt as a press without the layout
    /// appearing to move. The scale is returned on release with a short ease, so the
    /// button springs back rather than snapping.
    ///
    /// Applied in code rather than through a template trigger because a scale
    /// transform declared inside a property element does not join the template's name
    /// scope, so a trigger cannot reach it.
    /// </summary>
    public static void AttachPress(FrameworkElement element, double depth = 0.975)
    {
        if (element.GetValue(PressProperty) is true)
        {
            return;
        }

        element.SetValue(PressProperty, true);

        var transform = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transform;

        void To(double scale, int milliseconds, EasingMode mode) =>
            Scale(transform, scale, milliseconds, mode);

        element.MouseLeftButtonDown += (_, _) => To(depth, Motion.Move(80), EasingMode.EaseOut);
        element.MouseLeftButtonUp += (_, _) => To(1, Motion.Move(140), EasingMode.EaseOut);
        element.MouseLeave += (_, _) => To(1, Motion.Move(140), EasingMode.EaseOut);
    }

    /// <summary>
    /// Applies the press effect to every button in a tree, skipping the ones named in
    /// <paramref name="skip"/>.
    ///
    /// Used once after the window is built. Controls created later, such as pair tiles
    /// and process rows, attach it themselves.
    /// </summary>
    public static void AttachPressRecursive(DependencyObject root, ISet<string>? skip = null)
    {
        if (root is Button button && (skip is null || !skip.Contains(button.Name)))
        {
            AttachPress(button);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            AttachPressRecursive(VisualTreeHelper.GetChild(root, index), skip);
        }
    }

    /// <summary>
    /// Returns the element to its resting size, for a button that was disposed while
    /// held.
    /// </summary>
    public static void ResetPress(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform transform)
        {
            transform.ScaleX = 1;
            transform.ScaleY = 1;
        }
    }

    private static readonly DependencyProperty SlideProperty =
        DependencyProperty.RegisterAttached("Slide", typeof(bool), typeof(Interaction), new PropertyMetadata(false));

    private static readonly DependencyProperty PressProperty =
        DependencyProperty.RegisterAttached("Press", typeof(bool), typeof(Interaction), new PropertyMetadata(false));
    /// <summary>
    /// Gives a list wheel scrolling that moves one row per notch, smoothly.
    ///
    /// Two faults are fixed here.
    ///
    /// The scroll itself: the glide animated a private property that nothing read, and
    /// the offset was only set once the animation had finished. So the movement was a
    /// single jump at the end of a quarter of a second — which is exactly what "it pages
    /// instead of scrolling" looks like. The property now drives the offset on every
    /// frame through its own changed callback, so the list really moves while the glide
    /// runs.
    ///
    /// The step: the distance is measured from a real row rather than fixed, so one notch
    /// advances exactly one process however the list is styled.
    /// </summary>
    public static void AttachSmoothWheel(ItemsControl owner, ScrollViewer viewer)
    {
        if (owner.GetValue(SmoothProperty) is true)
        {
            return;
        }

        owner.SetValue(SmoothProperty, true);

        // The wheel is handled on the list, not on the viewer inside it.
        //
        // A ListBox handles the wheel itself before the event ever reaches the viewer it
        // contains, so a handler there was never called and the list kept its own
        // behaviour. Taking the preview on the list puts this ahead of that.
        //
        // The offset is a distance in pixels here, which only holds because the list
        // declares CanContentScroll false in its markup. With item scrolling the same
        // number means a count of rows, and one notch moves a page.
        owner.PreviewMouseWheel += (_, e) =>
        {
            var step = MeasureRowHeight(viewer);
            var target = viewer.VerticalOffset - Math.Sign(e.Delta) * step;

            Glide(viewer, target);

            // Handled, so the list's own scrolling never runs on top of this one.
            e.Handled = true;
        };
    }

    /// <summary>
    /// Eases the list to an offset.
    ///
    /// The animation runs on an attached property whose changed callback performs the
    /// scroll, which is the only way to make a ScrollViewer move gradually: its offset is
    /// read only, so it cannot be animated directly.
    /// </summary>
    public static void Glide(ScrollViewer viewer, double target)
    {
        var clamped = Math.Max(0, Math.Min(viewer.ScrollableHeight, target));

        if (Math.Abs(clamped - viewer.VerticalOffset) < 0.5)
        {
            return;
        }

        var from = viewer.VerticalOffset;

        if (Motion.ReduceMotion)
        {
            viewer.ScrollToVerticalOffset(clamped);
            return;
        }

        viewer.SetValue(IsGlidingProperty, true);

        var glide = new DoubleAnimation(from, clamped, TimeSpan.FromMilliseconds(GlideMilliseconds))
        {
            EasingFunction = Motion.Ease(EasingMode.EaseOut),
            FillBehavior = FillBehavior.Stop
        };

        glide.Completed += (_, _) =>
        {
            // The property is left at its resting value, so the next glide starts from
            // wherever the list has actually got to.
            viewer.BeginAnimation(AnimatedOffsetProperty, null);
            viewer.SetValue(AnimatedOffsetProperty, 0.0);
            viewer.SetValue(IsGlidingProperty, false);
            viewer.ScrollToVerticalOffset(clamped);
        };

        viewer.BeginAnimation(AnimatedOffsetProperty, glide);
    }

    /// <summary>
    /// The height of one row, including the container's margins.
    ///
    /// Taken from a realised row, so the step follows the styling rather than a constant
    /// that would drift out of date. Falls back to a sensible value when the list is empty
    /// or not yet laid out.
    /// </summary>
    private static double MeasureRowHeight(ScrollViewer viewer)
    {
        const double fallback = 30;

        if (viewer.Content is not Panel panel || panel.Children.Count == 0)
        {
            return fallback;
        }

        if (panel.Children[0] is not FrameworkElement row)
        {
            return fallback;
        }

        var height = row.ActualHeight;

        if (row is ListBoxItem item)
        {
            height += item.Margin.Top + item.Margin.Bottom;
        }

        return height > 8 ? height : fallback;
    }

    /// <summary>How long one glide takes. Short enough to feel attached to the wheel.</summary>
    private const int GlideMilliseconds = 180;

    /// <summary>
    /// The offset the glide is heading for.
    ///
    /// Animated, with a changed callback that performs the actual scroll — the offset
    /// itself cannot be animated because it is read only.
    /// </summary>
    public static readonly DependencyProperty AnimatedOffsetProperty =
        DependencyProperty.RegisterAttached("AnimatedOffset", typeof(double), typeof(Interaction),
            new PropertyMetadata(0.0, OnAnimatedOffsetChanged));

    /// <summary>
    /// True while a glide is running, so the list's own change notifications do not fight
    /// with the animation.
    /// </summary>
    private static readonly DependencyProperty IsGlidingProperty =
        DependencyProperty.RegisterAttached("IsGliding", typeof(bool), typeof(Interaction), new PropertyMetadata(false));

    private static void OnAnimatedOffsetChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        if (target is not ScrollViewer viewer)
        {
            return;
        }

        if (viewer.GetValue(IsGlidingProperty) is not true)
        {
            return;
        }

        viewer.ScrollToVerticalOffset((double)e.NewValue);
    }

    private static readonly DependencyProperty SmoothProperty =
        DependencyProperty.RegisterAttached("Smooth", typeof(bool), typeof(Interaction), new PropertyMetadata(false));
}
