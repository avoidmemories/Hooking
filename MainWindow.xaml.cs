using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using SnapHook.Models;
using SnapHook.Services;

namespace SnapHook;

public partial class MainWindow : Window
{
    private readonly AppPaths _paths = new();
    private readonly SettingsStore _store;
    private readonly SnapHookWorkerService _worker;

    /// <summary>The native notification-area icon and its system menu.</summary>
    private readonly TrayIconService _tray;

    /// <summary>
    /// Closing the window only hides it, so the engine keeps running in the tray.
    /// This flag is set when the application really has to end: Exit in the tray menu,
    /// or Windows signing out or shutting down.
    /// </summary>
    private bool _exitRequested;
    private readonly ProcessService _processes = new();
    private readonly StartupService _startup = new();

    /// <summary>
    /// Set while the process list is being selected by the code rather than by the
    /// user, so the title box is left as it is.
    /// </summary>
    private bool _suppressTitleSuggestion;
    private readonly Dictionary<string, ImageSource?> _iconCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<ProfileRow> _rows = [];

    private AppSettings _settings;
    private GameProfile? _selected;
    private bool _ready;
    private bool _started;
    private OpenAppInfo? _pendingProcess;

    /// <summary>Every candidate the process picker can offer, before filtering.</summary>
    private readonly List<ProcessCandidate> _processCandidates = [];
    private int _processListLoadVersion;
    private bool _updatingMode;
    private StarBorderEffect? _starEffect;

    /// <summary>True once the title has been typed in by hand.</summary>
    private bool _titleEditedByUser;

    /// <summary>False until the segmented control's backing has been placed on screen once.</summary>
    private bool _segmentPlaced;

    /// <summary>Mode the backing is currently showing, to tell a change from a redraw.</summary>
    private EngineMode _lastSegmentMode = EngineMode.Release;

    /// <summary>
    /// The brush behind each segment label, created once.
    ///
    /// Kept as a field rather than rebuilt on every change, because a colour can only be
    /// animated between values that a single brush instance holds.
    /// </summary>
    private readonly SolidColorBrush _releaseLabelBrush =
        new((Color)ColorConverter.ConvertFromString("#8A8A8A"));

    private readonly SolidColorBrush _blockingLabelBrush =
        new((Color)ColorConverter.ConvertFromString("#8A8A8A"));

    private readonly SolidColorBrush _hybridLabelBrush =
        new((Color)ColorConverter.ConvertFromString("#8A8A8A"));

    /// <summary>Row whose highlight is parked while another page is showing.</summary>
    private ProfileRow? _suspendedSelection;

    /// <summary>
    /// Set when the next pair render should stagger its chips in. Cleared after
    /// one use so ordinary redraws stay still.
    /// </summary>
    private bool _animateNextRender;

    /// <summary>
    /// The pair that was added last, so its chip can grow in while the rest of
    /// the panel stays still.
    /// </summary>
    private KeyPairPreset? _justAddedPair;

    /// <summary>
    /// True while the composer is editing the global pairs rather than a game's.
    ///
    /// Set by the "Edit global pairs" switch on the pairs page, so the two lists are
    /// never both editable at once and the panel heading always names the right one.
    /// </summary>
    private DispatcherTimer? _statusTimer;

    /// <summary>
    /// Keeps the engine running. Unlike the status poll it is never paused, so it also
    /// works while the window is hidden in the tray.
    /// </summary>
    private DispatcherTimer? _watchdog;

    /// <summary>
    /// Set when the application was started with Windows, so the window is never shown.
    /// </summary>
    private bool _startInTray;

    /// <summary>
    /// How often the running indicators and the engine status are re-read.
    ///
    /// A game can be started or closed at any moment, and nothing notifies the
    /// interface when it happens. Without a poll the dot keeps showing whatever
    /// was true when the list was last rebuilt, which is why it used to stay
    /// green after a game closed and stay grey after one started.
    /// </summary>
    private static readonly TimeSpan StatusRefreshInterval = TimeSpan.FromSeconds(2);

    // The drawer grows the window instead of overlapping content, so the two
    // heights are fixed constants. Both are even numbers.
        public MainWindow()
    {
        InitializeComponent();

        _store = new SettingsStore(_paths);
        _worker = new SnapHookWorkerService(_paths);
        _settings = _store.Load();
        _tray = new TrayIconService(
            "SnapHook",
            ShowFromTray,
            () => SetEngineEnabled(!_settings.SnapHookEnabled),
            ExitFromTray,
            _settings.SnapHookEnabled);

        ConfigureWindowChrome();
        Closing += Window_Closing;

        // Loaded fires only when the window is actually laid out. If the app is
        // started hidden, minimised or without a desktop, nothing below would run
        // and the engine would never be armed, with no trace anywhere. Content
        // rendering is the reliable hook, with Loaded kept as a fallback.
        Loaded += Window_Loaded;
        ContentRendered += Window_ContentRendered;
    }

    // ------------------------------------------------------------------ startup

    private void Window_ContentRendered(object? sender, EventArgs e) => Window_Loaded(this, new RoutedEventArgs());

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // Loaded and ContentRendered can both fire; the work must happen once.
        if (_started)
        {
            return;
        }

        _started = true;

        EnsureRoundedCorners();

        // Moves the startup setup to the scheduled task and removes old entries, before
        // the switch is read.
        _startup.Migrate();
        StartWithWindowsToggle.IsChecked = _startup.IsEnabled();

        RefreshProfiles();
        SelectInitialProfile();
        // The page is chosen first, so the render guard below has a page to check against.
        SelectSection(PresetSection.Pairs);
        RenderPairs();
        UpdateModeButtons();
        ApplySettings();

        _ready = true;
        UpdateMasterSwitch();
        RefreshStatus();
        StartStatusTimer();

        // Every button gets the press effect. Two kinds are left out: the engine
        // switch, which answers with its own glow, and the sidebar entries, whose
        // selection highlight is the feedback for a click. Toggle switches are
        // excluded inside the walk, since a CheckBox is not a Button.
        Interaction.AttachPressRecursive(this, new HashSet<string>(StringComparer.Ordinal)
        {
            "SettingsNav"
        });

        // The wheel is eased on the lists that scroll, so a notch carries rather
        // than jumping a fixed number of rows.
        AttachSmoothScrolling(ProcessList);
        AttachSmoothScrolling(ProfileList);

        _watchdog = new DispatcherTimer(TimeSpan.FromSeconds(3), DispatcherPriority.Background, (_, _) =>
        {
            _worker.EnsureRunning();
        }, Dispatcher);
        _watchdog.Start();

