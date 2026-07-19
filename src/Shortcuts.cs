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
using Microsoft.Win32;

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

internal static class RegisteredApplicationStore
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\App Paths";

    internal static List<ShortcutItem> Load()
    {
        var applications = new List<ShortcutItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        Add(applications, seen, RegistryHive.CurrentUser, RegistryView.Registry64);
        Add(applications, seen, RegistryHive.CurrentUser, RegistryView.Registry32);
        Add(applications, seen, RegistryHive.LocalMachine, RegistryView.Registry64);
        Add(applications, seen, RegistryHive.LocalMachine, RegistryView.Registry32);
        applications.Sort(delegate(ShortcutItem left, ShortcutItem right)
        {
            var named = StringComparer.CurrentCultureIgnoreCase.Compare(left.Name, right.Name);
            return named != 0 ? named : StringComparer.OrdinalIgnoreCase.Compare(left.Target, right.Target);
        });
        return applications;
    }

    private static void Add(List<ShortcutItem> applications, HashSet<string> seen, RegistryHive hive, RegistryView view)
    {
        try
        {
            using (var root = RegistryKey.OpenBaseKey(hive, view))
            using (var appPaths = root.OpenSubKey(KeyPath))
            {
                if (appPaths == null) return;
                foreach (var keyName in appPaths.GetSubKeyNames())
                using (var application = appPaths.OpenSubKey(keyName))
                {
                    var target = application == null ? null : application.GetValue(String.Empty) as string;
                    if (String.IsNullOrWhiteSpace(target)) continue;
                    target = Environment.ExpandEnvironmentVariables(target.Trim().Trim('"'));
                    if (!File.Exists(target) || !seen.Add(target)) continue;
                    var name = Path.GetFileNameWithoutExtension(keyName);
                    try
                    {
                        var description = FileVersionInfo.GetVersionInfo(target).FileDescription;
                        if (!String.IsNullOrWhiteSpace(description)) name = description.Trim();
                    }
                    catch { }
                    applications.Add(new ShortcutItem(name, target));
                }
            }
        }
        catch { }
    }
}

internal sealed class RegisteredApplicationDialog : Form
{
    private readonly List<RegisteredApplicationCard> cards = new List<RegisteredApplicationCard>();
    private readonly FlowLayoutPanel applicationList = new FlowLayoutPanel();
    private readonly TextBox filter = new TextBox();
    private readonly Label filterCue = new Label();
    private readonly Label count = new Label();
    private readonly Label empty = new Label();
    private readonly Button add = new Button();
    private readonly ToolTip toolTip = new ToolTip();
    private readonly Font headingFont = new Font("Microsoft YaHei UI", 18F, FontStyle.Bold);
    private RegisteredApplicationCard selectedCard;

    internal ShortcutItem Shortcut { get; private set; }

