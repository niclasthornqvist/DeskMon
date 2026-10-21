using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DeskMon.Notes;
using Path = System.Windows.Shapes.Path;   // the shape; files go through System.IO.Path by name

namespace DeskMon.Ui;

/// <summary>
/// The pictures window's screenshots one at a time, over the whole screen -
/// inside DeskMon rather than in Windows' Photos app, so it opens at once,
/// stays on top of a game the way the other windows do, and steps through
/// the game's pictures in the order the pictures window shows them.
///
///   ← →            previous, next
///   wheel          zoom, around the mouse
///   drag           move a zoomed picture about
///   double-click   between fitting the screen and 100% (a pixel a pixel)
///   Ctrl+C  F  M   copy, favourite, mark up
///   Esc            close - or leave mark-up without saving
///
/// Mark-up draws arrows, circles, boxes, freehand lines and text over the
/// picture and saves the result as a copy beside it. The screenshot itself is
/// never changed: the notes and the pictures window may show it, and a mark
/// drawn in the wrong place should cost nothing.
/// </summary>
public sealed class PictureViewer : Window
{
    private readonly UiTheme _theme;
    private readonly List<string> _paths;
    private int _index;

    private readonly Func<string, bool> _isFavourite;
    private readonly Func<string, bool> _toggleFavourite;

    /// <summary>A picture was added (a marked-up copy) or changed (a star): the pictures window lists them again.</summary>
    private readonly Action _changed;

    /// <summary>A picture was marked up in place - its file is new, so the notes make their preview of it again.</summary>
    private readonly Action<string> _replaced;

    /// <summary>Deletes a picture - to the Recycle Bin, and out of the notes. Null when it went, else why not.</summary>
    private readonly Func<string, string?> _delete;

    // The picture and what is drawn on it, in its own pixels; moved and scaled as one.
    private readonly Grid _viewport = new() { ClipToBounds = true, Background = Brushes.Transparent };
    private readonly Grid _stage = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Image _image = new() { Stretch = Stretch.Fill };
    private readonly Canvas _marks = new() { Background = Brushes.Transparent };
    private readonly ScaleTransform _scale = new();
    private readonly TranslateTransform _shift = new();

    private readonly TextBlock _title = new();
    private readonly TextBlock _status = new();
    private readonly TextBlock _hint = new();
    private readonly TextBlock _favouriteText = new();
    private readonly StackPanel _viewTools = new() { Orientation = Orientation.Horizontal };
    private readonly StackPanel _markTools = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly Button _previous;
    private readonly Button _next;

    private readonly DispatcherTimer _statusClear = new() { Interval = TimeSpan.FromSeconds(2.5) };

    /// <summary>Counts what was asked for, so a slow decode for a picture already stepped past doesn't land.</summary>
    private int _ticket;