        // Started with Windows: the window was shown invisibly only so that it would
        // be laid out and this startup code would run. It goes straight back to the tray.
        if (_startInTray)
        {
            _startInTray = false;
            ShowInTaskbar = true;
            Opacity = 1;
            HideToTray();
        }
    }

    /// <summary>
    /// Shows the window, or, when started with Windows, keeps it in the tray.
    ///
    /// WPF only lays the window out once it has been shown, and the startup work runs
    /// after the first frame. So the window is shown with zero opacity and kept out of
    /// the taskbar, and it is hidden again as soon as that startup work is finished.
    /// </summary>
    public void Start(bool inTray)
    {
        if (!inTray)
        {
            Show();
            return;
        }

        _startInTray = true;
        ShowInTaskbar = false;
        Opacity = 0;
        Show();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // The window's close button, and Alt+F4, only hide it. The engine keeps running in
        // the tray, which is the point of having one.
        if (!_exitRequested)
        {
            e.Cancel = true;
            HideToTray();
            return;
        }

        // A final save, before anything is torn down.
        //
        // Every edit already writes as it happens, so this is a safety net rather than
        // the main path: it covers a change made while another page was open, when the
        // list selection is parked and the profile list itself is not on screen.
        SaveSettings();

        StopStatusTimer();
        _watchdog?.Stop();
        _starEffect?.Dispose();
        _worker.Dispose();
        _tray.Dispose();
    }

    // ------------------------------------------------------------------ tray

    /// <summary>
    /// Hides the window into the notification area. The status poll is paused while
    /// nothing is on screen.
    /// </summary>
    private void HideToTray()
    {
        StopStatusTimer();
        Hide();
    }

    /// <summary>
    /// Brings the window back from the tray, or restores it from the taskbar.
    /// </summary>
    public void ShowFromTray()
    {
        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        // Restored in case the window is still in its invisible startup state.
        Opacity = 1;
        ShowInTaskbar = true;

        Show();
        Activate();

        // The poll was paused while hidden, so the running indicators are refreshed
        // straight away rather than after the first tick.
        StartStatusTimer();
        RefreshRunningIndicators();
    }

    /// <summary>
    /// Called when Windows ends the session, so the window closes for real instead of
    /// hiding.
    /// </summary>
    public void PrepareForExit() => _exitRequested = true;

    private void ExitFromTray()
    {
        _exitRequested = true;
        Close();
    }

    private void SelectInitialProfile()
    {
        var profile = _settings.Profiles.FirstOrDefault(x => x.Id == _settings.SelectedProfileId)
                      ?? _settings.Profiles.FirstOrDefault();

        if (profile is not null)
        {
            ProfileList.SelectedItem = _rows.FirstOrDefault(x => x.Profile.Id == profile.Id);
        }
    }

    // ------------------------------------------------------------------ settings

    private void ApplySettings()
    {
        _worker.Apply(_settings);
        UpdateMasterSwitch();
        RefreshStatus();
    }

    private void SaveSettings() => _store.Save(_settings);

    // ------------------------------------------------------------------ profiles

    /// <summary>
    /// Rebuilds the sidebar list. Only called when profiles themselves change.
    /// </summary>
    private void RefreshProfiles()
    {
        _rows.Clear();

        foreach (var profile in _settings.Profiles)
        {
            var row = new ProfileRow(profile);
            row.Refresh(GetIcon(profile));
            _rows.Add(row);
        }

        ProfileList.ItemsSource = null;
        ProfileList.ItemsSource = _rows;
    }

    /// <summary>
    /// Clicking a row always returns the content area to that game's key pairs.
    ///
    /// SelectionChanged alone was not enough: clicking the row that is already
    /// selected raises no event, so once the Settings page was open there was no
    /// way back by clicking a game. A mouse handler fires on every click,
    /// including the one on an already selected row.
    /// </summary>
    private void ProfileList_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
        {
            return;
        }

        // The delete cross handles its own click.
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        _selected = row.Profile;
        _settings.SelectedProfileId = row.Profile.Id;
        SaveSettings();

        if (PresetsPageIsHidden)
        {
            _animateNextRender = true;
        }

        SelectSection(PresetSection.Pairs);
        RenderPairs();
        UpdateModeButtons();
        RefreshStatus();
    }

    private bool PresetsPageIsHidden =>
        PairsPage.Visibility != Visibility.Visible || SettingsPage.Visibility == Visibility.Visible;

    private void ProfileList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProfileList.SelectedItem is not ProfileRow row)
        {
            return;
        }

        var previous = _selected;
        _selected = row.Profile;
        _settings.SelectedProfileId = row.Profile.Id;
        SaveSettings();

        // The chips cascade only when the set of pairs is actually different. Two
        // profiles that share their pairs then look like a still image with a label
        // change, which is what they are.
        var pairsChanged = previous is null || !SamePairs(previous, _selected);

        _animateNextRender = pairsChanged;
        _justAddedPair = null;

        SelectSection(PresetSection.Pairs);
        RenderPairs();
        UpdateModeButtons();
        RefreshStatus();
    }

    // ------------------------------------------------------------------ new profile page

    /// <summary>
    /// Opens the new profile form inside the content area.
    ///
    /// It is a page rather than a dialog so the app stays one window, and so the
    /// running process list can be browsed without losing sight of the sidebar.
    /// </summary>
    private async void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        // The list selection is parked while the form is open, so a stray click cannot
        // remove a game the user is no longer looking at.
        if (ProfileList.SelectedItem is ProfileRow current)
        {
            _suspendedSelection = current;
            ProfileList.SelectedItem = null;
        }

        NewProfileTitleBox.Text = string.Empty;
        BrowsePathBox.Text = string.Empty;
        NewProfileAutoToggle.IsChecked = true;
        ProcessSearchBox.Text = string.Empty;

        // Reset the flag so the title tracks the list again on a fresh visit.
        _titleEditedByUser = false;

        _pendingProcess = null;
        SelectSection(PresetSection.NewProfile);

        // Focus goes to the search box, which is where the user starts: they type a
        // few letters, then click the process. Putting the caret in the title made
        // it look like a field that had to be filled in first.
        ProcessSearchBox.Focus();

        // Enumerating processes and reading their image paths can be slow when a
        // process is protected or sits on a slow drive. The page is already visible,
        // so do that work away from the UI thread rather than making the plus button
        // appear unresponsive.
        await LoadProcessListAsync();
    }

    private void CancelNewProfile_Click(object sender, RoutedEventArgs e)
    {
        // Ignore the result of an enumeration that was still running when Back was
        // clicked. Process enumeration itself is intentionally not cancelled because
        // Windows API calls in progress cannot be safely interrupted.
        _processListLoadVersion++;
        SelectSection(PresetSection.Pairs);

        // The buttons belong to a game page, and the page has only just been asked for.
        // Setting the state here as well means they are live the moment the transition
        // lands, whether it came from the back arrow or from the list.
        SetGameActionsEnabled(_selected is not null);
    }

    /// <summary>
    /// Fills the process list with what is running right now.
    ///
    /// A catalogue of well known titles used to be offered as well, which meant the
    /// list showed games the machine did not even have installed. Only live
    /// processes are listed now, and Browse covers everything else.
    /// </summary>
    private async Task LoadProcessListAsync()
    {
        var loadVersion = ++_processListLoadVersion;
        _processCandidates.Clear();
        ProcessList.ItemsSource = null;

        var applications = await Task.Run(_processes.ListRunning);
        if (loadVersion != _processListLoadVersion || _currentSection != PresetSection.NewProfile)
        {
            return;
        }

        foreach (var app in applications)
        {
            _processCandidates.Add(new ProcessCandidate(app, true));
        }

        ApplyProcessFilter();
    }

    /// <summary>
    /// Narrows the list to what matches the search box.
    ///
    /// Running applications come first, because a game that is already open is
    /// nearly always the one being added.
    /// </summary>
    private void ApplyProcessFilter()
    {
        var query = ProcessSearchBox.Text.Trim();

        var matches = _processCandidates
            .Where(candidate => query.Length == 0 ||
                                candidate.App.ProcessName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                (candidate.App.WindowTitle ?? string.Empty).Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.IsRunning)
            .ThenBy(candidate => candidate.App.ProcessName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ProcessList.ItemsSource = matches;

        // Deliberately nothing is selected. Selecting the first entry filled the
        // title in with a process the user had not chosen, and made it look as
        // though that game was already picked.
        ProcessList.SelectedIndex = -1;
    }

    private void ProcessSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        ApplyProcessFilter();
    }

    /// <summary>
    /// Keeps the pending process in step when the path is edited by hand.
    ///
    /// The engine matches on the image name, so a path typed here has to produce one.
    /// Setting the field is what Browse does too, and both end up in the same place.
    /// </summary>
    private void BrowsePath_Changed(object sender, TextChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var path = BrowsePathBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var name = System.IO.Path.GetFileNameWithoutExtension(path);
        if (string.IsNullOrWhiteSpace(name))
        {
            return;
        }

        _pendingProcess = new OpenAppInfo(name, System.IO.File.Exists(path) ? path : null, null, false);
    }

    /// <summary>
    /// Records the chosen process and names it in the list row rather than in a
    /// text field of its own, which is what frees the vertical space.
    /// </summary>
    /// <summary>
    /// Picks the process under the pointer, whatever part of the row was hit.
    ///
    /// The list's own selection only follows a click that lands on the item's
    /// content. These rows are short with room around the text, so a click aimed at
    /// a row could fall on empty space and be ignored, which is why choosing a
    /// process sometimes needed a second try.
    /// </summary>
    private void ProcessList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // Walk up to the row that owns the element that was clicked.
        while (source is not null and not ListBoxItem)
        {
            source = VisualTreeHelper.GetParent(source);
        }

        if (source is ListBoxItem { DataContext: ProcessCandidate candidate })
        {
            ProcessList.SelectedItem = candidate;
        }
    }

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProcessList.SelectedItem is not ProcessCandidate candidate)
        {
            return;
        }

        // Show the path for a process chosen from the live list too. More
        // importantly, the same path travels into the profile on Add, so the rename
        // page can show it later just as it does for a browsed executable.
        BrowsePathBox.Text = candidate.App.ExecutablePath ?? string.Empty;
        _pendingProcess = candidate.App;

        // The title follows the selection until the user edits it themselves.
        //
        // Tracking it only while the box still read "New Profile" meant that after
        // the very first process filled it in, choosing another left the old name
        // in place and the new game was created under it.
        if (!_titleEditedByUser && !_suppressTitleSuggestion)
        {
            NewProfileTitleBox.Text = SuggestTitle(candidate.App);
        }

        ProcessList.Items.Refresh();
    }

    /// <summary>
    /// Opens a file picker for an executable, which is how a game that is not
    /// running yet is chosen.
    /// </summary>
    private void BrowseProcess_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Select a game executable",
            Filter = "Executable (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists = true
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        // The image name is what the engine matches on, so it is taken from the
        // chosen file rather than typed by hand.
        var fileName = System.IO.Path.GetFileNameWithoutExtension(dialog.FileName);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return;
        }

        _pendingProcess = new OpenAppInfo(fileName, dialog.FileName, null, false);

        // The file's own name: it is what the engine matches on, and it is what the
        // user just chose. A window title would be wrong here, since nothing is running
        // and there is none to take.
        var suggested = ProcessService.DescribeProcess(fileName);

        if (!_titleEditedByUser)
        {
            NewProfileTitleBox.Text = suggested;
        }

        BrowsePathBox.Text = dialog.FileName;

        // The list is cleared of any selection, so nothing appears to be chosen
        // alongside the browsed file, and a later Add cannot pick up a stale row.
        //
        // The file itself is deliberately not added to the list: that list shows what is
        // running, and an entry for something that is not running would be a lie.
        _suppressTitleSuggestion = true;
        ProcessList.SelectedIndex = -1;
        _suppressTitleSuggestion = false;

        // Filled in with the file that was just chosen.
        BrowsePathBox.Text = dialog.FileName;
    }

    /// <summary>
    /// Suggests a title for a new profile.
    ///
    /// The window title is preferred, because it is what a player recognises. It is
    /// used only when it actually identifies the game, though: a title that is empty,
    /// or one that is shared with another build — as the two GTA V versions are —
    /// would produce two rows the user cannot tell apart. In that case the executable
    /// name is described instead, which does distinguish them.
    /// </summary>
    private string SuggestTitle(OpenAppInfo app)
    {
        var fromProcess = ProcessService.DescribeProcess(app.ProcessName);

        var title = app.WindowTitle?.Trim();
        if (string.IsNullOrWhiteSpace(title))
        {
            return fromProcess;
        }

        // A title that another profile already uses is not an identifier.
        var takenByAnother = _settings.Profiles.Any(profile =>
            !profile.ProcessName.Equals(app.ProcessName, StringComparison.OrdinalIgnoreCase) &&
            profile.Title.Equals(title, StringComparison.OrdinalIgnoreCase));

        if (takenByAnother)
        {
            return fromProcess;
        }

        return title;
    }

    /// <summary>
    /// Marks the title as hand-written, so choosing another process stops
    /// overwriting it.
    /// </summary>
    private void NewProfileTitle_Changed(object sender, TextChangedEventArgs e)
    {
        // The page fills the box itself when it opens and when a process is
        // picked; only a change made while the form is on screen counts as user
        // input.
        if (_ready && NewProfileTitleBox.IsKeyboardFocusWithin)
        {
            _titleEditedByUser = true;
        }
    }

    private void ConfirmNewProfile_Click(object sender, RoutedEventArgs e)
    {
        var processName = _pendingProcess?.ProcessName;

        if (string.IsNullOrWhiteSpace(processName))
        {
            ShowToast("Choose a process or browse for an executable");
            return;
        }

        if (_settings.Profiles.Any(x => x.ProcessName.Equals(processName, StringComparison.OrdinalIgnoreCase)))
        {
            ShowToast($"{processName} is already in the list");
            return;
        }

        var title = NewProfileTitleBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(title) || title.Equals("New Profile", StringComparison.OrdinalIgnoreCase))
        {
            title = processName;
        }

        var profile = new GameProfile
        {
            Title = title,
            ProcessName = processName,
            ExecutablePath = _pendingProcess?.ExecutablePath,
            Mode = EngineMode.Release,
            Presets = NewProfileAutoToggle.IsChecked == true
                ? [new KeyPairPreset("W", "S"), new KeyPairPreset("A", "D")]
                : []
        };

        _settings.Profiles.Add(profile);
        _settings.SelectedProfileId = profile.Id;
        SaveSettings();

        RefreshProfiles();
        ProfileList.SelectedItem = _rows.FirstOrDefault(x => x.Profile.Id == profile.Id);

        // The worker is restarted from the current settings, so the new game is
        // armed at once instead of waiting for the next unrelated change.
        ApplySettings();
        SelectSection(PresetSection.Pairs);
    }

    /// <summary>
    /// One row of the process picker on the new profile page.
    /// </summary>
    private sealed record ProcessCandidate(OpenAppInfo App, bool IsRunning, bool IsBrowsed = false)
    {
        private ImageSource? _icon;
        private bool _iconRead;

        public string Headline => string.IsNullOrWhiteSpace(App.WindowTitle)
            ? App.ProcessName
            : App.WindowTitle!;

        /// <summary>
        /// Executable icon for the picker row.
        ///
        /// Resolved on first use and cached: extracting an icon touches the file on
        /// disk, so doing it eagerly for every candidate would cost even when the
        /// page is never opened.
        /// </summary>
        public ImageSource? Icon
        {
            get
            {
                if (!_iconRead)
                {
                    _icon = IconService.FromPath(App.ExecutablePath, 16);
                    _iconRead = true;
                }

                return _icon;
            }
        }

        /// <summary>
        /// A short note shown at the right of the row: which ones are live, and
        /// which one was just picked with Browse.
        /// </summary>
        public string StatusLabel => IsBrowsed ? "chosen" : string.Empty;
    }

    /// <summary>
    /// Removes a game, and moves the selection to the one that was above it.
    ///
    /// The neighbour above is what the user was looking at when they chose to remove
    /// this one. Falling back to the first entry in the list, as it did, jumped the
    /// view to the top of a long list for no reason the user could see.
    /// </summary>
    private void RemoveProfile(GameProfile profile)
    {
        var index = _settings.Profiles.IndexOf(profile);
        _settings.Profiles.Remove(profile);

        if (_settings.SelectedProfileId == profile.Id || _selected == profile)
        {
            // The entry above it, or the new first entry when the removed one was at
            // the top.
            var next = _settings.Profiles.Count == 0
                ? null
                : _settings.Profiles[Math.Max(0, index - 1)];

            _settings.SelectedProfileId = next?.Id ?? string.Empty;
            _selected = next;
        }

        SaveSettings();
        RefreshProfiles();

        // The row that is now selected, rather than whichever one happens to be first.
        var row = _rows.FirstOrDefault(x => x.Profile.Id == _settings.SelectedProfileId);
        ProfileList.SelectedItem = row;

        if (row is null)
        {
            // Nothing left to show, so the page goes back to an empty state instead of
            // describing a game that no longer exists.
            _previousPairKeys = null;
            RenderPairs();
            UpdateModeButtons();
        }

        ApplySettings();
    }

    /// <summary>
    /// Asks before deleting, because there is no undo and the profile carries its
    /// own key pairs.
    /// </summary>
    private bool ConfirmRemoval(GameProfile profile)
    {
        var pairs = profile.Presets.Count == 0
            ? "It has no key pairs."
            : $"Its {profile.Presets.Count} key pair(s) will be deleted too.";

        var question = "Remove \"" + profile.DisplayName + "\" (" + profile.ProcessName + ")?" +
                       Environment.NewLine + Environment.NewLine + pairs;

        return MessageBox.Show(
            question,
            "Remove game profile",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question,
            MessageBoxResult.Cancel) == MessageBoxResult.OK;
    }

    private void RenameProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        // Profiles made by older versions did not retain the path of a process chosen
        // from the live list. Resolve it on demand while that process is running, so
        // their edit page gains the same information without requiring users to add
        // the game again.
        var executablePath = _selected.ExecutablePath ?? ProcessService.TryGetPath(_selected.ProcessName);
        var dialog = new RenameWindow(_selected.Title, executablePath) { Owner = this };

        // Sized over this window and clipped, before anything is painted.
        dialog.CoverOwnerBeforeShow(this);

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _selected.Title = dialog.Value;

        // The path can be changed here without touching the name, which is useful when a
        // game is reinstalled somewhere else.
        _selected.ExecutablePath = dialog.Path;

        SaveSettings();
        RefreshProfiles();
    }

    
    // ------------------------------------------------------------------ pairs

    /// <summary>
    /// Pairs of the selected game.
    ///
    /// Each profile owns its list: a movement shooter and a driving game have no
    /// pairs in common, so nothing is shared and switching profile switches the
    /// whole set. With no game selected the global list is edited instead, which
    /// is what the "Any application" mode uses.
    /// </summary>
    /// <summary>
    /// The pair list the composer edits: the selected game's own.
    ///
    /// There is no shared list any more. The mode that used one has been replaced by
    /// the startup option, and pairs are now always the property of a game.
    /// </summary>
    private List<KeyPairPreset> CurrentPairList => _selected?.Presets ?? _settings.Presets;

    private IReadOnlyList<KeyPairPreset> CurrentPairs => CurrentPairList;

    private void KeyCaptureBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        e.Handled = true;

        if (sender is not TextBox box)
        {
            return;
        }

        var key = e.Key switch
        {
            Key.System => e.SystemKey,
            Key.ImeProcessed => e.ImeProcessedKey,
            Key.DeadCharProcessed => e.DeadCharProcessedKey,
            _ => e.Key
        };

        // Tab is captured like any other key, exactly as ONEKEY did. Reserving it
