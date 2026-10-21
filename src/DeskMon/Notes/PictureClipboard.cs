using System;
using System.Collections.Specialized;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DeskMon.Notes;

/// <summary>
/// A picture onto the clipboard, for pasting into a chat, a document or Paint.
///
/// Three ways at once, since programs differ in what they take: the image
/// itself (Paint, Word), as PNG too (browsers and Discord, which keep the
/// quality and the size), and the file (Explorer, and chat programs that
/// upload a file rather than an image). Each program pastes the first of
/// them it understands.
/// </summary>
public static class PictureClipboard
{
    /// <summary>Null when it worked, else why not, in a few words.</summary>
    public static string? Copy(string path)
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(path);

            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = new MemoryStream(bytes);
            image.EndInit();
            image.Freeze();

            // PNG as PNG, anything else re-encoded, so "PNG" never holds a JPEG.
            var png = new MemoryStream();
            if (path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
                png.Write(bytes);
            else
            {
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(image));
                encoder.Save(png);
            }
            png.Position = 0;

            var data = new DataObject();
            data.SetImage(image);
            data.SetData("PNG", png);
            data.SetFileDropList(new StringCollection { path });

            // Another program can be holding the clipboard for a moment -
            // a clipboard manager, a remote desktop - so a couple more tries.
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    Clipboard.SetDataObject(data, copy: true);
                    return null;
                }
                catch (System.Runtime.InteropServices.COMException) when (attempt < 3)
                {
                    Thread.Sleep(60);
                }
            }
        }
        catch (Exception ex)
        {
            return ex is System.Runtime.InteropServices.COMException
                ? "another program is using the clipboard"
                : ex.Message;
        }
    }
}
