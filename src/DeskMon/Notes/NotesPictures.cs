using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace DeskMon.Notes;

/// <summary>
/// Shows the screenshots in the notes as wide as the window, sharp, while the
/// notes file keeps the small preview it always had.
///
/// Why not simply store a bigger preview: WPF rewrites every image in the
/// document as PNG on each save, and the save runs on the UI thread every time
/// typing pauses. Measured with five 1080p screenshots, the file and the save
/// went from 2 MB / 25 ms at 480 px to 7.7 MB / 118 ms at 960 px and
/// 13 MB / 220 ms at 1280 px - a stutter after every sentence, growing with
/// each picture added.
///
/// So the two are kept apart. On screen, each screenshot is stretched to most
/// of the editor's width and, where its full-size file is still on disk, shown from
/// that file decoded at exactly that width. For the length of a save, the
/// stored preview and its stored width are put back - checked: the RTF writer
/// records whatever Source and Width are set at that moment, without needing a
/// layout pass, so the bytes come out identical to before and the "has
/// anything changed" comparison in SaveNow is not fooled into rewriting the
/// file because the window was resized.
/// </summary>
internal sealed class NotesPictures
{
    /// <summary>
    /// Narrower images are left at their own size: a small picture pasted in
    /// from elsewhere is an icon or a diagram, not a screenshot, and blowing
    /// it up to the window's width would only blur it.
    /// </summary>
    private const int MinScreenshotWidth = 400;

    /// <summary>
    /// Sharp copies are decoded at widths rounded up to this, so dragging the
    /// window wider re-reads the file every 128 px rather than every pixel.
    /// </summary>
    private const int DecodeStep = 128;

    /// <summary>The date label's size on screen, relative to its size on the 480 px preview.</summary>
    private const double StampOnScreen = 1.15;

    /// <summary>How much of the text column a screenshot is shown across - see Fit.</summary>
    private const double ShownShare = 1.0;

    /// <summary>
    /// Space above and below every picture, so the text before and after it
    /// isn't pressed against its edges. The notes' paragraphs have no spacing
    /// of their own - that keeps text compact - and a picture is a paragraph
    /// like any other, so without this it touched the lines either side.
    ///
    /// On the picture rather than its paragraph, and on screen only: it is
    /// put in place by Fit and taken off for the length of a save, like the
    /// sharp copy, so the file comes out exactly as it did before and opening
    /// notes never looks like a change that needs writing.
    /// </summary>
    private static readonly Thickness PictureGap = new(0, 8, 0, 8);

    /// <summary>
    /// The pictures' rounded corners, and the faint outline drawn round them -
    /// matching the rounded panels everything else in DeskMon sits in.
    ///
    /// Neither can be in the document itself. A picture is stored in the RTF
    /// as a bare image, and wrapping it in a Border would leave the writer
    /// nothing it knows how to save. So the corners are a Clip on the image -
    /// taken off for saves like <see cref="PictureGap"/> - and the outline an
    /// adorner, drawn over it in the editor's own adorner layer: the one its
    /// caret and selection use, inside the scroll viewer, so the outline
    /// scrolls with the picture and is cut off at the editor's edge with it.
    /// </summary>
    private const double PictureCorner = 6;

    private Pen _framePen = FrozenPen(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1);

    /// <summary>
    /// Round the picture under the mouse, as in the pictures window: the accent
    /// the widget's toolbar lights up in, saying this is the one a click opens.
    /// </summary>
    private Pen _hoverPen = FrozenPen(Color.FromRgb(0x60, 0xA5, 0xFA), 2);

