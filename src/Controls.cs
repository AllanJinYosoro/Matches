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

internal sealed class BufferedListView : ListView
{
    internal BufferedListView() { DoubleBuffered = true; }
}

internal sealed class SearchResult : IDisposable
{
    internal readonly string Path;
    internal readonly string Name;
    internal readonly string Directory;
    internal readonly string PowerAction;
    internal Image Image;

    internal SearchResult(string path, string name, string directory, string powerAction = null)
    {
        Path = path;
        Name = name;
        Directory = directory;
        PowerAction = powerAction;
    }

    public void Dispose()
    {
        if (Image != null) { Image.Dispose(); Image = null; }
    }
}
