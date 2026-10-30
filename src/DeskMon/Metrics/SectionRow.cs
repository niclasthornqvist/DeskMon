using System;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Media;

namespace DeskMon.Metrics;

/// <summary>
/// A section title such as "Usage" or "Temperature", optionally preceded by a
/// separator line.
///
/// Whether the separator shows depends on whether anything is drawn above this
/// section, and that can change at runtime because a game-only section appears
/// and disappears - hence the change notification.
/// </summary>
public sealed class SectionRow : INotifyPropertyChanged, IPanelRow
{
    private readonly UiTheme _theme;
    private readonly AppSettings _settings;
    private string? _suffix;
    private bool _showSeparator;
    private bool _isCollapsed;

    private readonly string? _description;

    public SectionRow(string title, UiTheme theme, AppSettings settings, string? icon = null,
                      string? description = null)
    {
        _theme = theme;
        _settings = settings;
        _description = description;
        Icon = ParseIcon(icon);

        // The name the section has in MetricRegistry, which is what settings
        // and the collapsed list are keyed by - not the display title, which
        // the user can rename.
        Key = title;
        _isCollapsed = settings.CollapsedSections.Contains(title, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// settings.json can rename any heading. What it says is used verbatim, so
    /// lower case or mixed case is possible; a section with no entry falls back
    /// to upper case, which is the house style.
    /// </summary>
    private string BaseTitle
        => _settings.SectionTitles.TryGetValue(Key, out string? custom)
           && !string.IsNullOrWhiteSpace(custom)
            ? custom
            : Key.ToUpperInvariant();

    /// <summary>
    /// "GAME" on its own, or "GAME The Witcher 3" once there is something to
    /// name. The suffix keeps its own capitalisation - a game's name is a
    /// proper noun, not a heading.
    ///
    /// Joined by a space only. This used to add ": " itself, which made the
    /// game heading the one heading with punctuation you couldn't control -
    /// now a colon, a dash or nothing is whatever the heading is set to in
    /// SectionTitles, e.g. "GAME:".
    /// </summary>
    public string Title => _suffix is null ? BaseTitle : $"{BaseTitle} {_suffix}";

    /// <summary>Sets or clears the part after the heading. Null restores the plain title.</summary>
    public void SetSuffix(string? suffix)
    {
        suffix = string.IsNullOrWhiteSpace(suffix) ? null : suffix.Trim();
        if (_suffix == suffix)
            return;

        _suffix = suffix;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
    }

    /// <summary>The heading's icon, or null for a heading without one.</summary>
    public Geometry? Icon { get; }

    public Visibility IconVisibility => Icon is null ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>
    /// A little taller than the heading's capitals, so the icon carries about
    /// the same visual weight as the word beside it. Follows FontSize, so a
    /// bigger panel gets bigger icons instead of the same 11 px ones looking
    /// ever smaller next to the text.
    /// </summary>
    public double IconSize => Math.Max(11, _settings.FontSize + 1);

    /// <summary>
    /// Parsed once and frozen, rather than handed to the template as a string
    /// to convert on every layout pass. A typo in the registry costs the icon,
    /// not the widget - the same rule as a bad lambda there.
    /// </summary>
    private static Geometry? ParseIcon(string? data)
    {
        if (string.IsNullOrWhiteSpace(data))
            return null;

        try
        {
            var geometry = Geometry.Parse(data);
            geometry.Freeze();
            return geometry;
        }
        catch
        {
            return null;
        }
    }

    public Brush Brush => _theme.Section;
    public Brush SeparatorBrush => _theme.Separator;

    /// <summary>
    /// The short bar before the heading, in the bar gradient's colours: ties
    /// the headings to the bars under them and marks where each section
    /// starts. As tall as the icon, so the two read as one mark.
    /// </summary>
    public Brush AccentBrush => _theme.SectionAccent;
    public double AccentHeight => IconSize;
    public Visibility AccentVisibility => _settings.SectionAccent ? Visibility.Visible : Visibility.Collapsed;
    public double FontSize => Math.Max(8, _settings.FontSize - 1.5);

    /// <summary>
    /// The fold arrow, drawn larger than the heading beside it - same reasoning
    /// as the metric rows, and the same size, so the two line up.
    /// </summary>
    public double IndicatorFontSize => MetricRow.ArrowSize(_settings);

    /// <summary>The section's name in the code, stable across renames.</summary>
    public string Key { get; }

    /// <summary>Whether this section's rows are hidden.</summary>
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed == value)
                return;

            _isCollapsed = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCollapsed)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Indicator)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Tooltip)));
        }
    }

    /// <summary>
    /// What the section is about, then what a click does - which depends on
    /// whether it is folded, so it says the one that will happen - and that it
    /// can be dragged. A card like the rows' (see Ui.InfoTip), with the two
    /// actions on lines of their own at its foot.
    /// </summary>
    public object Tooltip
        => Ui.InfoTip.Make(_theme, "SECTION", BaseTitle, _description, hints: new[]
        {
            _isCollapsed ? "Click to show its rows again." : "Click to fold its rows away.",
            "Drag it up or down to move the section.",
        });

    private bool _isDragging;

    /// <summary>
    /// The heading is being dragged to a new place. Drawn lifted - a stronger
    /// version of the hover highlight - so it is clear which section is moving.
    /// </summary>
    public bool IsDragging
    {
        get => _isDragging;
        set
        {
            if (_isDragging == value)
                return;

            _isDragging = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDragging)));
        }
    }

    /// <summary>
    /// Down means the rows are below, open; up means they folded away. Shown
    /// at the end of the heading so there is a visible hint that the title
    /// does something when clicked.
    /// </summary>
    public string Indicator => _isCollapsed ? "▴" : "▾";

    public Visibility SeparatorVisibility => _showSeparator ? Visibility.Visible : Visibility.Collapsed;

    public Thickness SeparatorMargin => new(0, 6, 0, 0);

    /// <summary>
    /// A later section gets breathing room around its separator line; the first
    /// one sits right under the hardware names.
    /// </summary>
    public Thickness TitleMargin => new(0, _showSeparator ? 5 : 1, 0, 2);

    /// <summary>Called by the window whenever the set of visible sections changes.</summary>
    public void SetSeparator(bool show)
    {
        if (_showSeparator == show)
            return;

        _showSeparator = show;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SeparatorVisibility)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(TitleMargin)));
    }

    /// <summary>Re-reads everything, for a settings change.</summary>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    public event PropertyChangedEventHandler? PropertyChanged;
}
