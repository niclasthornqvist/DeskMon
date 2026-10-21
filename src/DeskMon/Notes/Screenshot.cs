using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using Drawing = System.Drawing;

namespace DeskMon.Notes;

/// <summary>
/// Takes a screenshot of the monitor a window is on, for pasting into the
/// notes: the full-size image saved beside the notes (PNG or JPEG, see
/// <see cref="Save"/>), and a smaller copy to show inside them.
///
/// Two sizes because of how the notes are stored. RTF keeps images inside
/// the file, and WPF re-encodes every one as PNG: a full 1440p screenshot is
/// several megabytes of the notes file, rewritten on every autosave, which
/// would make typing in a note with a few screenshots in it lag. A 480-pixel
/// preview is a few hundred kilobytes and enough to recognise the scene; the
/// full image is a click away.
/// </summary>
internal static class Screenshot
{
    /// <summary>Width of the copy shown in the notes.</summary>
    public const int PreviewWidth = 480;

    /// <summary>
    /// Captures the whole monitor <paramref name="window"/> is on. Physical
    /// pixels: DeskMon is per-monitor DPI aware, so the monitor rectangle and
    /// the capture agree.
    /// </summary>
    public static Drawing.Bitmap CaptureMonitorOf(IntPtr window)
    {
        IntPtr monitor = MonitorFromWindow(window, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref info))
            throw new InvalidOperationException("Could not find the monitor to capture.");

        var area = info.rcMonitor;
        int width = area.Right - area.Left;
        int height = area.Bottom - area.Top;

