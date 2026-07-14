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

internal static class Program
{
    private static Mutex mutex;

    [STAThread]
    internal static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();

        bool first;
        mutex = new Mutex(true, "Local\\Matches.App", out first);
        if (!first)
        {
            try { EventWaitHandle.OpenExisting("Local\\Matches.Show").Set(); }
            catch (WaitHandleCannotBeOpenedException) { }
            return 0;
        }

        Native.SetProcessDPIAware();
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\Matches.Show"))
        using (var form = new LauncherForm())
        {
            var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, delegate
            {
                if (!form.IsDisposed) form.BeginInvoke((Action)form.ShowLauncher);
            }, null, Timeout.Infinite, false);
            Application.Run(form);
            registration.Unregister(null);
        }
        mutex.ReleaseMutex();
        mutex.Dispose();
        return 0;
    }

    private static int SelfTest()
    {
        try
        {
            if (SearchClient.QuoteArgument("abc") != "\"abc\"") return 1;
            if (SearchClient.QuoteArgument("C:\\a b\\") != "\"C:\\a b\\\\\"") return 1;
            var paths = SearchClient.ParseResults("\uFEFFC:\\测试 文件.txt\r\nD:\\目录\r\n");
            if (paths.Count != 2 || paths[0] != "C:\\测试 文件.txt" || paths[1] != "D:\\目录") return 1;
            var arguments = SearchClient.BuildArguments("Matches", "C:\\temp\\result.txt");
            if (arguments.Contains("name:") || !arguments.Contains("\"Matches\"")) return 1;
            if (!LauncherForm.IsUninstallQuery(" 卸载 ") || LauncherForm.IsUninstallQuery("卸载程序")) return 1;
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "appwiz.cpl"))) return 1;
            var row = new Rectangle(0, 0, 770, 48);
            if (LauncherForm.ResultActionAt(new Point(710, 24), row) != 1 ||
                LauncherForm.ResultActionAt(new Point(740, 24), row) != 2 ||
                LauncherForm.ResultActionAt(new Point(100, 24), row) != 0) return 1;
            var saved = ShortcutStore.Serialize(new ShortcutItem("测试", "C:\\目录\\程序.exe"));
            ShortcutItem restored;
            if (!ShortcutStore.TryParse(saved, out restored) || restored.Name != "测试" || restored.Target != "C:\\目录\\程序.exe") return 1;
            if (!LauncherForm.WebSearchUrl("bili 火柴").StartsWith("https://search.bilibili.com/", StringComparison.Ordinal) ||
                !LauncherForm.WebSearchUrl("火柴").StartsWith("https://www.google.com/search", StringComparison.Ordinal)) return 1;
            var defaults = ShortcutStore.Defaults();
            if (defaults.Count != 2 || defaults[0].Name != "桌面" || defaults[1].Name != "下载") return 1;
            if (LauncherForm.ListFolder(AppDomain.CurrentDomain.BaseDirectory).Count == 0) return 1;
            if (Ui.WebsiteIconPath("https://chatgpt.com/a") != Ui.WebsiteIconPath("https://chatgpt.com/b") ||
                Ui.WebsiteIconPath("C:\\temp") != null) return 1;
            if (!LauncherForm.CodexTerminalArguments("C:\\a b", "codex.exe").Contains("\"C:\\a b\"")) return 1;
            HotkeySettings testedHotkeys;
            if (!HotkeyStore.TryParse(new[] { "F4", "F5", "F6" }, out testedHotkeys) || testedHotkeys.Copy != Keys.F5 ||
                HotkeyStore.TryParse(new[] { "F4", "F4", "F6" }, out testedHotkeys)) return 1;
            return 0;
        }
        catch { return 1; }
    }
}

internal sealed class LauncherForm : Form
{
    private readonly Panel searchHost = new Panel();
    private readonly TextBox search = new TextBox();
    private readonly Label searchCue = new Label();
    private readonly FlowLayoutPanel shortcuts = new FlowLayoutPanel();
    private readonly ShortcutTile addTile;
    private readonly ListView results = new BufferedListView();
    private readonly ImageList resultRowHeight = new ImageList();
    private readonly Label shortcutHint = new Label();
    private readonly Label status = new Label();
    private readonly Button settings = new Button();
    private readonly Font resultPathFont = new Font("Microsoft YaHei UI", 8.5F);
    private readonly NotifyIcon tray = new NotifyIcon();
    private readonly ContextMenuStrip addMenu = new ContextMenuStrip();
    private readonly ContextMenuStrip settingsMenu = new ContextMenuStrip();
    private readonly System.Windows.Forms.Timer searchTimer = new System.Windows.Forms.Timer();
    private readonly SearchClient client;
    private readonly Engine engine;
    private readonly KeyboardHook keyboardHook;
    private List<ShortcutItem> shortcutItems;
    private HotkeySettings hotkeys;
    private int generation;
    private int hoveredResultIndex = -1;
    private int hoveredResultAction;
    private bool engineReady;
    private bool exiting;
    private bool webSearchMode;
    private bool folderMode;
    private string currentFolder;
    private bool websiteIconsLoading;

