// Diagnostic entry point used only while developing the interface.
//
// Renders MainWindow through WPF's own compositor (which works for a
// transparent, layered window where a screen grab does not) and prints the
// measured geometry of the important elements. Run with:
//
//   Hooking.exe --probe <output.png>
//
// It exits immediately afterwards and never touches the engine.

using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Hooking;

internal static class LayoutProbe
{
    public static int Run(string outputPath)
    {
        var errors = new StringBuilder();
        var window = new MainWindow();

        // Surface data binding problems instead of letting them pass silently.
        PresentationTraceSources.Refresh();
        var listener = new CollectingListener(errors);
        PresentationTraceSources.DataBindingSource.Listeners.Add(listener);
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;

        window.Show();
        Pump(window, 700);

        var report = new StringBuilder();
        report.AppendLine($"window          {window.ActualWidth} x {window.ActualHeight}");

        Describe(report, window, "Frame");
        Describe(report, window, "ProfileList");
        Describe(report, window, "MasterSwitch");
        Describe(report, window, "PairsPanel");
        Describe(report, window, "FirstKeyBox");
        Describe(report, window, "ModeReleaseSegment");
        Describe(report, window, "ModeBlockingSegment");
        Describe(report, window, "RenameProfileButton");
        Describe(report, window, "SettingsNav");

        // Anything whose right edge passes the frame is drawn outside the window.
        var frame = window.FindName("Frame") as FrameworkElement;
        if (frame is not null)
        {
            var limit = frame.ActualWidth - 2;
            var overflow = new List<string>();

            foreach (var name in new[]
                     {
                         "MasterSwitch", "MinimizeButton", "CloseButton", "PairsPanel",
                         "ModeBlockingSegment", "RenameProfileButton"
                     })
            {
                if (window.FindName(name) is not FrameworkElement element || !element.IsVisible)
                {
                    continue;
                }

                var corner = element.TransformToAncestor(frame).Transform(new Point(element.ActualWidth, 0));
                if (corner.X > limit)
                {
                    overflow.Add($"{name} ends at {corner.X:0.#} > {limit:0.#}");
                }
            }

            report.AppendLine(overflow.Count == 0
                ? "overflow        none"
                : $"overflow        {string.Join("; ", overflow)}");
        }

        // The toggle knob must actually move. It is measured at both ends rather
        // than assumed, because the previous implementation left it in place while
        // the track changed colour, which looked like a broken switch.
        if (window.FindName("StartWithWindowsToggle") is CheckBox toggle &&
            window.FindName("SettingsPage") is ScrollViewer settingsHost)
        {
            settingsHost.Visibility = Visibility.Visible;
            Pump(window, 200);

            double KnobLeft()
            {
                // The knob is the only Ellipse in the toggle template.
                var template = toggle.Template;
                if (template?.FindName("Knob", toggle) is not FrameworkElement knob)
                {
                    return double.NaN;
                }

                var parent = VisualTreeHelper.GetParent(knob) as UIElement;
                return parent is null ? double.NaN : knob.TranslatePoint(new Point(0, 0), parent).X;
            }

            toggle.IsChecked = false;
            Pump(window, 400);
            var off = KnobLeft();

            toggle.IsChecked = true;
            Pump(window, 400);
            var on = KnobLeft();

            report.AppendLine();
            report.AppendLine($"toggle knob off  = {off:0.#}");
            report.AppendLine($"toggle knob on   = {on:0.#}");
            report.AppendLine($"toggle knob moved= {on - off:0.#}px  (expected about 21)");

            toggle.IsChecked = false;
            Pump(window, 300);
        }

        // The cross inside a pair tile must sit centred in its own 22 pixel slot,
        // in both the resting and the hovered state. Hover adds an animation, so it
        // is measured while a pointer is over the tile.
        if (window.FindName("PairsPanel") is WrapPanel panel && panel.Children.Count > 0 &&
            panel.Children[0] is Border chip)
        {
            var button = FindByType<Button>(chip);
            if (button is not null)
            {
                void Report(string stage)
                {
                    var centre = button.TranslatePoint(
                        new Point(button.ActualWidth / 2, 0), chip).X;
                    // Right padding, then the centre of the cross column: the grid is
                    // spacer + label + cross, both spacers 26 wide, padding 8.
                    var slotCentre = chip.ActualWidth - 8 - 26 / 2.0;
                    // The glyph inside the button, not the button itself: the
                    // button can be centred while its content sits off to one side.
                    var glyph = FindByType<Viewbox>(button);
                    var glyphCentre = glyph is null
                        ? double.NaN
                        : glyph.TranslatePoint(new Point(glyph.ActualWidth / 2, 0), chip).X;

                    var pad = chip.Padding;
                    var label2 = FindByType<TextBlock>(chip);
                    var labelCentre = label2 is null
                        ? double.NaN
                        : label2.TranslatePoint(new Point(label2.ActualWidth / 2, 0), chip).X;

                    report.AppendLine(
                        $"chip {stage,-8} chipW={chip.ActualWidth:0.#} pad=({pad.Left:0.#},{pad.Right:0.#}) " +
                        $"labelCentre={labelCentre:0.#} (chip centre {(chip.ActualWidth / 2):0.#})  " +
                        $"cross glyph={glyphCentre:0.#} from right edge {(chip.ActualWidth - glyphCentre):0.#}  " +
                        $"btn={button.ActualWidth:0.#}");
                }

                Report("resting");

                chip.RaiseEvent(new MouseEventArgs(Mouse.PrimaryDevice, 0)
                {
                    RoutedEvent = UIElement.MouseEnterEvent
                });
                Pump(window, 300);
                Report("hover");
            }
        }

        // Rapid switching must leave the segments in the right place and their
        // labels readable. The earlier animation wrote its result back from a
        // completion handler, so a fast second click could be overruled by the
        // first one finishing late.
        if (window.FindName("ModeReleaseSegment") is RadioButton release &&
            window.FindName("ModeBlockingSegment") is RadioButton blocking &&
            window.FindName("SegmentBacking") is FrameworkElement backing)
        {
            for (var i = 0; i < 6; i++)
            {
                release.IsChecked = true;
                blocking.IsChecked = true;
                release.IsChecked = true;
            }

            Pump(window, 500);

            // The backing must end under whichever segment is checked.
            var host = backing.Parent as FrameworkElement;
            if (host is not null)
            {
                var actual = backing.TranslatePoint(new Point(0, 0), window).X;
                var expected = release.TranslatePoint(new Point(0, 0), host).X - host.Margin.Left;
                var backingCentre = actual + backing.ActualWidth / 2;
                var releaseCentre = release.TranslatePoint(new Point(release.ActualWidth / 2, 0), window).X;

                report.AppendLine();
                report.AppendLine($"segment after rapid switch  backing centre={backingCentre:0.#} gear={releaseCentre:0.#}");
                report.AppendLine($"segment aligned             {Math.Abs(backingCentre - releaseCentre) < 3}");
                report.AppendLine($"segment text               release=\"{release.Content}\" blocking=\"{blocking.Content}\"");
            }
        }

        // Switching the engine on and off in quick succession must leave exactly one
        // worker running.
        //
        // The stop is asynchronous, so a cleanup from an earlier stop could land
        // after a newer worker had started and take it down, which shows up as the
        // engine dying immediately and an empty log.
        if (window.FindName("MasterSwitch") is FrameworkElement masterSwitch)
        {
            var click = new MouseButtonEventArgs(Mouse.PrimaryDevice, 0, MouseButton.Left)
            {
                RoutedEvent = UIElement.MouseLeftButtonDownEvent
            };

            for (var i = 0; i < 5; i++)
            {
                masterSwitch.RaiseEvent(click);
                Pump(window, 700);
            }

            // Settled on, since the loop toggles an odd number of times from off.
            Pump(window, 2500);

            var workers = System.Diagnostics.Process.GetProcessesByName("HookingWorker").Length;
            report.AppendLine();
            report.AppendLine($"engine toggled 5x, workers alive = {workers} (expected 1 if armed, 0 if not)");
        }

        // Navigation: switching to Settings and back to a game must both work.
        // The return trip was broken, so it is checked rather than assumed.
        report.AppendLine();
        if (window.FindName("SettingsNav") is Button settingsNav &&
            window.FindName("SettingsPage") is ScrollViewer settingsView &&
            window.FindName("PairsPage") is Grid presetsView &&
            window.FindName("ProfileList") is ListBox profiles)
        {
            settingsNav.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

            // The page swap is animated, so the result is only settled once the
            // transition has run its course.
            Pump(window, 400);

            var settingsShown = settingsView.Visibility == Visibility.Visible &&
                                presetsView.Visibility == Visibility.Collapsed;
            report.AppendLine($"after Settings click   settings visible = {settingsShown}");

            // The add form is now a page inside the window, so it must show and
            // then hand the content area back.
            if (window.FindName("AddProfileButton") is Button addProfile &&
                window.FindName("NewProfilePage") is Grid newProfile)
            {
                addProfile.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

                // Layout runs at render priority, and the swap animates, so the
                // page must be allowed to settle before it can be measured.
                Pump(window, 500);

                var opened = newProfile.Visibility == Visibility.Visible;
                report.AppendLine($"after Add click        new profile vis = {opened}");

                if (opened)
                {
                    // The form's own controls have to be laid out, not merely
                    // present, or the page is blank in practice.
                    Describe(report, window, "NewProfilePathBox");
                    Describe(report, window, "ProcessList");
                    Describe(report, window, "NewProfileTitleBox");

                    if (window.FindName("ProcessList") is ListBox processes)
                    {
                        report.AppendLine($"process candidates     {processes.Items.Count}");
                    }
                }
            }

            // Re-selecting a row must bring the pairs back.
            if (profiles.Items.Count > 0)
            {
                profiles.SelectedIndex = -1;
                profiles.SelectedIndex = 0;
                Pump(window, 400);

                var pairsShown = presetsView.Visibility == Visibility.Visible &&
                                 settingsView.Visibility == Visibility.Collapsed;
                report.AppendLine($"after profile select   pairs visible    = {pairsShown}");
            }
        }

        var list = window.FindName("ProfileList") as ListBox;
        report.AppendLine($"profile rows    {list?.Items.Count ?? -1}");
        report.AppendLine($"preset chips    {(window.FindName("PairsPanel") as Panel)?.Children.Count ?? -1}");

        // The theme is verified by reading the brushes the window actually
        // resolved, so a palette that failed to load shows up here.
        foreach (var key in new[] { "WindowBrush", "PanelBrush", "AccentBrush", "SidebarBrush" })
        {
            if (window.TryFindResource(key) is SolidColorBrush brush)
            {
                report.AppendLine($"brush {key,-14} {brush.Color}");
            }
            else
            {
                report.AppendLine($"brush {key,-14} <missing>");
            }
        }

        var statusText = window.FindName("StatusText") as TextBlock;
        report.AppendLine($"status line     \"{statusText?.Text}\"");
        report.AppendLine($"binding errors  {listener.Count}");
        if (listener.Count > 0)
        {
            report.AppendLine("--- binding errors ---");
            report.Append(errors);
        }

        // Render the live visual tree. This captures the real composition,
        // including the rounded frame, without depending on the desktop.
        var width = (int)window.ActualWidth;
        var height = (int)window.ActualHeight;
        if (width > 0 && height > 0)
        {
            var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
            target.Render(window);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(target));
            using var stream = File.Create(outputPath);
            encoder.Save(stream);
            report.AppendLine($"rendered        {outputPath} ({width}x{height})");
        }

