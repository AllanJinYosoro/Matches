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

internal sealed class ShortcutItem
{
    internal readonly string Name;
    internal readonly string Target;

    internal ShortcutItem(string name, string target)
    {
        Name = name;
        Target = target;
    }
}

internal sealed class UrlShortcutDialog : Form
{
    private readonly TextBox name = new TextBox();
    private readonly TextBox address = new TextBox();
    internal ShortcutItem Shortcut { get; private set; }

    internal UrlShortcutDialog()
    {
        Text = "添加网页网址";
        ClientSize = new Size(420, 145);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label { Text = "名称", Location = new Point(18, 20), AutoSize = true });
        name.Location = new Point(72, 16);
        name.Size = new Size(328, 25);
        name.Text = "ChatGPT";
        Controls.Add(name);
        Controls.Add(new Label { Text = "网址", Location = new Point(18, 59), AutoSize = true });
        address.Location = new Point(72, 55);
        address.Size = new Size(328, 25);
        address.Text = "https://chatgpt.com";
        Controls.Add(address);

        var okay = new Button { Text = "添加", Location = new Point(244, 101), Size = new Size(74, 28) };
        var cancel = new Button { Text = "取消", Location = new Point(326, 101), Size = new Size(74, 28), DialogResult = DialogResult.Cancel };
        okay.Click += delegate
        {
            var url = address.Text.Trim();
            if (!url.Contains("://")) url = "https://" + url;
            Uri parsed;
            if (name.Text.Trim().Length == 0 || !Uri.TryCreate(url, UriKind.Absolute, out parsed) ||
                (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
            {
                MessageBox.Show(this, "请输入名称和有效的 http/https 网址。", "Matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Shortcut = new ShortcutItem(name.Text.Trim(), parsed.AbsoluteUri);
            DialogResult = DialogResult.OK;
        };
        Controls.Add(okay);
        Controls.Add(cancel);
        AcceptButton = okay;
        CancelButton = cancel;
    }
}

internal static class ShortcutStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Matches", "shortcuts.txt");

    internal static List<ShortcutItem> Load()
    {
        if (!File.Exists(FilePath))
        {
            var defaults = Defaults();
            Save(defaults);
            return defaults;
        }
        var items = new List<ShortcutItem>();
        foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
        {
            ShortcutItem item;
            if (TryParse(line, out item)) items.Add(item);
        }
        if (items.Count == 8 && items[0].Name == "桌面" && items[1].Name == "下载" &&
            items[2].Name == "文档" && items[3].Name == "图片" && items[4].Name == "计算器" &&
            items[5].Name == "记事本" && items[6].Name == "画图" && items[7].Name == "控制面板")
        {
            items = Defaults();
            Save(items);
        }
        return items;
    }

    internal static void Save(List<ShortcutItem> items)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        var lines = new List<string>();
        foreach (var item in items) lines.Add(Serialize(item));
        File.WriteAllLines(FilePath, lines.ToArray(), new UTF8Encoding(false));
    }

    internal static List<ShortcutItem> Defaults()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new List<ShortcutItem>
        {
            new ShortcutItem("桌面", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)),
            new ShortcutItem("下载", Path.Combine(profile, "Downloads"))
        };
    }

    internal static string Serialize(ShortcutItem item)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Name)) + "\t" +
               Convert.ToBase64String(Encoding.UTF8.GetBytes(item.Target));
    }

    internal static bool TryParse(string line, out ShortcutItem item)
    {
        item = null;
        try
        {
            var parts = line.Split('\t');
            if (parts.Length != 2) return false;
            var name = Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            var target = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1]));
            if (name.Length == 0 || target.Length == 0) return false;
            item = new ShortcutItem(name, target);
            return true;
        }
        catch { return false; }
    }
}

internal sealed class ShortcutTile : Control
{
    private readonly ShortcutItem item;
    private readonly bool add;
    private readonly Action action;
    private readonly Image icon;
    private bool hover;
    private bool pressed;
    private bool dragged;
    private int pressedAt;

    internal ShortcutTile(ShortcutItem shortcut, bool isAdd, Action clicked)
    {
        item = shortcut;
        add = isAdd;
        action = clicked;
        Size = new Size(86, 94);
        Margin = new Padding(5, 4, 5, 4);
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei UI", 9F);
        if (!add) icon = Ui.GetImage(item.Target, 48);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!add && e.Button == MouseButtons.Left)
        {
            pressed = true;
            dragged = false;
            pressedAt = Environment.TickCount;
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (pressed && !dragged && e.Button == MouseButtons.Left && Environment.TickCount - pressedAt >= 400)
        {
            dragged = true;
            DoDragDrop(item, DragDropEffects.Move);
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        pressed = false;
        if (e.Button == MouseButtons.Left && !dragged && action != null) action();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        if (hover)
        {
            using (var path = Ui.RoundRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 7))
            using (var brush = new SolidBrush(Color.FromArgb(218, 235, 250))) e.Graphics.FillPath(brush, path);
        }

        if (add)
        {
            var box = new Rectangle((Width - 48) / 2, 8, 48, 48);
            using (var path = Ui.RoundRectangle(box, 8))
            using (var pen = new Pen(Color.FromArgb(155, 155, 155)))
            {
                pen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawPath(pen, path);
            }
            using (var pen = new Pen(Color.FromArgb(155, 155, 155), 1.5F))
            {
                e.Graphics.DrawLine(pen, box.Left + 14, box.Top + 24, box.Right - 14, box.Top + 24);
                e.Graphics.DrawLine(pen, box.Left + 24, box.Top + 14, box.Left + 24, box.Bottom - 14);
            }
        }
        else e.Graphics.DrawImage(icon, new Rectangle((Width - 48) / 2, 8, 48, 48));

        TextRenderer.DrawText(e.Graphics, add ? "添加" : item.Name, Font,
            new Rectangle(2, 65, Width - 4, 22), Color.FromArgb(30, 30, 30),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            if (icon != null) icon.Dispose();
            if (ContextMenuStrip != null) ContextMenuStrip.Dispose();
        }
        base.Dispose(disposing);
    }
}
