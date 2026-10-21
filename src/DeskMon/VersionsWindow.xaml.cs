using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Sensors;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// What has changed, version by version, from <see cref="Changelog"/>.
///
/// Static content, so it is built once when the window is first opened and
/// never touched again - no timer, nothing to refresh. Like the other secondary
/// windows it hides rather than closes, so reopening it is instant and keeps
/// your scroll position.
/// </summary>
public partial class VersionsWindow : PanelWindow
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;

    /// <summary>
    /// Collapses a drag or resize into one write, as the other panel windows
    /// do - WPF raises those events once per mouse move.
    /// </summary>
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private bool _allowClose;

    public VersionsWindow(AppSettings settings, UiTheme theme)
    {
        _settings = settings;
        _theme = theme;

        InitializeComponent();
        new Banner(Panel, height: 210).Show(Notes.GameArt.Motif.Timeline);
        ApplyTheme();
        _theme.Changed += () =>
        {
            ApplyTheme();
            BuildHistory();   // its version names are in the accent
        };
        RestorePlacement(_settings.VersionsLeft, _settings.VersionsTop,
                         _settings.VersionsWidth, _settings.VersionsHeight);

        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.VersionsOpacity);
        BuildHistory();
        _ = ShowRequirementsAsync();
        RefreshUpdate();

        _placementSave.Tick += (_, _) => SavePlacementNow();

        Closing += OnClosing;
        LocationChanged += (_, _) => QueuePlacementSave();
        SizeChanged += (_, _) => QueuePlacementSave();
    }

    // --- History ----------------------------------------------------------

    /// <summary>
    /// The changelog as one heading per day, each folding its releases away.
    ///
    /// It used to be every release in full, one after another: fifty-odd
    /// versions and nearly two hundred lines, with the same date printed
    /// twenty times over on a busy day. Grouped, the date is said once; folded,
    /// the window opens on what changed most recently - which is what it is
    /// opened for - with the rest one click away.
    /// </summary>
    private void BuildHistory()
    {
        History.Children.Clear();
        var current = Changelog.Releases.FirstOrDefault();

        // GroupBy keeps the order things first appear in, so the days stay
        // newest first, as the changelog lists them.
        bool first = true;
        foreach (var day in Changelog.Releases.GroupBy(r => r.Date))
        {
            var releases = day.ToList();

            var body = new StackPanel
            {
                Margin = new Thickness(0, 2, 0, 8),
                Visibility = first ? Visibility.Visible : Visibility.Collapsed,
            };
            foreach (var release in releases)
                body.Children.Add(ReleaseBlock(release, ReferenceEquals(release, current)));

            History.Children.Add(DayHeading(day.Key, releases, body));
            History.Children.Add(body);
            first = false;
        }
    }

    /// <summary>
    /// "▾ Today · Sunday 19 October", with the versions it holds on the right
    /// - the range, so a folded day still says what is in it. Clicks fold and
    /// unfold the day below.
    /// </summary>
    private UIElement DayHeading(string date, IReadOnlyList<Changelog.Release> releases, UIElement body)
    {
        var heading = new Grid
        {
            Background = Brushes.Transparent,   // the whole strip clicks, not just the letters
            Cursor = Cursors.Hand,
            Margin = new Thickness(0, 2, 0, 2),
            ToolTip = "Show or hide this day's changes",
        };
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        heading.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var arrow = new TextBlock
        {
            Text = Arrow(body),
            Foreground = _theme.Label,
            Width = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var title = new TextBlock
        {
            Text = DayTitle(date),
            Foreground = _theme.Section,
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        Grid.SetColumn(title, 1);

        string range = releases.Count == 1
            ? releases[0].Version
            : $"{releases[^1].Version} – {releases[0].Version}";
        var count = new TextBlock
        {
            Text = releases.Count == 1 ? range : $"{range}  ·  {releases.Count} versions",
            Foreground = _theme.Header,
            FontSize = SmallFontSize,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(count, 2);

        heading.Children.Add(arrow);
        heading.Children.Add(title);
        heading.Children.Add(count);

        heading.MouseLeftButtonUp += (_, _) =>
        {
            body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            arrow.Text = Arrow(body);
        };

        return heading;
    }

    /// <summary>Down while open, right while folded - the same arrows the widget's section headings use.</summary>
    private static string Arrow(UIElement body) => body.Visibility == Visibility.Visible ? "▾" : "▸";

    /// <summary>
    /// "Today · Sunday 19 October", "Yesterday · …", else the day alone - the
    /// year only once it isn't this one. The changelog's own yyyy-MM-dd is
    /// for sorting, not for reading.
    /// </summary>
    private static string DayTitle(string date)
    {
        if (!DateTime.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day))
            return date;

        string text = day.ToString(day.Year == DateTime.Today.Year ? "dddd d MMMM" : "dddd d MMMM yyyy",
                                   CultureInfo.InvariantCulture);

        return (DateTime.Today - day.Date).Days switch
        {
            0 => "Today  ·  " + text,
            1 => "Yesterday  ·  " + text,
            _ => text,
        };
    }

    /// <summary>
    /// One release: its number in a narrow column on the left and its changes
    /// beside it, rather than the number on a line of its own above them -
    /// a line saved per release, and the numbers line up into a column the eye
    /// can run down. The running version is lit and says so.
    /// </summary>
    private UIElement ReleaseBlock(Changelog.Release release, bool isCurrent)
    {
        var block = new Grid { Margin = new Thickness(14, 3, 0, 3) };
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(VersionColumn) });
        block.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var number = new StackPanel();
        number.Children.Add(new TextBlock
        {
            Text = release.Version,
            Foreground = isCurrent ? _theme.Section : _theme.Label,
            FontWeight = isCurrent ? FontWeights.SemiBold : FontWeights.Normal,
        });
        if (isCurrent)
            number.Children.Add(new TextBlock
            {
                Text = "running",
                Foreground = _theme.Normal,
                FontSize = SmallFontSize,
            });
        block.Children.Add(number);

        var changes = new StackPanel();
        foreach (string change in release.Changes)
            changes.Children.Add(ChangeLine(change));
        Grid.SetColumn(changes, 1);
        block.Children.Add(changes);

        return block;
    }

    /// <summary>Wide enough for "1.47.0" at the panel font, with a gap after it.</summary>
    private double VersionColumn => Math.Round(UiTheme.PanelFontSize * 5.2);

    /// <summary>
    /// One change. A leading "Sessions:" or "Fixed:" - the part of the program
    /// it is about - is picked out, so a day's list can be scanned by area.
    /// Only a short leading phrase counts, or the first half of an ordinary
    /// sentence with a colon in it would be lit up too.
    ///
    /// A dim bullet with the text hanging off it: without one, a change that
    /// wraps onto a second line can't be told from the start of the next.
    /// </summary>
    private UIElement ChangeLine(string change)
    {
        var item = new Grid { Margin = new Thickness(0, 0, 0, 3) };
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        item.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        item.Children.Add(new TextBlock
        {
            Text = "•",
            Foreground = _theme.Header,
            Margin = new Thickness(0, 0, 7, 0),
        });

        var line = new TextBlock { TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(line, 1);
        item.Children.Add(line);

        int colon = change.IndexOf(": ", StringComparison.Ordinal);
        if (colon is > 0 and <= 22 && char.IsUpper(change[0]))
        {
            line.Inlines.Add(new Run(change[..colon] + "  ") { Foreground = AreaBrush, FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run(Capitalised(change[(colon + 2)..])) { Foreground = _theme.NotesText });
        }
        else
        {
            line.Inlines.Add(new Run(change) { Foreground = _theme.NotesText });
        }

        return item;
    }

    /// <summary>"the chart can show…" after its area label reads as the start of a sentence.</summary>
    private static string Capitalised(string text)
        => text.Length > 0 && char.IsLower(text[0]) ? char.ToUpperInvariant(text[0]) + text[1..] : text;

    /// <summary>The accent the toolbars light up in - set apart from the text without shouting.</summary>
    private Brush AreaBrush => _theme.Accent;

    // --- Requirements -----------------------------------------------------

    /// <summary>
    /// What DeskMon needs, each with whether this machine has it - the
    /// question that comes up when setting it up somewhere new, and this is
    /// the window that is about the program rather than the hardware.
    ///
    /// One line, ".NET 8 ✓   PawnIO ✓   PresentMon ✓", with what each is for in
    /// its tooltip: it used to take four lines above the history for an answer
    /// that is nearly always "all there". What is missing gets a line of its
    /// own below, saying how to install it, since that one does need reading.
    ///
    /// Drawn at once with "…" and filled in after: the PresentMon search walks
    /// folders, which has no business on the UI thread.
    /// </summary>
    private async Task ShowRequirementsAsync()
    {
        Requirements.Children.Clear();

        var line = new TextBlock { FontSize = SmallFontSize, TextWrapping = TextWrapping.Wrap };
        line.Inlines.Add(new Run("Needs   ") { Foreground = _theme.Header });
        Requirements.Children.Add(line);

        // The runtime is always there: this window could not be on screen without it.
        var dotnet = Requirement(line, ".NET 8", ".NET 8 Desktop Runtime\nNeeded to run DeskMon at all.");
        SetStatus(dotnet, Status.Installed);

        var pawn = Requirement(line, "PawnIO", "PawnIO\nNeeded for the CPU temperature.");
        var presentMon = Requirement(line, "PresentMon", "PresentMon\nNeeded for FPS and latency.");

        var missing = await Task.Run(() => Prerequisites.Check(_settings));

        SetStatus(pawn, StatusOf(missing, "PawnIO"));

        // Prerequisites does not look for PresentMon when FPS is switched off,
        // so "not in the missing list" would read as installed when it is
        // really unknown - say why instead.
        SetStatus(presentMon, _settings.MeasureFps ? StatusOf(missing, "PresentMon") : Status.Off);

        foreach (var item in missing)
        {
            if (item.Name == "PresentMon" && !_settings.MeasureFps)
                continue;

            Requirements.Children.Add(new TextBlock
            {
                Text = $"{item.Name} missing - {item.Install}",
                Foreground = _theme.Warn,
                FontSize = SmallFontSize,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 3, 0, 0),
            });
        }
    }

    private enum Status { Checking, Installed, Missing, Off }

    private static Status StatusOf(IReadOnlyList<Prerequisites.Missing> missing, string name)
        => missing.Any(m => m.Name == name) ? Status.Missing : Status.Installed;

    private double SmallFontSize => Math.Max(8, UiTheme.PanelFontSize - 3);

    // --- Updates ------------------------------------------------------------

    /// <summary>
    /// "Update   1.56.0 is out ↗" in the normal colour when the daily check
    /// found one, "up to date" when it didn't, or how to switch it on. Said
    /// again whenever a check finishes - MainWindow calls this.
    /// </summary>
    public void RefreshUpdate()
    {
        UpdateLine.Inlines.Clear();
        UpdateLine.FontSize = SmallFontSize;
        UpdateLine.Inlines.Add(new Run("Update  ") { Foreground = _theme.Header });

        if (!_settings.CheckForUpdates || string.IsNullOrWhiteSpace(_settings.UpdateRepository))
        {
            UpdateLine.Inlines.Add(new Run("not checked - set the release page under Alerts and updates in Settings")
            {
                Foreground = _theme.Unavailable,
            });
            return;
        }

        if (AppUpdate.Newer is { } release)
        {
            var link = new Hyperlink(new Run($"{release.Version} is out ↗"))
            {
                Foreground = _theme.Normal,
                TextDecorations = null,
                Cursor = Cursors.Hand,
                ToolTip = $"{release.Name}\nOpens the release page - you have {AppUpdate.Running}.",
            };
            link.Click += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url) { UseShellExecute = true }); }
                catch { /* no browser */ }
            };
            UpdateLine.Inlines.Add(link);
            return;
        }

        if (AppUpdate.Problem is { } problem)
        {
            UpdateLine.Inlines.Add(new Run($"couldn't check: {problem}") { Foreground = _theme.Warn });
            return;
        }

        UpdateLine.Inlines.Add(new Run(AppUpdate.CheckedAt is { } at
            ? $"{AppUpdate.Running} is the newest ✓  (checked {at:HH:mm})"
            : $"{AppUpdate.Running} - checked once a day, first a minute after start")
        {
            Foreground = AppUpdate.CheckedAt is null ? _theme.Label : _theme.Normal,
        });
    }

    /// <summary>Its name and a mark after it, "…" until the check is done. The purpose is in the tooltip.</summary>
    private Run Requirement(TextBlock line, string name, string purpose)
    {
        line.Inlines.Add(new Run(name + " ") { Foreground = _theme.NotesText, ToolTip = purpose });
        var mark = new Run("…") { ToolTip = purpose };
        line.Inlines.Add(mark);
        line.Inlines.Add(new Run("     "));
        SetStatus(mark, Status.Checking);
        return mark;
    }

    private void SetStatus(Run mark, Status status)
    {
        (mark.Text, mark.Foreground) = status switch
        {
            Status.Installed => ("✓", _theme.Normal),
            Status.Missing => ("✗ missing", _theme.Warn),
            Status.Off => ("– FPS off", _theme.Unavailable),
            _ => ("…", _theme.Unavailable),
        };
    }

    // --- Chrome -----------------------------------------------------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleHeaderMouseDown(e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>Hidden rather than closed, so it can be shown again later.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SavePlacementNow();

        if (_allowClose)
            return;

        e.Cancel = true;
        HideSmoothly();   // banner put out on screen first - see PanelWindow
    }

    public void AllowClose() => _allowClose = true;

    public void ShowAndFocus()
    {
        // The settings may have changed since it was last open - the check
        // switched on, or a release page set.
        RefreshUpdate();

        if (!IsOpen)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Topmost = _settings.NotesAlwaysOnTop;
        Activate();
    }

    /// <summary>Restarts the delay, so the write lands once the window stops moving.</summary>
    private void QueuePlacementSave()
    {
        _placementSave.Stop();
        _placementSave.Start();
    }

    private void SavePlacementNow()
    {
        if (!_placementSave.IsEnabled)
            return;

        _placementSave.Stop();
        SavePlacement();
    }

    private void SavePlacement()
    {
        if (WindowState != WindowState.Normal || !IsVisible)
            return;

        _settings.VersionsLeft = Left;
        _settings.VersionsTop = Top;
        _settings.VersionsWidth = Width;
        _settings.VersionsHeight = Height;
        _settings.Save();
    }

    private void ApplyTheme()
    {
        Panel.Background = _theme.NotesBackground;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        HeaderRule.Background = _theme.Separator;

        TitleText.Foreground = _theme.Section;
        TitleText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);
        RequirementsRule.Background = _theme.Separator;

        Foreground = _theme.Label;
        FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);
        FontFamily = _theme.PanelFont;
    }
}