    internal LauncherForm()
    {
        client = new SearchClient(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Matches", "engine", "es.exe"));
        engine = new Engine(AppDomain.CurrentDomain.BaseDirectory);
        hotkeys = HotkeyStore.Load();

        Text = "Matches";
        ClientSize = new Size(808, 540);
        StartPosition = FormStartPosition.Manual;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        Font = new Font("Microsoft YaHei UI", 9F);
        BackColor = Color.FromArgb(250, 250, 250);

        searchHost.Location = new Point(10, 10);
        searchHost.Size = new Size(788, 62);
        searchHost.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        searchHost.BackColor = Color.FromArgb(235, 236, 238);
        searchHost.Resize += delegate { Ui.ApplyRoundedRegion(searchHost, 9); };
        searchHost.MouseDown += DragWindow;

        search.BorderStyle = BorderStyle.None;
        search.BackColor = searchHost.BackColor;
        search.Font = new Font("Microsoft YaHei UI", 16F);
        search.Location = new Point(18, 16);
        search.Size = new Size(752, 32);
        search.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right;
        searchHost.Controls.Add(search);
        searchCue.AutoSize = true;
        searchCue.BackColor = searchHost.BackColor;
        searchCue.Font = search.Font;
        searchCue.Location = new Point(18, 14);
        searchCue.Text = "搜索本地文件或应用";
        searchCue.ForeColor = Color.FromArgb(120, 120, 120);
        searchCue.Cursor = Cursors.IBeam;
        searchCue.Click += delegate { search.Focus(); };
        searchHost.Controls.Add(searchCue);
        searchCue.BringToFront();

        shortcuts.Location = new Point(10, 84);
        shortcuts.Size = new Size(788, 394);
        shortcuts.Anchor = AnchorStyles.Left | AnchorStyles.Top | AnchorStyles.Right | AnchorStyles.Bottom;
        shortcuts.BackColor = BackColor;
        shortcuts.Padding = new Padding(6, 0, 0, 0);
        shortcuts.WrapContents = true;
        shortcuts.AutoScroll = true;
        shortcuts.AllowDrop = true;
        shortcuts.DragEnter += ShortcutDragOver;
        shortcuts.DragOver += ShortcutDragOver;
        shortcuts.DragDrop += ShortcutDragDrop;

        addTile = new ShortcutTile(null, true, delegate { addMenu.Show(Cursor.Position); });
        addTile.Location = new Point(704, 378);
        addTile.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;

        results.Location = shortcuts.Location;
        results.Size = shortcuts.Size;
        results.Anchor = shortcuts.Anchor;
        results.View = View.Details;
        results.FullRowSelect = true;
        results.HideSelection = false;
        results.MultiSelect = false;
        results.BorderStyle = BorderStyle.None;
        results.HeaderStyle = ColumnHeaderStyle.None;
        results.BackColor = BackColor;
        results.OwnerDraw = true;
        results.Columns.Add(String.Empty, 770);
        resultRowHeight.ImageSize = new Size(1, 48);
        resultRowHeight.ColorDepth = ColorDepth.Depth32Bit;
        resultRowHeight.Images.Add(new Bitmap(1, 48));
        results.SmallImageList = resultRowHeight;
        results.DrawItem += DrawResultItem;
        results.DrawSubItem += DrawResultSubItem;
        results.SelectedIndexChanged += delegate { results.Invalidate(); };
        results.MouseMove += ResultMouseMove;
        results.MouseLeave += ResultMouseLeave;
        results.MouseDown += ResultMouseDown;
        results.MouseDoubleClick += ResultMouseDoubleClick;
        results.Visible = false;

        shortcutHint.Location = new Point(16, 484);
        shortcutHint.Size = new Size(730, 22);
        shortcutHint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        shortcutHint.ForeColor = Color.FromArgb(115, 115, 115);
        UpdateShortcutHint();

        status.Location = new Point(16, 510);
        status.Size = new Size(700, 22);
        status.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
        status.ForeColor = Color.FromArgb(105, 105, 105);
        status.Text = "正在启动本地搜索…";

        settings.Location = new Point(764, 504);
        settings.Size = new Size(34, 30);
        settings.Anchor = AnchorStyles.Right | AnchorStyles.Bottom;
        settings.FlatStyle = FlatStyle.Flat;
        settings.FlatAppearance.BorderSize = 0;
        settings.BackColor = BackColor;
        settings.ForeColor = Color.FromArgb(80, 80, 80);
        settings.Font = new Font("Segoe UI Symbol", 15F);
        settings.Text = "⚙";
        settings.Cursor = Cursors.Hand;
        settings.Click += delegate { settingsMenu.Show(settings, new Point(settings.Width, 0), ToolStripDropDownDirection.AboveLeft); };

        Controls.Add(searchHost);
        Controls.Add(shortcuts);
        Controls.Add(addTile);
        Controls.Add(results);
        Controls.Add(shortcutHint);
        Controls.Add(status);
        Controls.Add(settings);
        addTile.BringToFront();
        settings.BringToFront();

        addMenu.Items.Add("添加文件或程序", null, delegate { AddFileShortcut(); });
        addMenu.Items.Add("添加文件夹", null, delegate { AddFolderShortcut(); });
        addMenu.Items.Add("添加网页网址", null, delegate { AddUrlShortcut(); });
        settingsMenu.Items.Add("快捷键设置...", null, delegate { EditHotkeys(); });
        settingsMenu.Items.Add(new ToolStripSeparator());
        settingsMenu.Items.Add("恢复默认快捷入口", null, delegate { RestoreDefaultShortcuts(); });
        settingsMenu.Items.Add("打开配置目录", null, delegate { OpenConfigurationDirectory(); });
        settingsMenu.Items.Add(new ToolStripSeparator());
        settingsMenu.Items.Add("退出 Matches", null, delegate { ExitApplication(); });

        var trayMenu = new ContextMenuStrip();
        trayMenu.Items.Add("显示 Matches", null, delegate { ShowLauncher(); });
        trayMenu.Items.Add("退出", null, delegate { ExitApplication(); });
        tray.Icon = SystemIcons.Application;
        tray.Text = "Matches";
        tray.ContextMenuStrip = trayMenu;
        tray.DoubleClick += delegate { ShowLauncher(); };
        tray.Visible = true;

        searchTimer.Interval = 120;
        searchTimer.Tick += BeginSearch;
        search.TextChanged += SearchTextChanged;
        search.KeyDown += SearchKeyDown;
        results.KeyDown += ResultsKeyDown;
        Resize += delegate
        {
            Ui.ApplyRoundedRegion(this, 12);
            if (results.Columns.Count == 1) results.Columns[0].Width = Math.Max(400, ClientSize.Width - 38);
        };
        MouseDown += DragWindow;
        FormClosing += OnFormClosing;
        Shown += OnShown;

        shortcutItems = ShortcutStore.Load();
        RebuildShortcuts();
        keyboardHook = new KeyboardHook(delegate
        {
            if (!IsDisposed && IsHandleCreated) BeginInvoke((Action)ToggleLauncher);
        });
        Ui.ApplyRoundedRegion(searchHost, 9);
        Ui.ApplyRoundedRegion(this, 12);
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ClassStyle |= 0x00020000;
            return parameters;
        }
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == hotkeys.Locate) { RunFileAction(1); return true; }
        if (keyData == hotkeys.Copy) { RunFileAction(2); return true; }
        if (keyData == hotkeys.Codex) { RunFileAction(3); return true; }
        if (keyData == Keys.Tab && search.Focused)
        {
            SetWebSearchMode(!webSearchMode);
            return true;
        }
        return base.ProcessCmdKey(ref message, keyData);
    }

    private void OnShown(object sender, EventArgs e)
    {
        ShowLauncher();
        CacheWebsiteIcons();
        Task.Factory.StartNew<string>(delegate { return engine.Start(); }).ContinueWith(delegate(Task<string> task)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate
            {
                if (task.IsFaulted)
                {
                    status.Text = "搜索服务启动失败：" + task.Exception.GetBaseException().Message;
                    return;
                }
                engineReady = task.Result == null;
                if (!webSearchMode && !folderMode) status.Text = engineReady ? "输入关键词搜索本地文件" : task.Result;
                if (engineReady && !webSearchMode && !folderMode && search.TextLength > 0) { searchTimer.Stop(); searchTimer.Start(); }
            });
        });
    }

    internal void ShowLauncher()
    {
        SetWebSearchMode(false);
        var area = Screen.FromPoint(Cursor.Position).WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + Math.Max(24, area.Height / 8));
        if (!Visible) Show();
        search.Select();
        Native.FocusWindow(Handle, search.Handle);
        search.SelectAll();
    }

    private void ToggleLauncher()
    {
        if (Visible) Hide();
        else ShowLauncher();
    }

    private void SetWebSearchMode(bool enabled)
    {
        webSearchMode = enabled;
        folderMode = false;
        currentFolder = null;
        search.ForeColor = enabled ? Color.FromArgb(0, 102, 204) : SystemColors.WindowText;
        search.Clear();
        UpdateSearchCue();
        shortcuts.Visible = true;
        addTile.Visible = true;
        results.Visible = false;
        ClearResults();
        status.Text = enabled ? "输入关键词后按 Enter，用 Chrome 搜索" :
            (engineReady ? "输入关键词搜索本地文件" : "正在启动本地搜索…");
    }

    internal static string WebSearchUrl(string query)
    {
        query = query.Trim();
        if (query.StartsWith("bili", StringComparison.OrdinalIgnoreCase))
        {
            var keyword = query.Substring(4).Trim();
            return keyword.Length == 0 ? "https://www.bilibili.com/" :
                "https://search.bilibili.com/all?keyword=" + Uri.EscapeDataString(keyword);
        }
        return query.Length == 0 ? null : "https://www.google.com/search?q=" + Uri.EscapeDataString(query);
    }

    private void SearchTextChanged(object sender, EventArgs e)
    {
        searchTimer.Stop();
        generation++;
        client.Cancel();
        UpdateSearchCue();
        if (webSearchMode)
        {
            shortcuts.Visible = true;
            addTile.Visible = true;
            results.Visible = false;
            ClearResults();
            status.Text = "输入关键词后按 Enter，用 Chrome 搜索";
            return;
        }
        if (folderMode)
        {
            shortcuts.Visible = false;
            addTile.Visible = false;
            results.Visible = true;
            status.Text = "路径已变更，按 Enter 打开";
            return;
        }
        var searching = search.TextLength > 0;
        shortcuts.Visible = !searching;
        addTile.Visible = !searching;
        results.Visible = searching;
        if (!searching)
        {
            ClearResults();
            status.Text = engineReady ? "输入关键词搜索本地文件" : "正在启动本地搜索…";
        }
        else searchTimer.Start();
    }

    private void UpdateSearchCue()
    {
        searchCue.Text = webSearchMode ? "网页搜索" : "搜索本地文件或应用";
        searchCue.ForeColor = webSearchMode ? Color.FromArgb(0, 102, 204) : Color.FromArgb(120, 120, 120);
        searchCue.Visible = search.TextLength == 0;
    }

    private void BeginSearch(object sender, EventArgs e)
    {
        searchTimer.Stop();
        if (!engineReady)
        {
            status.Text = "搜索服务仍在启动，请稍候";
            return;
        }
        var query = search.Text;
        var current = generation;
        status.Text = "正在搜索…";
        Task.Factory.StartNew(delegate { return client.Search(query); }).ContinueWith(delegate(Task<List<string>> task)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate
            {
                if (current != generation) return;
                if (task.IsFaulted)
                {
                    status.Text = "搜索失败：" + task.Exception.GetBaseException().Message;
                    return;
                }
                ShowResults(task.Result, query);
            });
        });
    }

    internal static bool IsUninstallQuery(string query)
    {
        return String.Equals(query.Trim(), "卸载", StringComparison.OrdinalIgnoreCase);
    }

    private void ShowResults(List<string> paths, string query)
    {
        results.BeginUpdate();
        ClearResults();
        if (IsUninstallQuery(query))
        {
            var path = Path.Combine(Environment.SystemDirectory, "appwiz.cpl");
            var item = new ListViewItem("卸载或更改程序");
            item.Tag = new SearchResult(path, item.Text, "控制面板 > 程序 > 程序和功能");
            results.Items.Add(item);
        }
        foreach (var path in paths)
        {
            var clean = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var name = Path.GetFileName(clean);
            if (name.Length == 0) name = path;
            var item = new ListViewItem(name);
            item.Tag = new SearchResult(path, name, Path.GetDirectoryName(clean) ?? String.Empty);
            results.Items.Add(item);
        }
        results.EndUpdate();
        if (results.Items.Count > 0) { results.Items[0].Selected = true; results.Items[0].Focused = true; }
        status.Text = results.Items.Count == 0 ? "没有找到匹配项" : "找到 " + results.Items.Count + " 项（最多显示 100 项）";
    }

    internal static List<string> ListFolder(string path)
    {
        var directories = Directory.GetDirectories(path);
        var files = Directory.GetFiles(path);
        Array.Sort(directories, StringComparer.CurrentCultureIgnoreCase);
        Array.Sort(files, StringComparer.CurrentCultureIgnoreCase);
        var entries = new List<string>(directories.Length + files.Length);
        entries.AddRange(directories);
        entries.AddRange(files);
        return entries;
    }

    private void BrowseFolder(string path)
    {
        try
        {
            path = path.Trim().Trim('"');
            if (path.Length == 0) { status.Text = "请输入文件夹路径"; return; }
            var fullPath = Path.GetFullPath(path);
            var entries = ListFolder(fullPath);
            webSearchMode = false;
            folderMode = true;
            currentFolder = fullPath;
            search.Text = fullPath;
            search.SelectionStart = search.TextLength;
            shortcuts.Visible = false;
            addTile.Visible = false;
            results.Visible = true;
            ShowResults(entries, String.Empty);
            status.Text = fullPath + " · " + entries.Count + " 项";
        }
        catch (Exception ex) { status.Text = "无法读取文件夹：" + ex.Message; }
    }

    private void BrowseParentFolder()
    {
        if (!folderMode || currentFolder == null) return;
        var parent = Directory.GetParent(currentFolder);
        if (parent != null) BrowseFolder(parent.FullName);
    }

    private void ClearResults()
    {
        foreach (ListViewItem item in results.Items)
        {
            var result = item.Tag as SearchResult;
            if (result != null) result.Dispose();
        }
        results.Items.Clear();
        hoveredResultIndex = -1;
        hoveredResultAction = 0;
    }

    private void DrawResultItem(object sender, DrawListViewItemEventArgs e)
    {
        using (var brush = new SolidBrush(e.Item.Selected ? Color.FromArgb(101, 34, 128) : BackColor))
            e.Graphics.FillRectangle(brush, e.Bounds);
    }

    private void DrawResultSubItem(object sender, DrawListViewSubItemEventArgs e)
    {
        var result = e.Item.Tag as SearchResult;
        if (result == null) return;
        if (result.Image == null) result.Image = Ui.GetImage(result.Path, 32);
        var selected = e.Item.Selected;
        var bounds = e.Bounds;
        e.Graphics.DrawImage(result.Image, new Rectangle(bounds.Left + 12, bounds.Top + 8, 32, 32));
        TextRenderer.DrawText(e.Graphics, result.Name, Font,
            new Rectangle(bounds.Left + 54, bounds.Top + 4, bounds.Width - 145, 21),
            selected ? Color.White : Color.FromArgb(32, 32, 32),
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        TextRenderer.DrawText(e.Graphics, result.Directory, resultPathFont,
            new Rectangle(bounds.Left + 54, bounds.Top + 25, bounds.Width - 145, 18),
            selected ? Color.FromArgb(235, 218, 242) : Color.FromArgb(140, 140, 140),
            TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        if (e.Item.Index == hoveredResultIndex) DrawResultActions(e.Graphics, bounds, selected);
    }

    private static Rectangle ResultButton(Rectangle row, int action)
    {
        return new Rectangle(row.Right - (action == 1 ? 70 : 36), row.Top + 9, 28, 28);
    }

    internal static int ResultActionAt(Point point, Rectangle row)
    {
        if (ResultButton(row, 1).Contains(point)) return 1;
        if (ResultButton(row, 2).Contains(point)) return 2;
        return 0;
    }

    private void DrawResultActions(Graphics graphics, Rectangle row, bool selected)
    {
        var oldSmoothing = graphics.SmoothingMode;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var color = selected ? Color.White : Color.FromArgb(88, 88, 88);
        for (var action = 1; action <= 2; action++)
        {
            var button = ResultButton(row, action);
            if (hoveredResultAction == action)
            {
                using (var brush = new SolidBrush(selected ? Color.FromArgb(130, 255, 255, 255) : Color.FromArgb(232, 226, 235)))
                using (var path = Ui.RoundRectangle(button, 5))
                    graphics.FillPath(brush, path);
            }
            using (var pen = new Pen(color, 1.6F))
            {
                if (action == 1)
                {
                    graphics.DrawLines(pen, new[] { new Point(button.Left + 6, button.Top + 11), new Point(button.Left + 6, button.Top + 8), new Point(button.Left + 12, button.Top + 8), new Point(button.Left + 14, button.Top + 11), new Point(button.Right - 6, button.Top + 11), new Point(button.Right - 6, button.Bottom - 7), new Point(button.Left + 6, button.Bottom - 7), new Point(button.Left + 6, button.Top + 11) });
                    graphics.DrawLine(pen, button.Left + 9, button.Bottom - 10, button.Right - 9, button.Top + 14);
                }
                else
                {
                    graphics.DrawRectangle(pen, button.Left + 10, button.Top + 7, 11, 13);
                    graphics.DrawRectangle(pen, button.Left + 7, button.Top + 10, 11, 13);
                }
            }
        }
        graphics.SmoothingMode = oldSmoothing;
    }

    private void ResultMouseMove(object sender, MouseEventArgs e)
    {
        var item = results.GetItemAt(e.X, e.Y);
        var index = item == null ? -1 : item.Index;
        var action = item == null ? 0 : ResultActionAt(e.Location, item.Bounds);
        if (index != hoveredResultIndex || action != hoveredResultAction)
        {
            var oldIndex = hoveredResultIndex;
            hoveredResultIndex = index;
            hoveredResultAction = action;
            results.Cursor = action == 0 ? Cursors.Default : Cursors.Hand;
            InvalidateResult(oldIndex);
            if (index != oldIndex) InvalidateResult(index);
        }
    }

    private void ResultMouseLeave(object sender, EventArgs e)
    {
        var oldIndex = hoveredResultIndex;
        hoveredResultIndex = -1;
        hoveredResultAction = 0;
        results.Cursor = Cursors.Default;
        InvalidateResult(oldIndex);
    }

    private void InvalidateResult(int index)
    {
        if (index >= 0 && index < results.Items.Count) results.Invalidate(results.Items[index].Bounds);
    }

    private void ResultMouseDown(object sender, MouseEventArgs e)
    {
        var item = results.GetItemAt(e.X, e.Y);
        if (item == null) return;
        var action = ResultActionAt(e.Location, item.Bounds);
        if (action == 0) return;
        item.Selected = true;
        var path = ((SearchResult)item.Tag).Path;
        if (action == 1) LocatePath(path);
        else CopyPath(path);
    }

    private void ResultMouseDoubleClick(object sender, MouseEventArgs e)
    {
        var item = results.GetItemAt(e.X, e.Y);
        if (item == null || ResultActionAt(e.Location, item.Bounds) != 0) return;
        item.Selected = true;
        OpenSelected();
    }

    private void RebuildShortcuts()
    {
        shortcuts.SuspendLayout();
        while (shortcuts.Controls.Count > 0)
        {
            var control = shortcuts.Controls[0];
            shortcuts.Controls.RemoveAt(0);
            control.Dispose();
        }
        foreach (var item in shortcutItems)
        {
            var current = item;
            var tile = new ShortcutTile(current, false, delegate { OpenShortcut(current); });
            tile.Tag = current;
            tile.AllowDrop = true;
            tile.DragEnter += ShortcutDragOver;
            tile.DragOver += ShortcutDragOver;
            tile.DragDrop += ShortcutDragDrop;
            var menu = new ContextMenuStrip();
            menu.Items.Add("打开", null, delegate { OpenShortcut(current); });
            menu.Items.Add("删除快捷入口", null, delegate { RemoveShortcut(current); });
            tile.ContextMenuStrip = menu;
            shortcuts.Controls.Add(tile);
        }
        shortcuts.ResumeLayout();
    }

    private void ShortcutDragOver(object sender, DragEventArgs e)
    {
        e.Effect = e.Data.GetDataPresent(typeof(ShortcutItem)) ? DragDropEffects.Move : DragDropEffects.None;
    }

    private void ShortcutDragDrop(object sender, DragEventArgs e)
    {
        var item = e.Data.GetData(typeof(ShortcutItem)) as ShortcutItem;
        if (item == null) return;
        var from = shortcutItems.IndexOf(item);
        if (from < 0) return;
        var point = shortcuts.PointToClient(new Point(e.X, e.Y));
        var targetControl = shortcuts.GetChildAtPoint(point);
        var target = targetControl == null ? shortcutItems.Count : shortcutItems.IndexOf(targetControl.Tag as ShortcutItem);
        if (target < 0) target = shortcutItems.Count;
        if (targetControl != null && point.X > targetControl.Right - targetControl.Width / 2) target++;
        shortcutItems.RemoveAt(from);
        if (from < target) target--;
        shortcutItems.Insert(Math.Max(0, Math.Min(target, shortcutItems.Count)), item);
        ShortcutStore.Save(shortcutItems);
        RebuildShortcuts();
        status.Text = "已调整快捷入口位置";
    }

    private void AddFileShortcut()
    {
        using (var dialog = new OpenFileDialog())
        {
            dialog.Title = "选择文件或程序";
            dialog.Filter = "所有文件 (*.*)|*.*";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            AddShortcut(new ShortcutItem(Path.GetFileNameWithoutExtension(dialog.FileName), dialog.FileName));
        }
    }

    private void AddFolderShortcut()
    {
        using (var dialog = new FolderBrowserDialog())
        {
            dialog.Description = "选择要添加的文件夹";
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            var name = new DirectoryInfo(dialog.SelectedPath).Name;
            if (name.Length == 0) name = dialog.SelectedPath;
            AddShortcut(new ShortcutItem(name, dialog.SelectedPath));
        }
    }

    private void AddUrlShortcut()
    {
        using (var dialog = new UrlShortcutDialog())
        {
            if (dialog.ShowDialog(this) == DialogResult.OK) AddShortcut(dialog.Shortcut);
        }
    }

    private void AddShortcut(ShortcutItem item)
    {
        foreach (var existing in shortcutItems)
        {
            if (String.Equals(existing.Target, item.Target, StringComparison.OrdinalIgnoreCase))
            {
                status.Text = "这个快捷入口已经存在";
                return;
            }
        }
        shortcutItems.Add(item);
        ShortcutStore.Save(shortcutItems);
        RebuildShortcuts();
        CacheWebsiteIcons();
        status.Text = "已添加快捷入口";
    }

    private void CacheWebsiteIcons()
    {
        if (websiteIconsLoading) return;
        var urls = new List<string>();
        foreach (var item in shortcutItems)
        {
            var path = Ui.WebsiteIconPath(item.Target);
            if (path != null && !File.Exists(path)) urls.Add(item.Target);
        }
        if (urls.Count == 0) return;
        websiteIconsLoading = true;
        Task.Factory.StartNew(delegate
        {
            var changed = false;
            foreach (var url in urls) if (Ui.CacheWebsiteIcon(url)) changed = true;
            return changed;
        }).ContinueWith(delegate(Task<bool> task)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate
            {
                websiteIconsLoading = false;
                if (!task.IsFaulted && task.Result) RebuildShortcuts();
            });
        });
    }

    private void RemoveShortcut(ShortcutItem item)
    {
        shortcutItems.Remove(item);
        ShortcutStore.Save(shortcutItems);
        RebuildShortcuts();
        status.Text = "已删除快捷入口";
    }

    private void RestoreDefaultShortcuts()
    {
        if (MessageBox.Show(this, "恢复默认快捷入口？当前自定义入口会被替换。", "Matches", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        shortcutItems = ShortcutStore.Defaults();
        ShortcutStore.Save(shortcutItems);
        RebuildShortcuts();
        status.Text = "已恢复默认快捷入口";
    }

    private void EditHotkeys()
    {
        using (var dialog = new HotkeySettingsDialog(hotkeys))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            hotkeys = dialog.Settings;
            HotkeyStore.Save(hotkeys);
            UpdateShortcutHint();
            status.Text = "快捷键设置已保存";
        }
    }

    private void UpdateShortcutHint()
    {
        shortcutHint.Text = hotkeys.Locate + "  打开所在文件夹    " + hotkeys.Copy +
            "  复制路径    " + hotkeys.Codex + "  在当前目录打开 Codex Terminal";
    }

    private void OpenShortcut(ShortcutItem item)
    {
        try
        {
            Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true });
            Hide();
        }
        catch (Exception ex) { status.Text = "无法打开快捷入口：" + ex.Message; }
    }

    private void OpenConfigurationDirectory()
    {
        var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Matches");
        Directory.CreateDirectory(directory);
        Process.Start("explorer.exe", SearchClient.QuoteArgument(directory));
    }

    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        if (!webSearchMode && e.KeyCode == Keys.Down && results.Items.Count > 0)
        {
            results.Focus();
            results.Items[0].Selected = true;
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter)
        {
            if (webSearchMode) OpenWebSearch();
            else if (folderMode) BrowseFolder(search.Text);
            else OpenSelected();
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (e.KeyCode == Keys.Escape && (webSearchMode || search.TextLength > 0))
        {
            if (webSearchMode || folderMode) SetWebSearchMode(false);
            else search.Clear();
            e.Handled = true;
        }
    }

    private void OpenWebSearch()
    {
        var url = WebSearchUrl(search.Text);
        if (url == null) return;
        try
        {
            Process.Start(new ProcessStartInfo(Ui.ChromeExecutable())
            {
                Arguments = SearchClient.QuoteArgument(url),
                UseShellExecute = true
            });
            Hide();
        }
        catch (Exception ex) { status.Text = "无法打开 Chrome：" + ex.Message; }
    }

    private void ResultsKeyDown(object sender, KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Right)
        {
            var path = SelectedPath();
            if (path != null && Directory.Exists(path)) { BrowseFolder(path); e.Handled = true; }
        }
        else if (e.KeyCode == Keys.Left && folderMode) { BrowseParentFolder(); e.Handled = true; }
        else if (e.KeyCode == Keys.Enter) { OpenSelected(); e.Handled = true; }
        else if (e.KeyCode == Keys.Escape) { SetWebSearchMode(false); search.Focus(); e.Handled = true; }
    }

    private string SelectedPath()
    {
        if (results.SelectedItems.Count > 0) return ((SearchResult)results.SelectedItems[0].Tag).Path;
        if (results.Items.Count > 0) return ((SearchResult)results.Items[0].Tag).Path;
        return null;
    }

    private void RunFileAction(int action)
    {
        var path = SelectedPath() ?? (folderMode ? currentFolder : null);
        if (path == null) { status.Text = "请先选择文件或文件夹"; return; }
        if (action == 1) LocatePath(path);
        else if (action == 2) CopyPath(path);
        else OpenCodexTerminal(folderMode ? currentFolder : (Directory.Exists(path) ? path : Path.GetDirectoryName(path)));
    }

    internal static string CodexTerminalArguments(string directory, string codex)
    {
        return "-w new -d " + SearchClient.QuoteArgument(directory) + " " + SearchClient.QuoteArgument(codex);
    }

    private void OpenCodexTerminal(string directory)
    {
        if (String.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            status.Text = "无法确定 Codex 工作目录";
            return;
        }
        var codex = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "OpenAI", "Codex", "bin", "codex.exe");
        if (!File.Exists(codex)) codex = "codex.exe";
        try
        {
            Process.Start(new ProcessStartInfo("wt.exe")
            {
                Arguments = CodexTerminalArguments(directory, codex),
                WorkingDirectory = directory,
                UseShellExecute = true
            });
            Hide();
        }
        catch (Exception ex) { status.Text = "无法打开 Codex Terminal：" + ex.Message; }
    }

    private void OpenSelected()
    {
        var path = SelectedPath();
        if (path == null) return;
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); Hide(); }
        catch (Exception ex) { status.Text = "无法打开：" + ex.Message; }
    }

    private void LocatePath(string path)
    {
        try
        {
            if (Path.GetPathRoot(path) == path) Process.Start("explorer.exe", SearchClient.QuoteArgument(path));
            else Process.Start("explorer.exe", "/select," + SearchClient.QuoteArgument(path));
        }
        catch (Exception ex) { status.Text = "无法定位：" + ex.Message; }
    }

    private void CopyPath(string path)
    {
        try { Clipboard.SetText(path); status.Text = "已复制完整路径"; }
        catch (Exception ex) { status.Text = "无法复制：" + ex.Message; }
    }

    private void DragWindow(object sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        Native.ReleaseCapture();
        Native.SendMessage(Handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
    }

    private void OnFormClosing(object sender, FormClosingEventArgs e)
    {
        if (!exiting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
    }

    private void ExitApplication()
    {
        exiting = true;
        engine.Stop();
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ClearResults();
            resultRowHeight.Dispose();
            resultPathFont.Dispose();
            searchTimer.Dispose();
            keyboardHook.Dispose();
            tray.Visible = false;
            tray.Dispose();
        }
        base.Dispose(disposing);
    }
}