    /// <param name="paths">The pictures in the order the pictures window shows them.</param>
    /// <param name="near">The window it is opened from: it fills that window's screen.</param>
    /// <param name="replaced">Told the path of a picture marked up in place; see <see cref="SaveMarkup"/>.</param>
    /// <param name="delete">Deletes a picture by its path; see <see cref="Delete"/>.</param>
    public PictureViewer(UiTheme theme, IEnumerable<string> paths, int index, Window near,
                         Func<string, bool> isFavourite, Func<string, bool> toggleFavourite, Action changed,
                         Action<string> replaced, Func<string, string?> delete)
    {
        _delete = delete;
        _theme = theme;
        _paths = paths.ToList();
        _index = Math.Clamp(index, 0, Math.Max(0, _paths.Count - 1));
        _isFavourite = isFavourite;
        _toggleFavourite = toggleFavourite;
        _changed = changed;
        _replaced = replaced;

        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Picture";
        Background = theme.WindowAt(0xF2);
        FontFamily = theme.PanelFont;
        FontSize = UiTheme.PanelFontSize;
        Foreground = theme.NotesText;
        UseLayoutRounding = true;
        FillScreenOf(near);

        _stage.Children.Add(_image);
        _stage.Children.Add(_marks);
        _stage.RenderTransform = new TransformGroup { Children = { _scale, _shift } };
        _viewport.Children.Add(_stage);

        _previous = SideButton("M7,1 L2,6 L7,11", HorizontalAlignment.Left, () => Step(-1));
        _next = SideButton("M2,1 L7,6 L2,11", HorizontalAlignment.Right, () => Step(1));
        _viewport.Children.Add(_previous);
        _viewport.Children.Add(_next);

        Content = Layout();
        BuildViewTools();
        BuildMarkTools();

        _viewport.SizeChanged += (_, _) => Fit();
        _viewport.MouseWheel += OnWheel;
        _viewport.MouseLeftButtonDown += OnViewportDown;
        _viewport.MouseMove += OnViewportMove;
        _viewport.MouseLeftButtonUp += (_, _) => EndDrag();
        _marks.MouseLeftButtonDown += OnMarkDown;
        _marks.MouseMove += OnMarkMove;
        _marks.MouseLeftButtonUp += OnMarkUp;
        PreviewKeyDown += OnKey;
        _statusClear.Tick += (_, _) => { _statusClear.Stop(); _status.Text = string.Empty; };

        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            ShowPicture(_index);
        };
    }

    // --- Layout ----------------------------------------------------------------

    private UIElement Layout()
    {
        var root = new DockPanel();

        var bar = new Grid { Margin = new Thickness(16, 10, 10, 8) };
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var heading = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _title.FontWeight = FontWeights.SemiBold;
        _title.Foreground = _theme.Section;
        _status.Margin = new Thickness(14, 0, 0, 0);
        _status.Foreground = _theme.Accent;
        heading.Children.Add(_title);
        heading.Children.Add(_status);
        bar.Children.Add(heading);

        var tools = new Grid();
        tools.Children.Add(_viewTools);
        tools.Children.Add(_markTools);
        Grid.SetColumn(tools, 1);
        bar.Children.Add(tools);

        var close = new Button
        {
            Style = (Style)FindResource("PanelButton"),
            Content = "✕",
            ToolTip = "Close (Esc)",
            Margin = new Thickness(8, 0, 0, 0),
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 2);
        bar.Children.Add(close);

        DockPanel.SetDock(bar, Dock.Top);
        root.Children.Add(bar);

        _hint.Foreground = _theme.Header;
        _hint.FontSize = Math.Max(9, UiTheme.PanelFontSize - 3);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.Margin = new Thickness(0, 6, 0, 10);
        DockPanel.SetDock(_hint, Dock.Bottom);
        root.Children.Add(_hint);

        root.Children.Add(_viewport);
        return root;
    }

    private void BuildViewTools()
    {
        var star = Tool(string.Empty, "Favourite (F)\nStarred pictures can be sorted first in the pictures window.", ToggleFavourite);
        star.Content = _favouriteText;
        _viewTools.Children.Add(star);
        _viewTools.Children.Add(Tool("COPY", "Copy the picture (Ctrl+C)\nPaste it into a chat, a document or Paint.", Copy));
        _viewTools.Children.Add(Tool("MARK UP", "Draw arrows, circles and text on it (M)\nSaved as a copy - this picture stays as it is.", StartMarkup));
        _viewTools.Children.Add(Tool("PHOTOS ↗", "Open it in Windows' own picture viewer", OpenOutside));

        _deleteButton = Tool("DELETE", "Delete this picture (Del)\nIt goes to the Recycle Bin and is taken out of the notes too.", Delete);
        _deleteButton.Margin = new Thickness(14, 0, 2, 0);   // set apart from the rest
        _viewTools.Children.Add(_deleteButton);
        _disarm.Tick += (_, _) => Disarm();
    }

    // --- Deleting ------------------------------------------------------------------
    //
    // Asked twice, as the pictures window's bin is: the first click (or Del)
    // arms it - it turns to the warning colour and asks - and a second within
    // a few seconds deletes. A stray click costs nothing; one that meant it
    // costs one more.

    private Button _deleteButton = null!;
    private readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(4) };
    private bool _armed;

    private void Delete()
    {
        if (_paths.Count == 0 || _markup)
            return;

        if (!_armed)
        {
            _armed = true;
            _deleteButton.Content = "DELETE? CLICK AGAIN";
            _deleteButton.Foreground = _theme.Warn;
            _disarm.Start();
            return;
        }

        Disarm();
        string path = Current;
        if (_delete(path) is { } error)
        {
            Note($"Couldn't delete it: {error}", warn: true);
            return;
        }

        // On to the one after - the one before, if that was the last - or
        // closed, with nothing left to show.
        _paths.RemoveAt(_index);
        if (_paths.Count == 0)
        {
            Close();
            return;
        }

        ShowPicture(Math.Min(_index, _paths.Count - 1));
        Note("Deleted - it is in the Recycle Bin");
    }

    private void Disarm()
    {
        _disarm.Stop();
        _armed = false;
        _deleteButton.Content = "DELETE";
        _deleteButton.ClearValue(ForegroundProperty);
    }

    private Button Tool(string text, string tooltip, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("ToolButton"),
            Content = text,
            ToolTip = tooltip,
            Margin = new Thickness(2, 0, 2, 0),
        };
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>A round ‹ or › at the side of the picture, over it, half see-through until pointed at.</summary>
    private Button SideButton(string chevron, HorizontalAlignment side, Action click)
    {
        var button = new Button
        {
            Style = (Style)FindResource("PanelButton"),
            Width = 44,
            Height = 44,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(14),
            Opacity = 0.75,
            Background = _theme.WindowAt(0xB0),
            Content = new Path
            {
                Data = Geometry.Parse(chevron),
                Stroke = _theme.NotesText,
                StrokeThickness = 2,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 9,
                Height = 12,
            },
        };
        button.MouseEnter += (_, _) => button.Opacity = 1;
        button.MouseLeave += (_, _) => button.Opacity = 0.75;
        button.Click += (_, _) => click();
        return button;
    }

    /// <summary>The whole of the screen the pictures window is on - taskbar included, like any picture viewer.</summary>
    private void FillScreenOf(Window near)
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        var area = SystemParameters.WorkArea;   // fallback: the main screen

        if (PresentationSource.FromVisual(near) is { CompositionTarget: { } target })
        {
            var centre = near.PointToScreen(new Point(near.ActualWidth / 2, near.ActualHeight / 2));
            var screen = System.Windows.Forms.Screen.FromPoint(new System.Drawing.Point((int)centre.X, (int)centre.Y)).Bounds;
            var fromDevice = target.TransformFromDevice;
            var topLeft = fromDevice.Transform(new Point(screen.Left, screen.Top));
            var bottomRight = fromDevice.Transform(new Point(screen.Right, screen.Bottom));
            area = new Rect(topLeft, bottomRight);
        }

        Left = area.Left;
        Top = area.Top;
        Width = area.Width;
        Height = area.Height;
    }

    // --- Showing a picture -----------------------------------------------------

    private string Current => _paths[_index];

    private async void ShowPicture(int index)
    {
        if (_paths.Count == 0)
        {
            Close();
            return;
        }

        _index = (index % _paths.Count + _paths.Count) % _paths.Count;   // round from the last back to the first
        int ticket = ++_ticket;
        if (_armed)
            Disarm();   // armed for the last picture, not this one
        string path = Current;

        _title.Text = $"{System.IO.Path.GetFileName(path)}   ·   {_index + 1} / {_paths.Count}";
        ShowFavourite();
        _previous.Visibility = _next.Visibility = _paths.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        _hint.Text = "← →  browse   ·   wheel  zoom   ·   drag  move   ·   double-click  100%   ·   Ctrl+C  copy   ·   F  favourite   ·   M  mark up   ·   Del  delete   ·   Esc  close";

        var picture = await Task.Run(() => Decode(path));
        if (ticket != _ticket)
            return;   // stepped on before it was ready

        if (picture is null)
        {
            _image.Source = null;
            Note("Couldn't open this picture - it may have been moved or deleted.", warn: true);
            return;
        }

        _image.Source = picture;
        _stage.Width = picture.PixelWidth;
        _stage.Height = picture.PixelHeight;
        _marks.Width = picture.PixelWidth;
        _marks.Height = picture.PixelHeight;
        Fit();

        if (_markUpOnOpen)
        {
            _markUpOnOpen = false;
            StartMarkup();
        }
    }

    /// <summary>The whole file, decoded at full size and closed: zooming in has to have every pixel.</summary>
    private static BitmapSource? Decode(string path)
    {
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private void Step(int by)
    {
        if (_markup)
            return;   // a step would throw the marks away
        ShowPicture(_index + by);
    }

    private void ShowFavourite()
    {
        bool on = _paths.Count > 0 && _isFavourite(Current);
        _favouriteText.Text = on ? "★ FAVOURITE" : "☆ FAVOURITE";
        _favouriteText.Foreground = on ? _theme.Accent : null;
        if (!on)
            _favouriteText.ClearValue(TextBlock.ForegroundProperty);
    }

    private void Note(string text, bool warn = false)
    {
        _status.Text = text;
        _status.Foreground = warn ? _theme.Warn : _theme.Accent;
        _statusClear.Stop();
        _statusClear.Start();
    }

    // --- Zoom and move -----------------------------------------------------------

    /// <summary>Screen pixels per DIP - so "100%" means one of the picture's pixels to one of the screen's.</summary>
    private double DeviceScale => PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;

    private double FitScale
    {
        get
        {
            if (_stage.Width is not > 0 || _viewport.ActualWidth <= 0)
                return 1;
            double fit = Math.Min(_viewport.ActualWidth / _stage.Width, _viewport.ActualHeight / _stage.Height);
            return Math.Min(fit, 1 / DeviceScale);   // a small picture at its own size, not blown up to a blur
        }
    }

    /// <summary>The whole picture, as large as fits, in the middle.</summary>
    private void Fit() => ZoomTo(FitScale, null);

    /// <summary>
    /// To <paramref name="scale"/>, keeping the picture's point under
    /// <paramref name="anchor"/> where it is - or centred, with no anchor or
    /// once it fits entirely.
    /// </summary>
    private void ZoomTo(double scale, Point? anchor)
    {
        if (_stage.Width is not > 0)
            return;

        scale = Math.Clamp(scale, FitScale * 0.5, 8);
        double width = _stage.Width * scale, height = _stage.Height * scale;
        double viewW = _viewport.ActualWidth, viewH = _viewport.ActualHeight;

        double x, y;
        if (anchor is { } at && _scale.ScaleX > 0)
        {
            double ratio = scale / _scale.ScaleX;
            x = at.X - (at.X - _shift.X) * ratio;
            y = at.Y - (at.Y - _shift.Y) * ratio;
        }
        else
        {
            x = (viewW - width) / 2;
            y = (viewH - height) / 2;
        }

        _scale.ScaleX = _scale.ScaleY = scale;
        Place(x, y);
    }

    /// <summary>
    /// Moves the picture, but never so far that it leaves the screen: a side
    /// smaller than the screen stays centred, a larger one keeps its edge at
    /// the screen's edge at most.
    /// </summary>
    private void Place(double x, double y)
    {
        double width = _stage.Width * _scale.ScaleX, height = _stage.Height * _scale.ScaleY;
        double viewW = _viewport.ActualWidth, viewH = _viewport.ActualHeight;

        _shift.X = width <= viewW ? (viewW - width) / 2 : Math.Clamp(x, viewW - width, 0);
        _shift.Y = height <= viewH ? (viewH - height) / 2 : Math.Clamp(y, viewH - height, 0);
    }

    private void OnWheel(object sender, MouseWheelEventArgs e)
    {
        if (_markup)
            return;
        ZoomTo(_scale.ScaleX * (e.Delta > 0 ? 1.2 : 1 / 1.2), e.GetPosition(_viewport));
        e.Handled = true;
    }

    private Point? _dragFrom;
    private Point _dragShift;

    private void OnViewportDown(object sender, MouseButtonEventArgs e)
    {
        if (_markup || e.OriginalSource is not (Grid or Image or Canvas))
            return;   // the side buttons take their own clicks

        if (e.ClickCount == 2)
        {
            // Between fitting the screen and a pixel a pixel, at the mouse.
            double full = 1 / DeviceScale;
            ZoomTo(Math.Abs(_scale.ScaleX - FitScale) < 0.001 ? full : FitScale, e.GetPosition(_viewport));
            return;
        }

        _dragFrom = e.GetPosition(_viewport);
        _dragShift = new Point(_shift.X, _shift.Y);
        _viewport.CaptureMouse();
        _viewport.Cursor = Cursors.SizeAll;
    }

    private void OnViewportMove(object sender, MouseEventArgs e)
    {
        if (_dragFrom is not { } from)
            return;
        var now = e.GetPosition(_viewport);
        Place(_dragShift.X + now.X - from.X, _dragShift.Y + now.Y - from.Y);
    }

    private void EndDrag()
    {
        if (_dragFrom is null)
            return;
        _dragFrom = null;
        _viewport.ReleaseMouseCapture();
        _viewport.ClearValue(CursorProperty);
    }

    // --- Commands ------------------------------------------------------------------

    private void Copy()
    {
        if (_paths.Count == 0)
            return;
        if (PictureClipboard.Copy(Current) is { } error)
            Note($"Couldn't copy: {error}", warn: true);
        else
            Note("Copied - paste it anywhere");
    }

    private void ToggleFavourite()
    {
        if (_paths.Count == 0)
            return;
        bool now = _toggleFavourite(Current);
        ShowFavourite();
        Note(now ? "Added to favourites" : "Taken out of favourites");
        _changed();
    }

    private void OpenOutside()
    {
        try
        {
            Process.Start(new ProcessStartInfo(Current) { UseShellExecute = true });
        }
        catch
        {
            Note("Windows has no program set to open this picture.", warn: true);
        }
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        // Typing a label: the box has the keys, except Esc, which it hands back.
        if (Keyboard.FocusedElement is TextBox)
            return;

        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.Escape when _markup:
                EndMarkup();
                break;
            case Key.Escape:
                Close();
                break;
            case Key.Left or Key.PageUp:
                Step(-1);
                break;
            case Key.Right or Key.PageDown or Key.Space:
                Step(1);
                break;
            case Key.C when ctrl && !_markup:
                Copy();
                break;
            case Key.Z when ctrl && _markup:
                Undo();
                break;
            case Key.F when !_markup:
                ToggleFavourite();
                break;
            case Key.M when !_markup:
                StartMarkup();
                break;
            case Key.Delete when !_markup:
                Delete();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // --- Mark-up -------------------------------------------------------------------
    //
    // Drawn on a canvas the picture's own size in pixels, so every mark is in
    // the picture's coordinates whatever the zoom - and saving is drawing the
    // two together at that size. The picture is fitted to the screen while
    // marking: zooming in and out mid-stroke is more than marking needs.

    private bool _markup;
    private string _tool = "arrow";
    private Color _colour;
    private readonly List<Button> _toolButtons = new();
    private readonly List<Border> _swatches = new();

    /// <summary>Strong on a game's screenshot, bright or dark: red first, then the theme's accent.</summary>
    private Color[] Colours => new[]
    {
        Color.FromRgb(0xEF, 0x44, 0x44),
        Color.FromRgb(0xFA, 0xCC, 0x15),
        Color.FromRgb(0x22, 0xC5, 0x5E),
        _theme.AccentColor,
        Colors.White,
    };

    private void BuildMarkTools()
    {
        foreach (var (key, label, tip) in new[]
                 {
                     ("arrow", "ARROW", "Drag from the tail to the point"),
                     ("circle", "CIRCLE", "Drag across what to ring"),
                     ("box", "BOX", "Drag across what to frame"),
                     ("pen", "PEN", "Draw freehand"),
                     ("text", "TEXT", "Click where the words go, type, then Enter"),
                 })
        {
            var button = Tool(label, tip, () => PickTool(key));
            button.Tag = key;
            _toolButtons.Add(button);
            _markTools.Children.Add(button);
        }

        _markTools.Children.Add(new Border { Width = 1, Margin = new Thickness(8, 3, 8, 3), Background = _theme.Separator });

        foreach (var colour in Colours)
        {
            var swatch = new Border
            {
                Width = 18,
                Height = 18,
                CornerRadius = new CornerRadius(9),
                Margin = new Thickness(3, 0, 3, 0),
                Background = new SolidColorBrush(colour),
                BorderThickness = new Thickness(2),
                Cursor = Cursors.Hand,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = colour,
            };
            swatch.MouseLeftButtonUp += (_, _) => PickColour(colour);
            _swatches.Add(swatch);
            _markTools.Children.Add(swatch);
        }

        _markTools.Children.Add(new Border { Width = 1, Margin = new Thickness(8, 3, 8, 3), Background = _theme.Separator });
        _markTools.Children.Add(Tool("UNDO", "Take the last mark away (Ctrl+Z)", Undo));
        _markTools.Children.Add(Tool("SAVE", "Save the marks onto this picture\nThe notes show it marked; the unmarked one goes to the Recycle Bin.", () => SaveMarkup(inPlace: true)));
        _markTools.Children.Add(Tool("SAVE COPY", "Save the marked picture beside this one\nThis one stays as it is.", () => SaveMarkup(inPlace: false)));
        _markTools.Children.Add(Tool("CANCEL", "Leave without saving (Esc)", EndMarkup));

        _colour = Colours[0];
    }

    /// <summary>Into mark-up as soon as the picture has loaded - for the pictures window's "Mark up…".</summary>
    public void MarkUpOnOpen() => _markUpOnOpen = true;

    private bool _markUpOnOpen;

    private void StartMarkup()
    {
        if (_markup || _image.Source is null)
            return;

        _markup = true;
        Fit();
        _viewTools.Visibility = Visibility.Collapsed;
        _markTools.Visibility = Visibility.Visible;
        _previous.Visibility = _next.Visibility = Visibility.Collapsed;
        _marks.Cursor = Cursors.Cross;
        _hint.Text = "Drag to draw   ·   Ctrl+Z  undo   ·   Esc  leave without saving   ·   SAVE marks this picture, SAVE COPY keeps it as it is";
        PickTool(_tool);
        PickColour(_colour);
    }

    private void EndMarkup()
    {
        CommitText();
        _marks.Children.Clear();
        _markup = false;
        _marks.ClearValue(CursorProperty);
        _markTools.Visibility = Visibility.Collapsed;
        _viewTools.Visibility = Visibility.Visible;
        ShowPicture(_index);   // the side buttons and hint back as they were
    }

    private void PickTool(string tool)
    {
        CommitText();
        _tool = tool;
        foreach (var button in _toolButtons)
        {
            if ((string)button.Tag == tool)
                button.Foreground = _theme.Accent;
            else
                button.ClearValue(ForegroundProperty);
        }
    }

    private void PickColour(Color colour)
    {
        _colour = colour;
        foreach (var swatch in _swatches)
            swatch.BorderBrush = (Color)swatch.Tag == colour ? _theme.NotesText : Brushes.Transparent;
    }

    /// <summary>Thick enough to see on a 4K picture fitted to a laptop screen; scales with the picture.</summary>
    private double StrokeWidth => Math.Max(3, _marks.Width / 320);

    private Shape? _drawing;
    private Point _from;

    private void OnMarkDown(object sender, MouseButtonEventArgs e)
    {
        if (!_markup)
            return;

        var at = e.GetPosition(_marks);
        e.Handled = true;

        if (_tool == "text")
        {
            StartText(at);
            return;
        }

        CommitText();
        _from = at;
        var brush = new SolidColorBrush(_colour);
        _drawing = _tool == "pen"
            ? new Polyline { Points = { at }, Stroke = brush }
            : new Path { Stroke = brush };
        _drawing.StrokeThickness = StrokeWidth;
        _drawing.StrokeStartLineCap = _drawing.StrokeEndLineCap = PenLineCap.Round;
        _drawing.StrokeLineJoin = PenLineJoin.Round;

        // A dark edge round every mark, so a white arrow shows on snow and a
        // red one on a red wall.
        _drawing.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            Color = Colors.Black, ShadowDepth = 0, BlurRadius = StrokeWidth * 1.5, Opacity = 0.7,
        };

        _marks.Children.Add(_drawing);
        _marks.CaptureMouse();
    }

    private void OnMarkMove(object sender, MouseEventArgs e)
    {
        if (_drawing is null)
            return;

        var to = e.GetPosition(_marks);
        switch (_drawing)
        {
            case Polyline line:
                line.Points.Add(to);
                break;
            case Path path:
                path.Data = _tool switch
                {
                    "circle" => new EllipseGeometry(new Rect(_from, to)),
                    "box" => new RectangleGeometry(new Rect(_from, to), StrokeWidth * 2, StrokeWidth * 2),
                    _ => Arrow(_from, to),
                };
                break;
        }
    }

    private void OnMarkUp(object sender, MouseButtonEventArgs e)
    {
        if (_drawing is null)
            return;

        _marks.ReleaseMouseCapture();

        // A click without a drag draws nothing worth keeping.
        var to = e.GetPosition(_marks);
        if (_drawing is Path && (to - _from).Length < 4)
            _marks.Children.Remove(_drawing);
        _drawing = null;
    }

    /// <summary>A line with an open arrowhead at <paramref name="to"/>, its size following the stroke.</summary>
    private Geometry Arrow(Point from, Point to)
    {
        var shape = new PathGeometry();
        var line = new PathFigure { StartPoint = from };
        line.Segments.Add(new LineSegment(to, true));
        shape.Figures.Add(line);

        var along = to - from;
        if (along.Length < 1)
            return shape;
        along.Normalize();

        double head = StrokeWidth * 5;
        var back = to - along * head;
        var across = new Vector(-along.Y, along.X) * head * 0.6;

        var tip = new PathFigure { StartPoint = back + across };
        tip.Segments.Add(new LineSegment(to, true));
        tip.Segments.Add(new LineSegment(back - across, true));
        shape.Figures.Add(tip);
        return shape;
    }

    private TextBox? _typing;

    /// <summary>A box to type in where the click was; Enter, a click elsewhere or another tool keeps it as text.</summary>
    private void StartText(Point at)
    {
        CommitText();

        _typing = new TextBox
        {
            FontSize = Math.Max(18, _marks.Height / 22),
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(_colour),
            CaretBrush = new SolidColorBrush(_colour),
            Background = new SolidColorBrush(Color.FromArgb(0x66, 0, 0, 0)),
            BorderThickness = new Thickness(0),
            MinWidth = 40,
        };
        Canvas.SetLeft(_typing, at.X);
        Canvas.SetTop(_typing, at.Y);
        _typing.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                CommitText();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape)
            {
                if (_typing is not null)
                    _marks.Children.Remove(_typing);
                _typing = null;
                Focus();
                e.Handled = true;
            }
        };
        _marks.Children.Add(_typing);
        _typing.Focus();
    }

    /// <summary>The text being typed, kept as a label - or dropped, if nothing was typed.</summary>
    private void CommitText()
    {
        if (_typing is not { } box)
            return;

        _typing = null;
        int at = _marks.Children.IndexOf(box);
        _marks.Children.Remove(box);

        if (!string.IsNullOrWhiteSpace(box.Text) && at >= 0)
        {
            var label = new TextBlock
            {
                Text = box.Text,
                FontSize = box.FontSize,
                FontWeight = box.FontWeight,
                FontFamily = box.FontFamily,
                Foreground = box.Foreground,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    Color = Colors.Black, ShadowDepth = 0, BlurRadius = 6, Opacity = 0.9,
                },
            };
            Canvas.SetLeft(label, Canvas.GetLeft(box) + 2);   // where the text sat inside the box
            Canvas.SetTop(label, Canvas.GetTop(box) + 1);
            _marks.Children.Insert(at, label);
        }

        Focus();
    }

    private void Undo()
    {
        CommitText();
        if (_marks.Children.Count > 0)
            _marks.Children.RemoveAt(_marks.Children.Count - 1);
    }

    /// <summary>
    /// The picture with its marks, at the picture's own size, either:
    ///
    ///   in place - the same file name and format, so the notes that show it
    ///   still link to it and only need their preview made again (told by
    ///   _replaced). The unmarked picture goes to the Recycle Bin first, so
    ///   a mark in the wrong place can still be taken back from there.
    ///
    ///   or as a copy - a PNG beside it, "2026-10-17_210412 marked.png", dated
    ///   a second after it so it sorts next to it; the original stays as it is.
    /// </summary>
    private void SaveMarkup(bool inPlace)
    {
        CommitText();
        if (_image.Source is not BitmapSource picture)
            return;
        if (_marks.Children.Count == 0)
        {
            Note("Nothing drawn yet.", warn: true);
            return;
        }

        string original = Current;
        try
        {
            var marked = Render(picture);
            string saved;

            if (inPlace)
            {
                // Written beside it first, so a failure leaves the original
                // untouched; then the original binned and this put in its place.
                var written = File.GetLastWriteTime(original);
                string temp = original + ".marking";
                Write(marked, temp, original);

                if (RecycleBin.Send(original) is { } error)
                {
                    File.Delete(temp);
                    Note("Couldn't save: " + error, warn: true);
                    return;
                }

                File.Move(temp, original);
                // Its date moved on a second, not to now: a picture dropped in
                // by hand is ordered by it, and a later one tells the pictures
                // window's thumbnail to be made again.
                File.SetLastWriteTime(original, written.AddSeconds(1));
                saved = original;
            }
            else
            {
                saved = MarkedName(original);
                Write(marked, saved, saved);
                var taken = NotesPictures.TimeFromFileName(original) ?? File.GetLastWriteTime(original);
                File.SetLastWriteTime(saved, taken.AddSeconds(1));
                _paths.Insert(_index + 1, saved);
            }

            _marks.Children.Clear();
            _markup = false;
            _markTools.Visibility = Visibility.Collapsed;
            _viewTools.Visibility = Visibility.Visible;
            _marks.ClearValue(CursorProperty);

            if (inPlace)
                _replaced(saved);
            _changed();
            ShowPicture(_paths.IndexOf(saved));
            Note(inPlace
                ? "Saved - the unmarked picture is in the Recycle Bin"
                : "Saved as " + System.IO.Path.GetFileName(saved));
        }
        catch (Exception ex)
        {
            Note("Couldn't save: " + ex.Message, warn: true);
        }
    }

    /// <summary>The picture and its marks drawn together, at the picture's own size.</summary>
    private BitmapSource Render(BitmapSource picture)
    {
        int width = picture.PixelWidth, height = picture.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawImage(picture, new Rect(0, 0, width, height));
            dc.DrawRectangle(new VisualBrush(_marks)
            {
                Stretch = Stretch.None,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
                ViewportUnits = BrushMappingMode.Absolute,
                Viewport = new Rect(0, 0, width, height),
            }, null, new Rect(0, 0, width, height));
        }

        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    /// <summary>
    /// To <paramref name="path"/>, in the format <paramref name="formatOf"/>'s
    /// name says: a JPEG screenshot stays a JPEG, so its name - which the notes
    /// link to - doesn't have to change. At 95 rather than the ScreenshotQuality
    /// it was taken at: it is being saved a second time, and a JPEG loses a
    /// little more with every save.
    /// </summary>
    private static void Write(BitmapSource picture, string path, string formatOf)
    {
        BitmapEncoder encoder = formatOf.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
                                || formatOf.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            ? new JpegBitmapEncoder { QualityLevel = 95 }
            : new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(picture));
        using var file = File.Create(path);
        encoder.Save(file);
    }

    /// <summary>"name marked.png", or "name marked 2.png" and on when that is taken.</summary>
    private static string MarkedName(string original)
    {
        string folder = System.IO.Path.GetDirectoryName(original)!;
        string stem = System.IO.Path.GetFileNameWithoutExtension(original);
        for (int n = 1; ; n++)
        {
            string name = n == 1 ? $"{stem} marked.png" : $"{stem} marked {n.ToString(CultureInfo.InvariantCulture)}.png";
            string path = System.IO.Path.Combine(folder, name);
            if (!File.Exists(path))
                return path;
        }
    }
}