        // The host is a WinExe, so a console may not be attached: the report goes
        // to a file next to the render.
        File.WriteAllText(Path.ChangeExtension(outputPath, ".txt"), report.ToString());

        window.Close();
        return 0;
    }

    private static T? FindByType<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindByType<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    private static void Describe(StringBuilder report, Window window, string name)
    {
        if (window.FindName(name) is not FrameworkElement element)
        {
            report.AppendLine($"{name,-15} <not found>");
            return;
        }

        var origin = element.TransformToAncestor(window).Transform(new Point(0, 0));
        var visibility = element.Visibility == Visibility.Visible ? "visible" : element.Visibility.ToString().ToLowerInvariant();
        report.AppendLine(
            $"{name,-15} {element.ActualWidth,7:0.#} x {element.ActualHeight,-7:0.#} " +
            $"at ({origin.X,6:0.#},{origin.Y,6:0.#})  {visibility}");
    }

    private static void Pump(Window window, int milliseconds)
    {
        var frame = new DispatcherFrame();
        var timer = new DispatcherTimer(TimeSpan.FromMilliseconds(milliseconds), DispatcherPriority.Background,
            (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);

        timer.Start();
        Dispatcher.PushFrame(frame);
        timer.Stop();
    }

    private sealed class CollectingListener(StringBuilder sink) : TraceListener
    {
        public int Count { get; private set; }

        public override void Write(string? message)
        {
        }

        public override void WriteLine(string? message)
        {
            Count++;
            sink.AppendLine($"  {message}");
        }
    }
}