internal sealed class BufferedListView : ListView
{
    internal BufferedListView() { DoubleBuffered = true; }
}

internal sealed class SearchResult : IDisposable
{
    internal readonly string Path;
    internal readonly string Name;
    internal readonly string Directory;
    internal Image Image;

    internal SearchResult(string path, string name, string directory)
    {
        Path = path;
        Name = name;
        Directory = directory;
    }

    public void Dispose()
    {
        if (Image != null) { Image.Dispose(); Image = null; }
    }
}

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

internal sealed class HotkeySettings
{
    internal readonly Keys Locate;
    internal readonly Keys Copy;
    internal readonly Keys Codex;

    internal HotkeySettings(Keys locate, Keys copy, Keys codex)
    {
        Locate = locate;
        Copy = copy;
        Codex = codex;
    }
}

internal sealed class HotkeySettingsDialog : Form
{
    private readonly ComboBox locate = new ComboBox();
    private readonly ComboBox copy = new ComboBox();
    private readonly ComboBox codex = new ComboBox();
    internal HotkeySettings Settings { get; private set; }

    internal HotkeySettingsDialog(HotkeySettings current)
    {
        Text = "快捷键设置";
        ClientSize = new Size(390, 188);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        AddRow("打开所在文件夹", locate, 18, current.Locate);
        AddRow("复制路径", copy, 58, current.Copy);
        AddRow("打开 Codex Terminal", codex, 98, current.Codex);
        var okay = new Button { Text = "保存", Location = new Point(214, 143), Size = new Size(74, 28) };
        var cancel = new Button { Text = "取消", Location = new Point(296, 143), Size = new Size(74, 28), DialogResult = DialogResult.Cancel };
        okay.Click += delegate
        {
            var value = new HotkeySettings((Keys)locate.SelectedItem, (Keys)copy.SelectedItem, (Keys)codex.SelectedItem);
            if (value.Locate == value.Copy || value.Locate == value.Codex || value.Copy == value.Codex)
            {
                MessageBox.Show(this, "三个快捷键不能重复。", "Matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Settings = value;
            DialogResult = DialogResult.OK;
        };
        Controls.Add(okay);
        Controls.Add(cancel);
        AcceptButton = okay;
        CancelButton = cancel;
    }

    private void AddRow(string text, ComboBox box, int top, Keys selected)
    {
        Controls.Add(new Label { Text = text, Location = new Point(18, top + 5), Size = new Size(190, 22) });
        box.Location = new Point(220, top);
        box.Size = new Size(150, 25);
        box.DropDownStyle = ComboBoxStyle.DropDownList;
        for (var key = Keys.F1; key <= Keys.F12; key++) box.Items.Add(key);
        box.SelectedItem = selected;
        Controls.Add(box);
    }
}

internal static class HotkeyStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Matches", "hotkeys.txt");

    internal static HotkeySettings Load()
    {
        try
        {
            HotkeySettings settings;
            if (File.Exists(FilePath) && TryParse(File.ReadAllLines(FilePath, Encoding.UTF8), out settings)) return settings;
        }
        catch { }
        return new HotkeySettings(Keys.F1, Keys.F2, Keys.F3);
    }

    internal static void Save(HotkeySettings settings)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllLines(FilePath, new[] { settings.Locate.ToString(), settings.Copy.ToString(), settings.Codex.ToString() }, new UTF8Encoding(false));
    }

