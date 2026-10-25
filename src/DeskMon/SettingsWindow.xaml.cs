using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DeskMon.Metrics;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// settings.json, as a window.
///
/// Every row is generated from <see cref="SettingsCatalogue"/> and reaches its
/// value through reflection on <see cref="AppSettings"/>, so this file knows
/// nothing about individual settings and cannot drift out of step with them.
///
/// Changes apply as you make them and are written a moment later. That is the
/// whole point: the reason colours and column widths used to be awkward was not
/// that editing JSON is hard, it is that you had to restart to find out whether
/// you liked the result. A handful of settings genuinely cannot be re-read
/// while running - they are captured when a window or the sensor library is
/// built - and those are tagged "Needs a reload" and come with the restart button.
///
/// The file is still the other half of this: anything you have hand-edited and
/// not touched here is left exactly as it was, because Save only writes the
/// keys this window reports having changed. See AppSettings.MarkEdited.
/// </summary>
public partial class SettingsWindow : PanelWindow
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;

    /// <summary>
    /// Width of the control column on the right. Fixed, so every box, tick
    /// and dropdown starts on the same line down the whole form - the ragged
    /// edge they had when they followed their labels was half of why the
    /// window looked cluttered. Fits "#FF4ADE80" with its swatch.
    /// </summary>
    private const double EditorColumn = 150;

    private static readonly Thickness RowMargin = new(0, 6, 0, 6);

    /// <summary>The descriptions and notes: small enough to read as secondary.</summary>
    private static double SmallFont => Math.Max(9, UiTheme.PanelFontSize - 2.5);

    /// <summary>
    /// Collapses a burst of edits into one write. A setting is applied on every
    /// keystroke, and rewriting settings.json per character would be silly -
    /// and it is written atomically, so each one is a rename on disk.
    /// </summary>
    private readonly DispatcherTimer _save = new() { Interval = TimeSpan.FromMilliseconds(700) };

    /// <summary>Same idea for a drag or a resize, as in the other windows.</summary>
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private bool _allowClose;

    /// <summary>One per tick box: re-reads its value from the settings. See <see cref="RefreshValues"/>.</summary>
    private readonly List<Action> _refreshers = new();

    /// <summary>
    /// A setting changed. The widget re-reads everything when this fires; it is
    /// raised before the file is written, because seeing the effect is what the
    /// window is for and the disk can lag behind.
    /// </summary>
    public event Action? Changed;

    /// <summary>The Reload button. Only the app itself can restart the app.</summary>
    public event Action? ReloadRequested;

    public SettingsWindow(AppSettings settings, UiTheme theme)
    {
        _settings = settings;
        _theme = theme;

        InitializeComponent();
        new Banner(Panel, height: 210).Show(Notes.GameArt.Motif.Gear);
        ApplyTheme();

        // Only the window's own chrome on every change - this runs per keystroke
        // in a colour box, and rebuilding the form would take the box away from
        // under the typing. A theme switch rebuilds it; see SwitchTheme.
        _theme.Changed += ApplyTheme;
        RestorePlacement(_settings.SettingsLeft, _settings.SettingsTop,
                         _settings.SettingsWidth, _settings.SettingsHeight);

        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.SettingsOpacity);

        SetUpColourPicker();
        Build();
        Note("Changes apply and save at once. Some settings may need a reload.");

        _save.Tick += (_, _) => SaveNow();
        _placementSave.Tick += (_, _) => SavePlacementNow();

        Closing += OnClosing;
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
                RefreshValues();   // anything changed elsewhere while it was hidden
        };
        LocationChanged += (_, _) => QueuePlacementSave();
        SizeChanged += (_, _) => QueuePlacementSave();
    }

    // --- Building the form ------------------------------------------------

    private void Build()
    {
        foreach (var group in SettingsCatalogue.Groups)
        {
            Rows.Children.Add(Heading(group, first: Rows.Children.Count == 0));

            foreach (var setting in group.Settings)
            {
                // Kept by Windows, so there is no property to look up.
                if (setting.Kind == SettingsCatalogue.EditorKind.Startup)
                {
                    Rows.Children.Add(StartupRow(setting));
                    continue;
                }

                // A key renamed in AppSettings but not here should cost that one
                // row, not the window.
                var property = typeof(AppSettings).GetProperty(setting.Key);
                if (property is not null)
                    Rows.Children.Add(Row(setting, property));
            }
        }
    }

    /// <summary>
    /// A group's title, with a rule above it to separate it from the group
    /// before - spacing alone left the groups running into each other - and
    /// the group's note under it, if it has one.
    /// </summary>
    private UIElement Heading(SettingsCatalogue.Group group, bool first)
    {
        var stack = new StackPanel { Margin = new Thickness(0, first ? 2 : 14, 0, 2) };

        if (!first)
            stack.Children.Add(new Border
            {
                Height = 1,
                Background = _theme.Separator,
                Margin = new Thickness(0, 0, 0, 14),
            });

        stack.Children.Add(new TextBlock
        {
            Text = group.Title.ToUpperInvariant(),
            Foreground = _theme.Section,
            FontWeight = FontWeights.SemiBold,
        });

        if (group.Note is { } note)
            stack.Children.Add(HelpText(note));

        return stack;
    }

    /// <summary>
    /// One setting: its name and description on the left, its control in the
    /// fixed column on the right - or, for a path or a list, under the text at
    /// full width, since a 150 px box would show a few characters of either.
    /// </summary>
    private UIElement Row(SettingsCatalogue.Setting setting, PropertyInfo property)
    {
        if (setting.Key == nameof(AppSettings.SectionTitles))
            return Headings();

        if (setting.Key == nameof(AppSettings.HiddenSections))
            return SectionsShown();

        if (setting.Key == nameof(AppSettings.Theme))
            return ThemeRow(setting, property);

        var text = Describe(setting);

        if (property.PropertyType == typeof(bool))
            return Switch(setting, property, text);

        FrameworkElement editor = setting.Kind switch
        {
            SettingsCatalogue.EditorKind.Font => FontChoice(setting, property),
            SettingsCatalogue.EditorKind.Folder => PathBox(setting, property, folder: true),
            SettingsCatalogue.EditorKind.Program => PathBox(setting, property, folder: false),
            SettingsCatalogue.EditorKind.ProgramList => ProgramTags(setting, property),

            _ => setting.Choices is { } choices
                ? Choice(setting, property, choices)
                : property.PropertyType == typeof(List<string>)
                    ? Text(setting, property, list: true)
                    : setting.Key.EndsWith("Color", StringComparison.Ordinal)
                        ? Colour(setting, property)
                        : Text(setting, property, list: false),
        };

        if (setting.Wide)
        {
            editor.Margin = new Thickness(0, 6, 0, 0);
            return new StackPanel { Margin = RowMargin, Children = { text, editor } };
        }

        // Beside the box, not instead of it: the box is where an exact value
        // goes, and where one outside the slider's range still can.
        Slider? slider = setting.Slider is { } range && editor is TextBox box
            ? NumberSlider(range, property, box)
            : null;

        return SideBySide(text, WithUnit(editor, setting.Unit), slider);
    }

    /// <summary>The name, a "Needs a reload" tag if it has one, and the description.</summary>
    private FrameworkElement Describe(SettingsCatalogue.Setting setting)
    {
        var label = new TextBlock
        {
            Text = setting.Label,
            Foreground = _theme.NotesText,
            TextWrapping = TextWrapping.Wrap,

            // The key, so the window still tells you what to look for in the
            // file - the reason the label used to *be* the key. The startup
            // switch is not in the file, so it says where it is instead.
            ToolTip = setting.Kind == SettingsCatalogue.EditorKind.Startup
                ? "Stored as a logon task named DeskMon in Windows' Task Scheduler"
                : $"settings.json: {setting.Key}",
        };

        // Said in words, beside the name: the * it replaced meant nothing
        // until you found the footnote explaining it.
        if (setting.NeedsReload)
            label.Inlines.Add(new System.Windows.Documents.Run("   Needs a reload")
            {
                Foreground = _theme.Warn,
                FontSize = SmallFont,
            });

        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        stack.Children.Add(label);

        if (!string.IsNullOrEmpty(setting.Help))
            stack.Children.Add(HelpText(setting.Help));

        return stack;
    }

    private TextBlock HelpText(string text)
        => new()
        {
            Text = text,
            Foreground = _theme.Label,
            FontSize = SmallFont,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 2, 0, 0),
        };

    /// <summary>
    /// Text on the left, control in the fixed right-hand column - and a slider,
    /// if the setting has one, just left of that column. The slider gets a
    /// column of its own rather than a share of the 150 px one, so the boxes
    /// still line up with every other row's.
    /// </summary>
    private Grid SideBySide(FrameworkElement text, FrameworkElement control, FrameworkElement? beside = null)
    {
        var grid = new Grid { Margin = RowMargin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorColumn) });

        text.Margin = new Thickness(0, 0, 12, 0);
        control.VerticalAlignment = VerticalAlignment.Center;

        Grid.SetColumn(control, 2);
        grid.Children.Add(text);
        grid.Children.Add(control);

        if (beside is not null)
        {
            beside.VerticalAlignment = VerticalAlignment.Center;
            beside.Margin = new Thickness(0, 0, 10, 0);
            Grid.SetColumn(beside, 1);
            grid.Children.Add(beside);
        }

        return grid;
    }

    /// <summary>Wide enough to place a value by eye, narrow enough to leave the text room.</summary>
    private const double SliderWidth = 120;

    /// <summary>
    /// A slider kept in step with a number box, both ways.
    ///
    /// The slider never writes the setting itself: it puts the number in the
    /// box, and the box's own handler applies it. That keeps one path to the
    /// setting - the same parsing, the same red border, the same "applied"
    /// note - whichever control was used. Typing moves the slider back the
    /// other way; a value outside the range parks it at the end, and the box
    /// keeps exactly what was typed.
    /// </summary>
    private Slider NumberSlider(SettingsCatalogue.SliderRange range, PropertyInfo property, TextBox box)
    {
        var slider = new Slider
        {
            Style = (Style)FindResource("DarkSlider"),
            Width = SliderWidth,
            Minimum = range.Min,
            Maximum = range.Max,
            TickFrequency = range.Step,
            IsSnapToTickEnabled = true,
            SmallChange = range.Step,
            LargeChange = range.Step * 10,
            Value = Convert.ToDouble(property.GetValue(_settings), CultureInfo.InvariantCulture),
            ToolTip = $"From {SliderText(range.Min, property, range)} to {SliderText(range.Max, property, range)}",
        };

        // Each side's change sets the other, which would fire straight back
        // without this.
        bool syncing = false;

        slider.ValueChanged += (_, e) =>
        {
            if (syncing)
                return;

            syncing = true;
            box.Text = SliderText(e.NewValue, property, range);
            syncing = false;
        };

        box.TextChanged += (_, _) =>
        {
            if (syncing || !TryRead(box.Text, property, list: false, out object? value) || value is null)
                return;

            syncing = true;
            slider.Value = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            syncing = false;
        };

        return slider;
    }

    /// <summary>
    /// A slider position as the box should show it: rounded to the step, so
    /// a drag writes 0.85 rather than 0.8500000000000001, and whole for an int
    /// setting. Invariant, like everything else in these boxes.
    /// </summary>
    private static string SliderText(double value, PropertyInfo property, SettingsCatalogue.SliderRange range)
    {
        double snapped = range.Min + Math.Round((value - range.Min) / range.Step) * range.Step;

        return property.PropertyType == typeof(int)
            ? ((int)Math.Round(snapped)).ToString(CultureInfo.InvariantCulture)
            : Math.Round(snapped, 4).ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>The control with its unit after it - "ms", "px" - when it has one.</summary>
    private FrameworkElement WithUnit(FrameworkElement editor, string? unit)
    {
        if (unit is null)
            return editor;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var label = new TextBlock
        {
            Text = unit,
            Foreground = _theme.Label,
            FontSize = SmallFont,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };

        Grid.SetColumn(label, 1);
        grid.Children.Add(editor);
        grid.Children.Add(label);
        return grid;
    }

    /// <summary>
    /// One box per section, rather than one box holding a map.
    ///
    /// The sections come from MetricRegistry, not from what happens to be in
    /// settings.json: a heading you have never renamed has no entry there, and
    /// it is exactly the one you are looking for when you come here. The label
    /// is the section's name in the code, which is also the key - the same
    /// convention as every other row in this window.
    /// </summary>
    private UIElement Headings()
    {
        var stack = new StackPanel();

        foreach (var section in MetricRegistry.Sections)
        {
            if (section.Metrics.Length == 0)
                continue;   // not drawn, so not worth naming

            string key = section.Title;

            var label = new TextBlock
            {
                Text = key,
                Foreground = _theme.NotesText,
                ToolTip = $"settings.json: SectionTitles → {key}",
            };

            _settings.SectionTitles.TryGetValue(key, out string? current);
            var box = NewBox(current ?? string.Empty, $"Leave empty to show {key.ToUpperInvariant()}");

            box.TextChanged += (_, _) =>
            {
                // Blank means "no entry", not "a heading with no text": an
                // empty string here would otherwise be a perfectly valid
                // instruction to draw nothing.
                if (string.IsNullOrWhiteSpace(box.Text))
                    _settings.SectionTitles.Remove(key);
                else
                    _settings.SectionTitles[key] = box.Text;

                _settings.MarkEdited(nameof(AppSettings.SectionTitles));
                Changed?.Invoke();
                Note($"{key} heading applied.");
                QueueSave();
            };

            stack.Children.Add(SideBySide(label, box));
        }

        return stack;
    }

    /// <summary>
    /// Which sections each layout shows: a row per section, from
    /// MetricRegistry like <see cref="Headings"/>, with a tick box under
    /// DESKTOP and one under GAME. Ticked is shown - the lists hold the
    /// unticked, so a section added later is shown until you say otherwise.
    /// </summary>
    private UIElement SectionsShown()
    {
        var grid = new Grid { Margin = RowMargin };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorColumn / 2) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(EditorColumn / 2) });

        void Place(UIElement element, int row, int column)
        {
            Grid.SetRow(element, row);
            Grid.SetColumn(element, column);
            grid.Children.Add(element);
        }

        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Place(ColumnHeading("DESKTOP", "Shown while no game is running - settings.json: HiddenSections"), 0, 1);
        Place(ColumnHeading("GAME", "Shown while a game runs - settings.json: HiddenSectionsInGame"), 0, 2);

        int row = 1;
        foreach (var section in MetricRegistry.Sections)
        {
            if (section.Metrics.Length == 0)
                continue;

            string key = section.Title;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var label = new TextBlock
            {
                Text = key,
                Foreground = _theme.NotesText,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 4, 12, 4),
            };
            Place(label, row, 0);
            Place(ShownBox(key, nameof(AppSettings.HiddenSections), () => _settings.HiddenSections, "desktop"), row, 1);
            Place(ShownBox(key, nameof(AppSettings.HiddenSectionsInGame), () => _settings.HiddenSectionsInGame, "game"), row, 2);
            row++;
        }

        return grid;
    }

    private TextBlock ColumnHeading(string text, string tooltip)
        => new()
        {
            Text = text,
            Foreground = _theme.Label,
            FontSize = SmallFont,
            FontWeight = FontWeights.SemiBold,
            ToolTip = tooltip,
            Margin = new Thickness(0, 0, 0, 2),
        };

    /// <summary>One tick: whether <paramref name="section"/> is shown in one layout.</summary>
    private CheckBox ShownBox(string section, string key, Func<List<string>> hidden, string layout)
    {
        bool IsShown() => !hidden().Contains(section, StringComparer.OrdinalIgnoreCase);

        var box = new CheckBox
        {
            Style = (Style)FindResource("DarkCheckBox"),
            IsChecked = IsShown(),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = $"Show {section.ToUpperInvariant()} on the {layout} layout",
        };

        void Commit(bool shown)
        {
            if (shown == IsShown())
                return;   // a refresh setting it back, not a click

            var list = hidden();
            list.RemoveAll(s => string.Equals(s, section, StringComparison.OrdinalIgnoreCase));
            if (!shown)
                list.Add(section);

            _settings.MarkEdited(key);
            Changed?.Invoke();
            Note($"{section} {(shown ? "shown" : "hidden")} on the {layout} layout.");
            QueueSave();
        }

        box.Checked += (_, _) => Commit(true);
        box.Unchecked += (_, _) => Commit(false);
        _refreshers.Add(() => box.IsChecked = IsShown());
        return box;
    }

    /// <summary>
    /// A tick box in the control column, like every other control, so the
    /// ticks line up with the boxes instead of hanging off the left edge.
    /// The text toggles it too: with the box at the far side, the text is
    /// what you would naturally click.
    /// </summary>
    private UIElement Switch(SettingsCatalogue.Setting setting, PropertyInfo property, FrameworkElement text)
    {
        var box = new CheckBox
        {
            Style = (Style)FindResource("DarkCheckBox"),
            IsChecked = property.GetValue(_settings) as bool? ?? false,
            HorizontalAlignment = HorizontalAlignment.Left,
        };

        box.Checked += (_, _) => Commit(setting, property, true);
        box.Unchecked += (_, _) => Commit(setting, property, false);

        // Setting it back to what the settings already say goes through
        // Commit, which sees the same value and does nothing - so this never
        // writes, it only redraws.
        _refreshers.Add(() => box.IsChecked = property.GetValue(_settings) as bool? ?? false);

        text.Cursor = Cursors.Hand;
        text.MouseLeftButtonUp += (_, _) => box.IsChecked = box.IsChecked != true;

        return SideBySide(text, box);
    }

    /// <summary>
    /// A number, a path or a comma-separated list. Applied on every keystroke
    /// if it parses; if it doesn't, the box goes red and nothing is written, so
    /// a half-typed number never reaches the panel.
    /// </summary>
    private FrameworkElement Text(SettingsCatalogue.Setting setting, PropertyInfo property, bool list)
    {
        var box = NewBox(Show(property, list), null);

        box.TextChanged += (_, _) =>
        {
            // Below the setting's floor counts as not a value yet, like "1." -
            // a text size of 0 applied on the way to typing something else
            // made WPF throw, and saved, kept DeskMon from starting.
            bool ok = TryRead(box.Text, property, list, out object? value)
                      && !(setting.Min is double min && value is IConvertible number
                           && Convert.ToDouble(number, CultureInfo.InvariantCulture) < min);
            MarkValid(box, ok);

            if (ok)
                Commit(setting, property, value);
        };

        return box;
    }

    /// <summary>
    /// A colour: a swatch that opens the picker, and the value as text beside
    /// it. The swatch is the way in; the text stays because it is the one
    /// place the exact value - opacity included - can be read or pasted, and
    /// it is what the picker writes into, so both routes share one path to
    /// the setting.
    /// </summary>
    private FrameworkElement Colour(SettingsCatalogue.Setting setting, PropertyInfo property)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var swatch = new Border
        {
            Width = 22,
            Height = 16,
            CornerRadius = new CornerRadius(3),
            BorderBrush = _theme.Border,
            BorderThickness = new Thickness(1),
        };

        var pick = new Button
        {
            Style = (Style)FindResource("PanelButton"),
            Width = 30,
            Height = 24,
            Margin = new Thickness(0, 0, 6, 0),
            Content = swatch,
            ToolTip = "Pick a colour",
        };

        var box = NewBox(property.GetValue(_settings) as string ?? string.Empty, null);
        ShowColour(swatch, box.Text);

        pick.Click += (_, _) => OpenColourPicker(pick, box, property);

        box.TextChanged += (_, _) =>
        {
            // Empty is allowed and is not a mistake: BarColor uses it to mean
            // "follow the value", and for the rest it falls back to the
            // built-in colour - which is how you undo an experiment.
            bool ok = string.IsNullOrWhiteSpace(box.Text) || TryColour(box.Text, out _);
            MarkValid(box, ok);

            if (!ok)
                return;

            ShowColour(swatch, box.Text);
            Commit(setting, property, box.Text);
        };

        Grid.SetColumn(box, 1);
        grid.Children.Add(pick);
        grid.Children.Add(box);
        return grid;
    }

    // --- Start with Windows -------------------------------------------------

    /// <summary>The startup switch is being set to what Windows says, not clicked.</summary>
    private bool _syncingStartup;

    /// <summary>
    /// A tick box like the others, but backed by the logon task instead of a
    /// property: it shows whether the task exists, and ticking it creates or
    /// removes the task.
    ///
    /// Greyed out while Windows is asked - schtasks takes a moment, and off
    /// the UI thread - and set back to what Windows then reports, so a change
    /// that was refused shows as not having happened rather than as done.
    /// </summary>
    private UIElement StartupRow(SettingsCatalogue.Setting setting)
    {
        var text = Describe(setting);
        var box = new CheckBox
        {
            Style = (Style)FindResource("DarkCheckBox"),
            HorizontalAlignment = HorizontalAlignment.Left,
            IsEnabled = false,
        };

        async void ShowActualState()
        {
            bool enabled = await Task.Run(Autostart.IsEnabled);

            _syncingStartup = true;
            box.IsChecked = enabled;
            _syncingStartup = false;
            box.IsEnabled = true;
        }

        async void Change(bool enable)
        {
            if (_syncingStartup)
                return;

            box.IsEnabled = false;
            Note(enable ? "Adding DeskMon to startup…" : "Removing DeskMon from startup…");

            string? error = await Task.Run(() => enable ? Autostart.Enable() : Autostart.Disable());

            Note(error ?? (enable
                ? "DeskMon will start when you log in."
                : "DeskMon will no longer start by itself."));

            ShowActualState();
        }

        box.Checked += (_, _) => Change(true);
        box.Unchecked += (_, _) => Change(false);

        text.Cursor = Cursors.Hand;
        text.MouseLeftButtonUp += (_, _) =>
        {
            if (box.IsEnabled)
                box.IsChecked = box.IsChecked != true;
        };

        ShowActualState();
        return SideBySide(text, box);
    }

    // --- The colour picker --------------------------------------------------

    /// <summary>A fresh set of settings, for what each colour is when left empty.</summary>
    private static readonly AppSettings Defaults = new();

    /// <summary>The colour row the open picker writes into.</summary>
    private TextBox? _colourTarget;

    /// <summary>Done once: the picker is shared, so only its target changes.</summary>
    private void SetUpColourPicker()
    {
        Picker.SetNormal(null, string.Empty);   // no "normal text" here; Default does that job
        Picker.SetApplyTooltip("Use this colour");

        Picker.Picked += colour =>
        {
            if (_colourTarget is null)
                return;

            // Written into the row's box rather than straight to the setting,
            // so a picked colour is checked, shown and saved exactly like a
            // typed one.
            byte alpha = (byte)Math.Round(AlphaSlider.Value / 100 * 255);
            _colourTarget.Text = $"#{alpha:X2}{colour.R:X2}{colour.G:X2}{colour.B:X2}";
            ColourPopup.IsOpen = false;
        };
    }

    /// <summary>
    /// Opens the picker under <paramref name="anchor"/>, starting from the
    /// row's colour - or, for an empty row, the built-in colour it stands for,
    /// so the picker opens on what the widget is actually showing.
    /// </summary>
    private void OpenColourPicker(FrameworkElement anchor, TextBox box, PropertyInfo property)
    {
        _colourTarget = box;

        Color start = TryColour(box.Text, out var current) ? current
                    : TryColour(property.GetValue(Defaults) as string ?? string.Empty, out var builtIn) ? builtIn
                    : Colors.White;

        Picker.SetColor(Color.FromRgb(start.R, start.G, start.B));
        AlphaSlider.Value = Math.Round(start.A / 255.0 * 100);

        ColourPopup.PlacementTarget = anchor;
        ColourPopup.IsOpen = true;
    }

    private void AlphaSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // Raised once while the XAML is still being loaded, before the label exists.
        if (AlphaText is not null)
            AlphaText.Text = $"{e.NewValue:0} %";
    }

    /// <summary>
    /// Empties the row, which is what "default" means for every colour here:
    /// UiTheme falls back to the built-in value, and BarColor goes back to
    /// following the reading.
    /// </summary>
    private void DefaultColour_Click(object sender, RoutedEventArgs e)
    {
        if (_colourTarget is not null)
            _colourTarget.Text = string.Empty;

        ColourPopup.IsOpen = false;
    }

    // --- Fonts ----------------------------------------------------------------

    /// <summary>
    /// The installed fonts, each drawn in itself. A font name typed by hand
    /// could be misspelt or not installed, and the widget would quietly fall
    /// back to Consolas with no sign why.
    /// </summary>
    private FrameworkElement FontChoice(SettingsCatalogue.Setting setting, PropertyInfo property)
    {
        var combo = new ComboBox
        {
            Style = (Style)FindResource("DarkCombo"),
            ItemTemplate = (DataTemplate)FindResource("FontPreviewItem"),
            Height = 24,
            Foreground = _theme.NotesText,
            ItemsSource = InstalledFonts.Names,

            // Null - an empty box - for a font that isn't installed here,
            // rather than showing a name the widget can't actually use.
            SelectedItem = InstalledFonts.Find(property.GetValue(_settings) as string),
        };

        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string chosen)
                Commit(setting, property, chosen);
        };

        return combo;
    }

    // --- Paths ----------------------------------------------------------------

    /// <summary>
    /// A path with Browse, which opens Windows' own folder or file dialog,
    /// and ✕, which empties it - empty being "the default" for all of these.
    /// The box stays editable, so a path can still be pasted.
    /// </summary>
    private FrameworkElement PathBox(SettingsCatalogue.Setting setting, PropertyInfo property, bool folder)
    {
        var box = (TextBox)Text(setting, property, list: false);

        // Marked as opening a window, like every such button outside the
        // widget: it brings up Windows' own picker.
        var browseLabel = new StackPanel { Orientation = Orientation.Horizontal };
        browseLabel.Children.Add(new TextBlock { Text = "Browse", VerticalAlignment = VerticalAlignment.Center });
        browseLabel.Children.Add(new System.Windows.Shapes.Path { Style = (Style)FindResource("OpensWindowMark") });

        var browse = new Button
        {
            Style = (Style)FindResource("PanelButton"),
            Width = double.NaN,
            Padding = new Thickness(10, 0, 10, 0),
            Margin = new Thickness(6, 0, 0, 0),
            Content = browseLabel,
            ToolTip = folder ? "Choose a folder" : "Choose the program",
        };

        var clear = new Button
        {
            Style = (Style)FindResource("PanelButton"),
            Content = "✕",
            ToolTip = folder ? "Use the default folder" : "Find it automatically",
        };

        browse.Click += (_, _) =>
        {
            string? chosen = folder
                ? PickFolder(setting.Label, box.Text)
                : PickProgram(setting.Label, box.Text);

            // Through the box, like a typed path, so it is saved the same way.
            if (chosen is not null)
                box.Text = chosen;
        };

        clear.Click += (_, _) => box.Text = string.Empty;

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        Grid.SetColumn(browse, 1);
        Grid.SetColumn(clear, 2);
        grid.Children.Add(box);
        grid.Children.Add(browse);
        grid.Children.Add(clear);
        return grid;
    }

    /// <summary>
    /// Opens in the current folder if it exists, else in the user's DeskMon
    /// folder, where the default notes and sessions folders live now - see DataFolder.
    /// </summary>
    private string? PickFolder(string label, string current)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Choose the {label.ToLowerInvariant()}",
            InitialDirectory = Directory.Exists(current) ? current : DataFolder.Root,
        };

        return dialog.ShowDialog(this) == true ? dialog.FolderName : null;
    }

    private string? PickProgram(string label, string current)
    {
        string? folder = null;
        try { folder = Path.GetDirectoryName(current); } catch { /* not a path yet */ }

        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"Choose the {label.ToLowerInvariant()}",
            Filter = "PresentMon|presentmon*.exe|Programs (*.exe)|*.exe",
            InitialDirectory = Directory.Exists(folder) ? folder : string.Empty,
        };

        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    // --- Program list -------------------------------------------------------
    //
    // "Never treat as a game" and "Always treat as a game" as tags rather than
    // one long comma-separated line: a line that grows with every browser and
    // player added becomes hard to read and harder to edit in the middle of. A
    // tag is removed with its ✕, and new ones come from a list of what is
    // running, so the name is spelt the way Windows spells it.

    /// <summary>One list of program names and the tags that show it.</summary>
    private sealed class ProgramTagList
    {
        public required SettingsCatalogue.Setting Setting { get; init; }
        public required PropertyInfo Property { get; init; }
        public required List<string> Programs { get; init; }
        public WrapPanel Tags { get; } = new();
    }

    /// <summary>
    /// The list the Add popup is adding to. There is one popup for every list,
    /// and this is set by whichever list's Add button opened it.
    /// </summary>
    private ProgramTagList? _addingTo;

    /// <summary>Programs with a window, gathered when the Add list opens.</summary>
    private IReadOnlyList<string> _running = Array.Empty<string>();

    private FrameworkElement ProgramTags(SettingsCatalogue.Setting setting, PropertyInfo property)
    {
        var list = new ProgramTagList
        {
            Setting = setting,
            Property = property,
            Programs = new List<string>(property.GetValue(_settings) as IEnumerable<string> ?? Array.Empty<string>()),
        };

        var add = new Button
        {
            Style = (Style)FindResource("PanelButton"),
            Width = double.NaN,
            Padding = new Thickness(10, 0, 10, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 4, 0, 0),
            Content = "+ Add program…",
        };

        add.Click += (_, _) =>
        {
            _addingTo = list;
            ProgramPopup.PlacementTarget = add;
            ProgramPopup.IsOpen = true;
        };

        ShowProgramTags(list);

        return new StackPanel
        {
            Children =
            {
                // Capped at about three rows of tags, then it scrolls - the
                // list can grow without pushing the rest of the form away.
                new ScrollViewer
                {
                    MaxHeight = 92,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Focusable = false,
                    Content = list.Tags,
                },
                add,
            },
        };
    }

    private void ShowProgramTags(ProgramTagList list)
    {
        list.Tags.Children.Clear();

        foreach (string name in list.Programs.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            var remove = new Button
            {
                Style = (Style)FindResource("PanelButton"),
                Width = 18,
                Height = 18,
                FontSize = 10,
                Content = "✕",
                ToolTip = $"Remove {name}",
            };

            string captured = name;
            remove.Click += (_, _) => RemoveProgram(list, captured);

            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new TextBlock
            {
                Text = name,
                Foreground = _theme.NotesText,
                FontSize = SmallFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 2, 0),
            });
            content.Children.Add(remove);

            list.Tags.Children.Add(new Border
            {
                Background = _theme.BarTrack,
                BorderBrush = _theme.Border,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(8, 1, 2, 1),
                Margin = new Thickness(0, 0, 6, 6),
                Child = content,
            });
        }
    }

    /// <summary>
    /// Adds a name, without ".exe" - matching accepts either, and the tags read
    /// better without - and ignores one already there in any capitalisation.
    /// </summary>
    private void AddProgram(string name)
    {
        if (_addingTo is not { } list)
            return;

        name = name.Trim();
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            name = name[..^4];

        if (name.Length == 0 || list.Programs.Contains(name, StringComparer.OrdinalIgnoreCase))
            return;

        list.Programs.Add(name);
        CommitPrograms(list);
        ShowRunningPrograms();   // it is listed now, so it leaves the Add list
    }

    private void RemoveProgram(ProgramTagList list, string name)
    {
        list.Programs.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        CommitPrograms(list);

        // Back in the Add list, if it is open on this list and the program is running.
        if (ProgramPopup.IsOpen && _addingTo == list)
            ShowRunningPrograms();
    }

    /// <summary>A new list each time: Commit skips a value equal to the current one, and a list is only ever equal to itself.</summary>
    private void CommitPrograms(ProgramTagList list)
    {
        ShowProgramTags(list);
        Commit(list.Setting, list.Property, new List<string>(list.Programs));
    }

    /// <summary>
    /// Clears the filter and gathers what is running. Off the UI thread:
    /// finding which processes own a window asks Windows about every window
    /// for every process.
    /// </summary>
    private async void ProgramPopup_Opened(object? sender, EventArgs e)
    {
        ProgramFilter.Text = string.Empty;
        ProgramHint.Text = "Looking…";
        ProgramList.Children.Clear();

        // Deferred: the popup's contents can't take focus while it is opening.
        _ = Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(ProgramFilter)));

        _running = await Task.Run(RunningPrograms);
        ShowRunningPrograms();
    }

    /// <summary>
    /// Names of the programs that have a window right now - which is where
    /// anything worth excluding is found: a browser, a video player. Hundreds
    /// of background processes would only bury them.
    /// </summary>
    private static IReadOnlyList<string> RunningPrograms()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        int self = Environment.ProcessId;

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.Id != self && process.MainWindowHandle != IntPtr.Zero)
                        names.Add(process.ProcessName);
                }
                catch
                {
                    // Exited while being looked at, or not ours to inspect.
                }
            }
        }

        return names.ToList();
    }

    /// <summary>Fills the Add list: running, not already listed, and matching the filter.</summary>
    private void ShowRunningPrograms()
    {
        ProgramList.Children.Clear();
        string filter = ProgramFilter.Text.Trim();

        var listed = _addingTo?.Programs ?? new List<string>();

        var matches = _running
            .Where(n => !listed.Contains(n, StringComparer.OrdinalIgnoreCase))
            .Where(n => filter.Length == 0 || n.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();

        ProgramHint.Text = matches.Count > 0
            ? "Running programs - click to add"
            : filter.Length > 0
                ? "Nothing running matches. Press Enter to add it as typed."
                : "No other programs with a window are running.";

        foreach (string name in matches)
        {
            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 3, 6, 3),
                Cursor = Cursors.Hand,
                Child = new TextBlock { Text = name, Foreground = _theme.NotesText },
            };

            // Hover like the dropdowns' rows, so it reads as clickable.
            row.MouseEnter += (_, _) => row.Background = _theme.BarTrack;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

            string captured = name;
            row.MouseLeftButtonUp += (_, _) => AddProgram(captured);

            ProgramList.Children.Add(row);
        }
    }

    private void ProgramFilter_TextChanged(object sender, TextChangedEventArgs e) => ShowRunningPrograms();

    private void ProgramFilter_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            AddTypedProgram();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            ProgramPopup.IsOpen = false;
            e.Handled = true;
        }
    }

    private void ProgramAddTyped_Click(object sender, RoutedEventArgs e) => AddTypedProgram();

    /// <summary>For a program that isn't running now - the filter text, as a name.</summary>
    private void AddTypedProgram()
    {
        if (string.IsNullOrWhiteSpace(ProgramFilter.Text))
            return;

        AddProgram(ProgramFilter.Text);
        ProgramFilter.Text = string.Empty;
    }

    private FrameworkElement Choice(SettingsCatalogue.Setting setting, PropertyInfo property, string[] choices)
    {
        var combo = new ComboBox
        {
            Style = (Style)FindResource("DarkCombo"),
            Height = 24,
            Foreground = _theme.NotesText,
        };

        foreach (string choice in choices)
            combo.Items.Add(choice);

        combo.SelectedItem = property.GetValue(_settings) as string;
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string chosen)
                Commit(setting, property, chosen);
        };

        return combo;
    }

    /// <summary>
    /// The help is under the label now, so boxes carry a tooltip only where
    /// there is something the text doesn't say - like what an empty heading box
    /// falls back to.
    /// </summary>
    private TextBox NewBox(string text, string? tooltip)
        => new()
        {
            Style = (Style)FindResource("DarkTextBox"),
            Text = text,
            Foreground = _theme.NotesText,
            ToolTip = tooltip,
            VerticalContentAlignment = VerticalAlignment.Center,
        };

    /// <summary>Red border while the text isn't something the setting can take.</summary>
    private void MarkValid(Control box, bool valid)
        => box.BorderBrush = valid ? _theme.Border : _theme.Crit;

    /// <summary>
    /// Paints the swatch, or empties it when there is no colour - an empty
    /// swatch is the honest picture of a setting that has no colour of its own.
    /// </summary>
    private static void ShowColour(Border swatch, string text)
    {
        if (!TryColour(text, out var colour))
        {
            swatch.Background = null;
            return;
        }

        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        swatch.Background = brush;
    }

    // --- Reading and writing values ---------------------------------------

    /// <summary>The current value, as it should appear in a box.</summary>
    private string Show(PropertyInfo property, bool list)
    {
        object? value = property.GetValue(_settings);

        if (list)
            return value is IEnumerable<string> items ? string.Join(", ", items) : string.Empty;

        return value switch
        {
            // Invariant, and the same shape the file uses: this is the file.
            double number => number.ToString("0.###", CultureInfo.InvariantCulture),
            int number => number.ToString(CultureInfo.InvariantCulture),
            null => string.Empty,
            _ => value.ToString() ?? string.Empty,
        };
    }

    /// <summary>
    /// Turns what was typed into the property's own type. False means "not yet"
    /// rather than "wrong" - "1." and "-" are what a number looks like halfway
    /// through being typed.
    /// </summary>
    private static bool TryRead(string text, PropertyInfo property, bool list, out object? value)
    {
        value = null;

        if (list)
        {
            value = text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .ToList();
            return true;
        }

        var type = property.PropertyType;

        if (type == typeof(string))
        {
            value = text;
            return true;
        }

        if (type == typeof(int))
        {
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int whole))
                return false;

            value = whole;
            return true;
        }

        if (type == typeof(double))
        {
            // Invariant on purpose: the box shows what the file holds, and the
            // file is JSON, where the decimal separator is always a point -
            // whatever this machine's locale would rather use.
            if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number))
                return false;

            value = number;
            return true;
        }

        return false;   // a type this window doesn't know how to edit
    }

    private static bool TryColour(string text, out Color colour)
    {
        colour = Colors.Transparent;

        try
        {
            colour = (Color)ColorConverter.ConvertFromString(text);
            return true;
        }
        catch
        {
            return false;   // not a colour yet
        }
    }

    /// <summary>
    /// Writes one setting into the live <see cref="AppSettings"/>, applies it,
    /// and queues the file write.
    /// </summary>
    private void Commit(SettingsCatalogue.Setting setting, PropertyInfo property, object? value)
    {
        if (Equals(property.GetValue(_settings), value))
            return;   // the same value again, e.g. re-picking a dropdown entry

        if (setting.Key == nameof(AppSettings.Theme) && value is string theme)
        {
            SwitchTheme(theme);
            return;
        }

        property.SetValue(_settings, value);
        _settings.MarkEdited(setting.Key);

        // A colour, font or corner a theme sets, changed by hand, makes it Custom.
        ThemeCatalogue.Edited(_settings, setting.Key);
        ShowTheme();

        Changed?.Invoke();

        Note(setting.NeedsReload
            ? $"{setting.Label} saved. Press Reload to apply it."
            : $"{setting.Label} applied.");

        QueueSave();
    }

    // --- Theme ---------------------------------------------------------------

    /// <summary>The Theme dropdown and the line describing the pick, kept in step by <see cref="ShowTheme"/>.</summary>
    private ComboBox? _themeCombo;
    private TextBlock? _themeDescription;

    /// <summary>
    /// A theme picked: its colours, fonts and corners written into the settings,
    /// everything repainted, and the form built again so every box below shows
    /// the value the theme gave it. After the dropdown's own event has run,
    /// since rebuilding takes the dropdown away.
    /// </summary>
    private void SwitchTheme(string theme)
    {
        ThemeCatalogue.Apply(_settings, theme);
        Changed?.Invoke();
        Note($"{_settings.Theme} theme applied. Change anything below to make it your own.");
        QueueSave();

        Dispatcher.BeginInvoke(new Action(RebuildForm), DispatcherPriority.Background);
    }

    /// <summary>The dropdown and its description to the theme the settings now have - after an edit made it Custom.</summary>
    private void ShowTheme()
    {
        if (_themeDescription is not null)
            _themeDescription.Text = ThemeCatalogue.Describe(_settings.Theme);
        if (_themeCombo is not null && !Equals(_themeCombo.SelectedItem, _settings.Theme))
            _themeCombo.SelectedItem = _settings.Theme;   // Commit sees the same value and does nothing
    }

    /// <summary>Every row made again from the settings, the scroll position kept.</summary>
    private void RebuildForm()
    {
        var scroller = Rows.Parent as ScrollViewer;
        double offset = scroller?.VerticalOffset ?? 0;

        Rows.Children.Clear();
        _refreshers.Clear();
        _themeCombo = null;
        _themeDescription = null;
        Build();

        scroller?.ScrollToVerticalOffset(offset);
    }

    /// <summary>The Theme dropdown, with what the picked theme looks like said under it.</summary>
    private UIElement ThemeRow(SettingsCatalogue.Setting setting, PropertyInfo property)
    {
        var combo = (ComboBox)Choice(setting, property, ThemeCatalogue.Names);
        _themeCombo = combo;

        _themeDescription = HelpText(ThemeCatalogue.Describe(_settings.Theme));
        var text = Describe(setting);
        ((StackPanel)text).Children.Add(_themeDescription);

        return SideBySide(text, combo);
    }

    /// <summary>Restarts the delay, so the write lands once you stop typing.</summary>
    private void QueueSave()
    {
        _save.Stop();
        _save.Start();
    }

    /// <summary>Writes settings.json. Debounced; see <see cref="_save"/>.</summary>
    private void SaveNow()
    {
        _save.Stop();
        _settings.Save();
    }

    private void Note(string text) => StatusText.Text = text;

    // --- Chrome -----------------------------------------------------------

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleHeaderMouseDown(e);

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        SaveNow();   // the restart reads the file, so it has to be on disk first
        ReloadRequested?.Invoke();
    }

    /// <summary>Hidden rather than closed, so it can be shown again later.</summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_save.IsEnabled)
            SaveNow();   // an edit from the last moment must not be lost

        SavePlacementNow();

        if (_allowClose)
            return;

        e.Cancel = true;
        HideSmoothly();   // banner put out on screen first - see PanelWindow
    }

    public void AllowClose() => _allowClose = true;

    /// <summary>
    /// Puts each tick box back in step with the settings.
    ///
    /// The form reads the settings once, when it is built - but some of them
    /// can change while it exists: "Always on top" and "Lock position" from the
    /// tray and the widget's menu, the FPS overlay from its hotkey. Those
    /// left a tick here that said the opposite of what was happening. The
    /// widget calls this when it changes one, and the window calls it itself
    /// whenever it is shown.
    ///
    /// Only the tick boxes, because those are the only settings changed
    /// anywhere else. Text boxes are left alone: refreshing one would move the
    /// caret out from under someone typing in it.
    /// </summary>
    public void RefreshValues()
    {
        foreach (var refresh in _refreshers)
            refresh();
    }

    public void ShowAndFocus()
    {
        if (!IsOpen)
            Show();

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Topmost = _settings.NotesAlwaysOnTop;
        Activate();
    }

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

        _settings.SettingsLeft = Left;
        _settings.SettingsTop = Top;
        _settings.SettingsWidth = Width;
        _settings.SettingsHeight = Height;
        _settings.Save();
    }

    private void ApplyTheme()
    {
        Panel.Background = _theme.NotesBackground;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        HeaderRule.Background = _theme.Separator;
        FooterRule.Background = _theme.Separator;

        TitleText.Foreground = _theme.Section;
        TitleText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);

        StatusText.Foreground = _theme.Header;
        StatusText.FontSize = Math.Max(8, UiTheme.PanelFontSize - 2.5);

        FontSize = UiTheme.PanelFontSize;
        FontFamily = _theme.PanelFont;

        // The popups, dressed like the notepad's colour popup.
        foreach (var panel in new[] { ColourPanel, ProgramPanel })
        {
            panel.Background = _theme.NotesBackground;
            panel.BorderBrush = _theme.Border;
        }

        Picker.ApplyTheme(_theme.NotesText, _theme.Border,
                          (Style)FindResource("DarkTextBox"),
                          (Style)FindResource("PanelButton"));

        AlphaLabel.Foreground = _theme.Label;
        AlphaText.Foreground = _theme.Label;
        ProgramHint.Foreground = _theme.Label;
        ProgramHint.FontSize = SmallFont;
    }
}
