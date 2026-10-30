using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeskMon.Notes;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// A notepad scoped to whichever game is running. Text is saved automatically -
/// there is no save button and no way to lose notes by closing the window.
/// </summary>
public partial class NotesWindow : PanelWindow
{
    private readonly AppSettings _settings;
    private readonly UiTheme _theme;
    private readonly NotesStore _store;
    private readonly SessionHistory _sessions;
    private readonly NotesSearch _search;

    /// <summary>Screenshots shown window-wide and sharp; the file keeps the small preview.</summary>
    private readonly NotesPictures _pictures;
    private readonly DispatcherTimer _autoSave = new() { Interval = TimeSpan.FromSeconds(1.5) };

    /// <summary>
    /// Collapses a drag or a resize into one write. WPF raises
    /// LocationChanged and SizeChanged once per mouse move, and each one used
    /// to rewrite the whole of settings.json - dozens of synchronous writes a
    /// second for a window being nudged across the desk.
    /// </summary>
    private readonly DispatcherTimer _placementSave = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private string _currentGame = NotesStore.GeneralNotes;

    /// <summary>The open game's picture behind the header; a drawn notepad for General.</summary>
    private readonly Banner _banner;
    private bool _loading;          // suppresses TextChanged/SelectionChanged while swapping files
    private bool _dirty;
    private bool _allowClose;       // only the app shutting down may really close this
    private bool _syncingFont;      // the pickers are following the caret, not being picked from
    private string _statusState = "";  // the status line's message, without the path after it

    /// <summary>
    /// The size list, in points rather than WPF's 1/96-inch units.
    ///
    /// Points because the notes are RTF, which stores sizes in half-points:
    /// a size in WPF units that isn't a whole number of half-points - 9, 11,
    /// 13 - was rounded on save and came back as 9.33 or 13.33, which matched
    /// nothing in the list. Every size here survives the trip exactly, and
    /// reads the same as Word or WordPad shows it when the file is opened there.
    /// </summary>
    private static readonly double[] PointSizes =
        { 8, 9, 10, 11, 12, 14, 16, 18, 20, 22, 24, 28, 36, 48, 72 };

    private const double DipsPerPoint = 96.0 / 72.0;

    /// <summary>
    /// What text starts at: 10 pt. It was 10.5 - UiTheme.PanelFontSize, 14
    /// units - which put an odd half size in the list for no reason but that
    /// it was the default. Text still at that old size is brought down to
    /// this as notes are opened; see <see cref="FromOldDefaultSize"/>.
    /// </summary>
    private const double DefaultPoints = 10;

    private const double DefaultTextSize = DefaultPoints * DipsPerPoint;

    /// <summary>The old default, 10.5 pt, in WPF units - as the notes files hold it.</summary>
    private const double OldDefaultTextSize = 14;

    /// <summary>
    /// The document exactly as it was last written - or as it was loaded, which
    /// is the same thing from the file's point of view.
    ///
    /// This, not <see cref="_dirty"/>, is what decides whether a save writes:
    /// see <see cref="SaveNow"/>. Null only before the first load - and while
    /// it is null, nothing is written at all.
    /// </summary>
    private byte[]? _saved;

    /// <summary>
    /// Whether the file the current notes came from is worth a backup copy
    /// before it is first overwritten: it had text in it, or it could not be
    /// read at all. Set on every load, cleared once the copy is made.
    /// </summary>
    private bool _backupBeforeWrite;

    /// <summary>
    /// The notes file's bytes as they are on disk - as loaded, then as last
    /// saved. What the backup before the first write copies, from memory,
    /// so it needs no file read on the UI thread - see NotesStore.BackupLater.
    /// </summary>
    private byte[]? _onDisk;

    /// <summary>
    /// A game's notes file read ahead on a worker - see ShowForAsync - for
    /// LoadInto to use instead of reading it on the UI thread. Null otherwise.
    /// </summary>
    private (string Game, byte[]? Rtf)? _preloaded;

    /// <summary>
    /// A key was pressed in the editor while a load was settling - see
    /// LoadGame's deferred reset, which would otherwise mark that typing saved.
    /// </summary>
    private bool _inputWhileLoading;

    /// <summary>
    /// The editing commands that change how the text looks without changing the
    /// text. WPF does not reliably raise TextChanged for these, so they are
    /// watched directly - and watched by command rather than by button, so a
    /// shortcut typed into the editor counts the same as the toolbar.
    ///
    /// Anything that alters the text itself arrives through TextChanged and
    /// needs no entry here.
    /// </summary>
    private static readonly HashSet<ICommand> FormattingCommands = new()
    {
        EditingCommands.ToggleBold,
        EditingCommands.ToggleItalic,
        EditingCommands.ToggleUnderline,
        EditingCommands.ToggleSubscript,
        EditingCommands.ToggleSuperscript,
        EditingCommands.ToggleBullets,
        EditingCommands.ToggleNumbering,
        EditingCommands.AlignLeft,
        EditingCommands.AlignCenter,
        EditingCommands.AlignRight,
        EditingCommands.AlignJustify,
        EditingCommands.IncreaseFontSize,
        EditingCommands.DecreaseFontSize,
        EditingCommands.IncreaseIndentation,
        EditingCommands.DecreaseIndentation,
    };

    public NotesWindow(AppSettings settings, UiTheme theme, NotesStore store, SessionHistory sessions)
    {
        _settings = settings;
        _theme = theme;
        _store = store;
        _sessions = sessions;
        _search = new NotesSearch(store);

        InitializeComponent();
        _pictures = new NotesPictures(Editor, Page);
        // Short and faint: it fades out behind the toolbars, before the notes
        // themselves, which have to stay easy to read.
        _banner = new Banner(Panel, height: 150, strength: 0.26);

        // A picture pasted in gets fitted - widened, spaced from the text -
        // straight away rather than at the next resize. FitSoon waits for the
        // paste to have happened, since this runs just before it.
        DataObject.AddPastingHandler(Editor, (_, _) => _pictures.FitSoon());

        // B, I, U and S follow the text after any editing command too -
        // Ctrl+B with nothing selected moves no selection, so SelectionChanged
        // alone left the button dark while bold was on for the next word.
        // After the command, not during it: it has applied its formatting by then.
        Editor.AddHandler(CommandManager.ExecutedEvent,
                          new ExecutedRoutedEventHandler((_, _) => SyncSoon()),
                          handledEventsToo: true);

        // A link added or removed, a colour set or taken back - in their own
        // popups, which move no selection either.
        LinkPopup.Closed += (_, _) => SyncFormatButtons();
        ColorPopup.Closed += (_, _) => SyncFormatButtons();

        // The mouse move that lights a picture's outline doesn't come when the
        // mouse leaves the editor straight off a picture at its edge.
        Editor.MouseLeave += (_, _) =>
        {
            _pictures.Hover(null);
            _pictures.IsOnBin(null, default, hover: true);
        };
        // On the button's release, before the editor sees it: the editor opens
        // its own Cut/Copy/Paste menu from its class handler for
        // ContextMenuOpening, which marks the event handled before a handler
        // added here is ever asked - so the picture's menu never showed.
        Editor.PreviewMouseRightButtonUp += Editor_PreviewMouseRightButtonUp;

        // Typing while a load settles: TextChanged ignores it then (_loading),
        // so it is noted here - see the deferred reset at the end of LoadGame.
        Editor.PreviewKeyDown += (_, _) =>
        {
            if (_loading)
                _inputWhileLoading = true;
        };
        ApplyTheme();
        _theme.Changed += ApplyTheme;   // a theme picked, or a colour changed, in the settings window
        SetUpPicker();
        SetUpFontPickers();
        RestorePlacement();

        // Once, the first time the window has the notes list: it was sized for
        // the old layout, and at that width the list would open folded away -
        // the new layout hidden behind a button. Widened by the list's width,
        // so the page keeps the room it had. A width picked after this stays.
        if (!_settings.NotesListWidened)
        {
            _settings.NotesListWidened = true;
            // The constant, not Sidebar.Width: a folded list has set that to
            // the icon strip's width already.
            if (_settings.NotesWidth is double width && width < ListFoldWidth + ListWidth)
                Width = width + ListWidth;
        }

        Topmost = _settings.NotesAlwaysOnTop;
        DimDuringGames(_settings, () => _settings.NotesOpacity);

        _autoSave.Tick += (_, _) => SaveNow();
        _placementSave.Tick += (_, _) => SavePlacementNow();
        _disarmDelete.Tick += (_, _) => DisarmDelete();

        // Catches a formatting command however it was given - Ctrl+I as well as
        // the toolbar button, which runs the same command. PreviewExecuted
        // rather than Executed: the editor's own command bindings mark Executed
        // handled, so a handler there would never be called.
        CommandManager.AddPreviewExecutedHandler(Editor, OnEditorCommand);

        Closing += OnClosing;
        PreviewKeyDown += OnWindowKeyDown;
        // Clicking back to the game saves - but only when something is waiting
        // to be: SaveNow serialises the whole document to compare it, and doing
        // that on every focus change put an RTF pass on the UI thread at the
        // exact moment you returned to the game. Every edit marks the notes
        // dirty (typing via TextChanged, formatting via OnEditorCommand and
        // ApplyToSelection), and closing, switching game and exit still do the
        // full comparison regardless.
        Deactivated += (_, _) =>
        {
            SearchPopup.IsOpen = false;

            if (_dirty)
                SaveNow();
            else
                SavePlacementNow();
        };
        // The search list is a popup, a window of its own: it would stay put
        // while this one moved away from under it.
        LocationChanged += (_, _) =>
        {
            SearchPopup.IsOpen = false;
            QueuePlacementSave();
        };
        SizeChanged += (_, _) =>
        {
            if (SearchPopup.IsOpen)
                AlignSearchPopup();
            FitList();
            QueuePlacementSave();
        };
        PreviewMouseDown += (_, _) =>
        {
            // A click anywhere but the box or its list puts the list away.
            if (SearchPopup.IsOpen && !SearchBox.IsMouseOver && !SearchPanel.IsMouseOver)
                SearchPopup.IsOpen = false;

            // The notes list laid over the page goes the same way, as a menu
            // does: it covers the button that brought it, so a click beside
            // it is the way to put it back.
            if (_listOverlay && !Sidebar.IsMouseOver)
                HideListOverlay();
        };
        // The line's own width, not the path's: the path is sized to fit it.
        StatusBar.SizeChanged += (_, e) => { if (e.WidthChanged) FitStatus(); };

        LoadGame(NotesStore.GeneralNotes);
    }

    // --- Frameless window behaviour --------------------------------------

    // --- Formatting ------------------------------------------------------
    //
    // These are WPF's own editing commands, which is why the usual keyboard
    // shortcuts work without being wired up: the buttons just run the same
    // command the shortcut would. Focus is handed back to the editor so a
    // click on the toolbar doesn't leave the caret stranded.