    internal static bool TryParse(string[] lines, out HotkeySettings settings)
    {
        settings = null;
        Keys locate, copy, codex;
        if (lines.Length != 3 || !Enum.TryParse(lines[0], out locate) || !Enum.TryParse(lines[1], out copy) ||
            !Enum.TryParse(lines[2], out codex) || locate < Keys.F1 || locate > Keys.F12 || copy < Keys.F1 ||
            copy > Keys.F12 || codex < Keys.F1 || codex > Keys.F12 || locate == copy || locate == codex || copy == codex) return false;
        settings = new HotkeySettings(locate, copy, codex);
        return true;
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

internal sealed class SearchClient
{
    private readonly string executable;
    private readonly object sync = new object();
    private Process running;

    internal SearchClient(string executablePath) { executable = executablePath; }

    internal List<string> Search(string query)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("es.exe 不存在", executable);
        var outputFile = Path.Combine(Path.GetTempPath(), "Matches-" + Guid.NewGuid().ToString("N") + ".txt");
        var info = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = BuildArguments(query, outputFile),
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        var process = new Process { StartInfo = info };
        lock (sync) running = process;
        try
        {
            process.Start();
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            if (process.ExitCode == 8) throw new InvalidOperationException("Everything 实例尚未就绪");
            if (process.ExitCode != 0) throw new InvalidOperationException(error.Length == 0 ? "ES 错误 " + process.ExitCode : error.Trim());
            return ParseResults(File.Exists(outputFile) ? File.ReadAllText(outputFile, Encoding.UTF8) : String.Empty);
        }
        finally
        {
            lock (sync) if (running == process) running = null;
            process.Dispose();
            try { if (File.Exists(outputFile)) File.Delete(outputFile); }
            catch { }
        }
    }

    internal void Cancel()
    {
        lock (sync)
        {
            try { if (running != null && !running.HasExited) running.Kill(); }
            catch { }
        }
    }

    internal static string BuildArguments(string query, string outputFile)
    {
        return "-instance Matches -n 100 -full-path-and-name -export-txt " + QuoteArgument(outputFile) +
               " -utf8-bom " + QuoteArgument(query);
    }

    internal static List<string> ParseResults(string output)
    {
        var list = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in output.Split('\n'))
        {
            var path = raw.TrimEnd('\r').TrimStart('\uFEFF');
            if (path.Length > 0 && seen.Add(path)) list.Add(path);
        }
        return list;
    }