        var bitmap = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format24bppRgb);
        using var canvas = Drawing.Graphics.FromImage(bitmap);
        canvas.CopyFromScreen(area.Left, area.Top, 0, 0, new Drawing.Size(width, height),
                              Drawing.CopyPixelOperation.SourceCopy);
        return bitmap;
    }

    /// <summary>
    /// Whether the capture came back black. Windows' screen capture can't see
    /// into a game running in exclusive fullscreen, and returns black for it
    /// rather than failing - which would otherwise be pasted into the notes as
    /// a black rectangle with no explanation.
    /// </summary>
    public static bool IsBlank(Drawing.Bitmap bitmap)
    {
        for (int y = 1; y < 10; y++)
            for (int x = 1; x < 10; x++)
            {
                var pixel = bitmap.GetPixel(bitmap.Width * x / 10, bitmap.Height * y / 10);
                if (pixel.R > 8 || pixel.G > 8 || pixel.B > 8)
                    return false;
            }

        return true;
    }

    /// <summary>
    /// The full-size file's extension for a quality setting. 100 is a
    /// lossless PNG; anything lower is a JPEG at that quality. One number
    /// rather than a format and a quality, because "how much space do I want
    /// to spend" is the only question anyone is asking - and PNG has no
    /// quality to set.
    /// </summary>
    public static string ExtensionFor(int quality) => quality >= 100 ? ".png" : ".jpg";

    /// <summary>
    /// Saves the full-size image, in the format <see cref="ExtensionFor"/>
    /// names. Measured on a 1080p desktop: PNG 549 KB, JPEG 90 133 KB,
    /// JPEG 50 76 KB. Game scenes compress far worse as PNG, so there the gap
    /// is wider still.
    /// </summary>
    public static void Save(Drawing.Bitmap bitmap, string path, int quality)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (quality >= 100)
        {
            bitmap.Save(path, Drawing.Imaging.ImageFormat.Png);
            return;
        }

        var jpeg = Array.Find(Drawing.Imaging.ImageCodecInfo.GetImageEncoders(),
                              codec => codec.FormatID == Drawing.Imaging.ImageFormat.Jpeg.Guid)!;

        // Clamped, because settings.json can be edited by hand: the encoder
        // takes 0-100, and 0 is a smear nobody would want.
        using var parameters = new Drawing.Imaging.EncoderParameters(1);
        parameters.Param[0] = new Drawing.Imaging.EncoderParameter(
            Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 10, 99));
        bitmap.Save(path, jpeg, parameters);
    }

    /// <summary>
    /// The copy for the notes, at most <see cref="PreviewWidth"/> wide, with
    /// the time it was taken stamped in its corner, as a frozen WPF image so
    /// it can cross from the capture thread to the UI.
    /// </summary>
    public static BitmapSource Preview(Drawing.Bitmap bitmap, DateTime takenAt)
        => Scaled(bitmap, PreviewWidth, takenAt);

    /// <summary>
    /// <paramref name="bitmap"/> at no more than <paramref name="maxWidth"/>
    /// pixels, with the time stamped in its corner when one is given. Also
    /// what NotesPictures draws the sharp on-screen copy with, so the stamp
    /// looks the same there as on the preview.
    /// </summary>
    /// <param name="stampScale">
    /// How big the label is drawn, 1 being its size on the preview. The
    /// on-screen copy works this out so the label keeps one size on screen
    /// however wide the window makes the picture - grown with the picture, it
    /// was twice that at 1100 px and shouted.
    /// </param>
    public static BitmapSource Scaled(Drawing.Bitmap bitmap, int maxWidth, DateTime? takenAt, double stampScale = 1)
    {
        int width = Math.Min(maxWidth, bitmap.Width);
        int height = Math.Max(1, (int)Math.Round((double)bitmap.Height * width / bitmap.Width));

        using var small = new Drawing.Bitmap(width, height, Drawing.Imaging.PixelFormat.Format24bppRgb);
        using (var canvas = Drawing.Graphics.FromImage(small))
        {
            canvas.InterpolationMode = Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            canvas.DrawImage(bitmap, 0, 0, width, height);
            if (takenAt is { } time)
                StampTime(canvas, width, height, time, stampScale);
        }

        using var stream = new MemoryStream();
        small.Save(stream, Drawing.Imaging.ImageFormat.Png);
        stream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;   // read now, so the stream can go
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    /// <summary>
    /// The date and time in the bottom-left corner of the preview, in place
    /// of a caption line under it: the picture carries its own date, and the
    /// notes have one less line of boilerplate per screenshot. Left rather
    /// than right since 1.59: the notes window puts the picture's bin in the
    /// bottom-right corner (NotesPictures), and the two read as one row.
    ///
    /// Only on the preview and on the sharp copy the notes window draws in its
    /// place (NotesPictures). The full-size file is left exactly as captured -
    /// it is the one you'd share or set as a wallpaper - and its file name
    /// already holds the same time.
    ///
    /// White on a translucent dark plate, so it reads on a bright sky and a
    /// dark cave alike, and is part of the image itself: the RTF keeps it, so
    /// it shows the same in WordPad.
    /// </summary>
    private static void StampTime(Drawing.Graphics canvas, int width, int height, DateTime takenAt, double scale)
    {
        string text = takenAt.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture);

        canvas.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        // Grid-fitted, not ClearType: ClearType's colour fringes are tuned for
        // the screen it was drawn on and look wrong once baked into a picture.
        canvas.TextRenderingHint = Drawing.Text.TextRenderingHint.AntiAliasGridFit;

        float s = (float)scale;
        using var font = new Drawing.Font("Segoe UI", 8.5f * s, Drawing.FontStyle.Regular, Drawing.GraphicsUnit.Point);
        var size = canvas.MeasureString(text, font);

        float padX = 6 * s, padY = 2 * s, margin = 6 * s, radius = 4 * s;
        float plateWidth = size.Width + padX * 2;
        float plateHeight = size.Height + padY * 2;
        float left = margin;
        float top = height - plateHeight - margin;

        using (var plate = new Drawing.Drawing2D.GraphicsPath())
        {
            float d = radius * 2;
            plate.AddArc(left, top, d, d, 180, 90);
            plate.AddArc(left + plateWidth - d, top, d, d, 270, 90);
            plate.AddArc(left + plateWidth - d, top + plateHeight - d, d, d, 0, 90);
            plate.AddArc(left, top + plateHeight - d, d, d, 90, 90);
            plate.CloseFigure();

            using var fill = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xA6, 0x10, 0x13, 0x1A));
            canvas.FillPath(fill, plate);
        }

        using var ink = new Drawing.SolidBrush(Drawing.Color.FromArgb(0xF0, 0xE6, 0xEA, 0xF0));
        canvas.DrawString(text, font, ink, left + padX, top + padY);
    }

    /// <summary>
    /// Keeps DeskMon's own windows out of a capture, or lets them back in.
    ///
    /// The widget and the notepad sit on top of the game, so a plain capture
    /// would have them in it - a screenshot of the notes pasted into the notes.
    /// Excluded from capture, they stay on screen for you and vanish from the
    /// image. Switched back on afterwards, because left excluded they would
    /// also vanish from your own screenshots and from a stream.
    /// </summary>
    public static void ExcludeFromCapture(IEnumerable<IntPtr> windows, bool exclude)
    {
        foreach (var window in windows)
            if (window != IntPtr.Zero)
                SetWindowDisplayAffinity(window, exclude ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE);
    }

    private const uint WDA_NONE = 0x00;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;
    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);
}
