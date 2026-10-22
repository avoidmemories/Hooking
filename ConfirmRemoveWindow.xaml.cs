using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SnapHook.Services;

namespace SnapHook;

/// <summary>
/// Confirms the removal of a game profile.
///
/// A profile carries its own key pairs and there is no undo, so the action is worth one
/// question. It is deliberately short: the name, what else goes with it, and one button
/// to go ahead. Cancelling has two other ways to happen — the close cross and a click
/// outside — so a Cancel button would only repeat them.
/// </summary>
public partial class ConfirmRemoveWindow : Window
{
    public ConfirmRemoveWindow(string displayName)
    {
        InitializeComponent();

        QuestionText.Text = $"Are you sure you want to remove \"{displayName}\" profile?";

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Covers the owner and fades in.
    ///
    /// Rather than blurring, which was tried repeatedly and never looked right, the
    /// backdrop behind the dialog is simply dimmed. The dimming is a rectangle inside
    /// this window, which is stretched to the owner's exact size, so there is nothing
    /// that can reach past the frame.
    /// </summary>
    /// <summary>
    /// Called by the owner just before the dialog is shown.
    ///
    /// The owner is not known until the dialog is created, so it is passed in and applied
    /// immediately — before ShowDialog paints anything.
    /// </summary>
    public void CoverOwnerBeforeShow(Window owner) => CoverOwner(owner);

    /// <summary>
    /// Stretches the dialog over its owner, before it is shown.
    ///
    /// Called as the window is being created rather than when it loads. A window is
    /// centred at its declared size first and only then can be moved and resized, so doing
    /// this later produced two states on screen: a small centred dialog, then a jump to
    /// the full size. Sizing it up front leaves a single state to paint.
    ///
    /// The stretch is what makes a click outside the dialog land on it, which is how the
    /// dialog is dismissed.
    /// </summary>
    private void CoverOwner(Window? owner)
    {
        if (owner is null)
        {
            return;
        }

        Left = owner.Left;
        Top = owner.Top;
        Width = owner.ActualWidth;
        Height = owner.ActualHeight;

        // The scrim is clipped to the same rounding the window underneath draws.
        // A RectangleGeometry is the only clip that follows a corner radius;
        // ClipToBounds clips to the rectangle and leaves the corners showing.
        var radius = FindResource("WindowRadius") is CornerRadius corners
            ? corners.TopLeft
            : 8;

        RoundedRoot.Clip = new RectangleGeometry(new Rect(0, 0, Width, Height), radius, radius);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {

        // Set before the window is shown, so the first painted frame is the start of the
        // animation rather than its end.
        Dialog.Opacity = 0;

        // Started once the window has rendered, so the fade does not overlap the layout
        // pass that follows a resize. Overlapping the two is what made it stutter.
        ContentRendered += (_, _) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                Dialog.BeginAnimation(OpacityProperty,
                    Motion.Value(1, Motion.Move(Motion.Normal), EasingMode.EaseOut));
            }, System.Windows.Threading.DispatcherPriority.Render);
        };
    }

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;

    /// <summary>
    /// A click that reached the backdrop is outside the dialog, which cancels it.
    /// </summary>
    private void Backdrop_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (DialogResult is null)
        {
            DialogResult = false;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
