using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Runtime.InteropServices;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

internal static class Ui
{
    internal static string WebsiteIconPath(string target)
    {
        Uri uri;
        if (!Uri.TryCreate(target, UriKind.Absolute, out uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)) return null;
        var origin = uri.GetLeftPart(UriPartial.Authority).ToLowerInvariant();
        byte[] hash;
        using (var sha = SHA256.Create()) hash = sha.ComputeHash(Encoding.UTF8.GetBytes(origin));
        var name = BitConverter.ToString(hash).Replace("-", String.Empty).ToLowerInvariant() + ".png";
        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Matches", "icons", name);
    }

    internal static bool CacheWebsiteIcon(string target)
    {
        var path = WebsiteIconPath(target);
        if (path == null || File.Exists(path)) return false;
        var uri = new Uri(target);
        var origin = uri.GetLeftPart(UriPartial.Authority);
        var sources = new[]
        {
            origin + "/favicon.ico",
            "https://www.google.com/s2/favicons?domain_url=" + Uri.EscapeDataString(origin) + "&sz=64"
        };
        foreach (var source in sources)
        {
            try
            {
                var request = (HttpWebRequest)WebRequest.Create(source);
                request.UserAgent = "Mozilla/5.0 Matches/1.0";
                request.Timeout = 5000;
                request.ReadWriteTimeout = 5000;
                using (var response = request.GetResponse())
                using (var input = response.GetResponseStream())
                using (var data = new MemoryStream())
                {
                    var buffer = new byte[8192];
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        if (data.Length + read > 2 * 1024 * 1024) throw new InvalidDataException("网站图标过大");
                        data.Write(buffer, 0, read);
                    }
                    data.Position = 0;
                    using (var image = Image.FromStream(data))
                    using (var bitmap = new Bitmap(64, 64))
                    using (var graphics = Graphics.FromImage(bitmap))
                    {
                        graphics.Clear(Color.Transparent);
                        graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        graphics.DrawImage(image, new Rectangle(0, 0, 64, 64));
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return true;
            }
            catch { }
        }
        return false;
    }

    private static Image GetWebsiteImage(string target, int size)
    {
        var path = WebsiteIconPath(target);
        if (path == null || !File.Exists(path)) return null;
        try
        {
            using (var image = Image.FromFile(path)) return new Bitmap(image, new Size(size, size));
        }
        catch
        {
            try { File.Delete(path); }
            catch { }
            return null;
        }
    }

    internal static string ChromeExecutable()
    {
        var paths = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Google", "Chrome", "Application", "chrome.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Google", "Chrome", "Application", "chrome.exe")
        };
        foreach (var path in paths) if (File.Exists(path)) return path;
        return "chrome.exe";
    }

    internal static GraphicsPath RoundRectangle(Rectangle bounds, int radius)
    {
        var diameter = radius * 2;
        var path = new GraphicsPath();
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    internal static void ApplyRoundedRegion(Control control, int radius)
    {
        if (control.Width <= 0 || control.Height <= 0) return;
        using (var path = RoundRectangle(new Rectangle(0, 0, control.Width, control.Height), radius))
        {
            var old = control.Region;
            control.Region = new Region(path);
            if (old != null) old.Dispose();
        }
    }

    internal static Image PowerImage(string action, int size)
    {
        const int canvas = 128;
        var source = new Bitmap(canvas, canvas);
        using (var graphics = Graphics.FromImage(source))
        using (var pen = new Pen(action == "restart" ? Color.FromArgb(43, 137, 230) : Color.FromArgb(232, 78, 68), 15F))
        {
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            pen.StartCap = pen.EndCap = LineCap.Round;
            if (action == "restart")
            {
                graphics.DrawArc(pen, 22, 22, 84, 84, -58, 285);
                using (var brush = new SolidBrush(pen.Color))
                    graphics.FillPolygon(brush, new[] { new Point(17, 29), new Point(49, 25), new Point(33, 56) });
            }
            else
            {
                graphics.DrawArc(pen, 23, 23, 82, 82, -43, 266);
                graphics.DrawLine(pen, 64, 13, 64, 61);
            }
        }
        var image = new Bitmap(size, size);
        using (var graphics = Graphics.FromImage(image))
        {
            graphics.InterpolationMode = InterpolationMode.HighQualityBicubic;
            graphics.DrawImage(source, new Rectangle(0, 0, size, size));
        }
        source.Dispose();
        return image;
    }

    internal static Image GetImage(string target, int size)
    {
        try
        {
            var websiteImage = GetWebsiteImage(target, size);
            if (websiteImage != null) return websiteImage;
            Uri uri;
            if (Uri.TryCreate(target, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)) target = ChromeExecutable();
            var identifier = typeof(Native.IShellItemImageFactory).GUID;
            Native.IShellItemImageFactory factory;
            if (Native.SHCreateItemFromParsingName(target, IntPtr.Zero, ref identifier, out factory) == 0)
            {
                IntPtr bitmap;
                var result = factory.GetImage(new Native.NativeSize(size, size), 0x00000005, out bitmap);
                Marshal.ReleaseComObject(factory);
                if (result == 0 && bitmap != IntPtr.Zero)
                {
                    try
                    {
                        var source = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                            bitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty,
                            System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
                        var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                        encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(source));
                        using (var stream = new MemoryStream())
                        {
                            encoder.Save(stream);
                            stream.Position = 0;
                            using (var loaded = new Bitmap(stream)) return new Bitmap(loaded);
                        }
                    }
                    finally { Native.DeleteObject(bitmap); }
                }
            }
        }
        catch { }
        return new Bitmap(SystemIcons.Application.ToBitmap(), new Size(size, size));
    }
}
