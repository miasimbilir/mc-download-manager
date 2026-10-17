using System.ComponentModel;
using Avalonia;
using Avalonia.Collections;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace MCDM.App.Views;

public enum SidebarKind { All, Category, Finished, Unfinished }

public sealed class SidebarEntry(SidebarKind kind, string label, string icon, FileCategory? category = null) : INotifyPropertyChanged
{
    public SidebarKind Kind { get; } = kind;
    public FileCategory? Category { get; } = category;
    public string Label { get; } = label;
    public Geometry? Icon { get; } = Icons.Get(icon);
    public Thickness Indent { get; } = new(kind == SidebarKind.Category ? 22 : 0, 0, 0, 0);
    public FontWeight Weight { get; } = kind == SidebarKind.Category ? FontWeight.Normal : FontWeight.SemiBold;

    private int count;
    public int Count
    {
        get => count;
        set
        {
            if (count == value) return;
            count = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText)));
        }
    }
    public string CountText => count > 0 ? count.ToString() : "";

    public bool Matches(DownloadItem i) => Kind switch
    {
        SidebarKind.Category => i.Category == Category,
        SidebarKind.Finished => i.IsFinished,
        SidebarKind.Unfinished => !i.IsFinished,
        _ => true,
    };

    public event PropertyChangedEventHandler? PropertyChanged;
}

public partial class MainWindow : Window
{
    private readonly App app = null!;
    private readonly DownloadStore store = null!;
    private readonly DataGridCollectionView view = null!;
    private readonly List<SidebarEntry> entries = [];
    private int membership;
    private bool trayHintShown;

    public MainWindow() => InitializeComponent();      // designer

    public MainWindow(App app)
    {
        this.app = app;
        store = app.Store;
        InitializeComponent();
        Icon = Icons.AppIcon;

        entries.Add(new SidebarEntry(SidebarKind.All, Loc.T("All"), "IconFolder"));
        foreach (var c in FileCategories.All) entries.Add(new SidebarEntry(SidebarKind.Category, c.Label(), Icons.KeyFor(c), c));
        entries.Add(new SidebarEntry(SidebarKind.Finished, Loc.T("Finished"), "IconCheckmarkCircle"));
        entries.Add(new SidebarEntry(SidebarKind.Unfinished, Loc.T("Unfinished"), "IconHistory"));
        Sidebar.ItemsSource = entries;
        Sidebar.SelectedIndex = 0;
        Sidebar.SelectionChanged += (_, _) =>
        {
            if (Sidebar.SelectedItem == null) Sidebar.SelectedIndex = 0;
            RefreshView();
        };

        view = new DataGridCollectionView(store.Items) { Filter = o => o is DownloadItem i && Visible(i) };
        List.ItemsSource = view;
        List.SelectedItem = null;       // the grid would otherwise start with the first row selected

        AddButton.Click += (_, _) => ShowAdd();
        StartButton.Click += (_, _) => store.ResumeAll();
        StopButton.Click += (_, _) => store.PauseAll();
        ClearButton.Click += (_, _) => store.ClearFinished();
        SettingsButton.Click += (_, _) => app.ShowSettings();
        Search.TextChanged += (_, _) => RefreshView();

        List.DoubleTapped += (_, e) =>
        {
            if (RowItem(e.Source) is { } item) app.ShowDetail(item.Id);
        };
        List.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        List.ContextMenu = new ContextMenu();
        List.ContextMenu.Opening += (_, _) => BuildContextMenu();

        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, (_, e) => e.DragEffects = DragDropEffects.Copy);
        AddHandler(DragDrop.DropEvent, OnDrop);

        LimitButton.Click += (_, _) => app.Settings.SpeedLimitEnabled = !app.Settings.SpeedLimitEnabled;
        var whenDone = new MenuFlyout();
        foreach (var action in App.WhenDoneOptions)
        {
            var choice = new MenuItem { Header = App.WhenDoneLabel(action), ToggleType = MenuItemToggleType.Radio };
            choice.Click += (_, _) => store.WhenDone = action;
            whenDone.Items.Add(choice);
        }
        whenDone.Opening += (_, _) =>
        {
            for (int i = 0; i < whenDone.Items.Count; i++)
                if (whenDone.Items[i] is MenuItem m) m.IsChecked = App.WhenDoneOptions[i] == store.WhenDone;
        };
        WhenDoneButton.Flyout = whenDone;

