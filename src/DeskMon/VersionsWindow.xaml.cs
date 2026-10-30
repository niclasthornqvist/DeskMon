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
        // Hidden, only noted, and redrawn on the next showing: the theme changes
        // on every step of a colour being dragged in the settings window, and
        // rebuilding the whole history each time made that drag lag - for a
        // window nobody could see.
        _theme.Changed += () =>
        {
            if (IsVisible)
                Retheme();
            else
                _themeStale = true;
        };
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _themeStale)
                Retheme();
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

    /// <summary>The theme changed while the window was hidden - see the constructor.</summary>
    private bool _themeStale;

    private void Retheme()
    {
        _themeStale = false;
        ApplyTheme();
        BuildHistory();   // its version names are in the accent
        ShowCards();      // the cards keep the colours they were made with
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

            // A thin line between days, so where one day's changes end and the
            // next date starts reads at a glance, folded or not.
            if (!first)
                History.Children.Add(new Border { Height = 1, Background = _theme.Separator, Margin = new Thickness(0, 2, 0, 2) });

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
            Margin = new Thickness(4, 4, 4, 4),
            ToolTip = "This day's changes\nClick to show or hide them.",
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

        // In a strip of its own so the hover can reach past the text on every
        // side - the heading's margin is the room it lights up in.
        var strip = new Grid { Background = Brushes.Transparent };
        strip.Children.Add(HoverStrip(strip));
        strip.Children.Add(heading);

        strip.MouseLeftButtonUp += (_, _) =>
        {
            body.Visibility = body.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
            arrow.Text = Arrow(body);
        };

        return strip;
    }

    /// <summary>
    /// The widget's row hover (MainWindow's RowHover), for a day heading: the
    /// same faint strip, faded in quickly and out a little slower, so it is
    /// plain which day a click folds. Behind the text; its colour by
    /// reference, so it follows the theme.
    /// </summary>
    private static Border HoverStrip(FrameworkElement owner)
    {
        var hover = new Border { CornerRadius = new CornerRadius(4), Opacity = 0, IsHitTestVisible = false };
        hover.SetResourceReference(Border.BackgroundProperty, "Theme.Ov.14");

        owner.MouseEnter += (_, _) => hover.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(1, TimeSpan.FromSeconds(0.12)));
        owner.MouseLeave += (_, _) => hover.BeginAnimation(UIElement.OpacityProperty,
            new System.Windows.Media.Animation.DoubleAnimation(0, TimeSpan.FromSeconds(0.2)));
        return hover;
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
        var block = new Grid { Margin = new Thickness(18, 3, 0, 3) };   // under the heading's text, which the hover strip moved in by 4
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

    /// <summary>What Prerequisites found missing; null while it is still looking.</summary>
    private IReadOnlyList<Prerequisites.Missing>? _missing;

    /// <summary>
    /// What DeskMon needs, each with whether this machine has it - the
    /// question that comes up when setting it up somewhere new, and this is
    /// the window that is about the program rather than the hardware. What is
    /// missing gets a line of its own under the cards, saying how to install
    /// it, since that one does need reading.
    ///
    /// The cards go up at once with "…" and are filled in after: the
    /// PresentMon search walks folders, which has no business on the UI thread.
    /// </summary>
    /// <summary>
    /// <see cref="ShowRequirementsCoreAsync"/> with nothing let out: the
    /// constructor starts it and moves on, so an error in it went nowhere
    /// until a garbage collection reported it as an "unobserved task".
    /// </summary>
    private async Task ShowRequirementsAsync()
    {
        try
        {
            await ShowRequirementsCoreAsync();
        }
        catch (Exception ex)
        {
            App.Log("checking what DeskMon needs", ex);
        }
    }

    private async Task ShowRequirementsCoreAsync()
    {
        _missing = null;
        ShowCards();

        var missing = await Task.Run(() => Prerequisites.Check(_settings));
        _missing = missing;
        ShowCards();

        Requirements.Children.Clear();
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
                Margin = new Thickness(0, 0, 0, 6),
            });
        }
    }

    /// <summary>
    /// The cards along the top - RUNNING, UPDATE, then one per thing DeskMon
    /// needs - drawn like the session history's (see StatCard). They were two
    /// lines of small text, "Needs .NET 8 ✓ PawnIO ✓ …" and "Update …", which
    /// read as fine print. Built again whole when either half changes: five
    /// cards, nothing worth keeping between.
    /// </summary>
    private void ShowCards()
    {
        Stats.Children.Clear();

        Stats.Children.Add(StatCard.Make(_theme, "RUNNING", AppUpdate.Running.ToString(),
                                         "The DeskMon version open now"));
        Stats.Children.Add(UpdateCard());

        // The runtime is always there: this window could not be on screen without it.
        Stats.Children.Add(RequirementCard(".NET 8", Status.Installed,
                                           ".NET 8 Desktop Runtime\nNeeded to run DeskMon at all."));
        Stats.Children.Add(RequirementCard("PAWNIO", StatusOf("PawnIO"),
                                           "PawnIO\nNeeded for the CPU temperature."));

        // Prerequisites does not look for PresentMon when FPS is switched off,
        // so "not in the missing list" would read as installed when it is
        // really unknown - say why instead.
        Stats.Children.Add(RequirementCard("PRESENTMON", _settings.MeasureFps ? StatusOf("PresentMon") : Status.Off,
                                           "PresentMon\nNeeded for FPS and latency."));
    }

    private enum Status { Checking, Installed, Missing, Off }

    private Status StatusOf(string name)
        => _missing is null ? Status.Checking
         : _missing.Any(m => m.Name == name) ? Status.Missing
         : Status.Installed;

    private Border RequirementCard(string name, Status status, string purpose)
    {
        var (text, brush) = status switch
        {
            Status.Installed => ("✓ installed", _theme.Normal),
            Status.Missing => ("✗ missing", _theme.Warn),
            Status.Off => ("FPS off", _theme.Unavailable),
            _ => ("…", _theme.Unavailable),
        };
        return StatCard.Make(_theme, name, text, purpose, valueBrush: brush);
    }

    private double SmallFontSize => Math.Max(8, UiTheme.PanelFontSize - 3);

    // --- Updates ------------------------------------------------------------

    /// <summary>
    /// The cards again with the update check's latest word. Called whenever a
    /// check finishes - MainWindow calls this - and on every open, since the
    /// check may have been switched on since.
    /// </summary>
    public void RefreshUpdate() => ShowCards();

    /// <summary>
    /// UPDATE: "1.56.0 is out ↗" in the normal colour when the daily check
    /// found one - the card a link to its release page - "up to date ✓" when
    /// it didn't, or that it is off, with how to switch it on in the tooltip.
    /// </summary>
    private Border UpdateCard()
    {
        if (!_settings.CheckForUpdates || string.IsNullOrWhiteSpace(_settings.UpdateRepository))
            return StatCard.Make(_theme, "UPDATE", "not checked",
                                 "Set the release page under Alerts and updates in Settings",
                                 valueBrush: _theme.Unavailable);

        if (AppUpdate.Newer is { } release)
        {
            var card = StatCard.Make(_theme, "UPDATE", $"{release.Version} is out ↗",
                                     $"{release.Name}\nOpens the release page - you have {AppUpdate.Running}.",
                                     valueBrush: _theme.Normal);
            card.Cursor = Cursors.Hand;
            card.MouseLeftButtonUp += (_, _) =>
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(release.Url) { UseShellExecute = true }); }
                catch { /* no browser */ }
            };
            return card;
        }

        if (AppUpdate.Problem is { } problem)
            return StatCard.Make(_theme, "UPDATE", "couldn't check", problem, valueBrush: _theme.Warn);

        return AppUpdate.CheckedAt is { } at
            ? StatCard.Make(_theme, "UPDATE", "up to date ✓", $"Checked at {at:HH:mm} - once a day", valueBrush: _theme.Normal)
            : StatCard.Make(_theme, "UPDATE", "not yet", "Checked once a day, first a minute after start", valueBrush: _theme.Label);
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