// for focus navigation made the key impossible to bind, which is the one
// thing that was reported as missing. Moving between the boxes is handled by
// the automatic jump below, so nothing is lost.
        if (key is Key.Back or Key.Delete)
        {
            box.Clear();
            return;
        }

        // A single key name, never a chord: the engine works on individual scan
        // codes, so "Ctrl+D" could not be resolved. ONEKEY stored plain names for
        // the same reason.
        box.Text = KeyNames.FromKey(key);

        // Jump to the second box once the first key is chosen, so a pair can be
        // typed without reaching for the mouse.
        if (ReferenceEquals(box, FirstKeyBox))
        {
            SecondKeyBox.Focus();
            SecondKeyBox.SelectAll();
        }
    }

    
    private void AddPair_Click(object sender, RoutedEventArgs e)
    {
        // A pair belongs to a game, so there is nowhere to put one without a
        // selection. The button is disabled in that state; this is the second line of
        // defence for the keyboard path.
        if (_selected is null)
        {
            return;
        }

        var first = FirstKeyBox.Text.Trim();
        var second = SecondKeyBox.Text.Trim();

        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            ShowToast("Choose two keys first");
            return;
        }

        if (first.Equals(second, StringComparison.OrdinalIgnoreCase))
        {
            ShowToast("The two keys must differ");
            return;
        }

        var keyFirst = KeyNames.ToKeyId(first);
        var keySecond = KeyNames.ToKeyId(second);
        if (keyFirst == 0 || keySecond == 0)
        {
            ShowToast($"\"{(keyFirst == 0 ? first : second)}\" is not a supported key");
            return;
        }

        if (keyFirst == keySecond)
        {
            ShowToast("The two keys must differ");
            return;
        }

        // Only an exact duplicate is refused. A key taking part in several pairs
        // is allowed on purpose: W:S and W:A together is the normal way to cover
        // both strafe directions, and the engine keeps one list of opponents per
        // key.
        var target = CurrentPairList;
        if (target.Any(x => IsSamePair(x, first, second)))
        {
            ShowToast("That exact pair is already listed");
            return;
        }

        var preset = new KeyPairPreset(first, second);
        target.Add(preset);

        FirstKeyBox.Clear();
        SecondKeyBox.Clear();
        FirstKeyBox.Focus();

        SaveSettings();

        // The chip that was just added is the one that grows in; the others are
        // left still, so the panel does not twitch.
        _justAddedPair = preset;
        _animateNextRender = false;
        RenderPairs();
        ApplySettings();
    }

    private void ClearComposer_Click(object sender, RoutedEventArgs e)
    {
        FirstKeyBox.Clear();
        SecondKeyBox.Clear();
        FirstKeyBox.Focus();
    }

    private static bool IsSamePair(KeyPairPreset preset, string first, string second) =>
        (preset.FirstKey.Equals(first, StringComparison.OrdinalIgnoreCase) &&
         preset.SecondKey.Equals(second, StringComparison.OrdinalIgnoreCase)) ||
        (preset.FirstKey.Equals(second, StringComparison.OrdinalIgnoreCase) &&
         preset.SecondKey.Equals(first, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when two profiles hold the same pairs, ignoring their order.
    ///
    /// Used to decide whether a switch needs its animation: if the pairs are the same
    /// there is nothing to show, and replaying the cascade would suggest a change that
    /// did not happen.
    /// </summary>
    private static bool SamePairs(GameProfile left, GameProfile right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Presets.Count != right.Presets.Count)
        {
            return false;
        }

        static string Key(KeyPairPreset pair) =>
            string.CompareOrdinal(pair.FirstKey, pair.SecondKey) <= 0
                ? $"{pair.FirstKey}|{pair.SecondKey}"
                : $"{pair.SecondKey}|{pair.FirstKey}";

        var leftSet = left.Presets.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var rightSet = right.Presets.Select(Key).OrderBy(x => x, StringComparer.Ordinal).ToList();

        return leftSet.SequenceEqual(rightSet, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Enables or disables the two buttons that act on the selected game.
    ///
    /// Dimmed as well as disabled, so the reason is visible rather than the button
    /// simply not responding.
    /// </summary>
    private void SetGameActionsEnabled(bool enabled)
    {
        foreach (var button in new[] { RenameProfileButton, RemoveProfileButton })
        {
            button.IsEnabled = enabled;

            // Dimmed, but not so far that the icons become unreadable — the point is to
            // show they are unavailable, not to hide them.
            button.Opacity = enabled ? 1.0 : 0.55;
        }
    }

    /// <summary>
    /// Enables or disables the pair composer.
    ///
    /// A pair belongs to a game, so with none selected the controls have nothing to
    /// act on. They are dimmed as well as disabled, so the reason is visible rather
    /// than the button simply not responding.
    /// </summary>
    /// <summary>
    /// Enables or disables the pair composer, and swaps it for a notice when there is
    /// nothing to attach a pair to.
    ///
    /// Dimming the controls on their own was not enough: they still looked like the
    /// thing to fill in, and the only way to find out otherwise was to try. The
    /// notice states the reason and takes their place.
    /// </summary>
    private void SetPairComposerEnabled(bool enabled)
    {
        PairComposer.IsEnabled = enabled;
        PairComposer.Opacity = enabled ? 1.0 : 0.4;

        // The input method is part of the same page, so it goes the same way. Leaving it
        // bright while the pair controls beside it were dimmed made the page look
        // half disabled.
        ModeTrough.IsEnabled = enabled;
        ModeTrough.Opacity = enabled ? 1.0 : 0.4;

        // The Add pair button is disabled as well, so a stray Enter cannot add a
        // pair that has nowhere to go.
        AddPairButton.IsEnabled = enabled;

        if (!enabled)
        {
            FirstKeyBox.Clear();
            SecondKeyBox.Clear();
        }
    }

    private void RenderPairs()
    {
        // Nothing is drawn into a page that is not on screen, and whatever was there is
        // taken out.
        //
        // Several paths re-render this panel on their way elsewhere, and drawing into it
        // made the page visible again behind whatever was opening. Emptying it as well
        // means a page that is shown unexpectedly has nothing in it to see.
        if (_currentSection != PresetSection.Pairs && _currentSection is not null)
        {
            PairsPanel.Children.Clear();
            return;
        }

        PairsPanel.Children.Clear();

        var pairs = CurrentPairs.ToList();

        // A pair needs a game to belong to. With none selected there is nowhere to put
        // it, and the composer is replaced by a notice saying so.
        SetPairComposerEnabled(_selected is not null);

        RenameProfileButton.Visibility = _selected is null ? Visibility.Collapsed : Visibility.Visible;
        RemoveProfileButton.Visibility = RenameProfileButton.Visibility;

        SetGameActionsEnabled(_selected is not null && _currentSection == PresetSection.Pairs);

        PairsLabel.Text = "PAIRS";

        if (_currentSection == PresetSection.Pairs)
        {
            HeaderProfile.Text = _selected?.DisplayName ?? string.Empty;
        }

        if (pairs.Count == 0)
        {
            var empty = new TextBlock
            {
                Text = _settings.Profiles.Count == 0
                    ? "Add a game profile first"
                    : _selected is null
                        ? "Select a game to see its key pairs"
                        : $"{_selected.DisplayName} has no pairs yet. Add one above.",
                Foreground = (Brush)FindResource("MutedTextBrush"),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 20, 0, 0)
            };

            // Centred in the panel rather than tucked under the heading, so an empty
            // state reads as a state and not as a stray line of text.
            PairsPanel.Children.Add(empty);
            return;
        }

        var chips = new List<UIElement>();

        foreach (var pair in pairs)
        {
            var chip = BuildPairChip(pair);
            PairsPanel.Children.Add(chip);
            chips.Add(chip);
        }

        // The chips assemble in sequence rather than appearing all at once. Only
        // worth doing when the panel was empty, so switching between two games
        // that both have pairs does not re-animate everything.
        if (_animateNextRender)
        {
            _animateNextRender = false;

            // The tiles that are new since the last render, found by comparing pairs
            // rather than by position, so a reordered list still animates correctly.
            var incoming = _previousPairKeys is null
                ? []
                : pairs
                    .Where(pair => !_previousPairKeys.Contains(PairKey(pair)))
                    .Select(pair => chips[pairs.IndexOf(pair)])
                    .ToList();

            if (incoming.Count > 0)
            {
                StaggerIn(incoming);
            }
        }
        else if (_justAddedPair is not null)
        {
            // Only the chip that was just added grows in.
            var index = pairs.IndexOf(_justAddedPair);
            if (index >= 0 && index < chips.Count)
            {
                AnimateChipIn((FrameworkElement)chips[index]);
            }

            _justAddedPair = null;
        }

        _previousPairKeys = pairs.Select(PairKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A pair reduced to a value that compares equal whichever way round it is written,
    /// so A:D and D:A are recognised as the same pair.
    /// </summary>
    private static string PairKey(KeyPairPreset pair) =>
        string.CompareOrdinal(pair.FirstKey, pair.SecondKey) <= 0
            ? $"{pair.FirstKey}|{pair.SecondKey}"
            : $"{pair.SecondKey}|{pair.FirstKey}";

    /// <summary>The pairs shown by the previous render, to tell new ones apart.</summary>
    private HashSet<string>? _previousPairKeys;

    /// <summary>
    /// A pair chip. Hovering dims the label and reveals the delete affordance,
    /// the same interaction ONEKEY uses.
    /// </summary>
    /// <summary>
    /// A pair tile.
    ///
    /// Laid out as two columns: the label takes the free space and is centred in
    /// it, and the cross has its own reserved column. Overlapping them in a single
    /// cell was what made the cross sit on top of the text and look cramped.
    ///
    /// Hover turns the tile red — outline, background tint and the cross together
    /// — which is the one place colour appears outside black and white.
    /// </summary>
    private Border BuildPairChip(KeyPairPreset pair)
    {
        var normalBackground = Color.FromRgb(0x0D, 0x0D, 0x0D);
        var hoverBackground = Color.FromRgb(0x1A, 0x0F, 0x11);
        var normalBorder = Color.FromRgb(0x26, 0x26, 0x26);
        var hoverBorder = Color.FromRgb(0xFF, 0x5E, 0x70);

        var danger = Color.FromRgb(0xFF, 0x5E, 0x70);
        var idleCross = Color.FromRgb(0x8A, 0x8A, 0x8A);

        var background = new SolidColorBrush(normalBackground);
        var borderBrush = new SolidColorBrush(normalBorder);
        var crossBrush = new SolidColorBrush(idleCross);

        // The content is composed from separate controls rather than a formatted
        // string. That keeps the reversible-arrow glyph genuinely horizontal while
        // allowing the tile to measure the real width of long names such as
        // Insert and PageUp.
        var label = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        label.Children.Add(new TextBlock
        {
            Text = pair.FirstKey,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        });

        var separator = new TextBlock
        {
            Text = "⇆",
            Foreground = (Brush)FindResource("MutedTextBrush"),
            FontFamily = new FontFamily("Segoe UI Symbol"),
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Width = 18,
            TextAlignment = TextAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 6, 0)
        };
        label.Children.Add(separator);

        label.Children.Add(new TextBlock
        {
            Text = pair.SecondKey,
            Foreground = (Brush)FindResource("TextBrush"),
            FontSize = 13,
            FontWeight = FontWeights.Medium,
            VerticalAlignment = VerticalAlignment.Center
        });

        // Shown in place of the pair once the cross has been clicked.
        //
        // Sized to the same font so the tile does not change size between the two states,
        // which keeps the panel from shifting when one is asked to confirm.
        var confirm = new TextBlock
        {
            Text = "Are you sure?",
            Foreground = (Brush)FindResource("DangerBrush"),
            FontSize = 12,
            FontWeight = FontWeights.Medium,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = 0
        };

        var remove = new Button
        {
            Content = MakeIcon("M4.5 4.5 L11.5 11.5 M11.5 4.5 L4.5 11.5", crossBrush, 1.7),
            Style = (Style)FindResource("QuietIconButton"),
            Width = 22,
            Height = 22,
            Opacity = 0,
            Cursor = Cursors.Hand,
        };

        // The label takes its natural width. The Border has only a minimum width, not a
        // fixed one, so a pair such as Insert ⇆ PageUp widens the tile just enough
        // instead of clipping or overflowing the text.
        var content = new Grid();
        content.Children.Add(label);
        content.Children.Add(confirm);

        remove.HorizontalAlignment = HorizontalAlignment.Center;
        remove.VerticalAlignment = VerticalAlignment.Center;
        content.Children.Add(remove);

        var chip = new Border
        {
            Background = background,
            BorderBrush = borderBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            MinWidth = 104,
            Height = 34,
            // Two pixels of margin all round: a one pixel outline sits on the
            // element's own edge, and without room the outer tiles lose a corner
            // to the scroll viewport.
            Margin = new Thickness(0, 0, 8, 8),
            Padding = new Thickness(6, 0, 6, 0),
            Cursor = Cursors.Hand,
            Child = content,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new ScaleTransform(1, 1)
        };

        // Tiles are created after the window walk, so they attach their own press
        // feedback.
        Interaction.AttachPress(chip, 0.965);

        var removing = false;

        void Remove()
        {
            if (removing)
            {
                return;
            }

            removing = true;
            AnimateChipOut(chip, () =>
            {
                CurrentPairList.Remove(pair);
                SaveSettings();
                RenderPairs();
                ApplySettings();
            });
        }

        // Two step removal, driven from anywhere on the tile.
        //
        // The cross alone was the target, and it is 22 pixels in the middle of a tile
        // three times as wide: a click near an edge landed on the tile, which has its own
        // press feedback, so the tile reacted while nothing happened — the "sometimes I
        // have to click three times" the user saw. The whole tile now runs the same
        // sequence, so the response always matches the press.
        var awaitingConfirm = false;

        void Ask()
        {
            if (awaitingConfirm)
            {
                Remove();
                return;
            }

            awaitingConfirm = true;

            Fade(label, 0, Motion.Fast);
            Fade(confirm, 1, Motion.Fast);
            Fade(remove, 0, Motion.Fast);
        }

        remove.Click += (_, args) =>
        {
            args.Handled = true;
            Ask();
        };

        chip.MouseEnter += (_, _) =>
        {
            AnimateBrush(background, hoverBackground, Motion.Fast);
            AnimateBrush(borderBrush, hoverBorder, Motion.Fast);
            AnimateBrush(crossBrush, danger, Motion.Fast);
            Fade(remove, 1, Motion.Fast);
            Fade(label, 0.35, Motion.Fast);
        };

        chip.MouseLeave += (_, _) =>
        {
            AnimateBrush(background, normalBackground, Motion.Normal);
            AnimateBrush(borderBrush, normalBorder, Motion.Normal);
            AnimateBrush(crossBrush, idleCross, Motion.Normal);
            Fade(remove, 0, Motion.Normal);
            Fade(label, 1, Motion.Normal);
        };

        // The tile itself is the target as well, so an edge click behaves like a click on
        // the cross.
        chip.MouseLeftButtonUp += (_, _) => Ask();

        // Leaving a tile that was asked to confirm puts it back, so a half finished
        // removal never stays on screen.
        chip.MouseLeave += (_, _) =>
        {
            if (!awaitingConfirm)
            {
                return;
            }

            awaitingConfirm = false;

            Fade(label, 1, Motion.Fast);
            Fade(confirm, 0, Motion.Fast);
        };

        return chip;
    }

    /// <summary>
    /// Animates a brush to a new colour. Colour is always allowed, even when the
    /// system asks for reduced motion, because it conveys the state change without
    /// moving anything.
    /// </summary>
    private static void AnimateBrush(SolidColorBrush brush, Color to, int milliseconds) =>
        brush.BeginAnimation(SolidColorBrush.ColorProperty, Motion.Color(to, Motion.Fade(milliseconds)));

    private static void Fade(UIElement element, double to, int milliseconds) =>
        element.BeginAnimation(OpacityProperty, Motion.Value(to, Motion.Fade(milliseconds)));

    private static ScaleTransform EnsureScaleTransform(FrameworkElement element)
    {
        if (element.RenderTransform is ScaleTransform existing)
        {
            return existing;
        }

        var transform = new ScaleTransform(1, 1);
        element.RenderTransformOrigin = new Point(0.5, 0.5);
        element.RenderTransform = transform;
        return transform;
    }

    /// <summary>
    /// A newly added chip grows from 90% to full size while fading in.
    ///
    /// Only the last chip animates: the others are re-created on every render, and
    /// animating them all would make the whole panel twitch whenever one pair is
    /// added.
    /// </summary>
    private static void AnimateChipIn(FrameworkElement chip)
    {
        var transform = EnsureScaleTransform(chip);

        chip.Opacity = 0;
        transform.ScaleX = 0.9;
        transform.ScaleY = 0.9;

        chip.BeginAnimation(OpacityProperty, Motion.Value(1, Motion.Move(Motion.Normal)));
        transform.BeginAnimation(ScaleTransform.ScaleXProperty,
            Motion.Value(0.9, 1, Motion.Move(Motion.Normal)));
        transform.BeginAnimation(ScaleTransform.ScaleYProperty,
            Motion.Value(0.9, 1, Motion.Move(Motion.Normal)));
    }

    /// <summary>
    /// Staggers a set of elements into view, each a little after the last, so a
    /// list assembles instead of appearing all at once.
    /// </summary>
    private static void StaggerIn(IEnumerable<UIElement> elements)
    {
        if (Motion.ReduceMotion)
        {
            foreach (var element in elements)
            {
                element.Opacity = 1;
            }

            return;
        }

        // Each chip fades while rising a few pixels, which reads as the row
        // settling into place rather than as items switching on one by one.
        var index = 0;
        foreach (var element in elements)
        {
            element.Opacity = 0;

            var fade = Motion.Value(1, Motion.Normal, EasingMode.EaseOut);
            fade.BeginTime = Motion.Stagger * index;
            element.BeginAnimation(OpacityProperty, fade);

            index++;
        }
    }

    /// <summary>
    /// Shrinks a chip away and removes it. The neighbours close up on their own
    /// once the element is taken out of the panel, which the wrap panel animates
    /// as it re-lays out.
    /// </summary>
    private static void AnimateChipOut(FrameworkElement chip, Action completed)
    {
        var transform = EnsureScaleTransform(chip);
        var duration = Motion.Move(Motion.Exit);

        var scale = Motion.Value(0.88, duration, EasingMode.EaseIn);
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

        var fade = Motion.Value(0, duration, EasingMode.EaseIn);
        fade.Completed += (_, _) => completed();
        chip.BeginAnimation(OpacityProperty, fade);
    }

    // ------------------------------------------------------------------ input method

    private void ModeRelease_Click(object sender, RoutedEventArgs e) => SetMode(EngineMode.Release);

    private void ModeBlocking_Click(object sender, RoutedEventArgs e) => SetMode(EngineMode.Blocking);

    private void ModeHybrid_Click(object sender, RoutedEventArgs e) => SetMode(EngineMode.Hybrid);

    /// <summary>
    /// Changes the input method of the selected game only. The choice belongs to
    /// the profile, so switching it here never affects another game.
    /// </summary>
    private void SetMode(EngineMode mode)
    {
        // Raised while the control is only being pointed at the current value, so
        // it must not write anything back.
        if (!_ready || _updatingMode)
        {
            return;
        }

        if (_selected is null)
        {
            ShowToast(_settings.Profiles.Count == 0
                ? "Add a game first"
                : "Select a game to change its input method");
            return;
        }

        if (_selected.Mode == mode)
        {
            return;
        }

        _selected.Mode = mode;
        SaveSettings();
        ApplySettings();
        UpdateModeButtons();
        ShowToast($"{_selected.DisplayName}: {EngineModes.ToLabel(mode)}");
    }

    /// <summary>
    /// Points the segmented control at the method the selected game uses, and
    /// slides the white backing under it.
    /// </summary>
    private void UpdateModeButtons()
    {
        // Guarded for the same reason as the pair panel.
        if (_currentSection != PresetSection.Pairs && _currentSection is not null)
        {
            return;
        }

        _updatingMode = true;

        var mode = _selected?.Mode ?? EngineMode.Release;
        ModeReleaseSegment.IsChecked = mode == EngineMode.Release;
        ModeHybridSegment.IsChecked = mode == EngineMode.Hybrid;
        ModeBlockingSegment.IsChecked = mode == EngineMode.Blocking;

        _updatingMode = false;

        // A profile switch raises this twice: once from the list's selection change, and
        // again from the mouse-up on the row. The second call used to find the mode already
        // recorded, re-place the backing at once, and cut the slide short after a few
        // frames, which is why a profile switch looked different from a button press.
        // When the segment is already where it belongs, the animation in progress is left
        // to finish.
        if (_segmentPlaced && mode == _lastSegmentMode)
        {
            return;
        }

        // Animated only when the method actually changed. On the first placement there is
        // nothing to travel from, so it is set straight away.
        var changed = _segmentPlaced;
        _lastSegmentMode = mode;

        // Backing and labels start together, from the same dispatcher pass, so the text and
        // the white shape travel as one movement. The segments report no width until they
        // have been measured, which is why this waits for the layout pass.
        Dispatcher.BeginInvoke(() =>
        {
            UpdateSegmentBacking(changed);
            AnimateSegmentLabels(mode);
        }, DispatcherPriority.Loaded);
    }

    /// <summary>
    /// Fades each segment label between its resting and active colours.
    ///
    /// Both segments are animated, not just the two involved: a segment that is not
    /// selected must return to grey at the same rate, or it would appear to hold its
    /// dark colour from the previous state.
    /// </summary>
    private void AnimateSegmentLabels(EngineMode mode)
    {
        var resting = (Color)ColorConverter.ConvertFromString("#8A8A8A");
        var active = (Color)ColorConverter.ConvertFromString("#000000");

        foreach (var (segment, brush, isActive) in new[]
                 {
                     (ModeReleaseSegment, _releaseLabelBrush, mode == EngineMode.Release),
                     (ModeHybridSegment, _hybridLabelBrush, mode == EngineMode.Hybrid),
                     (ModeBlockingSegment, _blockingLabelBrush, mode == EngineMode.Blocking)
                 })
        {
            var target = isActive ? active : resting;
            segment.Foreground = brush;

            // The brush's base colour always holds the last target, so it is where the
            // label was resting before this change. The animation is given that colour as
            // its explicit start and the new colour as its end, so both labels travel over
            // the same interval as the backing.
            var previous = brush.Color;

            brush.BeginAnimation(SolidColorBrush.ColorProperty, null);
            brush.Color = target;

            if (Motion.ReduceMotion || previous == target)
            {
                continue;
            }

            brush.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation
            {
                From = previous,
                To = target,
                Duration = TimeSpan.FromMilliseconds(SegmentTravel),
                EasingFunction = Motion.Ease(EasingMode.EaseInOut)
            });
        }
    }

    /// <summary>
    /// Places the white backing under the selected segment.
    ///
    /// It is a standalone element rather than the segment's own background,
    /// because only a separate element can travel between two positions. Its
    /// width comes from the segment it is moving to, so the two choices can differ
    /// in width and the backing still fits exactly.
    /// </summary>
    /// <summary>
    /// Slides the white backing under the selected segment.
    ///
    /// Both properties are animated from their current values rather than to a
    /// target alone, and over a longer curve than the rest of the interface. The
    /// segment is the one control where a slow, visible travel is the point: it is
    /// showing which of two exclusive choices is active, so the movement itself is
    /// the feedback. A short animation made it look like a jump.
    ///
    /// No key frames and no completion callback. An earlier version used both, and
    /// a fast second click let the first animation finish afterwards and overwrite
    /// the newer position, which is what made the labels flicker.
    /// </summary>
    private void UpdateSegmentBacking(bool animate = true)
    {
        var target = ModeHybridSegment.IsChecked == true
                ? ModeHybridSegment
                : ModeBlockingSegment.IsChecked == true
                    ? ModeBlockingSegment
                : ModeReleaseSegment;
        if (target.ActualWidth <= 0 || SegmentBacking.Parent is not UIElement parent)
        {
            return;
        }

        _segmentPlaced = true;

        var width = target.ActualWidth;
        var left = target.TranslatePoint(new Point(0, 0), parent).X;
        var settled = new Thickness(left, 0, 0, 0);

        // Where the backing actually is, remembered rather than read back from the
        // element.
        //
        // Reading the property was the bug: clearing an animation restores the property
        // to its base value, and the base only moves when an animation is left holding
        // its end state. Since every animation here was cleared before the next started,
        // the base stayed at the very first position, so one direction slid from the
        // right place and the other had nowhere to travel.
        var fromLeft = _segmentLeft;
        var fromWidth = _segmentWidth > 0 ? _segmentWidth : width;

        SegmentBacking.BeginAnimation(WidthProperty, null);
        SegmentBacking.BeginAnimation(MarginProperty, null);

        if (!animate || Motion.ReduceMotion)
        {
            SegmentBacking.Width = width;
            SegmentBacking.Margin = settled;

            _segmentLeft = left;
            _segmentWidth = width;
            return;
        }

        _segmentLeft = left;
        _segmentWidth = width;

        // Ease in and out, and longer than the default: the backing accelerates away
        // from the old segment and settles onto the new one, which reads as one
        // deliberate movement.
        SegmentBacking.BeginAnimation(WidthProperty,
            Motion.Value(fromWidth, width, SegmentTravel, EasingMode.EaseInOut));

        SegmentBacking.BeginAnimation(MarginProperty,
            Motion.Thickness(fromLeft, settled, SegmentTravel, EasingMode.EaseInOut));
    }

    /// <summary>The page on screen, so a repeat request can be ignored.</summary>
    private PresetSection? _currentSection;

    /// <summary>A page change that is under way and not yet on screen.</summary>
    private PresetSection? _pendingSection;

    /// <summary>Fires once a page transition has settled.</summary>
    private DispatcherTimer? _settleTimer;

    /// <summary>Where the segment backing is, kept across animations.</summary>
    private double _segmentLeft;

    /// <summary>How wide it is, kept for the same reason.</summary>
    private double _segmentWidth;

    /// <summary>
    /// How long the segment backing takes to travel.
    ///
    /// Longer than the interface default on purpose: this control shows which of two
    /// exclusive modes is active, so the movement carries the meaning.
    /// </summary>
    private const int SegmentTravel = 300;

    // ------------------------------------------------------------------ settings page

    /// <summary>
    /// Turns the launch with Windows entry on or off.
    ///
    /// The registry is the source of truth, so the switch is set from it at startup
    /// rather than from the settings file: an entry removed by some other means — a
    /// cleaner, a moved build, a reinstall — would otherwise leave the switch claiming
    /// a startup that no longer happens.
    /// </summary>
    private void StartWithWindows_Changed(object sender, RoutedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        var wanted = StartWithWindowsToggle.IsChecked == true;

        if (!_startup.SetEnabled(wanted))
        {
            ShowToast("Windows refused the change");

            // The switch follows the registry rather than the click, so it never
            // shows a state that is not real.
            StartWithWindowsToggle.IsChecked = _startup.IsEnabled();
            return;
        }

        _settings.StartWithWindows = wanted;
        SaveSettings();
    }


    /// <summary>
    /// Removes the selected game, from the bin button.
    ///
    /// No confirmation: the profile is cheap to recreate, and an extra dialog for a
    /// single row was more friction than the action deserves.
    /// </summary>
    private void RemoveSelectedProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            ShowToast("Select a game first");
            return;
        }

        // A confirmation, because there is no undo and a profile carries its own pairs.
        // Only Remove and the close cross: cancelling is what both of those do, so a
        // separate Cancel would be a third way to say the same thing.
        var dialog = new ConfirmRemoveWindow(_selected.DisplayName)
        {
            Owner = this
        };

        // Sized over this window and clipped, before anything is painted.
        dialog.CoverOwnerBeforeShow(this);

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var removed = _selected.DisplayName;
        RemoveProfile(_selected);
        ShowToast($"Removed {removed}");
    }

    /// <summary>
    /// Kept as the single place the engine state would be reported.
    ///
    /// The switch shows armed or not, and a failing worker shows up as the switch
    /// refusing to stay on, so there is nothing further to print.
    /// </summary>
    private void RefreshStatus()
    {
    }

    /// <summary>
    /// Notices that would have gone to a status line.
    ///
    /// Nothing is shown any more: the interface reflects every action it accepts,
    /// so a text confirmation beside it was redundant. The calls remain so the
    /// places that guard against a wrong input still read clearly.
    /// </summary>
    private static void ShowToast(string message)
    {
    }

    /// <summary>
    /// Finds the scroll viewer inside a list and gives it eased wheel handling.
    ///
    /// The viewer lives in the list's own template, so it has to be looked up after
    /// the control has been laid out.
    /// </summary>
    private void AttachSmoothScrolling(ItemsControl list)
    {
        void Attach()
        {
            if (FindDescendant<ScrollViewer>(list) is { } viewer)
            {
                Interaction.AttachSmoothWheel(list, viewer);
            }
        }

        if (list.IsLoaded)
        {
            Attach();
        }
        else
        {
            list.Loaded += (_, _) => Attach();
        }
    }

    /// <summary>
    /// First descendant of the requested type, or null.
    /// </summary>
    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var index = 0; index < count; index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T match)
            {
                return match;
            }

            if (FindDescendant<T>(child) is { } deeper)
            {
                return deeper;
            }
        }

        return null;
    }

    /// <summary>
    /// Re-reads whether each game is running and repaints the dots.
    ///
    /// Works on the existing rows instead of rebuilding them, so the selection, the
    /// scroll position and any hover state survive the update.
    /// </summary>
    private void RefreshRunningIndicators()
    {
        foreach (var row in _rows)
        {
            row.UpdateRunningState();
        }
    }

    private void StartStatusTimer()
    {
        // Guarded like the star timer: starting twice would leave the first one
        // ticking with no way to reach it.
        if (_statusTimer is not null)
        {
            return;
        }

        _statusTimer = new DispatcherTimer(StatusRefreshInterval, DispatcherPriority.Background, (_, _) =>
        {
            RefreshRunningIndicators();
        }, Dispatcher);

        _statusTimer.Start();
    }

    private void StopStatusTimer()
    {
        _statusTimer?.Stop();
        _statusTimer = null;
    }

    // ------------------------------------------------------------------ window chrome

    /// <summary>
    /// Drags the window. A press that lands on a control inside the drag region is
    /// ignored, because DragMove captures the mouse until it is released and would
    /// otherwise swallow the matching button-up.
    /// </summary>
    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (IsWithinInteractiveControl(e.OriginalSource as DependencyObject))
        {
            return;
        }

        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    /// <summary>
    /// True when the element under the cursor handles the click itself, so the
    /// window drag must step aside.
    /// </summary>
    private static bool IsWithinInteractiveControl(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is ButtonBase or TextBox or CheckBox or Slider or ListBoxItem or ScrollBar or Thumb)
            {
                return true;
            }

            if (source is FrameworkElement element && element.Name == "MasterSwitch")
            {
                return true;
            }

            // Clickable tiles such as the pair chips declare a hand cursor. A window drag
            // that began on one would take the mouse and the chip would never see its click.
            if (source is FrameworkElement { Cursor: not null } tile && ReferenceEquals(tile.Cursor, Cursors.Hand))
            {
                return true;
            }

            source = VisualTreeHelper.GetParent(source);
        }

        return false;
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Window_SourceInitialized(object? sender, EventArgs e) => ConfigureWindowChrome();

    /// <summary>
    /// Keeps the window fixed in size while drawing our own chrome.
    /// </summary>
    private void ConfigureWindowChrome()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var style = GetWindowLong(handle, GwlStyle);
        SetWindowLong(handle, GwlStyle, (style | WsThickFrame) & ~WsCaption);
    }

    // ------------------------------------------------------------------ navigation

    private enum PresetSection
    {
        /// <summary>Key pairs of the selected game, plus its input method.</summary>
        Pairs,

        Settings,

        /// <summary>Form for creating a game profile.</summary>
        NewProfile
    }

    /// <summary>
    /// Shows one content page, animating the swap.
    ///
    /// The outgoing page slides out and fades while the incoming one arrives from
    /// the opposite side, and the header follows it in slightly late. The pages
    /// overlap during the transition rather than one being hidden first, so the
    /// area is never blank.
    /// </summary>
    private void SelectSection(PresetSection section)
    {
        // Already on this page, or already moving to it: nothing to do.
        //
        // A click on a game can arrive here twice — once from the selection change and
        // once from the mouse handler — and a second cross-fade started while the first
        // was still running is what made the change look broken.
        if (_currentSection == section || _pendingSection == section)
        {
            return;
        }

        _pendingSection = section;

        // Marked current before anything below can raise a selection change. The restore of
        // the game's row (further down) fires the list handler, and that handler only draws
        // the key pairs when the pairs page is already the current one. Setting it at the
        // end, as before, left the pairs panel empty after the back arrow.
        _currentSection = section;

        var incoming = PageFor(section);
        var pages = new UIElement[] { PairsPage, SettingsPage, NewProfilePage };

        // Every other page is taken off screen at once, whatever state it was in. A page
        // left visible by an interrupted fade would otherwise sit behind the incoming one.
        //
        // Done again after the incoming page is chosen rather than only before, because
        // opening the add form also renders the pair panel for the current selection, and
        // that render is what put the pair page back on screen.
        foreach (var page in pages.Where(page => page != incoming))
        {
            HidePage(page);
        }

        // The header names the game and nothing else. Every other page carries its own
        // title inside its card, so no page name can appear here and leak between pages.
        HeaderTitle.Text = "Profile:";
        HeaderProfile.Text = section == PresetSection.Pairs && _selected is not null
            ? _selected.DisplayName
            : string.Empty;

        // The two parts of the greeting appear together or not at all.
        var onPairs = section == PresetSection.Pairs && _selected is not null;
        HeaderTitle.Visibility = onPairs ? Visibility.Visible : Visibility.Collapsed;

        SettingsNav.Tag = section == PresetSection.Settings ? "active" : null;

        // The rename and delete buttons belong to a game, so they are dimmed and inert on
        // every other page.
        //
        // Set here as well as when a transition settles, because a transition can be
        // refused if it is already on its way to this page, and a refused transition would
        // leave the buttons bright. Only ever dimmed, never brightened: brightening them
        // in passing was the flash.
        if (section != PresetSection.Pairs)
        {
            SetGameActionsEnabled(false);
        }

        // The row highlight only means something while a game's own page is showing, so it
        // is parked on every other page and restored on the way back.
        //
        // The condition used to name only the settings page, and restore the selection for
        // everything else — the add form included. That is what made the row light up, the
        // profile name appear beside "New Profile", the pair panel render behind it, and the
        // rename and delete buttons flash bright before correcting themselves.
        if (section == PresetSection.Pairs)
        {
            if (_suspendedSelection is not null && ProfileList.SelectedItem is null)
            {
                ProfileList.SelectedItem = _suspendedSelection;
                _suspendedSelection = null;
            }
        }
        else if (ProfileList.SelectedItem is ProfileRow current)
        {
            _suspendedSelection = current;

            // Cleared quietly: the handler would otherwise treat this as the user choosing
            // nothing and re-render the panel on the way out.
            _suppressTitleSuggestion = true;
            ProfileList.SelectedItem = null;
            _suppressTitleSuggestion = false;
        }

        // The engine switch stays on screen on every page: it controls the running engine,
        // not the page, so it must remain reachable while the add form is open.
        MasterSwitch.Visibility = Visibility.Visible;

        // The first call comes from startup, where there is nothing to animate
        // away from and the window has not been laid out yet.
        if (!_ready || Motion.ReduceMotion)
        {
            ShowPageImmediately(incoming);

            _currentSection = section;
            _pendingSection = null;
            return;
        }

        // Forward means entering the form; coming back reverses the direction.
        // The transition that is starting owns the state from here.
        _currentSection = section;
        _pendingSection = null;

        // The header follows the page it belongs to.
        HeaderProfile.Text = section == PresetSection.Pairs && _selected is not null
            ? _selected.DisplayName
            : string.Empty;

        var showGreeting = section == PresetSection.Pairs && _selected is not null;
        HeaderTitle.Visibility = showGreeting ? Visibility.Visible : Visibility.Collapsed;

        SlidePageIn(incoming, section == PresetSection.NewProfile ? 1 : -1);

        // Settled once the transition has run, so the game actions reflect the page that
        // is actually on screen.
        var settle = new DispatcherTimer(TimeSpan.FromMilliseconds(PageCrossFade + 20),
            DispatcherPriority.Background, (_, _) =>
            {
                _settleTimer?.Stop();
                RestoreGameActions();
            }, Dispatcher);

        _settleTimer?.Stop();
        _settleTimer = settle;
        settle.Start();
    }

    private UIElement PageFor(PresetSection section) => section switch
    {
        PresetSection.Settings => SettingsPage,
        PresetSection.NewProfile => NewProfilePage,
        _ => PairsPage
    };

    private static void ShowPageImmediately(UIElement page)
    {
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 1;
        page.Visibility = Visibility.Visible;

        if (page.RenderTransform is TranslateTransform transform)
        {
            transform.BeginAnimation(TranslateTransform.XProperty, null);
            transform.X = 0;
        }
    }

    /// <summary>
    /// Takes a page off screen and clears anything still animating on it.
    ///
    /// Clearing the animation matters: one left running keeps driving the property after
    /// the page is hidden, and the page reappears part faded the next time it is shown.
    /// </summary>
    private static void HidePage(UIElement page)
    {
        page.BeginAnimation(OpacityProperty, null);
        page.Opacity = 1;
        page.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// Brings a page in by fading it up.
    ///
    /// The incoming page is shown at once, so it is already on screen while the
    /// outgoing one is still clearing. That overlap is what keeps the change from
    /// looking like a blank flash.
    /// </summary>
    private static void SlidePageIn(UIElement page, int direction)
    {
        page.Visibility = Visibility.Visible;
        page.Opacity = 0;

        page.BeginAnimation(OpacityProperty,
            Motion.Value(1, PageCrossFade, EasingMode.EaseOut));
    }

    /// <summary>
    /// How long a page cross-fade takes.
    ///
    /// Longer than the interface default because a whole page is changing; short
    /// enough that the change still reads as immediate.
    /// </summary>
    private const int PageCrossFade = 200;

    /// <summary>
    /// The header follows the content in by a fraction of a second and eight
    /// pixels, which reads as the page settling rather than appearing at once.
    /// </summary>
    /// <summary>
    /// The header fades in just behind the page, which is enough to tie the two
    /// together without a second layout pass per frame.
    /// </summary>
    private void AnimateHeaderIn()
    {
        HeaderPanel.BeginAnimation(OpacityProperty, null);

        HeaderPanel.Opacity = 0;

        var fade = Motion.Value(1, Motion.Normal, EasingMode.EaseOut);
        fade.BeginTime = TimeSpan.FromMilliseconds(30);
        HeaderPanel.BeginAnimation(OpacityProperty, fade);
    }

    /// <summary>
    /// Gives an element a horizontal translate transform, reusing the existing one
    /// so an in-flight animation is not replaced mid-move.
    /// </summary>
    private static TranslateTransform EnsureTransform(UIElement element)
    {
        if (element.RenderTransform is TranslateTransform existing)
        {
            return existing;
        }

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        return transform;
    }

    private void SettingsNav_Click(object sender, RoutedEventArgs e) => SelectSection(PresetSection.Settings);

    /// <summary>
    /// Re-enables the game actions once a transition has settled.
    ///
    /// Called after the page change rather than inside it: the buttons depend on which
    /// page is on screen, and during a transition that answer is still changing.
    /// </summary>
    private void RestoreGameActions()
    {
        SetGameActionsEnabled(_currentSection == PresetSection.Pairs && _selected is not null);
    }

    // ------------------------------------------------------------------ master switch

    /// <summary>
    /// Toggles on press rather than on release.
    ///
    /// A click is a press and a release, and the release only reaches the element
    /// if the pointer is still over it when the button comes up. Reacting to the
    /// release therefore made the switch feel unresponsive: any small movement
    /// between the two halves silently dropped the click.
    /// </summary>
    private void MasterSwitch_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        SetEngineEnabled(!_settings.SnapHookEnabled);
    }

    /// <summary>
    /// Arms or disarms the engine. Used by the switch in the window and by the tray menu,
    /// so both always do the same thing.
    /// </summary>
    private void SetEngineEnabled(bool enabled)
    {
        _settings.SnapHookEnabled = enabled;
        SaveSettings();

        // The worker is restarted from the current settings, which is what actually
        // arms or disarms the engine.
        //
        // This call was missing, so flipping the switch only changed what was saved
        // and drawn: the running engine kept whatever it had. Whether snap tap then
        // worked depended on something else happening to restart it later, which is
        // exactly the "on every other try" behaviour.
        ApplySettings();

        // Painted after the restart, then the glow plays over the result.
        AnimateSwitchFlash();
    }

    private void UpdateMasterSwitch()
    {
        var on = _settings.SnapHookEnabled;

        // The orbiting light is created once, the first time the switch is
        // painted, and then only started or stopped.
        _starEffect ??= StarBorderEffect.Attach(MasterSwitch);

        MasterToggle.IsChecked = on;
        _tray.SetActive(on);
        // A fixed width label, so the plate never resizes as the state changes.
        MasterSwitchLabel.Text = on ? "ACTIVE" : "INACTIVE";

        MasterSwitchLabel.Foreground = (Brush)FindResource(on ? "TextBrush" : "SubTextBrush");
        MasterSwitch.BorderBrush = (Brush)FindResource(on ? "TextBrush" : "BorderBrushSoft");
        MasterSwitch.Background = (Brush)FindResource(on ? "RaisedBrush" : "PanelBrush");

        // Armed, the outline carries a travelling highlight; disarmed, it rests.
        if (on)
        {
            _starEffect.Start(TimeSpan.FromSeconds(2.6));
        }
        else
        {
            _starEffect.Stop();
        }
    }

    /// <summary>
    /// Acknowledge the click by letting the plate brighten and settle.
    ///
    /// The previous version restarted an animation that had already run, which
    /// produced a short bright blink. This one eases the brightness up and then
    /// back down along a single curve, so the change reads as the switch waking
    /// rather than as a flash.
    /// </summary>
    private void AnimateSwitchFlash()
    {
        var on = _settings.SnapHookEnabled;

        // Two stops rather than a jump: up to the lit shade, then down to rest.
        var lit = on ? Color.FromRgb(0x3A, 0x3A, 0x3A) : Color.FromRgb(0x2A, 0x2A, 0x2A);
        var rest = on ? Color.FromRgb(0x11, 0x11, 0x11) : Color.FromRgb(0x0D, 0x0D, 0x0D);

        var brush = new SolidColorBrush(lit);
        MasterSwitch.Background = brush;

        var settle = Motion.Color(rest, 420, EasingMode.EaseOut);
        settle.BeginTime = TimeSpan.FromMilliseconds(60);
        brush.BeginAnimation(SolidColorBrush.ColorProperty, settle);
    }

    /// <summary>
    /// Looks up a named part inside a button's own template.
    ///
    /// A name declared in a control template is not part of the window name scope,
    /// so it has to be resolved through the template rather than by field.
    /// </summary>
    private FrameworkElement? FindTemplatePart(string name) =>
        RemoveProfileButton.Template?.FindName(name, RemoveProfileButton) as FrameworkElement;

    // ------------------------------------------------------------------ helpers

    private ImageSource? GetIcon(GameProfile profile)
    {
        var path = profile.ExecutablePath ?? ProcessService.TryGetPath(profile.ProcessName);
        return IconService.FromPath(path);
    }

    private static FrameworkElement MakeIcon(string data, Brush stroke, double thickness)
    {
        // Drawn into a fixed square canvas, so the glyph is centred by the
        // container rather than by its own bounding box.
        var path = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse(data),
            Stroke = stroke,
            StrokeThickness = thickness,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round
        };

        var canvas = new Canvas { Width = 16, Height = 16 };
        canvas.Children.Add(path);

        return new Viewbox
        {
            Width = 16,
            Height = 16,
            Child = canvas,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private void EnsureRoundedCorners()
    {
        // Reserved for DPI dependent chrome adjustments. The frame is drawn by
        // WPF, so there is nothing to recompute today.
    }

    // ------------------------------------------------------------------ interop

    private const int GwlStyle = -16;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr handle, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr handle, int index, int value);

    /// <summary>
    /// One row of the sidebar. Raises change notifications so the running dot can
    /// be repainted without rebuilding the list.
    /// </summary>
    private sealed class ProfileRow : INotifyPropertyChanged
    {
        private static readonly Brush RunningBrushValue = new SolidColorBrush(Color.FromRgb(0xFF, 0xFF, 0xFF));
        private static readonly Brush StoppedBrush = new SolidColorBrush(Color.FromRgb(0x3F, 0x3F, 0x3F));

        private readonly GameProfile _profile;
        private bool _isRunning;

        public ProfileRow(GameProfile profile)
        {
            _profile = profile;

            RunningBrush = StoppedBrush;
            RunningHint = "Not running";
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public GameProfile Profile => _profile;

        public string DisplayName => _profile.DisplayName;

        public Visibility RemovalVisibility => Visibility.Visible;

        public string RemovalHint => $"Remove \"{DisplayName}\"";

        public ImageSource? Icon { get; private set; }

        public Brush RunningBrush { get; private set; }

        public string RunningHint { get; private set; }

        public bool IsRunning => _isRunning;

        public void Refresh(ImageSource? icon)
        {
            Icon = icon;
            UpdateRunningState();
        }

        /// <summary>
        /// Re-reads the process table and updates the indicator only when the
        /// answer actually changed, so the interface is not repainted twice a
        /// second for nothing.
        /// </summary>
        public void UpdateRunningState()
        {
            if (!_profile.Enabled)
            {
                SetRunning(false, "Disabled");
                return;
            }

            var running = ProcessService.IsRunning(_profile.ProcessName);
            SetRunning(running, running ? "Running" : "Not running");
        }

        private void SetRunning(bool running, string hint)
        {
            if (_isRunning == running && RunningHint == hint)
            {
                return;
            }

            _isRunning = running;
            RunningBrush = running ? RunningBrushValue : StoppedBrush;
            RunningHint = hint;

            Raise(nameof(RunningBrush));
            Raise(nameof(RunningHint));
            Raise(nameof(IsRunning));
        }

        private void Raise(string name) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