        store.Changed += OnStoreChanged;
        app.Settings.PropertyChanged += (_, _) => UpdateStatusBar();
        app.Server.PropertyChanged += (_, _) => UpdateStatusBar();
        Closing += (_, e) =>
        {
            if (app.Quitting) return;
            // Closing the window keeps the app (and the browser bridge) alive in the notification area.
            e.Cancel = true;
            Hide();
            if (!trayHintShown && app.Settings.OnboardingDone)
            {
                trayHintShown = true;
                ToastWindow.Show(app, AppInfo.Name, Loc.T("Still running in the notification area. Use the tray icon to quit."));
            }
        };
        OnStoreChanged();
    }

    private IReadOnlyList<DownloadItem> Selected => [.. List.SelectedItems.OfType<DownloadItem>()];

    private static DownloadItem? RowItem(object? source) =>
        (source as Visual)?.FindAncestorOfType<DataGridRow>(includeSelf: true)?.DataContext as DownloadItem;

    private SidebarEntry Current => Sidebar.SelectedItem as SidebarEntry ?? entries[0];

    private bool Visible(DownloadItem i)
    {
        if (!Current.Matches(i)) return false;
        var q = Search.Text?.Trim();
        return string.IsNullOrEmpty(q)
            || i.FileName.Contains(q, StringComparison.CurrentCultureIgnoreCase)
            || i.Url.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private int Membership()
    {
        var h = new HashCode();
        foreach (var i in store.Items) if (Visible(i)) h.Add(i.Id);
        return h.ToHashCode();
    }

    private void RefreshView()
    {
        membership = Membership();
        view.Refresh();
        UpdateEmptyState();
    }

    private void OnStoreChanged()
    {
        foreach (var e in entries) e.Count = store.Items.Count(e.Matches);
        // Re-filter only when an item entered or left the current view (e.g. finished while "Unfinished" is shown).
        var now = Membership();
        if (now != membership)
        {
            membership = now;
            view.Refresh();
        }
        UpdateEmptyState();
        UpdateStatusBar();
    }

    private void UpdateEmptyState()
    {
        var empty = !store.Items.Any(Visible);
        Empty.IsVisible = empty;
        if (!empty) return;
        var searching = !string.IsNullOrWhiteSpace(Search.Text);
        EmptyTitle.Text = searching ? Loc.T("No matching downloads") : Loc.T("No downloads in this view");
        EmptyHint.Text = searching ? "" : Loc.T("Download a file from your browser, drop a link here,\nor press Ctrl+N to enter a URL.");
    }

    private void UpdateStatusBar()
    {
        var running = app.Server.Running;
        BridgeDot.Fill = running ? new SolidColorBrush(Color.FromRgb(0x2E, 0xBD, 0x59)) : new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
        BridgeText.Text = running ? Loc.T("Browser bridge is running") : app.Server.StatusText;
        UnfinishedText.Text = store.UnfinishedCount.ToString();
        SpeedText.Text = Fmt.Speed(store.TotalSpeed);
        LimitButton.Content = app.SpeedLimitLabel;
        WhenDoneButton.Content = app.WhenDoneSummary;
        // Something that is switched on stands out; the usual state stays as quiet as the rest of the bar.
        var state = (app.Settings.SpeedLimitEnabled, store.WhenDone != QueueEndAction.None);
        if (state == highlighted) return;
        highlighted = state;
        static Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension Brush(bool on) => new(on ? "AccentBrush" : "SecondaryTextBrush");
        LimitButton[!ForegroundProperty] = Brush(state.Item1);
        WhenDoneButton[!ForegroundProperty] = Brush(state.Item2);
    }

    private (bool Limit, bool WhenDone)? highlighted;

    // ---- Dialogs ----

    public async void ShowAdd()
    {
        if (OwnedWindows.OfType<AddDownloadWindow>().FirstOrDefault() is { } open) { open.Activate(); return; }
        await new AddDownloadWindow(app).ShowDialog(this);
    }

    private bool onboardingOpen;

    public async void ShowOnboarding()
    {
        if (onboardingOpen) return;
        onboardingOpen = true;
        try { await new OnboardingWindow(app).ShowDialog(this); }
        finally { onboardingOpen = false; }
    }

    // ---- Input ----

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var inText = FocusManager?.GetFocusedElement() is TextBox;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (ctrl && e.Key == Key.N) { e.Handled = true; ShowAdd(); }
        else if (ctrl && e.Key == Key.F) { e.Handled = true; Search.Focus(); Search.SelectAll(); }
        else if (ctrl && e.Key == Key.OemComma) { e.Handled = true; app.ShowSettings(); }
        else if (inText) return;
        else if (ctrl && e.Key == Key.V)
        {
            e.Handled = true;
            try
            {
                var text = Clipboard is { } c ? await c.TryGetTextAsync() : null;
                if (!string.IsNullOrWhiteSpace(text)) await app.Clipboard.HandlePaste(text);
            }
            catch { }
        }
        else if (e.Key == Key.Delete)
        {
            e.Handled = true;
            foreach (var i in Selected) store.Remove(i.Id);
        }
        else if (e.Key == Key.Enter && Selected.Count > 0)
        {
            e.Handled = true;
            foreach (var i in Selected) app.ShowDetail(i.Id);
        }
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618   // the replacement (DataTransfer) only exists from Avalonia 11.3
        var text = e.Data.GetText();
#pragma warning restore CS0618
        if (string.IsNullOrWhiteSpace(text)) return;
        store.AddUrls(text, DownloadKind.file);
    }

    /// Right-clicking a row that is not selected selects it first, so the menu acts on what was clicked.
    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(List).Properties.IsRightButtonPressed) return;
        var item = RowItem(e.Source);
        if (item == null) List.SelectedItem = null;
        else if (!List.SelectedItems.Contains(item)) List.SelectedItem = item;
    }

    private void BuildContextMenu()
    {
        var menu = List.ContextMenu!;
        var items = Selected;
        var list = new List<object>();
        MenuItem Item(string header, Action action)
        {
            var m = new MenuItem { Header = header };
            m.Click += (_, _) => action();
            return m;
        }
        if (items.Count == 0)
        {
            list.Add(Item(Loc.T("Add URL…"), ShowAdd));
        }
        else
        {
            list.Add(Item(Loc.T("Open Download Window"), () => { foreach (var i in items) app.ShowDetail(i.Id); }));
            list.Add(new Separator());
            if (items.Any(i => i.State == DownloadState.queued))
                list.Add(Item(Loc.T("Download Now"), () => { foreach (var i in items.Where(i => i.State == DownloadState.queued)) store.StartNow(i.Id); }));
            if (items.Any(i => i.State.IsActive() || i.State == DownloadState.queued))
            {
                list.Add(Item(Loc.T("Pause"), () => { foreach (var i in items) store.Pause(i.Id); }));
                list.Add(Item(Loc.T("Cancel"), () => { foreach (var i in items) store.Cancel(i.Id); }));
            }
            if (items.Any(i => i.State is DownloadState.paused or DownloadState.failed or DownloadState.cancelled))
                list.Add(Item(Loc.T("Resume / Retry"), () => { foreach (var i in items) store.Resume(i.Id); }));
            if (items.Count == 1)
            {
                var item = items[0];
                list.Add(new Separator());
                list.Add(Item(Loc.T("Copy URL"), async () =>
                {
                    app.Clipboard.Ignore(item.Url);
                    try { if (Clipboard is { } c) await c.SetTextAsync(item.Url); } catch { }
                }));
                if (item.State == DownloadState.failed && item.Kind == DownloadKind.file)
                    list.Add(Item(Loc.T("Download in Browser"), () => store.HandOffToBrowser(item.Id)));
                if (item.State == DownloadState.completed)
                {
                    list.Add(Item(Loc.T("Open"), () => store.Open(item)));
                    list.Add(Item(Loc.T("Show in Folder"), () => store.Reveal(item)));
                }
            }
            list.Add(new Separator());
            list.Add(Item(Loc.T("Remove from List"), () => { foreach (var i in items) store.Remove(i.Id); }));
            if (items.Any(i => i.State == DownloadState.completed))
                list.Add(Item(Loc.T("Move File to Recycle Bin and Remove"), () => { foreach (var i in items) store.Remove(i.Id, deleteFile: true); }));
        }
        menu.ItemsSource = list;
    }
}