    internal static string QuoteArgument(string value)
    {
        var result = new StringBuilder("\"");
        var slashes = 0;
        foreach (var character in value)
        {
            if (character == '\\') { slashes++; continue; }
            if (character == '"')
            {
                result.Append('\\', slashes * 2 + 1).Append('"');
                slashes = 0;
                continue;
            }
            result.Append('\\', slashes).Append(character);
            slashes = 0;
        }
        result.Append('\\', slashes * 2).Append('"');
        return result.ToString();
    }
}

internal sealed class Engine
{
    private readonly string packaged;
    private readonly string local;

    internal Engine(string applicationDirectory)
    {
        packaged = Path.Combine(applicationDirectory, "third_party", "everything");
        local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Matches", "engine");
    }

    internal string Start()
    {
        try
        {
            Directory.CreateDirectory(local);
            Copy("Everything.exe");
            Copy("es.exe");
            if (File.Exists(Path.Combine(packaged, "Everything.lng"))) Copy("Everything.lng");
            CopyAs("LICENSE.txt", "LICENSE.txt");

            var everything = Path.Combine(local, "Everything.exe");
            var marker = Path.Combine(local, "service-installed");
            if (!File.Exists(marker))
            {
                var install = Process.Start(new ProcessStartInfo
                {
                    FileName = everything,
                    Arguments = "-instance Matches -install-service",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                install.WaitForExit();
                if (install.ExitCode != 0) return "Everything 服务安装未完成，请重启 Matches 后重试";
                File.WriteAllText(marker, "Everything 1.4.1.1032");
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = everything,
                Arguments = "-instance Matches -startup",
                UseShellExecute = false,
                CreateNoWindow = true
            });

            var es = Path.Combine(local, "es.exe");
            for (var attempt = 0; attempt < 40; attempt++)
            {
                Thread.Sleep(250);
                using (var probe = Process.Start(new ProcessStartInfo
                {
                    FileName = es,
                    Arguments = "-instance Matches -n 1 " + SearchClient.QuoteArgument("__matches_readiness_probe__"),
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    probe.WaitForExit();
                    if (probe.ExitCode != 8) return null;
                }
            }
            return "Everything 索引启动超时，请退出后重新打开 Matches";
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return ex.NativeErrorCode == 1223 ? "已取消 UAC，无法安装 Everything 搜索服务" : ex.Message;
        }
        catch (Exception ex) { return ex.Message; }
    }

    internal void Stop()
    {
        var everything = Path.Combine(local, "Everything.exe");
        if (!File.Exists(everything)) return;
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = everything,
                Arguments = "-instance Matches -exit",
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        catch { }
    }

    private void Copy(string file) { CopyAs(file, file); }

    private void CopyAs(string source, string destination)
    {
        var from = Path.Combine(packaged, source);
        if (!File.Exists(from)) throw new FileNotFoundException("缺少打包组件", from);
        var to = Path.Combine(local, destination);
        if (!File.Exists(to) || new FileInfo(from).Length != new FileInfo(to).Length) File.Copy(from, to, true);
    }
}

internal sealed class KeyboardHook : IDisposable
{
    private const int KeyboardLowLevel = 13;
    private const int KeyDown = 0x0100;
    private const int SystemKeyDown = 0x0104;
    private const int KeyUp = 0x0101;
    private const int SystemKeyUp = 0x0105;
    private readonly Action activated;
    private readonly Native.LowLevelKeyboardProc callback;
    private IntPtr hook;
    private bool controlDown;
    private DateTime lastControl = DateTime.MinValue;

    internal KeyboardHook(Action activation)
    {
        activated = activation;
        callback = Handle;
        hook = Native.SetWindowsHookEx(KeyboardLowLevel, callback, Native.GetModuleHandle(null), 0);
        if (hook == IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    private IntPtr Handle(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = (Keys)Marshal.ReadInt32(data);
            var down = message.ToInt32() == KeyDown || message.ToInt32() == SystemKeyDown;
            var up = message.ToInt32() == KeyUp || message.ToInt32() == SystemKeyUp;
            var control = key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey;
            if (control && down && !controlDown)
            {
                controlDown = true;
                var now = DateTime.UtcNow;
                if ((now - lastControl).TotalMilliseconds <= 400)
                {
                    lastControl = DateTime.MinValue;
                    activated();
                }
                else lastControl = now;
            }
            else if (control && up) controlDown = false;
            else if (!control && down) lastControl = DateTime.MinValue;
        }
        return Native.CallNextHookEx(hook, code, message, data);
    }

    public void Dispose()
    {
        if (hook != IntPtr.Zero) { Native.UnhookWindowsHookEx(hook); hook = IntPtr.Zero; }
    }
}

internal static class Native
{
    internal delegate IntPtr LowLevelKeyboardProc(int code, IntPtr message, IntPtr data);

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeSize
    {
        internal int Width;
        internal int Height;

        internal NativeSize(int width, int height)
        {
            Width = width;
            Height = height;
        }
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int hook, LowLevelKeyboardProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    internal static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    internal static extern IntPtr GetModuleHandle(string moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint from, uint to, bool attach);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    internal static void FocusWindow(IntPtr window, IntPtr control)
    {
        var currentThread = GetCurrentThreadId();
        var foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), IntPtr.Zero);
        var attached = foregroundThread != 0 && foregroundThread != currentThread &&
                       AttachThreadInput(currentThread, foregroundThread, true);
        try
        {
            SetForegroundWindow(window);
            BringWindowToTop(window);
            SetFocus(control);
        }
        finally
        {
            if (attached) AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    [DllImport("user32.dll")]
    internal static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    internal static extern IntPtr SendMessage(IntPtr window, uint message, IntPtr word, IntPtr value);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    internal static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid interfaceId,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    internal static extern bool DeleteObject(IntPtr value);
}