    internal RegisteredApplicationDialog(List<ShortcutItem> applications)
    {
        Text = "选择已安装的软件";
        ClientSize = new Size(760, 550);
        MinimumSize = new Size(650, 460);
        StartPosition = FormStartPosition.CenterParent;
        ShowInTaskbar = false;
        ShowIcon = false;
        MaximizeBox = false;
        Font = new Font("Microsoft YaHei UI", 10F);
        BackColor = Color.FromArgb(247, 248, 250);

        var heading = new Label
        {
            Text = "选择已安装的软件",
            Font = headingFont,
            ForeColor = Color.FromArgb(35, 35, 40),
            Location = new Point(24, 17),
            AutoSize = true
        };
        var description = new Label
        {
            Text = "从 Windows 注册表中找到的程序 · 选择后添加到主页",
            ForeColor = Color.FromArgb(112, 112, 120),
            Location = new Point(25, 54),
            AutoSize = true
        };

        var searchHost = new Panel
        {
            Location = new Point(24, 82),
            Size = new Size(712, 42),
            Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right,
            BackColor = Color.White
        };
        searchHost.Resize += delegate { Ui.ApplyRoundedRegion(searchHost, 10); };
        filter.BorderStyle = BorderStyle.None;
        filter.BackColor = searchHost.BackColor;
        filter.Font = new Font("Microsoft YaHei UI", 11F);
        filter.Location = new Point(14, 9);
        filter.Size = new Size(684, 26);
        filter.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        filterCue.Text = "搜索软件名称或可执行文件";
        filterCue.ForeColor = Color.FromArgb(145, 145, 152);
        filterCue.BackColor = searchHost.BackColor;
        filterCue.Location = new Point(15, 10);
        filterCue.AutoSize = true;
        filterCue.Cursor = Cursors.IBeam;
        filterCue.Click += delegate { filter.Focus(); };
        searchHost.Controls.Add(filter);
        searchHost.Controls.Add(filterCue);
        filterCue.BringToFront();

        applicationList.Location = new Point(18, 140);
        applicationList.Size = new Size(724, 340);
        applicationList.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
        applicationList.Padding = new Padding(6);
        applicationList.AutoScroll = true;
        applicationList.WrapContents = true;
        applicationList.BackColor = BackColor;

        empty.Text = "没有找到匹配的软件";
        empty.ForeColor = Color.FromArgb(125, 125, 132);
        empty.TextAlign = ContentAlignment.MiddleCenter;
        empty.Size = new Size(680, 72);
        empty.Margin = new Padding(6, 24, 6, 6);
        empty.Visible = false;

        count.Location = new Point(24, 505);
        count.Size = new Size(420, 24);
        count.Anchor = AnchorStyles.Left | AnchorStyles.Bottom;
        count.ForeColor = Color.FromArgb(112, 112, 120);

        var cancel = new Button
        {
            Text = "取消",
            Location = new Point(568, 496),
            Size = new Size(80, 38),
            Anchor = AnchorStyles.Right | AnchorStyles.Bottom,
            DialogResult = DialogResult.Cancel,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.White,
            Cursor = Cursors.Hand
        };
        cancel.FlatAppearance.BorderColor = Color.FromArgb(215, 215, 220);
        add.Text = "添加";
        add.Location = new Point(656, 496);
        add.Size = new Size(80, 38);
        add.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        add.FlatStyle = FlatStyle.Flat;
        add.FlatAppearance.BorderSize = 0;
        add.BackColor = Color.FromArgb(101, 34, 128);
        add.ForeColor = Color.White;
        add.Cursor = Cursors.Hand;
        add.Enabled = false;
        add.Click += delegate { if (selectedCard != null) AcceptApplication(selectedCard); };

        foreach (var application in applications)
        {
            var card = new RegisteredApplicationCard(application, SelectApplication, AcceptApplication);
            cards.Add(card);
            applicationList.Controls.Add(card);
            toolTip.SetToolTip(card, application.Target);
        }
        applicationList.Controls.Add(empty);

        Controls.Add(heading);
        Controls.Add(description);
        Controls.Add(searchHost);
        Controls.Add(applicationList);
        Controls.Add(count);
        Controls.Add(cancel);
        Controls.Add(add);
        AcceptButton = add;
        CancelButton = cancel;

        filter.TextChanged += FilterApplications;
        Shown += delegate { filter.Focus(); };
        Resize += delegate
        {
            Ui.ApplyRoundedRegion(searchHost, 10);
            Ui.ApplyRoundedRegion(add, 8);
            Ui.ApplyRoundedRegion(cancel, 8);
        };
        Ui.ApplyRoundedRegion(searchHost, 10);
        Ui.ApplyRoundedRegion(add, 8);
        Ui.ApplyRoundedRegion(cancel, 8);
        FilterApplications(null, EventArgs.Empty);
    }