    /// <summary>The outlines in the theme's shade and accent, redrawn at once - for the notes window's ApplyTheme.</summary>
    public void ApplyTheme(UiTheme theme)
    {
        _framePen = FrozenPen(theme.IsLight ? Color.FromArgb(0x40, 0, 0, 0) : Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF), 1);
        _hoverPen = FrozenPen(theme.AccentColor, 2);
        _warn = theme.Warn;
        foreach (var (_, frame) in _frames)
            frame.InvalidateVisual();
    }

    // --- The bin ------------------------------------------------------------------
    //
    // The pictures window's bin, on the picture itself: in its bottom-right
    // corner while the mouse is on the picture - across from the date in the
    // bottom-left, level with it - on the same dark badge the pictures window
    // draws its NOT IN NOTES on. A first click arms it - it reads
    // DELETE? in the warning colour - and a second within a few seconds
    // deletes, as there. Drawn by the frame rather than being a button in the
    // document: anything put in the document is saved in the notes file.

    private Brush _warn = Brushes.Orange;

    private Frame? _armed;
    private readonly DispatcherTimer _disarm = new() { Interval = TimeSpan.FromSeconds(4) };

    /// <summary>
    /// Whether <paramref name="point"/>, in the editor's coordinates, is on the
    /// bin of the picture under it - and its plate lit to say so, when
    /// <paramref name="hover"/>; the mouse moving off a picture lets it go.
    /// </summary>
    public bool IsOnBin(Image? image, Point point, bool hover = false)
    {
        bool on = false;
        foreach (var (framed, frame) in _frames)
        {
            bool here = framed == image && frame.Deletable
                        && frame.BinRect().Contains(_editor.TranslatePoint(point, framed));
            if (hover)
                frame.OverBin = here;
            on |= here;
        }
        return on;
    }

    /// <summary>Whether this picture's bin has been clicked once and is waiting for the second.</summary>
    public bool IsArmed(Image image) => _armed is { } frame && _frames.Exists(entry => entry.Image == image && entry.Frame == frame);

    /// <summary>The first click: this picture's bin asks DELETE?, any other's stops asking.</summary>
    public void Arm(Image image)
    {
        Disarm();
        _armed = _frames.Find(entry => entry.Image == image).Frame;
        _armed?.InvalidateVisual();

        _disarm.Tick -= OnDisarm;
        _disarm.Tick += OnDisarm;
        _disarm.Start();
    }

    public void Disarm()
    {
        _disarm.Stop();
        var was = _armed;
        _armed = null;
        was?.InvalidateVisual();
    }

    private void OnDisarm(object? sender, EventArgs e) => Disarm();

    private static Pen FrozenPen(Color colour, double thickness)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        var pen = new Pen(brush, thickness);
        pen.Freeze();
        return pen;
    }

    /// <summary>
    /// The outline round one picture, and its bin while the mouse is on it.
    /// Never hit-tested: clicks go to the editor, which asks
    /// <see cref="IsOnBin"/> before opening the picture.
    /// </summary>
    private sealed class Frame : Adorner
    {
        private readonly NotesPictures _owner;

        public Frame(Image image, NotesPictures owner) : base(image)
        {
            _owner = owner;
            IsHitTestVisible = false;
        }

        private bool _hovered;

        /// <summary>The blue outline instead of the faint one - see <see cref="Hover"/>.</summary>
        public bool Hovered
        {
            get => _hovered;
            set
            {
                if (_hovered == value)
                    return;
                _hovered = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext context)
        {
            var size = AdornedElement.RenderSize;
            if (size.Width < 4 || size.Height < 4)
                return;

            // Half the line's width in, so the line falls on whole pixels
            // rather than blurring across two - and inside the clipped corner,
            // not on it.
            var pen = _hovered ? _owner._hoverPen : _owner._framePen;
            double inset = pen.Thickness / 2;
            var edge = new Rect(size);
            edge.Inflate(-inset, -inset);
            context.DrawRoundedRectangle(null, pen, edge, PictureCorner - inset, PictureCorner - inset);

            if (!Deletable)
                return;

            // The bin and DELETE on their plate - DELETE? in the warning
            // colour once armed. See BinBadge, which the pictures window's
            // thumbnails share.
            bool armed = _owner._armed == this;
            var ink = armed ? _owner._warn : Ui.BinBadge.Ink;
            var text = Label();
            var bin = BinRect();
            context.DrawRoundedRectangle(_overBin ? Ui.BinBadge.PlateHot : Ui.BinBadge.Plate, null, bin,
                                         Ui.BinBadge.Height / 2, Ui.BinBadge.Height / 2);

            var icon = Ui.BinBadge.Icon.Clone();
            icon.Transform = new TranslateTransform(bin.Left + Ui.BinBadge.PadLeft,
                                                    bin.Top + (Ui.BinBadge.Height - Ui.BinBadge.IconSize) / 2);
            context.DrawGeometry(null, new Pen(ink, 1), icon);

            context.DrawText(text, new Point(bin.Left + Ui.BinBadge.PadLeft + Ui.BinBadge.IconSize + Ui.BinBadge.Gap,
                                             bin.Top + (Ui.BinBadge.Height - text.Height) / 2));
        }

        /// <summary>
        /// A screenshot with a file of its own, which the bin can delete - not
        /// a picture pasted in from elsewhere, which has nothing to delete but
        /// the text around it. Set by UpdateFrames.
        /// </summary>
        public bool Deletable
        {
            get => _deletable;
            set
            {
                if (_deletable == value)
                    return;
                _deletable = value;
                InvalidateVisual();
            }
        }

        private bool _deletable;

        /// <summary>The mouse is on the bin: its plate a shade more solid, as a button's would be.</summary>
        public bool OverBin
        {
            get => _overBin;
            set
            {
                if (_overBin == value)
                    return;
                _overBin = value;
                InvalidateVisual();
            }
        }

        private bool _overBin;

        /// <summary>
        /// DELETE, or DELETE? once armed, as it is drawn. Kept: the mouse
        /// moving over the picture asks for it on every move, to find the bin.
        /// </summary>
        private FormattedText Label()
        {
            bool armed = _owner._armed == this;
            var ink = armed ? _owner._warn : Ui.BinBadge.Ink;
            double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

            if (_label is null || _labelArmed != armed || _labelInk != ink || _labelDpi != dpi)
            {
                _label = Ui.BinBadge.Measure(armed, ink, dpi);
                (_labelArmed, _labelInk, _labelDpi) = (armed, ink, dpi);
            }
            return _label;
        }

        private FormattedText? _label;
        private bool _labelArmed;
        private Brush? _labelInk;
        private double _labelDpi;

        /// <summary>
        /// Where the bin is, in the picture's coordinates: its bottom-right
        /// corner, growing leftwards as DELETE becomes DELETE?.
        /// </summary>
        public Rect BinRect()
        {
            var size = AdornedElement.RenderSize;
            double width = Ui.BinBadge.Width(Label());
            return new Rect(size.Width - Ui.BinBadge.Right - width, size.Height - Ui.BinBadge.Bottom - Ui.BinBadge.Height,
                            width, Ui.BinBadge.Height);
        }
    }

    /// <summary>The pictures that have their frame - outline added, corners clipped.</summary>
    private readonly List<(Image Image, Frame Frame)> _frames = new();

    private bool _framesQueued;

    /// <summary>What the file holds for one picture, and what is shown instead.</summary>
    private sealed class Picture
    {
        public required ImageSource Stored { get; init; }
        public required double StoredWidth { get; init; }
        public required double StoredHeight { get; init; }

        /// <summary>Height over width, from the stored preview's pixels.</summary>
        public required double Aspect { get; init; }

        public string? FullPath { get; init; }

        /// <summary>
        /// The time stamped in the preview's corner, to stamp again on the
        /// sharp copy - which is drawn from the unstamped full-size file and
        /// would otherwise lose the date the moment it replaced the preview.
        /// Null for screenshots from before the stamp, which have a caption line.
        /// </summary>
        public DateTime? StampedAt { get; init; }

        /// <summary>Pixel width of the sharp copy on screen now; 0 for none.</summary>
        public int ShownWidth { get; set; }

        /// <summary>Pixel width being decoded right now; 0 for none.</summary>
        public int Loading { get; set; }

        /// <summary>The full-size file's own width, once known: no point decoding wider.</summary>
        public int FileWidth { get; set; } = int.MaxValue;

        /// <summary>
        /// This picture's group in the RTF, learned from a full save - see
        /// NotesPictures.Serialise - and what it was learned for: a preview
        /// swapped by mark-up, or a picture resized, is learned again.
        /// </summary>
        public string? RtfGroup { get; set; }
        public ImageSource? RtfGroupOf { get; set; }
        public double RtfGroupWidth { get; set; }
        public double RtfGroupHeight { get; set; }
    }

    // Weak, so pictures from notes that have been closed or edited away go
    // with their Image rather than living on in here.
    private readonly ConditionalWeakTable<Image, Picture> _pictures = new();
    private readonly RichTextBox _editor;

    /// <summary>What a screenshot is centred in - see CentringMargin. Null centres it in the text column.</summary>
    private readonly FrameworkElement? _page;
    private bool _fitQueued;

    public NotesPictures(RichTextBox editor, FrameworkElement? page = null)
    {
        _editor = editor;
        _page = page;

        // The window being resized, and the scrollbar appearing or going as
        // the notes grow past a screenful - which narrows the text by the
        // scrollbar's width without the editor itself changing size.
        editor.SizeChanged += (_, _) => FitSoon();
        editor.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, e) =>
        {
            if (e.ViewportWidthChange != 0)
                FitSoon();
            else if (e.VerticalChange != 0 || e.ViewportHeightChange != 0)
                SharpenSoon();   // pictures scrolled into view, or out of it
        }));

        // Edits can take a picture out - Backspace, cut - or bring one back:
        // Undo, paste. Its outline has to go with it, or arrive with it.
        editor.TextChanged += (_, _) => FrameSoon();
    }

    /// <summary>
    /// Brings the outlines in line with the pictures, once the edit in hand is
    /// done - coalesced, since typing raises a change per key.
    /// </summary>
    private void FrameSoon()
    {
        if (_framesQueued)
            return;

        _framesQueued = true;
        _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _framesQueued = false;
            UpdateFrames();
        }));
    }

    /// <summary>
    /// Every picture in the notes framed, and no frame left on one that has
    /// gone - deleted, or in notes that were switched away from. A frame left
    /// behind would stay in the adorner layer, holding on to its picture.
    /// </summary>
    private void UpdateFrames()
    {
        var images = Images(_editor.Document).ToList();
        var present = new HashSet<Image>(images.Select(entry => entry.Image));

        for (int i = _frames.Count - 1; i >= 0; i--)
        {
            var (image, frame) = _frames[i];
            if (present.Contains(image))
                continue;

            if (_hovered == frame)
                _hovered = null;
            AdornerLayer.GetAdornerLayer(image)?.Remove(frame);
            (VisualTreeHelper.GetParent(frame) as AdornerLayer)?.Remove(frame);
            image.SizeChanged -= OnFramedSizeChanged;
            _frames.RemoveAt(i);
        }

        // Framed, and given its bin if it is a screenshot with a file of its
        // own - the link the bin's click deletes through.
        foreach (var (image, link) in images)
        {
            Framed(image);
            if (_frames.Find(entry => entry.Image == image).Frame is { } frame)
                frame.Deletable = FullSizeFile(link) is not null;
        }
    }

    private Frame? _hovered;

    /// <summary>
    /// Lights up the outline of the picture under the mouse, or none for null.
    /// Told by the window rather than found from the picture's own mouse
    /// events: in an editable RichTextBox the elements in the document are
    /// disabled, and a disabled element is never told the mouse is over it.
    /// </summary>
    public void Hover(Image? image)
    {
        var frame = image is null ? null : _frames.Find(entry => entry.Image == image).Frame;
        if (frame == _hovered)
            return;

        if (_hovered is not null)
            _hovered.Hovered = false;
        _hovered = frame;
        if (frame is not null)
            frame.Hovered = true;
    }

    /// <summary>Gives a picture its outline and rounded corners, if it hasn't them yet.</summary>
    private void Framed(Image image)
    {
        if (_frames.Exists(entry => entry.Image == image))
            return;

        // Not laid out yet - notes loaded into a window not yet shown. Left
        // for the next pass rather than asked for again now: Fit runs once
        // layout has happened, after every load, resize and paste, and so does
        // every edit. Asking again from here would spin for as long as the
        // window stayed hidden.
        if (AdornerLayer.GetAdornerLayer(image) is not { } layer)
            return;

        var frame = new Frame(image, this);
        layer.Add(frame);
        _frames.Add((image, frame));

        image.SizeChanged += OnFramedSizeChanged;
        ClipCorners(image);
    }

    private void OnFramedSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (sender is Image image)
            ClipCorners(image);
    }

    /// <summary>The corners rounded to the size it is drawn at now.</summary>
    private static void ClipCorners(Image image)
    {
        double width = image.ActualWidth, height = image.ActualHeight;
        if (width < 1 || height < 1)
            return;

        var clip = new RectangleGeometry(new Rect(0, 0, width, height), PictureCorner, PictureCorner);
        clip.Freeze();
        image.Clip = clip;
    }

    /// <summary>
    /// A screenshot just inserted. Its file and preview are known exactly, so
    /// nothing has to be worked out from the document.
    /// </summary>
    public void Add(Image image, BitmapSource preview, string fullPath)
    {
        _pictures.AddOrUpdate(image, new Picture
        {
            Stored = preview,
            StoredWidth = image.Width,
            StoredHeight = image.Height,
            Aspect = (double)preview.PixelHeight / preview.PixelWidth,
            FullPath = fullPath,
            StampedAt = TimeFromFileName(fullPath),
        });
        FitSoon();
    }

    /// <summary>
    /// A picture's file was marked up in place: every picture in the notes
    /// showing it takes <paramref name="preview"/> as what the file stores,
    /// and its sharp copy is made again from the new file. The width and height
    /// the notes file stores for it are kept, so the RTF only changes in the
    /// picture itself. True if the notes show it - and so need saving.
    /// </summary>
    public bool Replace(string fullPath, BitmapSource preview)
    {
        bool found = false;

        foreach (var (image, link) in Images(_editor.Document))
        {
            string? file = FullSizeFile(link) ?? FullSizeFileInCaption(image);
            if (file is null || !string.Equals(Path.GetFullPath(file), Path.GetFullPath(fullPath), StringComparison.OrdinalIgnoreCase))
                continue;

            found = true;
            var known = Track(image, link);
            var fresh = new Picture
            {
                Stored = preview,
                StoredWidth = known?.StoredWidth ?? image.Width,
                StoredHeight = known?.StoredHeight ?? image.Height,
                Aspect = (double)preview.PixelHeight / preview.PixelWidth,
                FullPath = fullPath,
                StampedAt = known?.StampedAt ?? TimeFromFileName(fullPath),
            };
            _pictures.AddOrUpdate(image, fresh);   // ShownWidth 0: Fit loads the sharp copy anew
            image.Source = preview;
        }

        if (found)
            FitSoon();
        return found;
    }

    /// <summary>
    /// Fits the pictures once the current layout has settled. Deferred, because
    /// right after the document is loaded or swapped the editor has no width
    /// yet to fit them to - and coalesced, because a window drag raises a
    /// resize per mouse move.
    /// </summary>
    public void FitSoon()
    {
        if (_fitQueued)
            return;

        _fitQueued = true;
        _editor.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            _fitQueued = false;
            Fit();
        }));
    }

    /// <summary>
    /// Runs <paramref name="save"/> with every picture back as the file holds
    /// it - stored preview, stored width - and restores what was on screen
    /// afterwards, even if the save throws. All on the UI thread and with no
    /// layout pass in between, so nothing on screen flickers.
    /// </summary>
    public T WithStoredPictures<T>(Func<T> save)
        => WithPictures(save, placeholders: false, tracked: null);

    /// <param name="placeholders">
    /// Each tracked picture as a tiny stand-in of its own rather than its
    /// stored preview - see <see cref="Serialise"/>. Its stored width and
    /// height are kept, which is all its place in the RTF depends on.
    /// </param>
    /// <param name="tracked">Filled with the tracked pictures, the i-th wearing placeholder i.</param>
    private T WithPictures<T>(Func<T> save, bool placeholders, List<Picture>? tracked)
    {
        var shown = new List<(Image Image, ImageSource Source, double Width, double Height)>();
        var spaced = new List<(Image Image, Thickness Margin, Geometry? Clip)>();

        var underlined = new List<Hyperlink>();

        foreach (var (image, link) in Images(_editor.Document))
        {
            // Every picture, tracked or not: Fit spaces and rounds them all.
            spaced.Add((image, image.Margin, image.Clip));
            image.ClearValue(FrameworkElement.MarginProperty);
            image.ClearValue(UIElement.ClipProperty);

            // The picture's link as the document had it - see HideUnderline.
            if (link is not null && _linkDecorations.TryGetValue(link, out var original))
            {
                SetLocal(link, Inline.TextDecorationsProperty, original);
                underlined.Add(link);
            }

            if (!_pictures.TryGetValue(image, out var picture))
                continue;

            shown.Add((image, image.Source, image.Width, image.Height));
            image.Source = placeholders ? Placeholder(shown.Count - 1) : picture.Stored;
            tracked?.Add(picture);
            image.Width = picture.StoredWidth;
            image.Height = picture.StoredHeight;
        }

        try
        {
            return save();
        }
        finally
        {
            foreach (var (image, source, width, height) in shown)
            {
                image.Source = source;
                image.Width = width;
                image.Height = height;
            }

            foreach (var (image, margin, clip) in spaced)
            {
                image.Margin = margin;
                image.Clip = clip;
            }

            foreach (var link in underlined)
                link.TextDecorations = NoUnderline;
        }
    }

    // --- Saving without re-encoding every picture ------------------------------
    //
    // WPF's RTF writer encodes every picture as PNG on every save, and the notes
    // save after each pause in typing - twenty screenshots made that a stall of
    // 100 ms or more on the UI thread. A picture's part of the RTF is one group,
    // {\pict\picwgoalN\pichgoalN\pngblip <hex>}, which depends only on the
    // stored preview and its stored size. So each one's group is learned once,
    // from a real full save, and later saves write the document with tiny
    // stand-ins and put the learned groups back in their place - text work only.
    //
    // Learned only from a full save made at the same moment as a stand-in save,
    // with everything outside the picture groups compared between the two. Any
    // difference and the shortcut is never used again for these notes: the file
    // is always byte for byte what the full save would have written.

    /// <summary>A difference turned up between the two saves; full saves only from now on, for this editor.</summary>
    private bool _spliceBroken;

    /// <summary>
    /// The document as RTF, as <c>WithStoredPictures(save)</c> would write
    /// it - but without encoding the pictures again when their groups are
    /// already known. <paramref name="save"/> writes the document as it is.
    /// </summary>
    public byte[] Serialise(Func<byte[]> save)
    {
        if (_spliceBroken)
            return WithStoredPictures(save);

        var tracked = new List<Picture>();
        byte[] quick = WithPictures(save, placeholders: true, tracked);

        // Every picture known at the size it is stored at: the stand-ins swapped
        // for their groups, and that is the save.
        // Equals, not ==: a stored height is usually NaN (unset, so the width
        // decides), and NaN == NaN is false - every save would learn again.
        if (tracked.All(p => p.RtfGroup is not null && ReferenceEquals(p.RtfGroupOf, p.Stored)
                             && p.RtfGroupWidth.Equals(p.StoredWidth) && p.RtfGroupHeight.Equals(p.StoredHeight))
            && Splice(quick, tracked) is { } spliced)
            return spliced;

        // A picture new to these notes, or resized: the full save, as it
        // always was, and the groups learned from it for next time.
        byte[] full = WithStoredPictures(save);
        Learn(quick, full, tracked);
        return full;
    }

    /// <summary>Stand-in i: a few pixels of its own, so its group in the RTF can be told from every other.</summary>
    private static BitmapSource Placeholder(int index)
    {
        while (Placeholders.Count <= index)
        {
            int width = Placeholders.Count + 1;
            var pixels = new byte[width * 4];
            new Random(width).NextBytes(pixels);
            var bitmap = BitmapSource.Create(width, 1, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
            bitmap.Freeze();
            Placeholders.Add(bitmap);
        }

        return Placeholders[index];
    }

    private static readonly List<BitmapSource> Placeholders = new();

    /// <summary>Each stand-in's picture data as WPF writes it, by index - learned from the stand-in saves.</summary>
    private static readonly Dictionary<string, int> PlaceholderData = new(StringComparer.Ordinal);

    /// <summary>
    /// Pairs the two saves' picture groups in order. Everything between the
    /// groups must be the same in both, and so must every group that isn't a
    /// stand-in's; then the stand-ins' places in the full save are the real
    /// pictures' groups, kept on each picture.
    /// </summary>
    private void Learn(byte[] quick, byte[] full, List<Picture> tracked)
    {
        try
        {
            string a = System.Text.Encoding.Latin1.GetString(quick);
            string b = System.Text.Encoding.Latin1.GetString(full);
            var quickGroups = PictGroups(a);
            var fullGroups = PictGroups(b);

            if (quickGroups.Count != fullGroups.Count || quickGroups.Count < tracked.Count)
            {
                Broken("the saves have different numbers of pictures");
                return;
            }

            var learned = new Dictionary<int, string>();
            int quickAt = 0, fullAt = 0;
            for (int k = 0; k < quickGroups.Count; k++)
            {
                var (qs, qe) = quickGroups[k];
                var (fs, fe) = fullGroups[k];

                int between = qs - quickAt;
                if (between != fs - fullAt || string.CompareOrdinal(a, quickAt, b, fullAt, between) != 0)
                {
                    Broken("the text around the pictures differs");
                    return;
                }

                string quickGroup = a[qs..qe];
                string fullGroup = b[fs..fe];

                if (StandIn(quickGroup) is int index && index < tracked.Count)
                    learned[index] = fullGroup;
                else if (quickGroup != fullGroup)
                {
                    Broken("a picture that isn't a stand-in came out differently");
                    return;
                }

                quickAt = qe;
                fullAt = fe;
            }

            if (a.Length - quickAt != b.Length - fullAt
                || string.CompareOrdinal(a, quickAt, b, fullAt, a.Length - quickAt) != 0)
            {
                Broken("the text after the pictures differs");
                return;
            }

            if (learned.Count != tracked.Count)
            {
                Broken("a stand-in wasn't found in the save");
                return;
            }

            foreach (var (index, group) in learned)
            {
                var picture = tracked[index];
                picture.RtfGroup = group;
                picture.RtfGroupOf = picture.Stored;
                picture.RtfGroupWidth = picture.StoredWidth;
                picture.RtfGroupHeight = picture.StoredHeight;
            }
        }
        catch (Exception ex)
        {
            Broken(ex.Message);
        }
    }

    /// <summary>The stand-in save with each stand-in's group replaced by its picture's. Null if a stand-in can't be placed.</summary>
    private static byte[]? Splice(byte[] quick, List<Picture> tracked)
    {
        string text = System.Text.Encoding.Latin1.GetString(quick);
        var result = new System.Text.StringBuilder(text.Length + tracked.Sum(p => p.RtfGroup!.Length));
        int at = 0, placed = 0;

        foreach (var (start, end) in PictGroups(text))
        {
            result.Append(text, at, start - at);
            if (StandIn(text[start..end]) is int index && index < tracked.Count)
            {
                result.Append(tracked[index].RtfGroup);
                placed++;
            }
            else
            {
                result.Append(text, start, end - start);
            }
            at = end;
        }
        result.Append(text, at, text.Length - at);

        // Every picture accounted for exactly once, or this isn't the save it should be.
        return placed == tracked.Count ? System.Text.Encoding.Latin1.GetBytes(result.ToString()) : null;
    }

    /// <summary>Which stand-in a picture group is, from its data; null for a real picture.</summary>
    private static int? StandIn(string group)
    {
        string? data = PictureData(group);
        if (data is null)
            return null;

        if (PlaceholderData.TryGetValue(data, out int index))
            return index;

        // Not seen yet: a stand-in's data is short - a few pixels - and can be
        // matched against the stand-ins made so far by encoding each once.
        if (data.Length > 2048)
            return null;

        for (int i = 0; i < Placeholders.Count; i++)
        {
            if (PlaceholderData.ContainsValue(i))
                continue;
            if (PictureData(GroupOf(Placeholders[i])) is { } known)
            {
                PlaceholderData[known] = i;
                if (known == data)
                    return i;
            }
        }

        return null;
    }

    /// <summary>The group WPF writes for one picture on its own - how a stand-in's data is learned.</summary>
    private static string GroupOf(BitmapSource picture)
    {
        var document = new FlowDocument(new Paragraph(new InlineUIContainer(new Image
        {
            Source = picture,
            Width = picture.PixelWidth,
            Height = picture.PixelHeight,
        })));

        using var stream = new MemoryStream();
        new TextRange(document.ContentStart, document.ContentEnd).Save(stream, DataFormats.Rtf);
        string rtf = System.Text.Encoding.Latin1.GetString(stream.ToArray());
        return PictGroups(rtf) is { Count: > 0 } groups ? rtf[groups[0].Start..groups[0].End] : string.Empty;
    }

    /// <summary>The hex after \pngblip, without the line breaks, or null for a group that isn't a PNG.</summary>
    private static string? PictureData(string group)
    {
        int blip = group.IndexOf(@"\pngblip", StringComparison.Ordinal);
        if (blip < 0)
            return null;

        var data = new System.Text.StringBuilder(group.Length - blip);
        for (int i = blip + @"\pngblip".Length; i < group.Length - 1; i++)
            if (Uri.IsHexDigit(group[i]))
                data.Append(group[i]);
        return data.ToString();
    }

    /// <summary>Every {\pict ...} group, in order, found by counting braces - escaped ones skipped.</summary>
    private static List<(int Start, int End)> PictGroups(string rtf)
    {
        var groups = new List<(int, int)>();
        int from = 0;
        while ((from = rtf.IndexOf(@"{\pict", from, StringComparison.Ordinal)) >= 0)
        {
            int depth = 0, end = -1;
            for (int i = from; i < rtf.Length; i++)
            {
                char c = rtf[i];
                if (c == '\\') { i++; continue; }
                if (c == '{') depth++;
                else if (c == '}' && --depth == 0) { end = i + 1; break; }
            }

            if (end < 0)
                break;   // unbalanced: not one to touch
            groups.Add((from, end));
            from = end;
        }
        return groups;
    }

    private void Broken(string why)
    {
        _spliceBroken = true;
        App.Log("notes save shortcut", $"Turned off for this session: {why}. Saves write every picture again, as before.");
    }

    /// <summary>Sets a property back to exactly what it was - including "never set", which a plain assignment can't express.</summary>
    private static void SetLocal(DependencyObject target, DependencyProperty property, object value)
    {
        if (value == DependencyProperty.UnsetValue)
            target.ClearValue(property);
        else
            target.SetValue(property, value);
    }

    // --- The link underline under a picture ----------------------------------

    /// <summary>
    /// What each picture's link had for TextDecorations before the underline
    /// came off - "never set", usually, from the link's own style - so a save
    /// can put back exactly that and write the file as it always did.
    /// </summary>
    private readonly ConditionalWeakTable<Hyperlink, object> _linkDecorations = new();

    private static readonly TextDecorationCollection NoUnderline = FrozenNone();

    private static TextDecorationCollection FrozenNone()
    {
        var none = new TextDecorationCollection();
        none.Freeze();
        return none;
    }

    /// <summary>
    /// Takes the underline off a link that is nothing but a picture.
    ///
    /// A screenshot is its own link, so a click opens it full size, and WPF
    /// underlines every link - a picture included, along the bottom of its
    /// line. Once pictures had space under them, that line sat a few pixels
    /// below each one and read as a separator. A link with text in it keeps
    /// its underline: there it says "this is a link".
    /// </summary>
    private void HideUnderline(Hyperlink? link)
    {
        if (link is null || _linkDecorations.TryGetValue(link, out _))
            return;

        if (link.Inlines.Count != 1 || link.Inlines.FirstInline is not InlineUIContainer)
            return;

        _linkDecorations.Add(link, link.ReadLocalValue(Inline.TextDecorationsProperty));
        link.TextDecorations = NoUnderline;
    }

    private void Fit()
    {
        // The text column, ShownShare of it - all of it now: at 90% the
        // pictures looked small next to the page's edges. The column is the
        // scroll viewport, less a couple of pixels so a picture can never be
        // quite as wide as the line and push the caret after it onto a line
        // of its own.
        double column = Math.Floor(_editor.ViewportWidth) - 4;

        // The width is worked out as though the scrollbar were always there.
        // Sized to the column as it is, notes just past a screenful bounced:
        // the scrollbar came, the pictures narrowed, the notes got shorter,
        // the scrollbar went, the pictures widened - and round again, every
        // layout pass. Only the centring below follows the real column, and a
        // margin doesn't change how tall anything is, so that can't loop.
        double width = Math.Floor((column - HiddenScrollBarWidth()) * ShownShare);
        if (width < 50)
            return;   // not laid out yet, or squeezed to nothing

        // Centred by the space to its left rather than by the paragraph's
        // alignment. Centring the paragraph would be saved in the file - and
        // pictures in notes written before would differ from it the moment
        // they were opened, so merely opening them would rewrite them. The
        // margin, like the rest of the spacing, is on screen only.
        bool moved = false;

        foreach (var (image, link) in Images(_editor.Document))
        {
            HideUnderline(link);

            var picture = Track(image, link);

            // Every picture is spaced from the text; only a screenshot is
            // centred. A small picture can sit in a line of text, and pushing
            // it along by half the window would split the line.
            var margin = picture is null
                ? PictureGap
                : new Thickness(CentringMargin(image, column, width), PictureGap.Top, 0, PictureGap.Bottom);
            if (image.Margin != margin)
            {
                moved |= picture is not null;
                image.Margin = margin;
            }

            if (picture is null)
                continue;

            // Height set as well, not left to Stretch: an image read back from
            // RTF comes with a fixed height and Stretch.Fill, so widening it
            // alone squashed it into a strip.
            double height = Math.Round(width * picture.Aspect);
            if (image.Width != width || image.Height != height)
            {
                image.Width = width;
                image.Height = height;
            }

        }

        // Only for the pictures on or near the screen - see SharpenVisible.
        SharpenVisible();

        // After the sizes above, so the corners are clipped to the new ones.
        UpdateFrames();

        // The centring is measured from where a picture was last drawn, so a
        // picture just put in or just widened is measured once more after it
        // has been laid out. Settles in a pass: the second finds nothing to move.
        if (moved)
            FitSoon();
    }

    /// <summary>
    /// The space left of a picture that puts it in the middle of the page the
    /// notes are written on - not of the text column. The column sits off
    /// centre in the page: more padding to its left than its right, and the
    /// scrollbar's room on the right. Centred in the column, a picture
    /// filling it sat visibly to the right. Held within the column, so the
    /// picture never runs past the line and wraps.
    /// </summary>
    ///
    /// Measured off the picture itself: where it is drawn on the page, less
    /// the margin it has now, is where its line starts. That takes in every
    /// inset on the way - the page's padding, the editor's, the document's -
    /// without knowing any of them. Looking the column up through the
    /// editor's template instead found the wrong edge and left the pictures
    /// where they were. Stable: the line's start doesn't move with the margin,
    /// so the next pass works out the same figure.
    /// </summary>
    private double CentringMargin(Image image, double column, double width)
    {
        double inColumn = Math.Floor((column - width) / 2);
        if (_page is null || _page.ActualWidth <= 0 || !image.IsVisible)
            return inColumn;

        try
        {
            double lineStart = image.TranslatePoint(new Point(0, 0), _page).X - image.Margin.Left;
            double margin = Math.Floor((_page.ActualWidth - width) / 2 - lineStart);
            return Math.Clamp(margin, 0, Math.Max(0, column - width));
        }
        catch (InvalidOperationException)
        {
            return inColumn;   // not in the page's tree yet - mid load
        }
    }

    /// <summary>
    /// How much narrower the column would be with the scrollbar showing: 0
    /// while it shows, else its width. Measured off the bar the last time it
    /// was up, since the theme draws its own and the system's figure is wider.
    /// </summary>
    private double HiddenScrollBarWidth()
    {
        _scroller ??= FindScroller(_editor);
        if (_scroller is null)
            return SystemParameters.VerticalScrollBarWidth;

        if (_scroller.ComputedVerticalScrollBarVisibility == Visibility.Visible)
        {
            double bar = _scroller.ActualWidth - _scroller.ViewportWidth;
            if (bar > 0)
                _scrollBarWidth = bar;
            return 0;
        }

        // Never shown yet: the bar's own width from its style - 10, a third
        // less than the system's 17, which held every picture in notes too
        // short to scroll 7 px narrower than it needed to be.
        if (_scrollBarWidth is null
            && _scroller.Template?.FindName("PART_VerticalScrollBar", _scroller) is System.Windows.Controls.Primitives.ScrollBar part
            && part.Width > 0)
        {
            _scrollBarWidth = part.Width;
        }

        return _scrollBarWidth ?? SystemParameters.VerticalScrollBarWidth;
    }

    private ScrollViewer? _scroller;
    private double? _scrollBarWidth;

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer found)
                return found;
            if (FindScroller(child) is { } deeper)
                return deeper;
        }
        return null;
    }

    /// <summary>
    /// What is known about this picture, starting to track it if it is a
    /// screenshot seen for the first time - loaded from the file, or pasted.
    /// Null for an image too small to be one.
    /// </summary>
    private Picture? Track(Image image, Hyperlink? link)
    {
        if (_pictures.TryGetValue(image, out var known))
            return known;

        if (image.Source is not BitmapSource source || source.PixelWidth < MinScreenshotWidth)
            return null;

        // The picture being its own link is how screenshots are stored since
        // the date moved into the picture; the older ones, linked from a
        // caption, have the date in that caption and no stamp to repeat.
        string? ownFile = FullSizeFile(link);

        var picture = new Picture
        {
            Stored = source,
            StoredWidth = image.Width,
            StoredHeight = image.Height,
            Aspect = (double)source.PixelHeight / source.PixelWidth,
            FullPath = ownFile ?? FullSizeFileInCaption(image),
            StampedAt = ownFile is null ? null : TimeFromFileName(ownFile),
        };
        _pictures.Add(image, picture);
        return picture;
    }

    private bool _sharpenQueued;

    /// <summary>Once scrolling pauses for a frame, rather than for every pixel of it.</summary>
    private void SharpenSoon()
    {
        if (_sharpenQueued)
            return;

        _sharpenQueued = true;
        _editor.Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _sharpenQueued = false;
            SharpenVisible();
        }));
    }

    /// <summary>
    /// Sharp copies for the pictures on screen or a screen away from it, and
    /// back to the stored preview for the ones scrolled well out of sight.
    ///
    /// Every picture in the notes used to get its sharp copy, seen or not -
    /// each one first decoded from the full 4K file, a third of a gigabyte of
    /// memory churned for notes with a few dozen screenshots, and all of them
    /// kept. The preview stays in place meanwhile, so a picture scrolled to
    /// shows at once, softly, and sharpens a moment later. A wide gap between
    /// "load" and "drop" keeps a picture at the edge from flipping back and forth.
    /// </summary>
    private void SharpenVisible()
    {
        double screen = _editor.ViewportHeight;
        if (screen <= 0)
            return;   // not laid out yet - its first layout changes the viewport, and ScrollChanged comes back here

        foreach (var (image, link) in Images(_editor.Document))
        {
            if (!_pictures.TryGetValue(image, out var picture) || double.IsNaN(image.Width))
                continue;

            double distance = DistanceFromView(image, screen);
            if (distance <= screen)
            {
                LoadSharp(image, picture, image.Width);
            }
            else if (distance > screen * 3 && (picture.ShownWidth > 0 || picture.Loading > 0))
            {
                // Out of sight: the preview the notes store is enough until it comes back.
                picture.Loading = 0;   // one still decoding is not wanted any more
                picture.ShownWidth = 0;
                image.Source = picture.Stored;
            }
        }
    }

    /// <summary>
    /// How far the picture is from the visible part of the notes, in pixels -
    /// 0 when any of it is on screen. Infinitely far when it isn't laid out.
    /// </summary>
    private double DistanceFromView(Image image, double screen)
    {
        try
        {
            double top = image.TranslatePoint(new Point(0, 0), _editor).Y;
            double bottom = top + image.ActualHeight;
            return bottom < 0 ? -bottom : top > screen ? top - screen : 0;
        }
        catch (InvalidOperationException)
        {
            return double.PositiveInfinity;   // not in the editor's visual tree
        }
    }

    /// <summary>
    /// Replaces the stretched preview with the full-size file decoded at the
    /// width it is shown at - in the background, since a 1440p PNG takes a
    /// while to read. If the file has been moved or deleted, the preview just
    /// stays, scaled up.
    /// </summary>
    private static readonly System.Threading.SemaphoreSlim DecodeGate = new(2);

    private async void LoadSharp(Image image, Picture picture, double width)
    {
        if (picture.FullPath is not { } path)
            return;

        double scale = VisualTreeHelper.GetDpi(_editor).DpiScaleX;
        int wanted = (int)Math.Ceiling(width * scale / DecodeStep) * DecodeStep;
        wanted = Math.Min(wanted, picture.FileWidth);

        if (picture.ShownWidth >= wanted || picture.Loading >= wanted)
            return;   // what is shown, or already on its way, is enough

        picture.Loading = wanted;
        var stamp = picture.StampedAt;

        // Two at a time. Notes with forty screenshots asked for forty at once
        // on opening: every full-size file decoded together, a few hundred MB
        // for a moment and every core busy - for pictures mostly off screen.
        await DecodeGate.WaitAsync();
        (BitmapSource? Image, int FileWidth) decoded;
        try
        {
            // Asked for wider while it waited: that request decodes instead.
            if (picture.Loading != wanted)
                return;
            decoded = await Task.Run(() => Decode(path, wanted, stamp, width));
        }
        finally
        {
            DecodeGate.Release();
        }
        var (sharp, fileWidth) = decoded;

        // Still the copy wanted? Not if the picture was scrolled out of sight
        // meanwhile (SharpenVisible let it go) or a wider one was asked for.
        bool stillWanted = picture.Loading == wanted;
        if (stillWanted)
            picture.Loading = 0;
        if (fileWidth > 0)
            picture.FileWidth = fileWidth;

        if (!stillWanted)
            return;

        // A narrower copy finishing after a wider one must not replace it.
        if (sharp is null || sharp.PixelWidth <= picture.ShownWidth)
            return;

        picture.ShownWidth = sharp.PixelWidth;
        image.Source = sharp;
    }

    /// <summary>
    /// The file at no more than <paramref name="width"/> pixels - never wider
    /// than it really is - with the date stamped as on the preview. Through
    /// the same code as the preview (Screenshot.Scaled), so the label matches.
    /// The file is read in full and closed at once, so it isn't held open and
    /// can still be moved or deleted. Null if it can't be read.
    /// </summary>
    /// <param name="shownWidth">The width it will be drawn at, in DIPs.</param>
    private static (BitmapSource? Image, int FileWidth) Decode(string path, int width, DateTime? stamp, double shownWidth)
    {
        try
        {
            if (!File.Exists(path))
                return (null, 0);

            using var full = new System.Drawing.Bitmap(path);

            // The label at a fixed size on screen, whatever the picture's:
            // decoded pixels per shown DIP undoes both the DPI scale and the
            // rounding up to DecodeStep. The 1.15 is the size it has on a
            // 480 px preview stretched to the default ~550 px window, so a
            // stamp looks the same on the preview and on the sharp copy.
            double decoded = Math.Min(width, full.Width);
            double stampScale = decoded / shownWidth * StampOnScreen;

            return (Screenshot.Scaled(full, width, stamp, stampScale), full.Width);
        }
        catch
        {
            return (null, 0);
        }
    }

    /// <summary>
    /// The file names of the full-size pictures this document links to - the
    /// pictures window's "in the notes". Names rather than paths, as in
    /// NotesStore.ScreenshotNamesInNotes.
    /// </summary>
    public static HashSet<string> FileNamesIn(FlowDocument document)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (image, link) in Images(document))
            if ((FullSizeFile(link) ?? FullSizeFileInCaption(image)) is { } path)
                names.Add(Path.GetFileName(path));
        return names;
    }

    /// <summary>
    /// Whether the document holds any picture at all - screenshot or pasted.
    /// A picture adds nothing to the document's text, so notes that are only
    /// pictures read as empty to a text check; see NotesWindow.SaveNow.
    /// </summary>
    public static bool HasAny(FlowDocument document) => Images(document).Any();

    /// <summary>
    /// Takes the picture of <paramref name="fileName"/> out of the document,
    /// for a picture deleted in the pictures window - so the notes are not
    /// left with one whose file has gone. Its whole paragraph goes when the
    /// picture is all it holds, which is how screenshots are put in, and so
    /// does an old-style "open full size" caption under it; otherwise only
    /// the picture. True if it was found.
    /// </summary>
    public static bool Remove(FlowDocument document, string fileName)
    {
        foreach (var (image, link) in Images(document).ToList())
        {
            string? own = FullSizeFile(link);
            string? caption = own is null ? FullSizeFileInCaption(image) : null;
            string? path = own ?? caption;
            if (path is null || !string.Equals(Path.GetFileName(path), fileName, StringComparison.OrdinalIgnoreCase))
                continue;

            DependencyObject? node = image;
            while (node is not null and not Paragraph)
                node = LogicalTreeHelper.GetParent(node);

            if (node is not Paragraph paragraph)
                return false;

            if (caption is not null && paragraph.NextBlock is Paragraph captionLine)
                captionLine.SiblingBlocks.Remove(captionLine);

            // The picture alone - possibly with blank runs around it - takes
            // its paragraph with it; a picture sharing a line with text leaves
            // the text.
            if (new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.Trim().Length == 0)
                paragraph.SiblingBlocks.Remove(paragraph);
            else if (link is not null)
                link.SiblingInlines.Remove(link);
            else if (LogicalTreeHelper.GetParent(image) is InlineUIContainer container)
                container.SiblingInlines.Remove(container);

            return true;
        }

        return false;
    }

    /// <summary>"2026-10-17_210412.png" as the time it names - see NotesStore.ScreenshotPathFor.</summary>
    public static DateTime? TimeFromFileName(string path)
        => DateTime.TryParseExact(Path.GetFileNameWithoutExtension(path), "yyyy-MM-dd_HHmmss",
                                  System.Globalization.CultureInfo.InvariantCulture,
                                  System.Globalization.DateTimeStyles.None, out var time)
            ? time
            : null;

    /// <summary>
    /// Every image in the document, with the link around it if there is one.
    /// Walked through the logical tree, so pictures inside lists and the
    /// to-do items are found as well as top-level ones.
    /// </summary>
    private static IEnumerable<(Image Image, Hyperlink? Link)> Images(FlowDocument document)
    {
        var pending = new Stack<(DependencyObject Node, Hyperlink? Link)>();
        pending.Push((document, null));

        while (pending.Count > 0)
        {
            var (node, link) = pending.Pop();

            if (node is Image image)
            {
                yield return (image, link);
                continue;
            }

            var inside = node as Hyperlink ?? link;
            foreach (object child in LogicalTreeHelper.GetChildren(node))
                if (child is DependencyObject next)
                    pending.Push((next, inside));
        }
    }

    /// <summary>The picture's own link - how screenshots are stored from 1.42 on.</summary>
    private static string? FullSizeFile(Hyperlink? link)
        => link?.NavigateUri is { } uri ? LocalPictureFile(uri) : null;

    /// <summary>
    /// The picture a link points at, if it is one DeskMon may open: a .png or
    /// .jpg on a drive of this PC. Null for anything else.
    ///
    /// Not on the network, because the sharp copy is read as soon as the notes
    /// are opened - no click needed - and opening a file on \\server\ makes
    /// Windows sign in to that server, handing it a hash of your password. A
    /// link like that can come in with text pasted from anywhere. Mapped
    /// drives are network shares too. Not limited to the notes folder: a
    /// folder that has been moved since leaves every stored path pointing at
    /// the old place, and those pictures should still show.
    /// </summary>
    internal static string? LocalPictureFile(Uri uri)
    {
        if (!uri.IsAbsoluteUri || !uri.IsFile || uri.IsUnc)
            return null;

        string path = uri.LocalPath;
        if (!IsPictureFile(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return null;   // \\?\UNC\ and device paths as well as plain shares

        return Path.GetPathRoot(path) is { Length: > 0 } root && IsLocalDrive(root) ? path : null;
    }

    /// <summary>
    /// Asked once per drive: this runs for every picture after every
    /// keystroke, and a drive doesn't change from local to network while
    /// DeskMon runs - near enough.
    /// </summary>
    private static bool IsLocalDrive(string root)
        => LocalDrives.GetOrAdd(root.ToUpperInvariant(), r =>
        {
            try
            {
                return new DriveInfo(r).DriveType is DriveType.Fixed or DriveType.Removable or DriveType.Ram or DriveType.CDRom;
            }
            catch
            {
                return false;
            }
        });

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, bool> LocalDrives = new();

    /// <summary>
    /// Screenshots from before 1.42 had their link in a caption under the
    /// picture ("open full size") instead - the paragraph right after it.
    /// </summary>
    private static string? FullSizeFileInCaption(Image image)
    {
        DependencyObject? node = image;
        while (node is not null and not Paragraph)
            node = LogicalTreeHelper.GetParent(node);

        if (node is not Paragraph { NextBlock: Paragraph caption })
            return null;

        foreach (var inline in caption.Inlines)
            if (inline is Hyperlink link && FullSizeFile(link) is { } path)
                return path;

        return null;
    }

    private static bool IsPictureFile(string path)
        => path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase);
}
