using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using SnapHook.Services;

namespace SnapHook;

public partial class RenameWindow : Window
{
    public string Value { get; private set; }

    /// <summary>The path as it stands when the dialog is accepted.</summary>
    public string? Path { get; private set; }

    public RenameWindow(string initial, string? path)
    {
        InitializeComponent();

        Value = initial;
        Path = path;

        ValueBox.Text = initial;
        PathBox.Text = path ?? string.Empty;

        ValueBox.SelectAll();

        Loaded += OnLoaded;
    }

    /// <summary>
    /// Covers the owner window and fades the dialog in.
    ///
    /// Stretching over the owner is what makes "click outside to dismiss" work: the click
    /// lands on this window rather than passing through to the application behind. The
    /// backdrop it carries is a plain dimmed rectangle, which cannot reach past the frame
    /// because it is inside a window of exactly the owner's size.
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

        ValueBox.Focus();
        ValueBox.SelectAll();

        // Held at zero until the window is on screen, so the fade has somewhere to start.
        Dialog.BeginAnimation(OpacityProperty, null);
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

    private void ValueBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Save_Click(sender, e);
        }
        else if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    /// <summary>
    /// Picks an executable for the profile, leaving the name alone.
    ///
    /// The name is what the user recognises and the path is what the engine is launched
    /// from, so they are separate fields and changing one never touches the other.
    /// </summary>
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a game executable",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) == true)
        {
            PathBox.Text = dialog.FileName;
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var text = ValueBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            ValueBox.Focus();
            return;
        }

        Value = text;

        // An emptied path is treated as none rather than as an empty string, so the profile
        // falls back to whatever Windows reports for the process.
        var path = PathBox.Text.Trim();
        Path = string.IsNullOrWhiteSpace(path) ? null : path;

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;


    /// <summary>
    /// A click that reached the backdrop is a click outside the dialog, so the
    /// dialog closes and keeps nothing.
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