    internal static bool Matches(ShortcutItem application, string query)
    {
        query = query.Trim();
        return query.Length == 0 ||
            application.Name.IndexOf(query, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
            Path.GetFileName(application.Target).IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0 ||
            application.Target.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private void FilterApplications(object sender, EventArgs e)
    {
        filterCue.Visible = filter.TextLength == 0;
        var visible = 0;
        applicationList.SuspendLayout();
        foreach (var card in cards)
        {
            var matches = Matches(card.Item, filter.Text);
            card.Visible = matches;
            if (matches) visible++;
        }
        empty.Visible = visible == 0;
        applicationList.ResumeLayout();
        if (selectedCard != null && !Matches(selectedCard.Item, filter.Text)) SelectApplication(null);
        count.Text = "显示 " + visible + " / " + cards.Count + " 个软件";
    }

    private void SelectApplication(RegisteredApplicationCard card)
    {
        selectedCard = card;
        foreach (var item in cards) item.Selected = item == card;
        add.Enabled = card != null;
    }

    private void AcceptApplication(RegisteredApplicationCard card)
    {
        SelectApplication(card);
        Shortcut = card.Item;
        DialogResult = DialogResult.OK;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            toolTip.Dispose();
            headingFont.Dispose();
            filter.Font.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class RegisteredApplicationCard : Control
{
    private readonly Image icon;
    private readonly Action<RegisteredApplicationCard> select;
    private readonly Action<RegisteredApplicationCard> accept;
    private readonly Font titleFont = new Font("Microsoft YaHei UI", 10F, FontStyle.Bold);
    private readonly Font detailFont = new Font("Microsoft YaHei UI", 8.5F);
    private bool hover;
    private bool selected;

    internal readonly ShortcutItem Item;

    internal RegisteredApplicationCard(ShortcutItem item, Action<RegisteredApplicationCard> selected,
        Action<RegisteredApplicationCard> accepted)
    {
        Item = item;
        select = selected;
        accept = accepted;
        icon = Ui.GetImage(item.Target, 44);
        Size = new Size(220, 76);
        Margin = new Padding(5);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleRole = AccessibleRole.ListItem;
        AccessibleName = item.Name;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.StandardClick | ControlStyles.StandardDoubleClick, true);
    }

    internal bool Selected
    {
        get { return selected; }
        set { if (selected != value) { selected = value; Invalidate(); } }
    }

    protected override void OnClick(EventArgs e)
    {
        Focus();
        select(this);
        base.OnClick(e);
    }

    protected override void OnDoubleClick(EventArgs e)
    {
        accept(this);
        base.OnDoubleClick(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Enter) { accept(this); e.Handled = true; }
        else if (e.KeyCode == Keys.Space) { select(this); e.Handled = true; }
        base.OnKeyDown(e);
    }

    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(0, 0, Width - 1, Height - 1);
        using (var path = Ui.RoundRectangle(bounds, 10))
        using (var background = new SolidBrush(selected ? Color.FromArgb(240, 230, 246) :
            hover || Focused ? Color.FromArgb(238, 242, 248) : Color.White))
        using (var border = new Pen(selected ? Color.FromArgb(101, 34, 128) : Color.FromArgb(226, 227, 232),
            selected ? 1.8F : 1F))
        {
            e.Graphics.FillPath(background, path);
            e.Graphics.DrawPath(border, path);
        }
        e.Graphics.DrawImage(icon, new Rectangle(12, 16, 44, 44));
        TextRenderer.DrawText(e.Graphics, Item.Name, titleFont, new Rectangle(66, 13, Width - 76, 24),
            Color.FromArgb(38, 38, 44), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, Path.GetFileName(Item.Target), detailFont, new Rectangle(66, 41, Width - 76, 20),
            Color.FromArgb(122, 122, 130), TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (Focused) ControlPaint.DrawFocusRectangle(e.Graphics, new Rectangle(4, 4, Width - 8, Height - 8));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            icon.Dispose();
            titleFont.Dispose();
            detailFont.Dispose();
        }
        base.Dispose(disposing);
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
    private bool selected;
    private bool pressed;
    private bool dragged;
    private int pressedAt;

    internal ShortcutTile(ShortcutItem shortcut, bool isAdd, Action clicked)
    {
        item = shortcut;
        add = isAdd;
        action = clicked;
        Size = new Size(96, 104);
        Margin = new Padding(6, 5, 6, 5);
        Cursor = Cursors.Hand;
        Font = new Font("Microsoft YaHei UI", 10F);
        if (!add) icon = Ui.GetImage(item.Target, 54);
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }

    internal bool Selected
    {
        get { return selected; }
        set { if (selected != value) { selected = value; Invalidate(); } }
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
        if (selected || hover)
        {
            using (var path = Ui.RoundRectangle(new Rectangle(0, 0, Width - 1, Height - 1), 8))
            using (var brush = new SolidBrush(selected ? Color.FromArgb(101, 34, 128) : Color.FromArgb(218, 235, 250))) e.Graphics.FillPath(brush, path);
        }

        if (add)
        {
            var box = new Rectangle((Width - 54) / 2, 9, 54, 54);
            using (var path = Ui.RoundRectangle(box, 9))
            using (var pen = new Pen(Color.FromArgb(155, 155, 155)))
            {
                pen.DashStyle = DashStyle.Dash;
                e.Graphics.DrawPath(pen, path);
            }
            using (var pen = new Pen(Color.FromArgb(155, 155, 155), 1.7F))
            {
                e.Graphics.DrawLine(pen, box.Left + 16, box.Top + 27, box.Right - 16, box.Top + 27);
                e.Graphics.DrawLine(pen, box.Left + 27, box.Top + 16, box.Left + 27, box.Bottom - 16);
            }
        }
        else e.Graphics.DrawImage(icon, new Rectangle((Width - 54) / 2, 9, 54, 54));

        TextRenderer.DrawText(e.Graphics, add ? "添加" : item.Name, Font,
            new Rectangle(2, 72, Width - 4, 24), selected ? Color.White : Color.FromArgb(30, 30, 30),
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
