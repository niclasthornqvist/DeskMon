using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Animation;
using DeskMon.Ui;

namespace DeskMon;

/// <summary>
/// The card shown by the widget as a game quits: "Anything to note?". A note
/// is most likely to be written right then, while the evening is fresh - and
/// least likely once it means opening the notes window and finding the game.
/// What is typed goes to the end of the game's notes under a heading for the
/// session; see NotesWindow.AppendSessionNote.
///
/// Never in the way: it doesn't take the keyboard until clicked, closes by
/// itself after 20 s if left alone - the countdown waits while the mouse is
/// on it - and stays open once something is typed, until saved or skipped.
/// </summary>
public partial class SessionNotePrompt : Window
{
    /// <summary>How long it waits for you before closing.</summary>
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(20);

    private readonly Action<string> _save;
    private readonly System.Windows.Threading.DispatcherTimer _timer = new();
    private DateTime _closesAt;
    private TimeSpan _left = Wait;
    private bool _typed;

    /// <param name="summary">The session in a line: "Factorio · 2 h 14 min · 58 fps · score 91".</param>
    /// <param name="near">The widget's place on screen, to stand beside.</param>
    /// <param name="save">Given the note when SAVE or Enter is pressed.</param>
    public SessionNotePrompt(UiTheme theme, string summary, Rect near, Action<string> save)
    {
        _save = save;
        InitializeComponent();

        Card.Background = theme.Popup;
        Card.BorderBrush = theme.AccentAt(0x80);
        Summary.Text = summary;
        Summary.Foreground = theme.NotesText;
        Summary.FontWeight = FontWeights.SemiBold;
        Question.Foreground = theme.Label;
        Hint.Foreground = theme.Header;
        Hint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);
        Countdown.Background = theme.Accent;
        FontFamily = theme.PanelFont;
        FontSize = UiTheme.PanelFontSize - 1;

        // Invisible until placed: shown first at wherever Windows puts it, it
        // would flash there before moving beside the widget.
        Opacity = 0;
        Loaded += (_, _) => Place(near);

        _timer.Interval = TimeSpan.FromMilliseconds(100);
        _timer.Tick += (_, _) => Tick();
        _closesAt = DateTime.UtcNow + Wait;
        _timer.Start();

        // The mouse on it holds the countdown: reading the summary or
        // reaching for the box shouldn't race the clock.
        MouseEnter += (_, _) => _left = _closesAt - DateTime.UtcNow;
        MouseLeave += (_, _) => _closesAt = DateTime.UtcNow + _left;

        // Clicked anywhere, it takes the keyboard and puts the caret in the box.
        PreviewMouseDown += (_, _) =>
        {
            Activate();
            NoteBox.Focus();
        };
    }

    /// <summary>
    /// Under the widget, left edges lined up; above it when there is no room
    /// below; kept on the screens either way.
    /// </summary>
    private void Place(Rect near)
    {
        double height = ActualHeight;
        double left = near.Left;
        double top = near.Bottom + 4;
        if (top + height > SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight)
            top = near.Top - height - 4;

        Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft,
                          SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - ActualWidth);
        Top = Math.Clamp(top, SystemParameters.VirtualScreenTop,
                         SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - height);

        // A short fade in, so it arrives rather than blinks into place.
        BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
    }

    private void Tick()
    {
        if (_typed)
            return;

        var left = IsMouseOver ? _left : _closesAt - DateTime.UtcNow;
        Countdown.Width = Math.Max(0, Card.ActualWidth * left.TotalMilliseconds / Wait.TotalMilliseconds);
        if (left <= TimeSpan.Zero)
            Close();
    }

    /// <summary>Typing stops the clock for good: a half-written note must not vanish.</summary>
    private void NoteBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_typed || NoteBox.Text.Length == 0)
            return;
        _typed = true;
        Countdown.Visibility = Visibility.Collapsed;
    }

    /// <summary>Enter saves and Esc skips; Shift+Enter starts a new line, as in a chat box.</summary>
    private void NoteBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) != 0)
        {
            int at = NoteBox.CaretIndex;
            NoteBox.Text = NoteBox.Text.Insert(at, Environment.NewLine);
            NoteBox.CaretIndex = at + Environment.NewLine.Length;
            e.Handled = true;
        }
        else if (e.Key == Key.Enter)
        {
            Save_Click(sender, e);
            e.Handled = true;
        }
    }

    /// <summary>
    /// The note typed and not saved yet, or null - and from then on none, so
    /// it can't also be saved by a SAVE after this. For DeskMon closing with
    /// the card still up, which would otherwise lose what was typed.
    /// </summary>
    public string? TakeTypedNote()
    {
        if (_answered)
            return null;
        _answered = true;
        string note = NoteBox.Text.Trim();
        return note.Length > 0 ? note : null;
    }

    /// <summary>Saved, skipped or taken - see TakeTypedNote.</summary>
    private bool _answered;

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_answered)
            return;
        _answered = true;
        string note = NoteBox.Text.Trim();
        Close();
        // Nothing typed is the same as SKIP: an empty heading in the notes
        // would only be clutter.
        if (note.Length > 0)
            _save(note);
    }

    /// <summary>Skipped on purpose, so closing DeskMon after it doesn't save the text after all.</summary>
    private void Skip_Click(object sender, RoutedEventArgs e)
    {
        _answered = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        base.OnClosed(e);
    }
}