    private void Bold_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleBold);
    private void Italic_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleItalic);
    private void Underline_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleUnderline);
    private void AlignLeft_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.AlignLeft);
    private void AlignCenter_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.AlignCenter);
    private void AlignRight_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.AlignRight);
    private void Numbering_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleNumbering);
    private void Bullets_Click(object sender, RoutedEventArgs e) => Run(EditingCommands.ToggleBullets);

    private void Run(RoutedUICommand command)
    {
        Editor.Focus();
        command.Execute(null, Editor);
    }

    /// <summary>
    /// Crosses the selection out, or takes the line through it away again.
    ///
    /// Not a command like bold: WPF has none for it. Underline lives in the same
    /// property, so the decorations already there are kept and only the
    /// strikethrough is added or removed - setting it outright would wipe the
    /// underline off underlined text.
    /// </summary>
    private void Strike_Click(object sender, RoutedEventArgs e)
    {
        object current = Editor.Selection.GetPropertyValue(Inline.TextDecorationsProperty);
        var existing = current as TextDecorationCollection;
        bool struck = existing?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true;

        var next = new TextDecorationCollection(
            existing?.Where(d => d.Location != TextDecorationLocation.Strikethrough) ?? []);
        if (!struck)
            next.Add(TextDecorations.Strikethrough);
        next.Freeze();

        ApplyToSelection(Inline.TextDecorationsProperty, next);
    }

    // --- Headings ---------------------------------------------------------
    //
    // RTF as WPF writes it has no notion of a heading, only of how text looks,
    // so a heading is a size and bold on the whole line. Sizes from the size
    // list, so the picker names them and they survive the RTF's half points.

    private const double Heading1Points = 18;
    private const double Heading2Points = 14;

    private void Heading1_Click(object sender, RoutedEventArgs e) => ToggleHeading(Heading1Points);
    private void Heading2_Click(object sender, RoutedEventArgs e) => ToggleHeading(Heading2Points);

    /// <summary>
    /// Makes every line the selection touches a heading of this size, or plain
    /// text again if they already are one - so the button is its own undo, as
    /// bold's is.
    ///
    /// Whole lines rather than the selection, because a heading is a line:
    /// the caret alone in it is enough. Set on the paragraph as well as on its
    /// text, so a heading line that is still empty starts typing at its size.
    /// </summary>
    private void ToggleHeading(double points)
    {
        Editor.Focus();

        var first = Editor.Selection.Start.Paragraph;
        var last = Editor.Selection.End.Paragraph;
        if (first is null || last is null)
            return;   // the caret is beside a picture or a table, not in a line

        var lines = new TextRange(first.ContentStart, last.ContentEnd);
        double size = points * DipsPerPoint;

        bool already = lines.GetPropertyValue(TextElement.FontSizeProperty) is double current
                       && Math.Abs(current - size) < 0.01
                       && lines.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight
                       && weight == FontWeights.Bold;

        double newSize = already ? DefaultTextSize : size;
        FontWeight newWeight = already ? FontWeights.Normal : FontWeights.Bold;

        // One undo step for the lot, not one per property per line.
        Editor.BeginChange();
        try
        {
            lines.ApplyPropertyValue(TextElement.FontSizeProperty, newSize);
            lines.ApplyPropertyValue(TextElement.FontWeightProperty, newWeight);

            // Stepped through by position, not by NextBlock: the lines can be
            // in different lists, and NextBlock from a line outside a list
            // walks on past it to the end of the document.
            for (Paragraph? line = first;
                 line is not null && line.ContentStart.CompareTo(last.ContentStart) <= 0;
                 line = line.ContentEnd.GetNextInsertionPosition(LogicalDirection.Forward)?.Paragraph)
            {
                line.FontSize = newSize;
                line.FontWeight = newWeight;
            }
        }
        finally
        {
            Editor.EndChange();
        }

        MarkDirty();
        SyncFontPickers();
        SyncFormatButtons();
    }

    /// <summary>
    /// Marks the notes unsaved when a command has changed their formatting.
    ///
    /// Without this, bold and italic were applied on screen and never written:
    /// nothing told the window anything had happened, so the autosave never
    /// started and <see cref="SaveNow"/> had nothing to do. It showed up as
    /// formatting that could be added but not removed - adding it was usually
    /// followed by typing, which did mark the document unsaved and carried the
    /// formatting to disk with it, while removing it on its own was lost.
    ///
    /// It fires before the command runs, so the odd no-op - Ctrl+I with nothing
    /// selected - also marks the notes unsaved. That costs one comparison in
    /// SaveNow and no write at all.
    /// </summary>
    private void OnEditorCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (!_loading && FormattingCommands.Contains(e.Command))
        {
            MarkDirty();

            // Ctrl+] and Ctrl+[ change the size without moving the caret, so no
            // SelectionChanged follows to update the pickers. Deferred because
            // this runs before the command does - and shared with the other
            // syncs asked for, so the selection is read once (see SyncSoon).
            SyncSoon();
        }
    }

    /// <summary>
    /// The notes differ from the file: say so and start the clock that writes
    /// them once you stop.
    /// </summary>
    private void MarkDirty()
    {
        _dirty = true;
        ShowStatus("Unsaved changes…");

        _autoSave.Stop();
        _autoSave.Start();
    }

    // --- Text colour ------------------------------------------------------
    //
    // Unlike the buttons above, there is no built-in command for colour, so the
    // selection's Foreground is set directly.

    /// <summary>
    /// Dresses the picker in this window's colours and wires Apply.
    ///
    /// The picker is a plain control with no theme of its own, so that it can
    /// be dropped into any of these windows; the window it lands in tells it
    /// what to look like.
    /// </summary>
    private void SetUpPicker()
    {
        ColorPopupPanel.Background = _theme.NotesBackground;
        ColorPopupPanel.BorderBrush = _theme.Border;

        Picker.ApplyTheme(_theme.NotesText, _theme.Border,
                          (Style)FindResource("DarkTextBox"),
                          (Style)FindResource("PanelButton"));

        // Applying the window's own text colour is how a colour is cleared:
        // that colour is stripped out again when the notes are saved, so text
        // in it is text with no colour of its own. See Notes/NotesRtf.cs.
        Picker.SetNormal(_theme.NotesTextColor, "Back to the normal text colour");

        Picker.Picked += colour =>
        {
            var brush = new SolidColorBrush(colour);
            brush.Freeze();
            ApplyColor(brush);

            // Closed on Apply: the colour is on the text, and leaving the
            // square hanging over the notes would just be in the way.
            ColorPopup.IsOpen = false;
        };
    }

    /// <summary>
    /// Opens the full picker, starting from the colour of the text the caret is
    /// in - so it continues from what is there instead of from an arbitrary
    /// default, and reopening it after a pick shows that pick.
    ///
    /// The selection is read before the popup opens. It survives either way,
    /// since a Popup takes no focus from the editor, but reading it first means
    /// the picker never depends on that staying true.
    /// </summary>
    private void MoreColors_Click(object sender, RoutedEventArgs e)
    {
        Picker.SetColor(SelectionColour());
        ColorPopup.IsOpen = true;
    }

    /// <summary>
    /// The colour of the selected text, or of the caret's position. Falls back
    /// to the window's normal text colour, which is also what a mixed selection
    /// reports - GetPropertyValue returns UnsetValue when the range has more
    /// than one colour in it.
    /// </summary>
    private Color SelectionColour()
    {
        try
        {
            object value = Editor.Selection.GetPropertyValue(TextElement.ForegroundProperty);
            if (value is SolidColorBrush brush)
                return brush.Color;
        }
        catch
        {
            // An empty or detached selection; the fallback is right anyway.
        }

        return _theme.NotesTextColor;
    }

    /// <summary>
    /// Colours the selection - or, with nothing selected, whatever you type
    /// next, which is how the bold and italic buttons behave as well.
    ///
    /// Marked unsaved here rather than left to TextChanged, for the same reason
    /// the formatting commands are: see <see cref="OnEditorCommand"/>. This is
    /// not a command, so the handler there never sees it.
    /// </summary>
    private void ApplyColor(Brush brush)
        => ApplyToSelection(TextElement.ForegroundProperty, brush);

    /// <summary>
    /// Formats the selection, or the text typed next if nothing is selected.
    /// Shared by colour, font and size, none of which is a command - so the
    /// notes are marked unsaved here rather than in <see cref="OnEditorCommand"/>.
    /// </summary>
    private void ApplyToSelection(DependencyProperty property, object value)
    {
        Editor.Focus();
        Editor.Selection.ApplyPropertyValue(property, value);

        MarkDirty();
        SyncFormatButtons();   // strikethrough and colour aren't commands, so the command hook doesn't see them
    }

    // --- Font and size ---------------------------------------------------
    //
    // Formatting on the text itself, saved in the RTF like bold or colour.
    // These replace the NotesFontFamily and NotesFontSize settings, which set
    // one font for every note and could not be varied within one.

    /// <summary>
    /// Fills the font list with what is installed here, so nothing offered can
    /// fail to render. A note written elsewhere in a font missing here still
    /// loads - WPF substitutes - and the picker shows blank for it.
    /// </summary>
    private void SetUpFontPickers()
    {
        FontPicker.ItemsSource = InstalledFonts.Names;
        SizePicker.ItemsSource = PointSizes;
    }

    private void FontPicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingFont || FontPicker.SelectedItem is not string name)
            return;

        ApplyToSelection(TextElement.FontFamilyProperty, new FontFamily(name));
    }

    private void SizePicker_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_syncingFont || SizePicker.SelectedItem is not double points)
            return;

        ApplyToSelection(TextElement.FontSizeProperty, points * DipsPerPoint);
    }

    private void Editor_SelectionChanged(object sender, RoutedEventArgs e) => SyncSoon();

    /// <summary>A sync is queued and not yet run - see SyncSoon.</summary>
    private bool _syncQueued;

    /// <summary>
    /// The pickers and lit buttons brought up to date once, after the input
    /// at hand. Each sync reads the whole selection about nine times, and an
    /// arrow key asked twice - SelectionChanged and the command hook - so
    /// Shift+arrow over a long selection stuttered. However many ask in one
    /// go, one sync runs.
    /// </summary>
    private void SyncSoon()
    {
        if (_syncQueued)
            return;
        _syncQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _syncQueued = false;
            SyncFontPickers();
            SyncFormatButtons();
        }, DispatcherPriority.Input);
    }

    /// <summary>
    /// Lights every formatting button whose formatting is on where the cursor
    /// is - or across all of the selection - as a word processor does: there
    /// was no telling from the toolbar whether a click would turn something
    /// on or off. With nothing selected, it is what typing would give,
    /// including bold just switched on with Ctrl+B. A selection that is only
    /// partly bold reads as not bold, as clicking makes it all bold.
    ///
    /// The to-do button stays dark: it writes a new list rather than turning
    /// something on, and a ticked or unticked box in the notes is its own sign.
    /// </summary>
    private void SyncFormatButtons()
    {
        try
        {
            var selection = Editor.Selection;
            var decorations = selection.GetPropertyValue(Inline.TextDecorationsProperty) as TextDecorationCollection;
            bool bold = selection.GetPropertyValue(TextElement.FontWeightProperty) is FontWeight weight
                        && weight.ToOpenTypeWeight() >= FontWeights.SemiBold.ToOpenTypeWeight();

            Ui.Lit.SetIsOn(BoldButton, bold);
            Ui.Lit.SetIsOn(ItalicButton, selection.GetPropertyValue(TextElement.FontStyleProperty) is FontStyle style
                                         && style != FontStyles.Normal);
            Ui.Lit.SetIsOn(UnderlineButton, decorations?.Any(d => d.Location == TextDecorationLocation.Underline) == true);
            Ui.Lit.SetIsOn(StrikeButton, decorations?.Any(d => d.Location == TextDecorationLocation.Strikethrough) == true);

            // Headings by the same test ToggleHeading uses to take one off
            // again: the heading's size, and bold.
            double? size = selection.GetPropertyValue(TextElement.FontSizeProperty) is double dips ? dips : null;
            Ui.Lit.SetIsOn(Heading1Button, bold && size is double h1 && Math.Abs(h1 - Heading1Points * DipsPerPoint) < 0.01);
            Ui.Lit.SetIsOn(Heading2Button, bold && size is double h2 && Math.Abs(h2 - Heading2Points * DipsPerPoint) < 0.01);

            // One alignment lit for the paragraph; none when the selection
            // spans paragraphs aligned differently.
            var alignment = selection.GetPropertyValue(Paragraph.TextAlignmentProperty) as TextAlignment?;
            Ui.Lit.SetIsOn(AlignLeftButton, alignment is TextAlignment.Left or TextAlignment.Justify);
            Ui.Lit.SetIsOn(AlignCenterButton, alignment == TextAlignment.Center);
            Ui.Lit.SetIsOn(AlignRightButton, alignment == TextAlignment.Right);

            // The list the cursor's paragraph is in, by its marker: numbers,
            // or any of the bullet shapes.
            var marker = (selection.Start.Paragraph?.Parent as ListItem)?.Parent is List list ? list.MarkerStyle : (TextMarkerStyle?)null;
            Ui.Lit.SetIsOn(NumberingButton, marker is TextMarkerStyle.Decimal or TextMarkerStyle.LowerLatin or TextMarkerStyle.UpperLatin
                                                     or TextMarkerStyle.LowerRoman or TextMarkerStyle.UpperRoman);
            Ui.Lit.SetIsOn(BulletsButton, marker is TextMarkerStyle.Disc or TextMarkerStyle.Circle
                                                   or TextMarkerStyle.Square or TextMarkerStyle.Box);

            // Inside a link - at the cursor, or where the selection starts.
            Ui.Lit.SetIsOn(LinkButton, InLink(selection.Start));

            // Coloured: a text colour other than the notes' own. Its picture
            // is already a rainbow, so lit is the one sign that colour is on.
            Ui.Lit.SetIsOn(MoreColorsButton,
                selection.GetPropertyValue(TextElement.ForegroundProperty) is SolidColorBrush colour
                && Editor.Foreground is SolidColorBrush plain
                && colour.Color != plain.Color
                && !InLink(selection.Start));   // a link's own blue isn't a colour you chose
        }
        catch
        {
            // A selection caught mid document swap, as in SyncFontPickers; the next move corrects it.
        }
    }

    /// <summary>Whether <paramref name="at"/> is inside a link - walked up from the run it is in.</summary>
    private static bool InLink(TextPointer at)
    {
        for (var element = at.Parent as TextElement; element is not null; element = element.Parent as TextElement)
            if (element is Hyperlink)
                return true;
        return false;
    }

    /// <summary>
    /// Shows the font and size of the text at the caret, the way a word
    /// processor does - otherwise the pickers show whatever was picked last,
    /// which says nothing about the text you are in. Blank for a selection
    /// mixing several, since GetPropertyValue returns UnsetValue for those.
    ///
    /// Guarded so that following the caret is never taken for a pick: a pick
    /// would apply the font to the selection.
    /// </summary>
    private void SyncFontPickers()
    {
        _syncingFont = true;
        try
        {
            object family = Editor.Selection.GetPropertyValue(TextElement.FontFamilyProperty);
            FontPicker.SelectedItem = family is FontFamily font
                ? InstalledFonts.Find(font.Source)
                : null;

            object size = Editor.Selection.GetPropertyValue(TextElement.FontSizeProperty);
            SizePicker.SelectedIndex = size is double dips
                ? Array.FindIndex(PointSizes, p => Math.Abs(p * DipsPerPoint - dips) < 0.01)
                : -1;
        }
        catch
        {
            // A selection caught mid document swap; the next move corrects it.
        }
        finally
        {
            _syncingFont = false;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        => HandleHeaderMouseDown(e);


    /// <summary>Hides rather than closes - see <see cref="OnClosing"/>.</summary>
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>The game whose notes are on screen.</summary>
    public string CurrentGame => _currentGame;

    // --- Pictures ---------------------------------------------------------
    //
    // What the pictures window needs from here. It shows the screenshots of
    // whichever game these notes are for, so it follows this window rather
    // than having a game picker of its own.

    /// <summary>The pictures button: the widget owns that window, so it is asked to open or close it.</summary>
    public event Action? PicturesRequested;

    /// <summary>
    /// Lights the pictures button while that window is on screen. Told by the
    /// widget, which owns the window - the button's own click only asks, so a
    /// window put away by its own ✕, or hidden with the notes, still unlights it.
    /// </summary>
    public void SetPicturesOpen(bool open) => PicturesButton.IsChecked = open;

    /// <summary>Other notes were loaded - the pictures window follows to that game's folder.</summary>
    public event Action? CurrentGameChanged;

    /// <summary>A screenshot was put in, so a pictures window that is open has one more to show.</summary>
    public event Action? PicturesChanged;

    private void Pictures_Click(object sender, RoutedEventArgs e) => PicturesRequested?.Invoke();

    // --- History ----------------------------------------------------------
    //
    // The copies NotesStore.Backup has always made, one before the first save
    // after each load, offered back. They were only reachable through
    // Explorer before, which nobody does mid-panic over lost notes.

    private void History_Click(object sender, RoutedEventArgs e) => HistoryPopup.IsOpen = true;

    /// <summary>Lists this note's copies, newest first, each with when it was taken and its size.</summary>
    private void HistoryPopup_Opened(object? sender, EventArgs e)
    {
        HistoryList.Children.Clear();
        var copies = _store.BackupsOf(_currentGame);

        HistoryHint.Text = copies.Count == 0
            ? "No copies yet. One is kept each time these notes are opened and then changed - the last 20."
            : "Click one to put it back. The notes as they are now are kept as a copy first, so this can be undone the same way.";

        // Unarmed on every opening, so a first click left from last time
        // can't turn the next one into a delete. Hidden with nothing to clear.
        _clearArmed = false;
        ClearHistoryButton.Content = "Clear history";
        ClearHistoryButton.ClearValue(ForegroundProperty);
        ClearHistoryButton.Visibility = copies.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        double small = Math.Max(9, UiTheme.PanelFontSize - 3);

        foreach (var copy in copies)
        {
            var line = new Grid();
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            line.Children.Add(new TextBlock
            {
                Text = copy.Taken.ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture),
                Foreground = _theme.NotesText,
                FontSize = small + 1,
            });

            // The size, because it is the one clue to what is in a copy
            // without opening it: a copy far smaller than its neighbours is
            // the one taken just after something was lost.
            var size = new TextBlock
            {
                // Invariant, so a Swedish Windows shows "1.5 MB" like every other number here.
                Text = copy.Bytes < 1024 * 1024
                    ? $"{Math.Max(1, copy.Bytes / 1024)} KB"
                    : (copy.Bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
                Foreground = _theme.Label,
                FontSize = small,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(10, 0, 0, 0),
            };
            Grid.SetColumn(size, 1);
            line.Children.Add(size);

            // Rows like the search results', so the popups behave alike.
            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4, 6, 4),
                Cursor = Cursors.Hand,
                Child = line,
            };
            row.MouseEnter += (_, _) => row.Background = _theme.Separator;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

            var captured = copy;
            row.MouseLeftButtonUp += (_, _) => RestoreCopy(captured);

            HistoryList.Children.Add(row);
        }
    }

    /// <summary>Whether Clear history has had its first click - see ClearHistory_Click.</summary>
    private bool _clearArmed;

    /// <summary>
    /// Deletes every copy of this note in one go. Asks twice, the second
    /// time naming the count, like the sessions window's Delete all: the
    /// copies are what is left when notes go missing, and they don't come back.
    /// The popup closing disarms it - see HistoryPopup_Opened.
    /// </summary>
    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        if (!_clearArmed)
        {
            _clearArmed = true;
            int count = HistoryList.Children.Count;
            ClearHistoryButton.Content = $"Click again to delete {count} {(count == 1 ? "copy" : "copies")}";
            ClearHistoryButton.Foreground = _theme.Warn;
            return;
        }

        HistoryPopup.IsOpen = false;
        if (_store.DeleteBackups(_currentGame) is { } error)
        {
            ShowStatus("History not cleared: " + error, problem: true);
            return;
        }

        // A fresh copy before the next save, so the notes on screen are
        // still covered - clearing took the one made after this load.
        _backupBeforeWrite = true;
        ShowStatus("History cleared");
    }

    /// <summary>
    /// Puts a copy back as these notes and opens it.
    ///
    /// What is on screen is saved first, so the copy Restore takes of the file
    /// holds it - otherwise the last second of typing would be the one thing
    /// a restore could not be undone back to. If that save fails, nothing is
    /// restored.
    ///
    /// async void as the click handler it is: the Recycle Bin look runs on a
    /// worker, and everything that can throw is caught inside.
    /// </summary>
    private async void RestoreCopy(NotesStore.BackupCopy copy)
    {
        HistoryPopup.IsOpen = false;

        SaveNow();
        if (_dirty)
        {
            ShowStatus("Not restored - the notes on screen could not be saved first", problem: true);
            return;
        }

        string game = _currentGame;
        if (_store.Restore(game, copy.Path) is { } error)
        {
            ShowStatus("Restore failed: " + error, problem: true);
            return;
        }

        LoadGame(game);

        // The copy can show pictures deleted since; their files are put back
        // from the Recycle Bin, so the pictures window lists them again at full
        // size. Each from the folder its link names - usually this game's, but
        // one copied in from another game's notes lives in that one's. On a
        // worker: every entry in the bin is read, and a full bin is thousands.
        var shown = NotesPictures.FilePathsIn(Editor.Document);
        var byFolder = shown.Where(path => !File.Exists(path))
                            .GroupBy(path => Path.GetDirectoryName(path) ?? string.Empty, StringComparer.OrdinalIgnoreCase)
                            .Where(group => group.Key.Length > 0)
                            .Select(group => (Folder: group.Key, Names: group.Select(p => Path.GetFileName(p)).ToList()))
                            .ToList();
        var back = byFolder.Count == 0
            ? new List<string>()
            : await Task.Run(() => byFolder.SelectMany(group => RecycleBin.Restore(group.Folder, group.Names)).ToList());
        if (back.Count > 0)
            PicturesChanged?.Invoke();

        string pictures = back.Count switch
        {
            0 => "",
            1 => "  ·  1 picture back from the Recycle Bin",
            _ => $"  ·  {back.Count} pictures back from the Recycle Bin",
        };

        // Said rather than left to be noticed: a picture whose file is gone
        // from the Recycle Bin too shows in the notes, from their preview,
        // yet never in the pictures window - which read as the restore failing.
        int gone = shown.Count(path => !File.Exists(path));
        if (gone > 0)
        {
            string which = gone == 1 ? "1 picture's file is" : $"{gone} pictures' files are";
            ShowStatus($"Restored the copy from {When(copy.Taken)}{pictures}  ·  {which} gone for good - only the preview in the notes is left", problem: true);
            return;
        }
        ShowStatus($"Restored the copy from {When(copy.Taken)}{pictures}  ·  the notes before it are in HISTORY");
    }

    /// <summary>Where the open game's screenshots are kept.</summary>
    public string PictureFolder => _store.ScreenshotFolderFor(_currentGame);

    /// <summary>
    /// Every screenshot file name some notes still show: these notes as they
    /// are on screen - unsaved edits included - and every other game's as
    /// saved. A picture not in here is unused.
    /// </summary>
    public HashSet<string> PicturesInUse()
    {
        var names = NotesPictures.FileNamesIn(Editor.Document);
        names.UnionWith(_store.ScreenshotNamesInNotes(exceptGame: _currentGame));
        return names;
    }

    /// <summary>
    /// How many screenshots these notes show - each one's preview is stored
    /// in the notes file, so this is what makes saving them slow. As on screen,
    /// unsaved edits included.
    /// </summary>
    public int PictureCountInNotes() => NotesPictures.FileNamesIn(Editor.Document).Count;

    /// <summary>Where the screenshots these notes show have their files, as on screen - for telling which have none left.</summary>
    public HashSet<string> PicturePathsInNotes() => NotesPictures.FilePathsIn(Editor.Document);

    /// <summary>
    /// Deletes a picture: its file to the Recycle Bin, then out of these notes
    /// if they show it - in that order, as the pictures window deletes, so a
    /// file Windows refuses leaves the notes as they were. The pictures window
    /// lists the change. Null when it went, else why not.
    /// </summary>
    public string? DeletePicture(string fullPath)
    {
        if (RecycleBin.Send(fullPath) is { } error)
            return error;

        RemovePicture(Path.GetFileName(fullPath));
        PicturesChanged?.Invoke();
        ShowStatus($"{Path.GetFileName(fullPath)} deleted - it is in the Recycle Bin");
        return null;
    }

    /// <summary>
    /// Takes a picture out of these notes - its file is being deleted - and
    /// saves straight away, rather than leaving the notes pointing at a file
    /// that is gone until the next autosave. One undo step, so Ctrl+Z brings
    /// the picture back (though not its full-size file, which is in the
    /// Recycle Bin).
    /// </summary>
    public bool RemovePicture(string fileName)
    {
        bool removed;
        Editor.BeginChange();
        try
        {
            removed = NotesPictures.Remove(Editor.Document, fileName);
        }
        finally
        {
            Editor.EndChange();
        }

        if (removed)
        {
            MarkDirty();
            SaveNow();
        }

        return removed;
    }

    /// <summary>
    /// Switches which game's notes are loaded, without showing the window.
    /// Used when a game exits: the notes go back to General, but a window that
    /// was closed must stay closed. Null or empty means the General notes.
    /// </summary>
    /// <param name="running">
    /// Whether <paramref name="gameName"/> is the game running - or, for none,
    /// that none is. False for a caller that only wants a game's notes on
    /// screen: a screenshot taken from the camera button with no game
    /// running goes into whichever notes are open, and passing that game
    /// along here put it under PLAYING NOW.
    /// </param>
    public void SwitchTo(string? gameName, bool running = true)
    {
        string game = string.IsNullOrWhiteSpace(gameName) ? NotesStore.GeneralNotes : gameName!;

        // The list's PLAYING NOW, from the callers that know what is running;
        // set before the load, whose list refresh shows it.
        if (running)
            _playing = IsGeneral(game) ? null : game;

        if (!string.Equals(game, _currentGame, StringComparison.OrdinalIgnoreCase))
            LoadGame(game);   // saves what is on screen first
        else
            RefreshList();
    }

    /// <summary>
    /// Switches to a game's notes and makes sure the window is visible. Does not
    /// steal focus: during a game that would tab the player out of it.
    /// </summary>
    /// <param name="running">See SwitchTo: false when the game is only the notes wanted, not the one running.</param>
    public void ShowFor(string? gameName, bool running = true)
    {
        SwitchTo(gameName, running);

        Topmost = _settings.NotesAlwaysOnTop;

        if (!IsOpen)
            Show();
    }

    /// <summary>Brings the window forward, for when the user asked for it explicitly.</summary>
    public void ShowAndFocus(string? gameName)
    {
        ShowFor(gameName);

        if (WindowState == WindowState.Minimized)
            WindowState = WindowState.Normal;

        Activate();
        Editor.Focus();
    }

    /// <param name="mayRefuse">
    /// For a switch asked for here - the picker, a search result, NEW: when
    /// the notes on screen can be neither saved nor kept as a copy, they stay
    /// on screen and this returns false. A switch from a game starting or
    /// ending can't wait for that, so it goes ahead with the copy kept if
    /// it could be.
    /// </param>
    private bool LoadGame(string game, bool mayRefuse = false)
    {
        SaveNow();   // never lose what is on screen

        // The save failed - the file held by another program, the disk full -
        // and the notes on screen are about to be replaced. They used to be
        // lost with it; kept as a copy, HISTORY can put them back.
        string? keptFor = null;
        if (_dirty && _saved is not null)
        {
            if (KeepUnsavedCopy())
            {
                keptFor = _currentGame;
            }
            else if (mayRefuse)
            {
                ShowStatus("Not switched - these notes couldn't be saved, or kept as a copy. Copy what you typed somewhere safe.",
                           problem: true);
                return false;
            }
        }

        // An armed delete was aimed at the note it named; a second click
        // after switching must not delete this one instead.
        DisarmDelete();

        // From here until the new notes are fully loaded, the editor does not
        // hold what is in _currentGame's file - so nothing may be written. A
        // null baseline is what SaveNow refuses on; it is set again below only
        // once the load has completed. Without this, a load that failed part
        // way would leave the previous game's baseline paired with this game.
        _saved = null;

        _loading = true;
        try
        {
            _currentGame = game;
            var (state, problem) = LoadInto(game);

            // To-do items written before they had their own layout get it now.
            // Before the baseline below, so this alone never rewrites the file:
            // it is saved the new way along with the next real edit.
            Checklist.TidyAll(Editor.Document);

            // The baseline the next save compares against. Taken from the
            // document just loaded rather than from the file's own bytes,
            // because WPF's writer normalises what it reads - the file and a
            // round trip of it are the same notes but not the same bytes, and
            // comparing them would make every save rewrite the file.
            _saved = Serialise();

            // Title is the taskbar entry, and names the game since nothing else
            // there does. The header names it too, large: with the drop-down
            // gone, it is what says whose notes these are.
            Title = $"Notes - {game}";
            TitleText.Text = game;
            // General is only ever emptied, never removed, so the button says
            // what it will really do there rather than promising a delete.
            DeleteNoteButton.ToolTip = IsGeneral(game)
                ? "Clear General\nGeneral can't be deleted, only cleared. The old notes go to the Recycle Bin."
                : "Delete this note\nIt goes to the Recycle Bin.";
            RefreshList();
            ShowLastSession();
            _dirty = false;
            ShowStatus(state, problem);

            // Said over the load's own message: it is the one thing to act on.
            if (keptFor is not null)
                ShowStatus($"{keptFor} couldn't be saved - what was on screen is kept in its HISTORY", problem: true);

            if (IsGeneral(game))
                _banner.Show(GameArt.Motif.Notepad);
            else
                _banner.ShowGame(game, _settings, _store.ScreenshotFolderFor(game), GameArt.Motif.Gamepad);
            CurrentGameChanged?.Invoke();
        }
        finally
        {
            // Swapping the document raises TextChanged again *after* this
            // method returns, which would mark freshly loaded notes as unsaved
            // and write the file straight back. Every such round trip re-runs
            // WPF's RTF serializer over the content, so hold the guard until
            // the queue has drained rather than clearing it here.
            _inputWhileLoading = false;
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                _loading = false;

                // Keys pressed before this ran are real edits, which input
                // priority let in ahead of it; clearing the flag for them
                // left that typing to wait for the next edit or a close.
                if (_inputWhileLoading)
                {
                    _inputWhileLoading = false;
                    MarkDirty();
                    return;
                }

                _dirty = false;
                _autoSave.Stop();
            }));
        }

        return true;
    }

    /// <summary>
    /// <see cref="ShowFor"/> with the game's notes file read on a worker
    /// first - for a game starting, where reading megabytes of notes with
    /// screenshots on the UI thread held up every window at the launch.
    /// The document itself is still built on the UI thread, as WPF needs.
    /// </summary>
    public async Task ShowForAsync(string? gameName)
    {
        string game = string.IsNullOrWhiteSpace(gameName) ? NotesStore.GeneralNotes : gameName!;
        if (!string.Equals(game, _currentGame, StringComparison.OrdinalIgnoreCase))
        {
            byte[]? rtf = await Task.Run(() => _store.LoadRtf(game));
            _preloaded = (game, rtf);
        }

        try
        {
            ShowFor(game);
        }
        finally
        {
            _preloaded = null;   // only ever for the one load it was read for
        }
    }

    /// <summary>
    /// The notes on screen as a copy in the backups, for when they couldn't
    /// be saved over their file. False if that failed too.
    /// </summary>
    private bool KeepUnsavedCopy()
    {
        try
        {
            return _store.KeepCopy(_currentGame, Serialise()) is null;
        }
        catch
        {
            return false;   // the document itself can't be written out
        }
    }

    /// <summary>
    /// Puts a game's notes in the editor. Prefers the .rtf; falls back to a
    /// plain .txt from before formatting existed, which is imported as text and
    /// written back as .rtf on the next save. The .txt is left alone.
    /// Returns what the status line should say, and whether that is a problem.
    /// </summary>
    private (string Status, bool Problem) LoadInto(string game)
    {
        var document = new FlowDocument();
        Editor.Document = document;
        ApplyDocumentDefaults(document);

        // Nothing to protect until shown otherwise: a new game has no file, and
        // a .txt import is never written over (its .rtf is a new file).
        _backupBeforeWrite = false;

        // Read ahead on a worker when this load came from ShowForAsync.
        byte[]? rtf = _preloaded is { } ahead && string.Equals(ahead.Game, game, StringComparison.OrdinalIgnoreCase)
            ? ahead.Rtf
            : _store.LoadRtf(game);
        _onDisk = rtf is { Length: > 0 } ? rtf : null;
        if (rtf is { Length: > 0 })
        {
            try
            {
                using var stream = new MemoryStream(rtf);
                Contents.Load(stream, DataFormats.Rtf);
                FromOldDefaultSize(document);
                MoveCaretToEnd();
                _pictures.FitSoon();

                // An empty file is not worth a copy - and copying it would
                // fill the backups with blanks that push the good ones out.
                // Pictures count: notes that are only screenshots are worth a copy too.
                _backupBeforeWrite = !string.IsNullOrWhiteSpace(Contents.Text) || NotesPictures.HasAny(document);
                return (LoadedText(game), false);
            }
            catch
            {
                // Corrupt or truncated file: fall through rather than wiping
                // it - and back it up before anything is written over it,
                // since a file that cannot be read here might still be
                // recoverable elsewhere.
                _backupBeforeWrite = true;
                return ("Could not read the notes file - a copy is kept before anything is saved over it", true);
            }
        }

        string? legacy = _store.LoadLegacyText(game);
        if (!string.IsNullOrEmpty(legacy))
        {
            Contents.Text = legacy;
            MoveCaretToEnd();
            return ("Imported successfully from the old .txt  ·  saved as .rtf with your next change", false);
        }

        return ("New notes  ·  nothing saved yet", false);
    }

    /// <summary>The whole document as one range, for loading and saving.</summary>
    private TextRange Contents
        => new(Editor.Document.ContentStart, Editor.Document.ContentEnd);

    private void MoveCaretToEnd() => Editor.CaretPosition = Editor.Document.ContentEnd;

    private void ApplyDocumentDefaults(FlowDocument document)
    {
        document.Background = Brushes.Transparent;
        document.Foreground = _theme.NotesText;
        // Only what text with no font of its own starts as - anything picked
        // on the toolbar is saved on the text and wins over this.
        document.FontFamily = new FontFamily(UiTheme.PanelFontFamily);
        document.FontSize = DefaultTextSize;
        document.PagePadding = new Thickness(0, 2, 0, 2);
    }

    /// <summary>
    /// Brings text at the old default size, 10.5 pt, down to the new one.
    ///
    /// The RTF writer records the size of every run, the default included, so
    /// text written before the default changed carries 10.5 in the file - and
    /// would go on showing it, with a blank in the size list now that 10.5 is
    /// not in it. Only that exact size is changed: anything picked on purpose
    /// stays as it was.
    ///
    /// Done as the notes load, before the baseline SaveNow compares against
    /// is taken, so opening notes still writes nothing: the file takes the
    /// new size the next time the notes are actually changed.
    /// </summary>
    private static void FromOldDefaultSize(DependencyObject node)
    {
        if (node is TextElement element
            && element.ReadLocalValue(TextElement.FontSizeProperty) is double size
            && Math.Abs(size - OldDefaultTextSize) < 0.01)
        {
            element.FontSize = DefaultTextSize;
        }

        foreach (object child in LogicalTreeHelper.GetChildren(node))
            if (child is TextElement or FlowDocument)
                FromOldDefaultSize((DependencyObject)child);
    }

    // --- The notes list -------------------------------------------------

    /// <summary>The game being played, for the list's PLAYING NOW - see SwitchTo. Null between games.</summary>
    private string? _playing;

    /// <summary>
    /// When each note was last played or edited, and the line its row shows
    /// for it, by file name. Read on a worker - see LoadActivityLater - and
    /// kept, so a rebuilt list has its lines at once instead of blinking empty.
    /// </summary>
    private readonly Dictionary<string, (DateTime When, string Line)> _activity = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Only the newest read is used: an older one finishing late would put back stale lines.</summary>
    private int _activityTicket;

    /// <summary>The notes the list was last built from, for a late answer to rebuild it with.</summary>
    private List<string> _listed = new();

    /// <summary>
    /// Below this width the list folds away behind a button: at the old
    /// window's width the list and a page beside it would both be too
    /// narrow to use.
    /// </summary>
    private const double ListFoldWidth = 560;

    /// <summary>The list laid over the page on a narrow window, from the button by the title.</summary>
    private bool _listOverlay;

    /// <summary>The window is under <see cref="ListFoldWidth"/>, and the list is folded away - see FitList.</summary>
    private bool _narrow;

    /// <summary>
    /// The list open, and folded down to its icons. The strip is the 24 px
    /// icon, the rows' 6 px margins and 8 px padding each side, the 1 px edge
    /// and a little to spare: at 52 it came out a pixel short and cut the
    /// icons' right edge off.
    /// </summary>
    private const double ListWidth = 210;
    private const double IconRailWidth = 58;

    /// <summary>
    /// The list shows only its icons (the « button). Never laid over the page:
    /// opened from the narrow window's button it is wanted in full.
    /// </summary>
    private bool ListFolded => _settings.NotesListFolded && !_narrow;

    /// <summary>Whether the list on screen is folded - what FitList last laid out.</summary>
    private bool _shownFolded;

    /// <summary>
    /// The list shows every note there is, which is what makes this useful
    /// away from the game: you can read up on a puzzle months later.
    /// </summary>
    private void RefreshList()
    {
        // General is added by BuildList, pinned first, whether or not it has a
        // file: deleting it only clears it, and a cleared General has no file
        // until something is typed into it - which left it missing from the
        // list once you had switched away.
        var games = _store.ListGames().Where(g => !IsGeneral(g)).ToList();

        // The list is made from file names, which have the characters Windows
        // forbids replaced: "The Witcher 3: Wild Hunt" is listed as "The
        // Witcher 3- Wild Hunt". Compared by name alone, the running game
        // wasn't found and was added a second time - two entries for one
        // file. Its file's entry takes the real name instead. The game being
        // played is listed even before it has notes, like the one on screen.
        void Include(string? game)
        {
            if (game is null || IsGeneral(game))
                return;
            int listed = games.FindIndex(g => SameNote(g, game));
            if (listed >= 0)
                games[listed] = game;
            else
                games.Add(game);
        }
        Include(_currentGame);
        Include(_playing);

        _listed = games;
        BuildList();
        LoadActivityLater(games);
    }

    /// <summary>
    /// Lays the list out from <see cref="_listed"/> and what is known of when
    /// each was played: General, PLAYING NOW, then RECENT with the newest
    /// first, so the notes you are likely to want are at the top rather than
    /// somewhere in the alphabet.
    /// </summary>
    private void BuildList()
    {
        double small = Math.Max(9, UiTheme.PanelFontSize - 3);
        bool folded = ListFolded;
        NoteEntry Row(string name, string? detail, Brush? detailBrush = null) => new()
        {
            Name = name, Detail = detail, SmallFontSize = small, Folded = folded,
            NameBrush = _theme.NotesText, DetailBrush = detailBrush ?? _theme.Header,
        };
        NoteEntry Heading(string text) => new()
        {
            Name = text, IsHeading = true, SmallFontSize = small, NameBrush = _theme.Header,
        };
        // Folded to icons there is no room for PLAYING NOW or RECENT: the
        // headings go, and the order alone says which is which.
        void AddHeading(List<NoteEntry> list, string text)
        {
            if (!folded)
                list.Add(Heading(text));
        }
        string? LineFor(string game) => _activity.TryGetValue(NotesStore.SafeFileName(game), out var a) ? a.Line : null;
        DateTime WhenFor(string game) => _activity.TryGetValue(NotesStore.SafeFileName(game), out var a) ? a.When : DateTime.MinValue;

        string? playing = _playing is null ? null : _listed.FirstOrDefault(g => SameNote(g, _playing));
        var entries = new List<NoteEntry> { Row(NotesStore.GeneralNotes, "Notes for any game") };
        if (playing is not null)
        {
            AddHeading(entries, "PLAYING NOW");
            entries.Add(Row(playing, "● Playing now", _theme.Normal));
        }

        var rest = _listed.Where(g => playing is null || !SameNote(g, playing))
                          .OrderByDescending(WhenFor)
                          .ThenBy(g => g, StringComparer.CurrentCultureIgnoreCase)
                          .ToList();
        if (rest.Count > 0)
        {
            AddHeading(entries, "RECENT");
            entries.AddRange(rest.Select(g => Row(g, LineFor(g))));
        }

        // Guarded like a load: setting the selection here is the list
        // following the notes on screen, not someone picking a note.
        bool wasLoading = _loading;
        _loading = true;
        try
        {
            NoteList.ItemsSource = entries;
            NoteList.SelectedItem = entries.FirstOrDefault(n => !n.IsHeading && SameNote(n.Name, _currentGame));
        }
        finally
        {
            _loading = wasLoading;
        }

        ShowNoteDetail();
    }

    /// <summary>
    /// The grey line under the title: playing now, or when the note was last
    /// played or edited. Nothing for General, which belongs to no game.
    /// </summary>
    private void ShowNoteDetail()
    {
        bool playing = _playing is not null && SameNote(_currentGame, _playing);
        string? line = IsGeneral(_currentGame) ? null
                     : playing ? "● Playing now"
                     : _activity.TryGetValue(NotesStore.SafeFileName(_currentGame), out var a) ? "Last " + a.Line : null;

        NoteDetail.Text = line ?? "";
        NoteDetail.Foreground = playing ? _theme.Normal : _theme.Header;
        NoteDetail.Visibility = line is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>
    /// Reads when every listed note was last played and edited, on a worker:
    /// it opens every session log, the very read that froze the sessions
    /// window on the UI thread (bugtest M5). The list is up already, by name;
    /// this only adds the lines and the order.
    /// </summary>
    private async void LoadActivityLater(IReadOnlyList<string> games)
    {
        int ticket = ++_activityTicket;
        Dictionary<string, (DateTime When, string Line)> found;
        try
        {
            found = await Task.Run(() => ReadActivity(games));
        }
        catch
        {
            return;   // only ever a footnote under a name - never worth an error
        }

        if (ticket != _activityTicket)
            return;

        // Rebuilt only when something changed: a new ItemsSource scrolls the
        // list back to the top, which would be a jolt for nothing.
        bool changed = found.Count != _activity.Count
                    || found.Any(kv => !_activity.TryGetValue(kv.Key, out var old) || old != kv.Value);
        if (!changed)
            return;

        _activity.Clear();
        foreach (var (key, value) in found)
            _activity[key] = value;
        BuildList();
    }

    /// <summary>Off the UI thread: file reads only, no WPF.</summary>
    private Dictionary<string, (DateTime When, string Line)> ReadActivity(IReadOnlyList<string> games)
    {
        var found = new Dictionary<string, (DateTime When, string Line)>(StringComparer.OrdinalIgnoreCase);
        foreach (string game in games)
        {
            var last = _sessions.LastSession(game);
            DateTime? edited = _store.LastSaved(game);
            if (last is null && edited is null)
                continue;

            // Whichever was later counts as "recent": notes read up on away
            // from the game belong near the top as much as the game last played.
            DateTime when = last is null ? edited!.Value
                          : edited is DateTime e && e > last.End ? e
                          : last.End;
            // The session says more than the edit - how long it ran - so it
            // wins the line even when the notes were edited after it.
            string line = last is not null
                ? $"{last.Start.ToString("d MMM", CultureInfo.InvariantCulture)} · {SessionRecord.Describe(last.Length)}"
                : $"edited {edited!.Value.ToString("d MMM", CultureInfo.InvariantCulture)}";
            found[NotesStore.SafeFileName(game)] = (when, line);
        }
        return found;
    }

    /// <summary>One note's file, however its name is spelled - see RefreshList.</summary>
    private static bool SameNote(string a, string b)
        => string.Equals(NotesStore.SafeFileName(a), NotesStore.SafeFileName(b), StringComparison.OrdinalIgnoreCase);

    private void NoteList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NoteList.SelectedItem is not NoteEntry { IsHeading: false } note)
            return;

        // Laid over the page, the list has done its job once a note is picked.
        HideListOverlay();

        // Refused, the list goes back to the notes still on screen.
        if (!string.Equals(note.Name, _currentGame, StringComparison.OrdinalIgnoreCase)
            && !LoadGame(note.Name, mayRefuse: true))
            Dispatcher.BeginInvoke(new Action(BuildList));
    }

    /// <summary>
    /// Docks the list beside the page, or on a narrow window folds it away
    /// behind the button by the title - and lays it over the page while that
    /// button is on. The page then takes the list's column too.
    /// </summary>
    private void FitList()
    {
        bool narrow = ActualWidth > 0 && ActualWidth < ListFoldWidth;
        if (!narrow)
            _listOverlay = false;

        Grid.SetColumn(NotePane, narrow ? 0 : 1);
        Grid.SetColumnSpan(NotePane, narrow ? 2 : 1);
        Sidebar.Visibility = !narrow || _listOverlay ? Visibility.Visible : Visibility.Collapsed;
        ListButton.Visibility = narrow ? Visibility.Visible : Visibility.Collapsed;
        ListButton.IsChecked = _listOverlay;

        // Over the page it needs a solid ground, or the notes would show
        // through it; beside the page, the faint shade the cards have.
        Sidebar.Background = narrow ? new SolidColorBrush(_theme.WindowColor) : _theme.Shade(0x08);

        // Against what is on screen, not ListFolded before: the « button
        // changes the setting first and then calls this, so the setting read
        // here was already the new one, no change was ever seen - and nothing
        // animated or rebuilt.
        bool wasFolded = _shownFolded;
        bool wasNarrow = _narrow;
        _narrow = narrow;
        bool folded = ListFolded;
        _shownFolded = folded;

        // Folded, everything with words gives way: the heading, the search
        // box (to its magnifier) and NEW NOTE's label. The fold button stays,
        // centred in the strip, and has nothing to do on a narrow window,
        // whose list is either away or wanted in full.
        //
        // Animated only for the « button itself: a window opening, or one
        // dragged across the narrow line, just takes its layout.
        double width = folded ? IconRailWidth : ListWidth;
        if (folded != wasFolded && narrow == wasNarrow && IsVisible)
        {
            AnimateFold(width, folded);
        }
        else
        {
            Sidebar.BeginAnimation(WidthProperty, null);
            Sidebar.Width = width;
            FoldTurn.BeginAnimation(RotateTransform.AngleProperty, null);
            FoldTurn.Angle = folded ? 180 : 0;
        }
        ListHeading.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        SearchRow.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        SearchRailButton.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;
        NewNoteLabel.Visibility = folded ? Visibility.Collapsed : Visibility.Visible;
        NewNoteButton.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        // The 18 px scrollbar would take most of the strip's room from the
        // icons; hidden, the wheel still scrolls a long list.
        ScrollViewer.SetVerticalScrollBarVisibility(NoteList, folded ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto);

        FoldListButton.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(FoldListButton, folded ? 0 : 1);
        Grid.SetColumnSpan(FoldListButton, folded ? 2 : 1);
        FoldListButton.HorizontalAlignment = folded ? HorizontalAlignment.Center : HorizontalAlignment.Right;
        ListTop.Margin = folded ? new Thickness(0, 6, 0, 4) : new Thickness(0, 6, 6, 4);
        // In the narrow strip the line keeps closer to the edges, or it would be a dash.
        ListRule.Margin = folded ? new Thickness(8, 0, 8, 8) : new Thickness(10, 0, 10, 8);
        FoldListButton.ToolTip = folded ? "Show the names\nOpen the list out again." : "Only the icons\nFold the list down to its icons, for more room on the page.";

        // The rows carry whether they show their names - see NoteEntry.
        if (folded != wasFolded && NoteList.ItemsSource is not null)
            BuildList();
    }

    /// <summary>
    /// The fold, as one smooth movement: the list slides to its new width
    /// with an ease at both ends, its rows fade up as the names come or go -
    /// so the swap to icons, which happens all at once, reads as part of
    /// the slide - and the « turns round to » on the way. A quarter of a
    /// second: long enough to see where the list went, short enough not to
    /// wait on.
    /// </summary>
    private void AnimateFold(double to, bool folded)
    {
        var time = TimeSpan.FromMilliseconds(260);
        var ease = new CubicEase { EasingMode = EasingMode.EaseInOut };

        // From the width it has on screen right now, so a click mid-slide
        // turns it round from there instead of jumping. FillBehavior.Stop
        // hands back to the local value, already the end width, once done.
        double from = Sidebar.ActualWidth > 0 ? Sidebar.ActualWidth : to;
        Sidebar.Width = to;
        Sidebar.BeginAnimation(WidthProperty,
            new DoubleAnimation(from, to, time) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        NoteList.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0.15, 1, time) { EasingFunction = ease, FillBehavior = FillBehavior.Stop });

        FoldTurn.BeginAnimation(RotateTransform.AngleProperty,
            new DoubleAnimation(folded ? 180 : 0, time) { EasingFunction = ease });
    }

    private void FoldList_Click(object sender, RoutedEventArgs e)
    {
        SetListFolded(!_settings.NotesListFolded);
    }

    /// <summary>Remembered, like the widget's folded sections: the room on the page is wanted every time, not once.</summary>
    private void SetListFolded(bool folded)
    {
        if (_settings.NotesListFolded == folded)
            return;
        _settings.NotesListFolded = folded;
        _settings.Save();
        FitList();
    }

    /// <summary>The magnifier on the folded list: open it out, into the box, as Ctrl+F does.</summary>
    private void SearchRail_Click(object sender, RoutedEventArgs e) => FocusSearch();

    /// <summary>
    /// Into the search box, ready to type over - opening the list first,
    /// laid over the page on a narrow window or out of its icons, since the
    /// box is in it.
    /// </summary>
    private void FocusSearch()
    {
        ShowListOverlay();
        // Laid over the page the list is shown in full anyway, so the folded
        // setting - for the wide window - is left as it was.
        if (!_narrow)
            SetListFolded(false);
        Keyboard.Focus(SearchBox);
        SearchBox.SelectAll();
    }

    private void ListButton_Click(object sender, RoutedEventArgs e)
    {
        _listOverlay = ListButton.IsChecked == true;
        FitList();
    }

    private void ShowListOverlay()
    {
        if (ListButton.Visibility != Visibility.Visible || _listOverlay)
            return;
        _listOverlay = true;
        FitList();
    }

    private void HideListOverlay()
    {
        if (!_listOverlay)
            return;
        _listOverlay = false;
        FitList();
    }

    private bool _tidying;

    private void Editor_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_loading)
            return;

        // The line being typed on, laid out as a to-do item if it has become
        // one - a box typed or pasted by hand - or back to an ordinary line if
        // its box was deleted. Guarded, since tidying is an edit too and
        // raises this again; it changes nothing the second time.
        if (!_tidying && Editor.CaretPosition.Paragraph is { } line)
        {
            _tidying = true;
            try { Checklist.Tidy(line); }
            finally { _tidying = false; }
        }

        // Restarting the timer inside MarkDirty means the write happens once
        // you pause, not on every keystroke.
        MarkDirty();
    }

    /// <summary>
    /// The document as it would be written: RTF, with the window's own text
    /// colour taken back out.
    ///
    /// WPF records the colour the text is drawn in, inherited or not, so the
    /// near-white this window draws in has to come out again - otherwise the
    /// file opens as near-white on white in WordPad. See NotesRtf.
    ///
    /// Pictures go out as the file has always held them - the small preview at
    /// its own width - however wide and sharp they are on screen. See NotesPictures.
    ///
    /// Through NotesPictures.Serialise, which writes the pictures from what it
    /// learned at the last full save rather than encoding each one again - the
    /// part that made saving notes full of screenshots stall typing.
    /// </summary>
    private byte[] Serialise()
    {
        byte[] rtf = _pictures.Serialise(() =>
        {
            using var stream = new MemoryStream();
            Contents.Save(stream, DataFormats.Rtf);
            return stream.ToArray();
        });

        // After the pictures are put back, not inside the save: the colour
        // table is the document's, and stripping it before the stand-ins were
        // swapped would make the two saves NotesPictures compares differ.
        return NotesRtf.WithoutColour(rtf, _theme.NotesTextColor);
    }

    public void SaveNow()
    {
        SavePlacementNow();   // whatever a drag left pending goes down with it
        _autoSave.Stop();

        // Nothing has been loaded yet, so what the editor holds is the blank
        // document it was built with - it belongs to no file and must never be
        // written to one.
        //
        // This is not hypothetical. The constructor's first LoadGame calls
        // SaveNow before loading anything, and from 1.20.1 to 1.28.2 that call
        // compared the blank document with a baseline that did not exist yet,
        // judged them different, and wrote the blank document over
        // General.rtf. Every start of DeskMon emptied the General notes.
        if (_saved is null)
            return;

        try
        {
            var timer = Stopwatch.StartNew();
            byte[] rtf = Serialise();
            PaceAutoSave(timer.Elapsed);

            // Whether to write is decided by the content, not by _dirty.
            //
            // Trusting the flag is what lost formatting: WPF does not reliably
            // raise TextChanged for a change that only alters how text looks,
            // so an edit nothing had announced was skipped here and the file
            // kept its old formatting. The flag still decides *when* to try -
            // it runs the autosave clock - but what gets written is settled by
            // comparing the document with what the file already holds.
            //
            // It also means an edit typed and undone writes nothing, where it
            // used to rewrite the file with identical content.
            if (_saved is not null && rtf.AsSpan().SequenceEqual(_saved))
            {
                _dirty = false;
                ShowStatus(RestingText());   // typed and undone: nothing to save
                return;
            }

            // Before the first write after a load, keep a copy of what the file
            // held. Done here rather than on load, so merely opening notes
            // never writes anything - and only when the file had something in
            // it, so a run of empty saves can never push the last good copy out.
            // From the bytes in hand, on the writer thread - see BackupLater.
            if (_backupBeforeWrite)
            {
                if (_onDisk is { } original)
                    _store.BackupLater(_currentGame, original);
                _backupBeforeWrite = false;
            }

            // Pictures count as content. They add no characters to the text,
            // so new notes that held only a screenshot - taken in a game with
            // no notes yet - read as empty, were never written, and lost the
            // picture while the status line said they were saved.
            string? error = _store.SaveRtf(_currentGame, rtf,
                                           hasContent: !string.IsNullOrWhiteSpace(Contents.Text)
                                                       || NotesPictures.HasAny(Editor.Document));

            _dirty = error is not null;
            if (error is null)
            {
                _saved = rtf;
                _onDisk = rtf;   // what the next backup (after Clear history) copies
            }

            // Date as well as time. Notes outlive the session they were written
            // in, so "Saved 21:04" is no use the next day - it reads as though
            // the file was touched this evening when it may be weeks old.
            if (error is null)
                ShowStatus(SavedNowText());
            else
                ShowStatus("Save failed: " + error, problem: true);
        }
        catch (Exception ex)
        {
            _dirty = true;
            ShowStatus("Save failed: " + ex.Message, problem: true);
        }
    }

    /// <summary>The autosave's wait while saving is quick, and the longest it may stretch to.</summary>
    private static readonly TimeSpan QuickestAutoSave = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan SlowestAutoSave = TimeSpan.FromSeconds(8);

    /// <summary>
    /// Waits longer before the next autosave when the last one took a while.
    ///
    /// A save has to turn the whole document into RTF to tell whether anything
    /// changed, and WPF re-encodes every picture as PNG while doing it - with
    /// twenty screenshots that is a pause of 100 ms or more on the UI thread,
    /// and it came 1.5 s into every break in typing. Twenty times the last
    /// save's cost, between 1.5 and 8 s, makes those pauses rarer without
    /// leaving much unsaved: leaving the window, switching notes and closing
    /// still save straight away, whatever the wait.
    /// </summary>
    private void PaceAutoSave(TimeSpan lastSave)
    {
        var wait = TimeSpan.FromTicks(Math.Clamp(lastSave.Ticks * 20, QuickestAutoSave.Ticks, SlowestAutoSave.Ticks));
        if (Math.Abs((wait - _autoSave.Interval).TotalMilliseconds) > 100)
            _autoSave.Interval = wait;
    }

    // --- What the status line says ------------------------------------------
    //
    // Every message says plainly whether what was just done worked - "Loaded
    // successfully", "Saved successfully" - and what the notes are like now.
    // The time of the last save comes after it, because notes outlive the
    // session they were written in: opening month-old notes says how old they
    // are, which is the first thing worth knowing about them.

    /// <summary>Opened from their file: "Loaded successfully · last saved 19 Oct, 02:52:03".</summary>
    private string LoadedText(string game)
        => _store.LastSaved(game) is DateTime when
            ? $"Loaded successfully  ·  last saved {When(when)}"
            : "Loaded successfully";

    /// <summary>Just written: "Saved successfully · 20 Oct, 01:14:07".</summary>
    private static string SavedNowText() => $"Saved successfully  ·  {When(DateTime.Now)}";

    /// <summary>
    /// How the open notes stand when nothing in particular just happened:
    /// typing not yet saved, everything saved and when, or never saved at all.
    /// For putting the line back after a message that has stopped being true.
    /// </summary>
    private string RestingText()
    {
        if (_dirty)
            return "Unsaved changes…";

        return _store.LastSaved(_currentGame) is DateTime when
            ? $"All changes saved  ·  last saved {When(when)}"
            : "New notes  ·  nothing saved yet";
    }

    /// <summary>
    /// "19 Oct, 02:52:03". Always with the day, since a window left open
    /// overnight would otherwise show yesterday's save as today's; the year
    /// only once it isn't this one.
    /// </summary>
    private static string When(DateTime when)
        => when.ToString(when.Year == DateTime.Today.Year ? "d MMM, HH:mm:ss" : "d MMM yyyy, HH:mm",
                         CultureInfo.InvariantCulture);

    /// <summary>The message is about something that went wrong - drawn in the warning colour.</summary>
    private bool _statusProblem;

    /// <summary>
    /// What happened, then the file it happened to, with its whole path - so
    /// the line says where the notes were saved, not just what they are called.
    /// A <paramref name="problem"/> - a failed save, a file that couldn't be
    /// read, a delete waiting for its second click - is drawn in the warning
    /// colour, so it can't be read past as one more "saved" message.
    /// </summary>
    private void ShowStatus(string state, bool problem = false)
    {
        // Typing calls this on every keystroke with the same "Unsaved changes…",
        // and refitting the line measures the path over and over. Only for the
        // same notes, though: two notes can share a message and not a path.
        if (state == _statusState && problem == _statusProblem && _currentGame == _statusGame)
            return;

        _statusState = state;
        _statusProblem = problem;
        _statusGame = _currentGame;
        FitStatus();
    }

    /// <summary>The notes the status line was last written for - see <see cref="ShowStatus"/>.</summary>
    private string? _statusGame;

    /// <summary>The label before every message.</summary>
    private const string StatusLabel = "Status:  ";

    /// <summary>
    /// The space the path button takes besides the path itself: its padding,
    /// the folder icon and the ↗, with their margins. Measured from the button
    /// once it has been laid out; this is only the first guess.
    /// </summary>
    private double _pathChrome = 40;

    /// <summary>
    /// Writes the status line at the width it has: the state, then the path
    /// on its button in whatever room is left. The full path used to be cut
    /// off at the end, which loses the file name - the one part worth reading
    /// - so folders are dropped from the middle instead, after the drive:
    /// <c>E:\…\app\notes\The Witcher 3.rtf</c>. The whole path is on hover
    /// either way.
    /// </summary>
    private void FitStatus()
    {
        string path = _store.PathFor(_currentGame);
        PathButton.ToolTip = path + "\nShow it in Explorer.";

        // "Status:" dim, the message a shade brighter - or in the warning
        // colour when something went wrong.
        StatusText.Inlines.Clear();
        // Spelt out: this window has a Run method of its own, for the toolbar.
        StatusText.Inlines.Add(new System.Windows.Documents.Run(StatusLabel) { Foreground = _theme.Header });
        StatusText.Inlines.Add(new System.Windows.Documents.Run(_statusState)
        {
            Foreground = _statusProblem ? _theme.Warn : _theme.Label,
        });

        if (PathButton.ActualWidth > 0 && PathText.ActualWidth > 0)
            _pathChrome = PathButton.ActualWidth - PathText.ActualWidth;

        // Measured rather than read from the text's ActualWidth: the state was
        // only just set, and the layout hasn't caught up with it yet.
        double room = StatusBar.ActualWidth - Width(StatusLabel + _statusState, StatusText)
                      - PathButton.Margin.Left - PathButton.Margin.Right - _pathChrome;

        string root = Path.GetPathRoot(path) ?? "";
        string[] folders = (Path.GetDirectoryName(path) ?? "")[root.Length..]
            .Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
        string file = Path.GetFileName(path);

        // Not laid out yet: the full path, and SizeChanged fits it once it is.
        string text = path;
        bool laidOut = StatusBar.ActualWidth > 0;
        for (int drop = 1; laidOut && drop <= folders.Length && Width(text, PathText) > room; drop++)
            text = root + "…" + Path.DirectorySeparatorChar
                 + string.Concat(folders.Skip(drop).Select(f => f + Path.DirectorySeparatorChar)) + file;

        PathText.Text = text;

        // Still too wide with every folder gone: TextTrimming cuts the rest -
        // which needs a limit, since a StackPanel would otherwise let it run on.
        PathText.MaxWidth = laidOut ? Math.Max(0, room) : double.PositiveInfinity;

        static double Width(string s, TextBlock font) => new FormattedText(
            s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
            new Typeface(font.FontFamily, font.FontStyle, font.FontWeight, font.FontStretch),
            font.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(font).PixelsPerDip).Width;
    }

    private void PathButton_Click(object sender, RoutedEventArgs e) => RevealNotesFile();

    /// <summary>
    /// Opens the notes folder with the open note's file selected - or just the
    /// folder, for notes that have not been written to disk yet.
    /// </summary>
    private void RevealNotesFile()
    {
        string path = _store.PathFor(_currentGame);
        string args = File.Exists(path)
            ? $"/select,\"{path}\""
            : $"\"{Path.GetDirectoryName(path)}\"";
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch { /* nothing useful to do */ }
    }

    // --- Checkboxes and links in the editor --------------------------------

    /// <summary>
    /// A click on an item's box ticks it rather than moving the caret; a
    /// Ctrl+click on a link opens it. Everything else is an ordinary click.
    /// </summary>
    private void Editor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var point = e.GetPosition(Editor);

        if (Checklist.BoxAt(Editor, point) is { } item)
        {
            Checklist.Toggle(Editor, item);
            MarkDirty();
            e.Handled = true;
            return;
        }

        // A picture opens on a plain click: there is no text inside it to put
        // a caret in, so the click has nothing else to mean - except on its
        // bin, which asks DELETE? on the first click and deletes on the second.
        if (PictureLinkAt(point) is { } picture)
        {
            e.Handled = true;

            if (ImageIn(picture) is { } image && _pictures.IsOnBin(image, point)
                && picture.NavigateUri is { } uri && NotesPictures.LocalPictureFile(uri) is { } file)
            {
                if (!_pictures.IsArmed(image))
                {
                    _pictures.Arm(image);
                    return;
                }

                _pictures.Disarm();
                if (DeletePicture(file) is { } error)
                    ShowStatus($"Couldn't delete the picture: {error}", problem: true);
                return;
            }

            _pictures.Disarm();   // a click on the picture itself lets an armed bin go
            OpenLink(picture);
            return;
        }

        // Ctrl, as in Word: a plain click has to stay free to put the caret
        // inside the link, or its text could never be edited.
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && LinkAt(point) is { } link)
        {
            OpenLink(link);
            e.Handled = true;
        }
    }

    private bool _handCursor;

    /// <summary>
    /// A hand over anything clickable, so the boxes and links say they are -
    /// and over a picture, its outline lit as in the pictures window.
    /// </summary>
    private void Editor_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        var point = e.GetPosition(Editor);
        var picture = PictureLinkAt(point);
        var image = picture is null ? null : ImageIn(picture);
        _pictures.Hover(image);
        _pictures.IsOnBin(image, point, hover: true);   // the bin's plate lit while pointed at

        bool hand = Checklist.BoxAt(Editor, point) is not null
                    || picture is not null
                    || (Keyboard.Modifiers.HasFlag(ModifierKeys.Control) && LinkAt(point) is not null);

        if (hand == _handCursor)
            return;   // set only on a change: this runs for every pixel the mouse moves

        _handCursor = hand;
        if (hand)
            Editor.Cursor = Cursors.Hand;
        else
            Editor.ClearValue(CursorProperty);   // back to the editor's own I-beam
    }

    /// <summary>
    /// Enter continues a checklist; Ctrl+Enter ticks the item the caret is in,
    /// for ticking things off without reaching for the mouse.
    /// </summary>
    private void Editor_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Ctrl+K, as in Word and most editors, so a link doesn't need the mouse.
        if (e.Key == Key.K && Keyboard.Modifiers == ModifierKeys.Control)
        {
            LinkPopup.IsOpen = true;
            e.Handled = true;
            return;
        }

        // Ctrl+Shift like the two list shortcuts, since the to-do list sits next
        // to them; T and C because plain Ctrl+T/C are taken or expected elsewhere.
        if (Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (e.Key == Key.T)
            {
                TodoPopup.IsOpen = true;
                e.Handled = true;
                return;
            }
            if (e.Key == Key.C)
            {
                // Through the click handler so the picker starts on the selection's colour.
                MoreColors_Click(MoreColorsButton, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
        }

        if (e.Key != Key.Enter)
            return;

        if (Keyboard.Modifiers == ModifierKeys.None && Checklist.HandleEnter(Editor))
        {
            MarkDirty();
            e.Handled = true;
        }
        else if (Keyboard.Modifiers == ModifierKeys.Control
                 && Editor.CaretPosition.Paragraph is { } paragraph
                 && Checklist.IsItem(paragraph, out _, out _, out _))
        {
            Checklist.Toggle(Editor, paragraph);
            MarkDirty();
            e.Handled = true;
        }
    }

    private Hyperlink? LinkAt(Point point) => LinkAt(point, pictureOnly: false);

    /// <summary>
    /// A link whose content at this point is a picture - a screenshot's
    /// preview. Found through the text position, not by hit-testing the
    /// Image: pictures inside an editable RichTextBox take no mouse input of
    /// their own. Tested: the position over any part of the picture leads
    /// up through its InlineUIContainer to the Hyperlink, and a point in the
    /// empty space beside it finds nothing.
    /// </summary>
    private Hyperlink? PictureLinkAt(Point point) => LinkAt(point, pictureOnly: true);

    /// <summary>The picture a picture's link is wrapped round.</summary>
    private static Image? ImageIn(Hyperlink link) => (link.Inlines.FirstInline as InlineUIContainer)?.Child as Image;

    private Hyperlink? LinkAt(Point point, bool pictureOnly)
    {
        DependencyObject? node = Editor.GetPositionFromPoint(point, snapToText: false)?.Parent;
        bool overPicture = false;

        while (node is TextElement element)
        {
            if (element is InlineUIContainer container)
            {
                // The text position covers the picture's whole box, and that
                // box includes the space NotesPictures puts round it - above,
                // below, and the strip to its left that centres it. Without
                // this, a click in that empty space opened the picture too.
                if (container.Child is not FrameworkElement picture || !IsOver(picture, point))
                    return null;

                overPicture = true;
            }

            if (element is Hyperlink link)
                return !pictureOnly || overPicture ? link : null;

            node = element.Parent;
        }

        return null;
    }

    /// <summary>Whether the point, in the editor's coordinates, is on the picture itself - not its margin.</summary>
    private bool IsOver(FrameworkElement picture, Point point)
    {
        try
        {
            var bounds = picture.TransformToAncestor(Editor)
                                .TransformBounds(new Rect(picture.RenderSize));
            return bounds.Contains(point);
        }
        catch (InvalidOperationException)
        {
            return false;   // not in the editor's visual tree - nothing on screen to click
        }
    }

    /// <summary>
    /// Opens a link: a screenshot's full-size file, or a web page. Nothing
    /// else - a link pasted in from elsewhere could point at a program, and a
    /// click in a notepad should never run one.
    /// </summary>
    private void OpenLink(Hyperlink link)
    {
        if (link.NavigateUri is not { IsAbsoluteUri: true } uri)
            return;

        try
        {
            if (uri.IsFile)
            {
                // A picture on this PC only - see LocalPictureFile for why a
                // network one is never opened.
                if (NotesPictures.LocalPictureFile(uri) is not { } path)
                {
                    ShowStatus("Not opened - only .png and .jpg pictures on this PC open from the notes, not files on a network share", problem: true);
                    return;
                }

                // In DeskMon's own viewer - which says so itself if the file
                // has been moved or deleted. "Open in Photos app" on the
                // right-click menu still goes to Windows'.
                ViewPicture(path, markUp: false);
            }
            else if (uri.Scheme is "http" or "https")
            {
                Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            ShowStatus("Could not open the link: " + ex.Message, problem: true);
        }
    }

    // --- New and delete ------------------------------------------------------
    //
    // Notes of your own beside the per-game ones - a name in the picker like
    // any game's - and a way to be rid of any of them. General can't go: it is
    // where the notes fall back to with no game running, so deleting it clears
    // it instead.

    private static bool IsGeneral(string game)
        => string.Equals(game, NotesStore.GeneralNotes, StringComparison.OrdinalIgnoreCase);

    private void NewNote_Click(object sender, RoutedEventArgs e) => NewNotePopup.IsOpen = true;

    /// <summary>
    /// Empty each time, rather than the last name: that note exists now, and
    /// opening it again is what the picker is for. Focus deferred, as for the
    /// link popup - nothing in it can take focus while it is opening.
    /// </summary>
    private void NewNotePopup_Opened(object? sender, EventArgs e)
    {
        NewNoteName.Text = string.Empty;
        NewNoteHint.Text = "Shows in the list beside the games' notes.";
        NewNoteHint.Foreground = _theme.Label;

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => Keyboard.Focus(NewNoteName)));
    }

    private void NewNoteName_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            CreateNewNote();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            NewNotePopup.IsOpen = false;
            Editor.Focus();
            e.Handled = true;
        }
    }

    private void NewNoteCreate_Click(object sender, RoutedEventArgs e) => CreateNewNote();

    /// <summary>
    /// Opens a new, empty note under the name typed - or the note already
    /// called that, rather than a second one the file system would refuse.
    ///
    /// Named by its file name, not as typed: "Ideas: boss" is stored as
    /// "Ideas- boss", and the picker lists file names, so keeping the typed
    /// form would show the one note twice.
    ///
    /// Written at once, empty as it is. A save normally skips an empty note
    /// with no file - so launching a game never litters the folder - but a
    /// note made on purpose would then vanish from the list as soon as you
    /// switched away from it before typing.
    /// </summary>
    private void CreateNewNote()
    {
        if (string.IsNullOrWhiteSpace(NewNoteName.Text))
        {
            // Kept open, as the link popup is, rather than closing on a slip.
            NewNoteHint.Text = "Give it a name first.";
            NewNoteHint.Foreground = _theme.Warn;
            return;
        }

        string name = NotesStore.SafeFileName(NewNoteName.Text);
        NewNotePopup.IsOpen = false;

        string? existing = IsGeneral(name)
            ? NotesStore.GeneralNotes
            : _store.ListGames().FirstOrDefault(g => string.Equals(g, name, StringComparison.OrdinalIgnoreCase));

        // Refused: the notes on screen couldn't be saved, and the status line
        // says so. Opened with a copy kept, that message stays up rather than
        // being written over with the news of the new note.
        if (existing is not null)
        {
            if (!LoadGame(existing, mayRefuse: true))
                return;
            if (!_statusProblem)
                ShowStatus($"There already is a note called {existing} - opened it");
        }
        else
        {
            if (!LoadGame(name, mayRefuse: true))
                return;
            if (_saved is not null)
            {
                // The error first: with a copy of the last notes kept, the
                // status is already a problem, and a note created all the same
                // used to be reported as "Could not create the file: " - blank.
                string? error = _store.SaveRtf(name, _saved, hasContent: true);
                if (error is not null)
                    ShowStatus("Could not create the file: " + error, problem: true);
                else if (!_statusProblem)
                    ShowStatus("New note created successfully");
            }
        }

        Editor.Focus();
    }

    /// <summary>
    /// A delete asks twice, like the pictures window's: the first click arms
    /// the bin and says what it is about to delete, a second within this time
    /// does it. In the window, rather than a white Windows dialog on the dark
    /// panel.
    /// </summary>
    private readonly DispatcherTimer _disarmDelete = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _deleteArmed;

    private void DeleteNote_Click(object sender, RoutedEventArgs e)
    {
        if (!_deleteArmed)
        {
            ArmDelete();
            return;
        }

        DisarmDelete();
        DeleteCurrentNote();
    }

    private void ArmDelete()
    {
        _deleteArmed = true;

        // Set on the button, so the bin and the label both turn: the icon
        // strokes in the button's Foreground. A local value outranks the
        // template's hover colour, so it stays orange under the mouse too.
        DeleteNoteButton.Foreground = _theme.Warn;

        // What exactly is about to go, in the status line: the button has
        // room for one word.
        // In the warning colour, like the button: it is a question, waiting.
        ShowStatus(IsGeneral(_currentGame)
            ? "Click CLEAR again to clear General"
            : $"Click DELETE again to delete {_currentGame}",
            problem: true);

        _disarmDelete.Stop();
        _disarmDelete.Start();
    }

    private void DisarmDelete()
    {
        _disarmDelete.Stop();
        if (!_deleteArmed)
            return;

        _deleteArmed = false;
        DeleteNoteButton.ClearValue(ForegroundProperty);

        // The warning would otherwise stay up after it had stopped being true.
        ShowStatus(RestingText());
    }

    /// <summary>
    /// Sends the open note to the Recycle Bin and goes to General - which, when
    /// General was the one deleted, is a fresh empty page.
    /// </summary>
    private void DeleteCurrentNote()
    {
        string note = _currentGame;
        bool general = IsGeneral(note);

        // Saved first, so what lands in the Recycle Bin is the note as it was
        // on screen - not as of the last autosave, up to 1.5 s of typing behind.
        SaveNow();

        if (_store.Delete(note) is { } error)
        {
            // The file is still there and saved, so carrying on as before is right.
            ShowStatus("Could not delete: " + error, problem: true);
            return;
        }

        // The editor still holds the deleted note, and LoadGame saves what is
        // on screen first. That save would find nothing changed and skip - but
        // a null baseline makes it certain the note isn't written straight back.
        _saved = null;
        LoadGame(NotesStore.GeneralNotes);

        ShowStatus(general
            ? "General cleared successfully - the old notes are in the Recycle Bin"
            : $"Deleted {note} successfully - it is in the Recycle Bin");
    }

    // --- Search ------------------------------------------------------------
    //
    // A box always on show at the right of the action row, searched as you
    // type: the matches drop down under it with every keystroke, with nothing
    // to open first and no Enter to press.

    /// <summary>
    /// How many re-reads for searching are under way - see
    /// <see cref="SearchBox_GotKeyboardFocus"/>. A count, not a flag: leaving
    /// the box and coming back starts a second before the first is done, and
    /// the first finishing must not say "No notes contain that" while the
    /// second is still reading.
    /// </summary>
    private int _readingNotes;

    /// <summary>Ctrl+F anywhere in the window, as in every other program: into the box, ready to type over.</summary>
    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && Keyboard.Modifiers == ModifierKeys.Control)
        {
            FocusSearch();
            e.Handled = true;
        }
    }

    /// <summary>
    /// Saves first, so the notes on screen are searched as they are now, then
    /// re-reads whichever notes changed since the last search. Once each time
    /// the box is entered rather than on every keystroke: a re-read loads
    /// files, while the search itself is an in-memory scan. A query left in
    /// the box from last time drops its matches down again straight away.
    /// </summary>
    private async void SearchBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (_dirty)
            SaveNow();

        _readingNotes++;
        ShowSearchResults();

        try
        {
            await _search.RefreshAsync();
        }
        catch (Exception ex)
        {
            SearchResults.Children.Clear();
            SearchHint.Text = "Could not read the notes: " + ex.Message;
            return;
        }
        finally
        {
            _readingNotes--;
        }

        // Only if still searching: the box may have been left while the
        // notes were being read, and the list closed with it.
        if (SearchBox.IsKeyboardFocused)
            ShowSearchResults();
    }

    /// <summary>
    /// Leaving the box puts the list away - unless it is the list being
    /// clicked, which is how a match is picked.
    /// </summary>
    private void SearchBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!SearchPanel.IsMouseOver)
            SearchPopup.IsOpen = false;
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchPlaceholder.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        ShowSearchResults();
    }

    private void SearchIcon_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.Focus(SearchBox);

        // The click closed the list on its way here (the window's
        // PreviewMouseDown), and with the box already focused, focusing it
        // again doesn't open it - so it is opened here.
        ShowSearchResults();
        e.Handled = true;
    }

    private void SearchBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            // Clears first, which also closes the list; a second Esc goes
            // back to the notes.
            if (SearchBox.Text.Length > 0)
                SearchBox.Clear();
            else
                Editor.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter
                 && SearchBox.Text.Trim() is { Length: >= 2 } query   // as the list: one letter isn't a search
                 && _search.Find(query, max: 1) is { Count: > 0 } first)
        {
            OpenHit(first[0], query);
            e.Handled = true;
        }
    }

    /// <summary>
    /// Drops the list down from the box's left edge, out over the page. The
    /// box used to be at the window's right, where the list had to be lined
    /// up on its right edge instead; in the notes list it is at the left, and
    /// the list's own placement is the right one.
    /// </summary>
    private void AlignSearchPopup() => SearchPopup.HorizontalOffset = 0;

    /// <summary>
    /// The matches, each with its game and the line it is on, the matched
    /// part in the brighter text colour so the eye finds it. An empty box
    /// closes the list.
    /// </summary>
    private void ShowSearchResults()
    {
        SearchResults.Children.Clear();
        string query = SearchBox.Text.Trim();

        if (query.Length == 0)
        {
            SearchPopup.IsOpen = false;
            return;
        }

        AlignSearchPopup();
        SearchPopup.IsOpen = true;

        // One letter matches nearly every note, and a list of all of them
        // answers nothing.
        if (query.Length < 2)
        {
            SearchHint.Text = "Type one more letter to search every note.";
            return;
        }

        var hits = _search.Find(query);
        SearchHint.Text = hits.Count switch
        {
            // The first search has nothing to look in until the notes are read.
            0 when _readingNotes > 0 => "Reading notes…",
            0 => "No notes contain that.",
            1 => "1 match - Enter opens it",
            >= 60 => "The first 60 matches - Enter opens the first",
            _ => $"{hits.Count} matches - Enter opens the first",
        };

        double small = Math.Max(9, UiTheme.PanelFontSize - 3);

        foreach (var hit in hits)
        {
            var line = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                Foreground = _theme.Label,
                FontSize = small + 1,
            };
            line.Inlines.Add(new Run(hit.Before));
            line.Inlines.Add(new Run(hit.Match) { Foreground = _theme.NotesText, FontWeight = FontWeights.SemiBold });
            line.Inlines.Add(new Run(hit.After));

            var content = new StackPanel();
            content.Children.Add(new TextBlock
            {
                Text = hit.Game,
                Foreground = _theme.Section,
                FontWeight = FontWeights.SemiBold,
                FontSize = small,
            });
            content.Children.Add(line);

            var row = new Border
            {
                Background = Brushes.Transparent,
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6, 4, 6, 4),
                Cursor = Cursors.Hand,
                Child = content,
            };

            row.MouseEnter += (_, _) => row.Background = _theme.Separator;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;

            var captured = hit;
            row.MouseLeftButtonUp += (_, _) => OpenHit(captured, query);

            SearchResults.Children.Add(row);
        }
    }

    /// <summary>
    /// Opens the game the match is in and selects the match, scrolled to a
    /// third of the way down so there is context above it.
    /// </summary>
    private void OpenHit(NotesSearch.Hit hit, string query)
    {
        SearchPopup.IsOpen = false;

        // Refused, there is nothing to select: the match is in other notes.
        if (!string.Equals(hit.Game, _currentGame, StringComparison.OrdinalIgnoreCase)
            && !LoadGame(hit.Game, mayRefuse: true))
            return;

        // Laid out first, or there are no positions yet to scroll to.
        Editor.UpdateLayout();

        if (NotesSearch.Locate(Editor.Document, query, hit.Occurrence) is not { } found)
            return;

        Editor.Focus();
        Editor.Selection.Select(found.Start, found.End);

        Rect at = found.Start.GetCharacterRect(LogicalDirection.Forward);
        if (!at.IsEmpty)
            Editor.ScrollToVerticalOffset(Math.Max(0, Editor.VerticalOffset + at.Top - Editor.ViewportHeight / 3));
    }

    // --- Screenshots -------------------------------------------------------

    /// <summary>
    /// The camera button. The capture itself is the widget's job: it knows
    /// which window is the game, and it has windows of its own to keep out of
    /// the picture.
    /// </summary>
    public event Action? ScreenshotRequested;

    private void Screenshot_Click(object sender, RoutedEventArgs e) => ScreenshotRequested?.Invoke();

    /// <summary>
    /// Names the hotkey as it is set now, read as the tooltip opens: it can be
    /// changed or turned off in Settings while this window is open.
    /// </summary>
    private void ScreenshotButton_ToolTipOpening(object sender, ToolTipEventArgs e)
        => ScreenshotButton.ToolTip = string.IsNullOrWhiteSpace(_settings.ScreenshotHotkey)
            ? "Insert a screenshot\nTakes a picture of the game and puts it in the notes at the cursor."
            : $"Insert a screenshot ({_settings.ScreenshotHotkey})\nTakes a picture of the game and puts it in the notes at the cursor - in a game, press the key.";

    /// <summary>
    /// Adds a note written in the card shown as a game quits - see
    /// SessionNotePrompt - to the end of that game's notes, under a heading
    /// naming the session. At the end, like a journal, so the notes read in
    /// the order they were played.
    ///
    /// Into the editor only when the notes on screen are that game's: written
    /// to the file behind them, they would be lost on the window's next save.
    /// Any other game's go straight into its file - see AppendToFile. That
    /// used to switch the window to the game and back: two loads of notes
    /// full of screenshots on the UI thread, freezing every window as the
    /// note was saved, and the switch put the game that had just quit under
    /// PLAYING NOW - or took an open window off the game that is running.
    /// </summary>
    /// <returns>Null when it went in, or why it couldn't.</returns>
    public async Task<string?> AppendSessionNoteAsync(string game, string heading, string text)
    {
        if (ShowsNotesOf(game))
            return AppendToEditor(heading, text);

        string? error = await Task.Run(() => AppendToFile(_store, _theme.AccentColor, game, heading, text));

        // Switched to this game while the file was written: it may have
        // loaded the notes without the entry, and saving them would drop it.
        // Loaded again - with nothing typed yet there is nothing to lose.
        if (error is null && ShowsNotesOf(game) && !_dirty)
            LoadGame(game);
        return error;
    }

    /// <summary>
    /// <see cref="AppendSessionNoteAsync"/> waiting for the disk, for the way
    /// out - see MainWindow.SaveTypedSessionNote - where no later moment comes.
    /// </summary>
    public string? AppendSessionNote(string game, string heading, string text)
        => ShowsNotesOf(game) ? AppendToEditor(heading, text) : AppendToFile(_store, _theme.AccentColor, game, heading, text);

    /// <summary>Whether the editor holds <paramref name="game"/>'s notes, loaded and so the ones its next save writes.</summary>
    private bool ShowsNotesOf(string game) => _saved is not null && SameNote(_currentGame, game);

    /// <summary>The entry put into the notes on screen, and saved at once.</summary>
    private string? AppendToEditor(string heading, string text)
    {
        var document = Editor.Document;

        // A gap above the heading unless the notes are empty or already end
        // in a blank line, so it reads as a new entry, not part of the last.
        if (document.Blocks.LastBlock is Paragraph last && new TextRange(last.ContentStart, last.ContentEnd).Text.Trim().Length > 0)
            document.Blocks.Add(new Paragraph());

        // In the accent and bold, like a link stands out - set on the run, so
        // it is saved in the RTF and the heading still stands out in WordPad.
        document.Blocks.Add(new Paragraph(new Run(heading)
        {
            FontWeight = FontWeights.SemiBold,
            Foreground = _theme.Accent,
        }));
        foreach (string line in NoteLines(text))
            document.Blocks.Add(new Paragraph(new Run(line)));

        MarkDirty();
        SaveNow();
        bool saved = !_dirty;

        if (IsOpen)
            ShowStatus(saved ? "Session note added at the end" : "Session note added - not saved yet", problem: !saved);
        return saved ? null : "the notes couldn't be saved";
    }

    /// <summary>
    /// The entry added to the end of a game's notes file, for notes that
    /// aren't on screen - or with no notes window at all. On a worker, or on
    /// the way out: file work only, no WPF. The file is backed up first, as
    /// the window's own first save after a load does. Notes still in an old
    /// .txt get it as their first lines, so the new .rtf - which is read in
    /// its place from now on - keeps them.
    /// </summary>
    internal static string? AppendToFile(NotesStore store, Color accent, string game, string heading, string text)
    {
        byte[]? rtf = store.LoadRtf(game);
        var lines = NoteLines(text);
        if (rtf is not { Length: > 0 } && store.LoadLegacyText(game) is { Length: > 0 } legacy)
            lines = NoteLines(legacy).Append(string.Empty).Concat(lines).ToArray();

        if (NotesRtf.WithParagraphsAdded(rtf, heading, accent, lines, DefaultTextSize) is not { } added)
            return "the notes file couldn't be read as RTF";

        if (rtf is { Length: > 0 })
            store.BackupLater(game, rtf);
        return store.SaveRtf(game, added, hasContent: true);
    }

    /// <summary>A note's lines, however its line breaks were typed.</summary>
    private static string[] NoteLines(string text) => text.Replace("\r\n", "\n").Split('\n');

    /// <summary>
    /// Puts a screenshot into the notes: the preview, itself a link to the
    /// full-size file and with the time stamped in its corner (see
    /// Screenshot.StampTime), then an empty line to carry on typing under it.
    ///
    /// The picture is the link, rather than an "open full size" link under
    /// it: the picture is what you'd click anyway, and a link inside the RTF
    /// wrapped around an image survives saving and loading - checked, it comes
    /// back as the same Hyperlink around the same image.
    ///
    /// At the caret when you are working in the notes - the camera button -
    /// and at the end otherwise: from the hotkey in a game, the caret is
    /// wherever it was last left, perhaps mid-sentence, and a picture dropped
    /// in there would split it.
    /// </summary>
    public void InsertScreenshot(BitmapSource preview, string fullPath, DateTime takenAt)
        => InsertPicture(preview, fullPath,
                         $"Screenshot added successfully - click it for full size, saved in {Path.GetDirectoryName(fullPath)}");

    /// <summary>
    /// Puts a picture taken out of the notes back in, from the pictures
    /// window: the file is still in the game's folder, so the preview is made
    /// again from it - the same size and time stamp as when it was taken, so
    /// it comes back looking as it did. The time is the one in the file's
    /// name, which is when it was taken; the file's own date is a fallback for
    /// a picture dropped into the folder by hand.
    ///
    /// At the end of the notes, as a screenshot from the hotkey goes: the
    /// pictures window has the focus, so the caret is wherever it was last
    /// left. Cut and paste moves it from there.
    /// </summary>
    /// <returns>Null when it went in, or why it couldn't.</returns>
    public async Task<string?> AddPictureBack(string fullPath)
    {
        // The picture is from the open game's folder; remembered so that
        // switching notes while the preview is made can't put it in another's.
        string game = _currentGame;
        DateTime takenAt = NotesPictures.TimeFromFileName(fullPath) ?? File.GetLastWriteTime(fullPath);

        BitmapSource preview;
        try
        {
            // Read whole and checked before it is decoded, so the file isn't
            // held open and nothing but a plain PNG or JPEG reaches GDI+ in
            // this elevated process - see Screenshot.LoadChecked.
            preview = await Task.Run(() =>
            {
                using var bitmap = Screenshot.LoadChecked(fullPath);
                return Screenshot.Preview(bitmap, takenAt);
            });
        }
        catch (Exception ex)
        {
            return ex.Message;
        }

        if (!string.Equals(game, _currentGame, StringComparison.OrdinalIgnoreCase))
            return $"the notes switched to {_currentGame} - open {game} and try again";

        // Shown if it had been put away, so the picture is seen going back in.
        if (!IsOpen)
            Show();

        InsertPicture(preview, fullPath, $"{Path.GetFileName(fullPath)} added back successfully, at the end of the notes");
        return null;
    }

    /// <summary>
    /// A picture's file was marked up in place - here, or in the pictures
    /// window. The notes keep a preview of it inside their own file, so that
    /// is made again from the new file, as AddPictureBack makes one, and the
    /// notes saved with it: the marks show here, and in the RTF.
    /// </summary>
    public async void PictureReplaced(string fullPath)
    {
        DateTime takenAt = NotesPictures.TimeFromFileName(fullPath) ?? File.GetLastWriteTime(fullPath);

        BitmapSource preview;
        try
        {
            // Checked before it is decoded - see Screenshot.LoadChecked.
            preview = await Task.Run(() =>
            {
                using var bitmap = Screenshot.LoadChecked(fullPath);
                return Screenshot.Preview(bitmap, takenAt);
            });
        }
        catch (Exception ex)
        {
            ShowStatus($"The marked picture couldn't be shown in the notes: {ex.Message}", problem: true);
            return;
        }

        if (_pictures.Replace(fullPath, preview))
        {
            MarkDirty();
            ShowStatus($"{Path.GetFileName(fullPath)} marked up - the notes show it as it is now");
        }
    }

    // --- Right-click on a picture ----------------------------------------------

    /// <summary>
    /// A picture's own menu - view it, mark it up, copy it - instead of the
    /// editor's Cut/Copy/Paste, which a picture has little use for. Anywhere
    /// else in the notes the editor's menu opens as it always has.
    /// </summary>
    private void Editor_PreviewMouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // A picture on this PC only: copy, mark up and Photos all open the
        // file, and a network one must not be - see LocalPictureFile.
        if (PictureLinkAt(e.GetPosition(Editor)) is not { NavigateUri: { } uri }
            || NotesPictures.LocalPictureFile(uri) is not { } path)
            return;   // text, or a picture that isn't a screenshot: the editor's own menu

        // Handled, so the editor's menu doesn't open as well.
        e.Handled = true;

        var menu = new ContextMenu
        {
            Style = (Style)FindResource("DarkContextMenu"),
            PlacementTarget = Editor,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
        };

        void Add(string header, Action click)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => click();
            menu.Items.Add(item);
        }

        Add("View", () => ViewPicture(path, markUp: false));
        Add("Mark up…", () => ViewPicture(path, markUp: true));
        Add("Copy picture", () =>
        {
            if (PictureClipboard.Copy(path) is { } error)
                ShowStatus($"Couldn't copy the picture: {error}", problem: true);
            else
                ShowStatus("Picture copied - paste it anywhere");
        });
        menu.Items.Add(new Separator());
        Add("Open in Photos app", () =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ShowStatus("Could not open the picture: " + ex.Message, problem: true);
            }
        });

        // No Delete here: the picture's own bin, in its corner, deletes - the
        // same bin and the same two clicks as in the pictures window.
        menu.IsOpen = true;
    }

    /// <summary>The picture in DeskMon's viewer - straight into mark-up for "Mark up…".</summary>
    private void ViewPicture(string path, bool markUp)
    {
        if (!File.Exists(path))
        {
            ShowStatus($"{Path.GetFileName(path)} isn't there any more - it may have been moved or deleted", problem: true);
            return;
        }

        string folder = Path.GetDirectoryName(path)!;
        var viewer = new PictureViewer(
            _theme, new[] { path }, 0, this,
            isFavourite: p => PictureFavourites.Load(folder).Contains(Path.GetFileName(p)),
            toggleFavourite: p => PictureFavourites.Toggle(folder, Path.GetFileName(p)),
            changed: () => PicturesChanged?.Invoke(),   // the pictures window lists the change
            replaced: PictureReplaced,
            delete: DeletePicture);

        if (markUp)
            viewer.MarkUpOnOpen();
        viewer.Show();
    }

    private void InsertPicture(BitmapSource preview, string fullPath, string status)
    {
        bool working = IsActive;
        if (!working)
            Editor.CaretPosition = Editor.Document.ContentEnd;

        // Inserted at the preview's own width - which is what the file stores -
        // and then widened to the window by NotesPictures.
        var image = new Image
        {
            Source = preview,
            Width = preview.PixelWidth,
            Stretch = Stretch.Uniform,
        };

        var picture = new Paragraph(new Hyperlink(new InlineUIContainer(image))
        {
            NavigateUri = new Uri(fullPath),
        });

        // No caption line: the date is stamped into the picture itself.
        var after = new Paragraph();

        InsertBlocks(new Block[] { picture, after }, () => after.ContentStart, focus: working);
        _pictures.Add(image, preview, fullPath);
        PicturesChanged?.Invoke();
        ShowStatus(status);

        // Scrolled to, so it can be seen going in: without the focus, moving
        // the caret doesn't move the view, and a picture added at the end of
        // long notes would land out of sight. After layout, when it has a place.
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(picture.BringIntoView));
    }

    /// <summary>
    /// A message in the status line, for the widget to report a capture that
    /// failed - which is all it is used for, so it shows as a problem.
    /// </summary>
    public void ShowMessage(string message) => ShowStatus(message, problem: true);

    // --- Window lifetime --------------------------------------------------

    /// <summary>
    /// Closing hides instead: a WPF window cannot be shown again once really
    /// closed, and the next game launch needs this one back.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        SaveNow();

        if (_allowClose)
            return;

        e.Cancel = true;
        HideSmoothly();   // banner put out on screen first - see PanelWindow
    }

    /// <summary>Called by the app on shutdown, when closing for real is correct.</summary>
    public void AllowClose() => _allowClose = true;

    private void RestorePlacement()
        => RestorePlacement(_settings.NotesLeft, _settings.NotesTop,
                            _settings.NotesWidth, _settings.NotesHeight);

    /// <summary>Restarts the delay, so the write lands once the window stops moving.</summary>
    private void QueuePlacementSave()
    {
        _placementSave.Stop();
        _placementSave.Start();
    }

    /// <summary>Writes a pending placement change now, if there is one.</summary>
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

        _settings.NotesLeft = Left;
        _settings.NotesTop = Top;
        _settings.NotesWidth = Width;
        _settings.NotesHeight = Height;
        _settings.Save();
    }

    private void ApplyTheme()
    {
        var background = _theme.NotesBackground;
        var text = _theme.NotesText;

        // Same shell as the widget: translucent panel, hairline border.
        Panel.Background = background;
        Panel.BorderBrush = _theme.Border;
        _theme.Dress(Panel);
        Sidebar.BorderBrush = _theme.Separator;
        ListFoot.BorderBrush = _theme.Separator;
        ListHeading.Foreground = _theme.Section;
        ListHeading.FontSize = Math.Max(9, UiTheme.PanelFontSize - 1);
        FitList();   // the list's ground is a theme colour too

        // The window's own text in the theme's font; the notes themselves keep
        // theirs - the editor's font is part of what is saved.
        FontFamily = _theme.PanelFont;
        _pictures.ApplyTheme(_theme);

        // The note's name, a few steps over the text: the drop-down that
        // named it is gone, so this is what says whose notes these are.
        TitleText.Foreground = _theme.NotesText;
        TitleText.FontSize = UiTheme.PanelFontSize + 4;
        NoteDetail.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);

        // The rows carry their colours, so a theme change needs them made again.
        if (NoteList.ItemsSource is not null)
            BuildList();

        Editor.Foreground = text;
        Editor.CaretBrush = text;
        Editor.SelectionBrush = _theme.Section;
        Editor.FontFamily = new FontFamily(UiTheme.PanelFontFamily);
        Editor.FontSize = DefaultTextSize;
        ApplyDocumentDefaults(Editor.Document);

        // Set here, not left to ToolIcon: outside a button, the style's
        // binding to the button's colour has nothing to find.
        SearchIcon.Stroke = _theme.Label;
        SearchPlaceholder.Foreground = _theme.Header;
        StatusText.Foreground = _theme.Header;
        StatusText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);

        // The path the same size as the state beside it. Its colour is left to
        // ToolButton, so it lights up on hover like every other button.
        PathButton.FontSize = StatusText.FontSize;
        FitStatus();   // a new font size changes what fits

        // The panel's shade at a few percent rather than fixed greys, like the
        // controls in PanelStyles: lifted off whatever the window colour is,
        // light or dark. The page a touch fainter than the toolbar and session
        // cards, so the notes - the one thing here that is read - stay the
        // calmest surface.
        var card = _theme.Shade(0x0E);
        ToolbarCard.Background = Opaque(card);
        SessionStrip.Background = card;
        Page.Background = _theme.Shade(0x08);
        SessionHeading.Foreground = _theme.Section;
        SessionHeading.FontSize = Math.Max(9, UiTheme.PanelFontSize - 2.5);
        SessionText.Foreground = _theme.NotesText;
        SessionText.FontSize = Math.Max(9, UiTheme.PanelFontSize - 2.5);

        // Dressed like the colour popup, so the popups read as one family.
        foreach (var panel in new[] { TodoPopupPanel, SearchPanel, LinkPopupPanel, NewNotePanel, HistoryPanel })
        {
            panel.Background = _theme.NotesBackground;
            panel.BorderBrush = _theme.Border;
        }

        HistoryHeading.Foreground = _theme.Section;
        HistoryHint.Foreground = _theme.Label;
        HistoryHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);

        NewNoteHeading.Foreground = _theme.Section;
        NewNoteNameLabel.Foreground = _theme.Label;
        NewNoteHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);

        LinkHeading.Foreground = _theme.Section;
        LinkTextLabel.Foreground = _theme.Label;
        LinkAddressLabel.Foreground = _theme.Label;
        LinkHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);

        SearchHint.Foreground = _theme.Label;
        SearchHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);
        TodoHeading.Foreground = _theme.Section;
        TodoChecksHint.Foreground = _theme.Header;
        TodoChecksHint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);
    }

    /// <summary>
    /// A see-through shade worked out as the solid colour it gives over the
    /// window, for the formatting toolbar: it sits where the game's picture
    /// behind the header (the banner) is still showing, and a bright picture
    /// through the see-through card made the buttons hard to read. Looks the
    /// same as the shade over a plain window, but nothing shows through it.
    /// </summary>
    private Brush Opaque(Brush shade)
    {
        if (shade is not SolidColorBrush { Color: var over })
            return shade;

        double amount = over.A / 255.0;
        var window = _theme.WindowColor;
        byte Channel(byte under, byte top) => (byte)Math.Round(under + (top - under) * amount);
        var solid = new SolidColorBrush(Color.FromRgb(Channel(window.R, over.R), Channel(window.G, over.G), Channel(window.B, over.B)));
        solid.Freeze();
        return solid;
    }

    // --- Last session -----------------------------------------------------

    /// <summary>
    /// Shows the newest line of the open game's session history under its
    /// notes. Nothing under General: those notes belong to no game, so no
    /// session is theirs. It used to show the last game played there, which
    /// put one game's session under notes that had nothing to do with it.
    ///
    /// Safe from any thread, because sessions are finished by the game
    /// watcher, which is not guaranteed to be on this one.
    /// </summary>
    public void RefreshLastSession()
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(new Action(RefreshLastSession));
            return;
        }

        ShowLastSession();

        // A session just written moves its game up the list and changes its
        // line. Not done from a load, whose list refresh reads them already.
        LoadActivityLater(_listed);
    }

    /// <summary>The card itself, for a load - see <see cref="RefreshLastSession"/>.</summary>
    private void ShowLastSession()
    {
        bool general = string.Equals(_currentGame, NotesStore.GeneralNotes, StringComparison.OrdinalIgnoreCase);
        var last = general ? null : _sessions.LastSession(_currentGame);

        if (last is null)
        {
            SessionStrip.Visibility = Visibility.Collapsed;
            return;
        }

        ShowSession(last);
        SessionStrip.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// The last session, readable: when, how long and the average on the
    /// first line, then the worst readings grouped by the widget's sections.
    ///
    /// This used to be the log line as written - a dozen readings run
    /// together with dots, CPU twice with nothing but the unit to tell the
    /// two apart. Grouped, the CPU under Usage is load and the one under
    /// Temperature is heat, the same way the widget shows them.
    /// </summary>
    private void ShowSession(SessionRecord session)
    {
        // Each item glued together with non-breaking spaces, so a narrow
        // window wraps between items - never "39" at the end of one line and
        // "fps average" at the start of the next.
        static string Keep(string text) => text.Replace(' ', ' ');

        // "Last session" itself is in the XAML, in the table's first column.
        SessionText.Inlines.Clear();
        SessionText.Inlines.Add(new Run(
            Keep($"{session.Start.ToString("ddd d MMM", CultureInfo.InvariantCulture)}, {session.Start:HH:mm}–{session.End:HH:mm}") +
            "  ·  " + Keep(SessionRecord.Describe(session.Length))));

        if (session.AverageFps is double fps)
            SessionText.Inlines.Add(new Run("  ·  " + Keep($"{fps:0} fps average")));

        // Only "cut short" is called out: it changes what the numbers mean.
        if (session.CutShort)
            SessionText.Inlines.Add(new Run("  ·  cut short")
            {
                Foreground = _theme.Warn,
                ToolTip = "Cut short\nDeskMon was closed during the game, so the session may have run longer than this.",
            });

        // A table, not a line of text per section: the readings used to run
        // on in one wrapped TextBlock each, so each line's CPU, GPU... started
        // wherever the one before ended. Here each reading's label and value
        // is a column of its own, sized to the widest in it, so the first
        // reading of every line - CPU, CPU, FPS - sits under the others, the
        // second under the second, and so on. The first column is shared with
        // the "Last session" heading above (SessionName in the XAML), so the
        // date lines up with the readings too.
        SessionPeaks.Children.Clear();
        SessionPeaks.RowDefinitions.Clear();
        SessionPeaks.ColumnDefinitions.Clear();

        var sections = session.PeaksBySection().ToList();
        int readings = sections.Count == 0 ? 0 : sections.Max(s => s.Peaks.Count);

        SessionPeaks.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, SharedSizeGroup = "SessionName" });
        for (int i = 0; i < readings * 2; i++)
            SessionPeaks.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        double small = Math.Max(9, UiTheme.PanelFontSize - 3);

        void Cell(string text, Brush brush, int row, int column, Thickness margin)
        {
            var cell = new TextBlock { Text = text, Foreground = brush, FontSize = small, Margin = margin };
            Grid.SetRow(cell, row);
            Grid.SetColumn(cell, column);
            SessionPeaks.Children.Add(cell);
        }

        for (int row = 0; row < sections.Count; row++)
        {
            var (section, peaks) = sections[row];
            SessionPeaks.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            // The same 12 after the name as after "Last session" above, so
            // the shared column is as wide in both. A pixel between lines.
            Cell(section, _theme.Header, row, 0, new Thickness(0, 1, 12, 0));

            for (int i = 0; i < peaks.Count; i++)
            {
                Cell(peaks[i].Label, _theme.Label, row, 1 + i * 2, new Thickness(0, 1, 5, 0));
                Cell(peaks[i].Text, _theme.NotesText, row, 2 + i * 2, new Thickness(0, 1, 16, 0));
            }
        }

        // The session in full on hover, the same tooltip as the session
        // history's (see Ui/SessionTip): the card only had room for the date,
        // length and peaks, and the score, lows and stutters were a window
        // away. One each - a ToolTip can only belong to one element.
        SessionText.ToolTip = DeskMon.Ui.SessionTip.Make(_theme, _settings, session);
        SessionPeaks.ToolTip = DeskMon.Ui.SessionTip.Make(_theme, _settings, session);
    }

    /// <summary>
    /// Folds or unfolds the last session's readings. Folded by default, so the
    /// notes are what the window is about and the card is a single line; it
    /// stays as left while the window is open, but isn't saved - each start
    /// begins on the notes again.
    /// </summary>
    private void SessionFold_Click(object sender, MouseButtonEventArgs e)
    {
        bool open = SessionPeaks.Visibility != Visibility.Visible;
        SessionPeaks.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SessionHeading.Text = (open ? "▾ " : "▸ ") + "Last session";
        e.Handled = true;
    }

    /// <summary>The ALL SESSIONS button: the widget owns that window, so it is asked to open or close it.</summary>
    public event Action<string>? SessionsRequested;

    /// <summary>
    /// Lights ALL SESSIONS while the session history is on screen - told by
    /// the widget, as for <see cref="SetPicturesOpen"/>, so the window being
    /// put away from anywhere else unlights it too.
    /// </summary>
    public void SetSessionsOpen(bool open) => AllSessionsButton.IsChecked = open;

    private void AllSessions_Click(object sender, RoutedEventArgs e) => SessionsRequested?.Invoke(_currentGame);

    // --- Links -----------------------------------------------------------
    //
    // A web link around the selected text, or a new one at the caret. Opened
    // with Ctrl+click, like Word - see Editor_PreviewMouseLeftButtonDown - so
    // a plain click can still put the caret inside it to edit the text.

    /// <summary>
    /// The accent blue the toolbars light up in, rather than WPF's default
    /// link blue, which is made for white pages and is hard to read on the
    /// dark notepad. Set on the link itself, so it is saved in the RTF.
    /// </summary>
    private static readonly Brush LinkBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x60, 0xA5, 0xFA)));

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    /// <summary>The link the caret was in when the popup opened - edited rather than nested inside a new one.</summary>
    private Hyperlink? _editingLink;

    /// <summary>
    /// The selection when the popup opened. Kept as pointers, which follow the
    /// text, because the popup's text boxes take the focus and the editor's own
    /// selection is not to be trusted after that.
    /// </summary>
    private TextPointer? _linkStart, _linkEnd;

    private void Link_Click(object sender, RoutedEventArgs e) => LinkPopup.IsOpen = true;

    /// <summary>
    /// Filled from what is there: an existing link to edit, else the selected
    /// text - and its address too, if the selection is one - else an address
    /// on the clipboard, which is where a link usually comes from.
    /// </summary>
    private void LinkPopup_Opened(object? sender, EventArgs e)
    {
        _linkStart = Editor.Selection.Start;
        _linkEnd = Editor.Selection.End;

        // A whole line picked by triple-click ends at the very start of the
        // next paragraph. Taken as it is, linking it would delete the line
        // break between the two and join them; pulled back, it is the one line.
        if (_linkEnd.Paragraph is { } endParagraph
            && !ReferenceEquals(endParagraph, _linkStart.Paragraph)
            && new TextRange(endParagraph.ContentStart, _linkEnd).IsEmpty
            && _linkEnd.GetNextInsertionPosition(LogicalDirection.Backward) is { } lineEnd
            && lineEnd.CompareTo(_linkStart) >= 0)
            _linkEnd = lineEnd;

        _editingLink = TextLinkAt(_linkStart);

        LinkHint.Text = "Ctrl+click a link in the notes to open it.";
        LinkHint.Foreground = _theme.Label;

        if (_editingLink is { } link)
        {
            LinkHeading.Text = "Edit link";
            LinkApply.Content = "Save";
            LinkRemove.Visibility = Visibility.Visible;
            LinkText.Text = new TextRange(link.ContentStart, link.ContentEnd).Text;
            LinkAddress.Text = link.NavigateUri?.ToString() ?? string.Empty;
        }
        else
        {
            string selected = OneLine(new TextRange(_linkStart, _linkEnd).Text);
            LinkHeading.Text = "Add link";
            LinkApply.Content = "Add";
            LinkRemove.Visibility = Visibility.Collapsed;
            LinkText.Text = selected;
            LinkAddress.Text = LooksLikeAddress(selected) ? selected : ClipboardAddress() ?? string.Empty;
        }

        // Straight to whichever box still needs filling in. Deferred for the
        // same reason as the to-do popup: not focusable while it opens.
        var first = LinkAddress.Text.Length == 0 || LinkText.Text.Length > 0 ? LinkAddress : LinkText;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            Keyboard.Focus(first);
            first.SelectAll();
        }));
    }

    private void LinkBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            ApplyLink();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            LinkPopup.IsOpen = false;
            Editor.Focus();
            e.Handled = true;
        }
    }

    private void LinkApply_Click(object sender, RoutedEventArgs e) => ApplyLink();

    private void ApplyLink()
    {
        if (WebAddress(LinkAddress.Text) is not { } uri)
        {
            // Kept open with what was typed, rather than closing on a mistake.
            LinkHint.Text = "That doesn't look like a web address - e.g. wiki.example.com/page";
            LinkHint.Foreground = _theme.Warn;
            return;
        }

        // The address itself when no text is given, so the link is never
        // an invisible, unclickable nothing.
        string text = LinkText.Text.Trim();
        if (text.Length == 0)
            text = LinkAddress.Text.Trim();

        LinkPopup.IsOpen = false;

        // One undo step for the whole thing, not one per inline moved.
        string? failed = null;
        Editor.BeginChange();
        try
        {
            var link = _editingLink is { } existing
                ? EditLink(existing, text, uri)
                : NewLink(text, uri);

            if (link is not null)
                Editor.CaretPosition = AfterLink(link);
        }
        catch (Exception ex)
        {
            // WPF refuses some placements - a link inside another one, say.
            // Said in the status line; never worth taking the notepad down.
            failed = ex.Message;
        }
        finally
        {
            Editor.EndChange();
        }

        Editor.Focus();
        if (failed is not null)
        {
            ShowStatus("Could not make the link here: " + failed, problem: true);
            return;
        }

        MarkDirty();
        ShowStatus("Link added successfully - Ctrl+click it to open");
    }

    /// <summary>
    /// Where to leave the caret: in the text after the link, never at its edge.
    /// The editor treats the position right after a link's end as still inside
    /// it, so the next word typed there became part of the link. When nothing
    /// follows - a link at the end of a line - a space is put after it to type
    /// into instead.
    /// </summary>
    private static TextPointer AfterLink(Hyperlink link)
    {
        // Qualified: this window has a method called Run as well.
        if (link.NextInline is System.Windows.Documents.Run or Span)
            return link.NextInline.ContentStart;

        var space = new System.Windows.Documents.Run(" ");
        link.SiblingInlines.InsertAfter(link, space);
        return space.ContentEnd;
    }

    /// <summary>A selection as one line, for the one-line text box: line breaks and runs of spaces as single spaces.</summary>
    private static string OneLine(string text)
        => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static Hyperlink EditLink(Hyperlink link, string text, Uri uri)
    {
        link.NavigateUri = uri;

        // Replaced only if it was changed: its own bold or colour would be lost.
        if (new TextRange(link.ContentStart, link.ContentEnd).Text != text)
        {
            link.Inlines.Clear();
            link.Inlines.Add(new Run(text));
        }

        return link;
    }

    /// <summary>
    /// Wraps the selection as it is - formatting and all - when the text was
    /// left alone and it lies within one paragraph. Otherwise the selection is
    /// replaced by the typed text, since a link cannot run across paragraphs.
    /// </summary>
    private Hyperlink? NewLink(string text, Uri uri)
    {
        if (_linkStart is null || _linkEnd is null)
            return null;

        var range = new TextRange(_linkStart, _linkEnd);
        Hyperlink? link = null;

        // Compared the way the text box was filled, so a double space in the
        // selection doesn't count as the text having been changed.
        if (!range.IsEmpty && OneLine(range.Text) == text && _linkStart.Paragraph is { } paragraph
            && ReferenceEquals(paragraph, _linkEnd.Paragraph))
        {
            try
            {
                link = new Hyperlink(_linkStart.GetInsertionPosition(LogicalDirection.Forward),
                                     _linkEnd.GetInsertionPosition(LogicalDirection.Backward));
            }
            catch (InvalidOperationException)
            {
                // A selection that starts inside bold text and ends outside
                // it can't be wrapped whole; replaced below instead, which
                // loses the bold but makes the link.
            }
        }

        if (link is null)
        {
            range.Text = string.Empty;
            link = new Hyperlink(new Run(text), range.Start.GetInsertionPosition(LogicalDirection.Forward));
        }

        link.NavigateUri = uri;
        link.Foreground = LinkBrush;
        return link;
    }

    /// <summary>The text stays where it was, with its formatting; only the link around it goes.</summary>
    private void LinkRemove_Click(object sender, RoutedEventArgs e)
    {
        LinkPopup.IsOpen = false;
        if (_editingLink is not { } link)
            return;

        InlineCollection? siblings = link.Parent switch
        {
            Paragraph p => p.Inlines,
            Span s => s.Inlines,
            _ => null,
        };
        if (siblings is null)
            return;

        Editor.BeginChange();
        try
        {
            foreach (var inline in link.Inlines.ToList())
            {
                link.Inlines.Remove(inline);
                siblings.InsertBefore(link, inline);
            }
            siblings.Remove(link);
        }
        finally
        {
            Editor.EndChange();
        }

        MarkDirty();
        Editor.Focus();
    }

    /// <summary>
    /// The text link around this point, if any. Screenshot links are left
    /// out: their address is the picture's file, and editing it here would
    /// cut the picture off from its full-size copy.
    /// </summary>
    private static Hyperlink? TextLinkAt(TextPointer at)
    {
        for (DependencyObject? node = at.Parent; node is TextElement element; node = element.Parent)
            if (element is Hyperlink link)
                return link.Inlines.OfType<InlineUIContainer>().Any() || link.NavigateUri is { IsFile: true }
                    ? null
                    : link;

        return null;
    }

    /// <summary>
    /// A web address, with https:// added when it was typed without one.
    /// Only http and https: OpenLink opens nothing else, and a link that
    /// does nothing when clicked would look broken.
    /// </summary>
    private static Uri? WebAddress(string typed)
    {
        string address = typed.Trim();
        if (address.Length == 0 || address.Any(char.IsWhiteSpace))
            return null;

        if (!address.Contains("://", StringComparison.Ordinal))
            address = "https://" + address;

        return Uri.TryCreate(address, UriKind.Absolute, out var uri)
               && uri.Scheme is "http" or "https"
               && uri.Host.Length > 0
            ? uri
            : null;
    }

    /// <summary>
    /// Strict on purpose - only what starts like an address - so an ordinary
    /// word with a full stop in it isn't put in the address box.
    /// </summary>
    private static bool LooksLikeAddress(string text)
        => !text.Any(char.IsWhiteSpace)
           && (text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
               || text.StartsWith("www.", StringComparison.OrdinalIgnoreCase));

    private static string? ClipboardAddress()
    {
        try
        {
            string text = Clipboard.ContainsText() ? Clipboard.GetText().Trim() : string.Empty;
            return LooksLikeAddress(text) ? text : null;
        }
        catch
        {
            // Another program can hold the clipboard open; an empty box is fine.
            return null;
        }
    }

    // --- To-do list -------------------------------------------------------
    //
    // The items are typed into the popup, a box per item, and go into the
    // notes as one list. It used to ask only how many, and put in that many
    // empty lines to be filled in the editor afterwards.

    /// <summary>The item boxes in the popup, top to bottom.</summary>
    private readonly List<TextBox> _todoBoxes = new();

    private void Todo_Click(object sender, RoutedEventArgs e) => TodoPopup.IsOpen = true;

    /// <summary>
    /// Puts the cursor in the last item, ready to type. Items from a popup
    /// that was clicked away from are still there: a stray click outside it
    /// shouldn't cost a list half written. Deferred because the popup's
    /// contents are not ready to take focus while it is opening.
    /// </summary>
    private void TodoPopup_Opened(object? sender, EventArgs e)
    {
        if (_todoBoxes.Count == 0)
            AddTodoBox(0, "");

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() => FocusTodoBox(_todoBoxes[^1])));
    }

    private void TodoCreate_Click(object sender, RoutedEventArgs e) => CreateTodoList();

    /// <summary>
    /// The markers beside the items follow the kind picked. Raised once
    /// while the window is still being built, before the items exist.
    /// </summary>
    private void TodoKind_Checked(object sender, RoutedEventArgs e)
    {
        if (TodoItems is null)
            return;

        for (int i = 0; i < _todoBoxes.Count; i++)
            ((TextBlock)((Grid)TodoItems.Children[i]).Children[0]).Text = TodoMarker(i);
    }

    /// <summary>What goes before item <paramref name="index"/>, as the notes will show it.</summary>
    private string TodoMarker(int index)
        => TodoChecks.IsChecked == true ? Checklist.Open
         : TodoNumbered.IsChecked == true ? $"{index + 1}."
         : "•";

    /// <summary>A row - marker and box - at <paramref name="index"/>, holding <paramref name="text"/>.</summary>
    private TextBox AddTodoBox(int index, string text)
    {
        var box = new TextBox
        {
            Style = (Style)FindResource("DarkTextBox"),
            Text = text,
            Margin = new Thickness(0, 0, 0, 4),
        };
        box.PreviewKeyDown += TodoBox_PreviewKeyDown;
        DataObject.AddPastingHandler(box, TodoBox_Pasting);

        var marker = new TextBlock
        {
            Foreground = _theme.Label,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 4),
        };

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(22) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.Children.Add(marker);
        Grid.SetColumn(box, 1);
        row.Children.Add(box);

        _todoBoxes.Insert(index, box);
        TodoItems.Children.Insert(index, row);
        TodoKind_Checked(this, new RoutedEventArgs());   // numbers below it move down one
        return box;
    }

    private void RemoveTodoBox(int index)
    {
        _todoBoxes.RemoveAt(index);
        TodoItems.Children.RemoveAt(index);
        TodoKind_Checked(this, new RoutedEventArgs());
    }

    private static void FocusTodoBox(TextBox box)
    {
        Keyboard.Focus(box);
        box.CaretIndex = box.Text.Length;
        box.BringIntoView();
    }

    /// <summary>
    /// Typed like a list in a word processor: Enter starts the next item, and
    /// Enter on an empty last item ends the list and puts it in - so a whole
    /// list goes in without touching the mouse. Backspace in an empty item
    /// takes it away; the arrows move between items.
    /// </summary>
    private void TodoBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var box = (TextBox)sender;
        int index = _todoBoxes.IndexOf(box);
        bool empty = box.Text.Length == 0;
        bool last = index == _todoBoxes.Count - 1;

        switch (e.Key)
        {
            case Key.Enter when Keyboard.Modifiers == ModifierKeys.Control:
                CreateTodoList();
                break;
            case Key.Enter when empty && last:
                // The empty item is only the signal to stop - unless it is
                // the only one, which gives an empty list to type into.
                if (index > 0)
                    RemoveTodoBox(index);
                CreateTodoList();
                break;
            case Key.Enter when empty && !last:
                FocusTodoBox(_todoBoxes[index + 1]);
                break;
            case Key.Enter:
                // What is after the caret goes down with the new item, as
                // Enter in the middle of a line does in the notes.
                string rest = box.Text[box.CaretIndex..];
                box.Text = box.Text[..box.CaretIndex];
                var added = AddTodoBox(index + 1, rest);
                FocusTodoBox(added);
                added.CaretIndex = 0;
                break;
            case Key.Back when empty && _todoBoxes.Count > 1:
                RemoveTodoBox(index);
                FocusTodoBox(_todoBoxes[Math.Max(0, index - 1)]);
                break;
            case Key.Up when index > 0:
                FocusTodoBox(_todoBoxes[index - 1]);
                break;
            case Key.Down when !last:
                FocusTodoBox(_todoBoxes[index + 1]);
                break;
            case Key.Escape:
                TodoPopup.IsOpen = false;
                Editor.Focus();
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    /// <summary>
    /// Several lines pasted in become an item each - a list copied from a
    /// guide or a chat goes in whole. A box of one line would otherwise keep
    /// only the first.
    /// </summary>
    private void TodoBox_Pasting(object sender, DataObjectPastingEventArgs e)
    {
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string text || !text.Contains('\n'))
            return;

        e.CancelCommand();

        var box = (TextBox)sender;
        var lines = text.Replace("\r", "").Split('\n')
                        .Select(line => line.Trim())
                        .Where(line => line.Length > 0)
                        .ToList();
        if (lines.Count == 0)
            return;

        box.SelectedText = lines[0];
        int index = _todoBoxes.IndexOf(box);
        TextBox lastAdded = box;
        for (int i = 1; i < lines.Count; i++)
            lastAdded = AddTodoBox(index + i, lines[i]);

        FocusTodoBox(lastAdded);
    }

    /// <summary>
    /// Puts the items in the notes and empties the popup for the next list.
    /// Empty items are left out; with none written at all, one empty item goes
    /// in for the caret to start in, as the button used to give.
    /// </summary>
    private void CreateTodoList()
    {
        var texts = _todoBoxes.Select(b => b.Text.Trim()).Where(t => t.Length > 0).ToList();
        if (texts.Count == 0)
            texts.Add("");

        TodoPopup.IsOpen = false;
        _todoBoxes.Clear();
        TodoItems.Children.Clear();

        if (TodoChecks.IsChecked == true)
            InsertChecklist(texts);
        else
            InsertList(texts, TodoNumbered.IsChecked == true
                ? TextMarkerStyle.Decimal
                : TextMarkerStyle.Disc);
    }

    /// <summary>
    /// Adds a list after the paragraph the cursor is in, and puts the cursor
    /// at the end of its last item, so Enter carries the list on.
    ///
    /// After the whole top-level block rather than at the cursor itself:
    /// splitting a paragraph, or nesting inside a list the cursor happens to
    /// be in, is never what "new to-do list" means. An empty paragraph at the
    /// cursor is replaced instead, or every list would sit under a blank line.
    /// </summary>
    private void InsertList(IReadOnlyList<string> texts, TextMarkerStyle marker)
    {
        // Fully qualified: List alone is System.Collections.Generic's here.
        var list = new System.Windows.Documents.List { MarkerStyle = marker };
        foreach (string text in texts)
            list.ListItems.Add(new ListItem(new Paragraph(new Run(text))));

        InsertBlocks(new Block[] { list },
                     () => list.ListItems.LastListItem.Blocks.LastBlock.ContentEnd);
    }

    /// <summary>
    /// Tickable items: plain paragraphs that start with a box, rather than a
    /// list - see <see cref="Checklist"/> for why.
    /// </summary>
    private void InsertChecklist(IReadOnlyList<string> texts)
    {
        var items = new List<Paragraph>();
        foreach (string text in texts)
            items.Add(new Paragraph(new Run(Checklist.NewItem + text)));

        InsertBlocks(items, () => items[^1].ContentEnd);

        // Once they are in the document: the indent is measured in the size
        // they are drawn at, which they only have from the document.
        Editor.BeginChange();
        try
        {
            foreach (var item in items)
                Checklist.Tidy(item);
        }
        finally
        {
            Editor.EndChange();
        }
    }

    /// <summary>
    /// Puts blocks after the one the caret is in - or in place of it, when it
    /// is an empty line - as one undo step, and then moves the caret to where
    /// typing should continue. Shared by the lists and the screenshots.
    /// </summary>
    /// <param name="focus">
    /// False for a screenshot taken from inside a game: focusing the editor
    /// there would be asking to pull the notepad over the game.
    /// </param>
    private void InsertBlocks(IReadOnlyList<Block> added, Func<TextPointer> caret, bool focus = true)
    {
        var blocks = Editor.Document.Blocks;
        Block? anchor = TopLevelBlock(Editor.CaretPosition);

        Editor.BeginChange();
        try
        {
            if (anchor is null)
            {
                foreach (var block in added)
                    blocks.Add(block);
            }
            else
            {
                Block after = anchor;
                foreach (var block in added)
                {
                    blocks.InsertAfter(after, block);
                    after = block;
                }

                if (anchor is Paragraph paragraph
                    && new TextRange(paragraph.ContentStart, paragraph.ContentEnd).IsEmpty)
                    blocks.Remove(paragraph);
            }
        }
        finally
        {
            Editor.EndChange();
        }

        if (focus)
            Editor.Focus();
        Editor.CaretPosition = caret();

        // TextChanged would usually see to this, but it is not guaranteed for
        // a change made through the document rather than by typing - the same
        // gap that once lost formatting; see OnEditorCommand.
        MarkDirty();
    }

    /// <summary>The block directly under the document that holds this position, or null.</summary>
    private static Block? TopLevelBlock(TextPointer position)
    {
        DependencyObject? node = position.Parent;
        while (node is TextElement element)
        {
            if (element is Block block && block.Parent is FlowDocument)
                return block;
            node = element.Parent;
        }
        return null;
    }

    /// <summary>
    /// A row of the notes list: a note, or a heading over a group of them.
    /// Made fresh for each build rather than changed in place, like the
    /// hardware window's rows - see BuildList.
    /// </summary>
    private sealed class NoteEntry
    {
        public string Name { get; init; } = "";
        public bool IsHeading { get; init; }
        public string? Detail { get; init; }
        public Brush? NameBrush { get; init; }
        public Brush? DetailBrush { get; init; }
        public double SmallFontSize { get; init; }

        /// <summary>The list folded down to its icons: the name goes to the tooltip.</summary>
        public bool Folded { get; init; }
        public Visibility TextVisibility => Folded ? Visibility.Collapsed : Visibility.Visible;
        public Thickness IconMargin => Folded ? new Thickness(0) : new Thickness(0, 0, 9, 0);

        /// <summary>Folded, the icon in the middle of the strip, not at its left with the spare room after it.</summary>
        public HorizontalAlignment RowAlignment => Folded ? HorizontalAlignment.Center : HorizontalAlignment.Stretch;
        public string? IconToolTip => !Folded ? null : string.IsNullOrEmpty(Detail) ? Name : $"{Name}\n{Detail}";

        public Visibility HeadingVisibility => IsHeading ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RowVisibility => IsHeading ? Visibility.Collapsed : Visibility.Visible;
        public Visibility DetailVisibility => string.IsNullOrEmpty(Detail) ? Visibility.Collapsed : Visibility.Visible;
    }
}
