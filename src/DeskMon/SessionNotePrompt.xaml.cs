using System;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
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
    private bool _typed;

    /// <summary>
    /// The countdown, as an animation of the bar's width share rather than a
    /// timer resizing it. The timer ran at background priority and resized the
    /// bar ten times a second, each a layout of this see-through, shadowed
    /// window; right after a game quits the widget is busy, so the ticks came
    /// late and the bar stalled and jumped. An animation is drawn smoothly
    /// whatever the UI thread is doing, and its end is what closes the card.
    /// </summary>
    private AnimationClock? _countdown;

    /// <summary>The widget's place, kept so a late layout (Loaded) can stand beside it.</summary>
    private Rect _near;
    private bool _shown;

    /// <param name="summary">The session in a line: "Factorio · 2 h 14 min · 58 fps · score 91".</param>
    /// <param name="near">The widget's place on screen, to stand beside - see Follow.</param>
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
        _near = near;
        Loaded += (_, _) =>
        {
            Place();
            StartCountdown();
        };

        // The mouse on it holds the countdown: reading the summary or
        // reaching for the box shouldn't race the clock.
        MouseEnter += (_, _) => _countdown?.Controller?.Pause();
        MouseLeave += (_, _) =>
        {
            if (!_typed)
                _countdown?.Controller?.Resume();
        };

        // Clicked anywhere, it takes the keyboard and puts the caret in the box.
        PreviewMouseDown += (_, _) =>
        {
            Activate();
            NoteBox.Focus();
        };
    }

    /// <summary>
    /// Moves the card with the widget - called by it as it is dragged or
    /// changes size, so the card stays locked to its side rather than left
    /// behind where the game quit.
    /// </summary>
    public void Follow(Rect near)
    {
        _near = near;
        if (IsLoaded)
            Place();
    }

    /// <summary>
    /// The window's margin round the card, for its shadow: the window is this
    /// much bigger than the card on every side, so the edges are lined up by
    /// the card, not by the window.
    /// </summary>
    private const double ShadowMargin = 6;

    /// <summary>The space between the card and the widget.</summary>
    private const double Gap = 6;

    /// <summary>
    /// To the widget's left, tops lined up - or to its right when the widget
    /// is against the left edge of its screen - and kept on that screen.
    /// The widget's own screen rather than the whole desktop: on the left
    /// edge of a second monitor, "the left" would be the other monitor.
    /// </summary>
    private void Place()
    {
        var area = WorkAreaAround(_near);
        double width = ActualWidth, height = ActualHeight;

        double left = _near.Left - Gap - (width - ShadowMargin);
        if (left + ShadowMargin < area.Left)
            left = _near.Right + Gap - ShadowMargin;
        double top = _near.Top - ShadowMargin;

        Left = Math.Clamp(left, area.Left - ShadowMargin, Math.Max(area.Left - ShadowMargin, area.Right - width + ShadowMargin));
        Top = Math.Clamp(top, area.Top - ShadowMargin, Math.Max(area.Top - ShadowMargin, area.Bottom - height + ShadowMargin));

        // A short fade in the first time, so it arrives rather than blinks
        // into place; not on every move after, which would flicker.
        if (!_shown)
        {
            _shown = true;
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }
    }

    /// <summary>
    /// The work area of the screen the widget is on, in WPF's units. Screens
    /// are found in pixels, so converted both ways; the desktop as a whole if
    /// the window has no screen yet.
    /// </summary>
    private Rect WorkAreaAround(Rect near)
    {
        if (PresentationSource.FromVisual(this)?.CompositionTarget is not { } target)
            return new Rect(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
                            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        var centre = target.TransformToDevice.Transform(new Point(near.Left + near.Width / 2, near.Top + near.Height / 2));
        var area = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)centre.X, (int)centre.Y)).WorkingArea;
        return new Rect(target.TransformFromDevice.Transform(new Point(area.Left, area.Top)),
                        target.TransformFromDevice.Transform(new Point(area.Right, area.Bottom)));
    }

    /// <summary>
    /// The bar shrinks from full to nothing over Wait, from its left edge,
    /// by scaling rather than resizing - see _countdown - and the card closes
    /// as it runs out.
    /// </summary>
    private void StartCountdown()
    {
        var scale = new ScaleTransform(1, 1);
        Countdown.RenderTransformOrigin = new Point(0, 0.5);
        Countdown.RenderTransform = scale;

        var shrink = new DoubleAnimation(1, 0, new Duration(Wait));
        _countdown = shrink.CreateClock();
        _countdown.Completed += (_, _) =>
        {
            // Completed also comes when the clock is stopped for typing.
            if (!_typed)
                Close();
        };
        scale.ApplyAnimationClock(ScaleTransform.ScaleXProperty, _countdown);

        // Shown under the mouse - the widget's side is where it often is -
        // it waits from the start, as if the mouse had just come onto it.
        if (IsMouseOver)
            _countdown.Controller?.Pause();
    }

    /// <summary>Typing stops the clock for good: a half-written note must not vanish.</summary>
    private void NoteBox_TextChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        if (_typed || NoteBox.Text.Length == 0)
            return;
        _typed = true;
        _countdown?.Controller?.Stop();
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
        // Set first, so the stopped clock's Completed doesn't close it twice.
        _typed = true;
        _countdown?.Controller?.Stop();
        base.OnClosed(e);
    }
}
