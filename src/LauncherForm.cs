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

internal sealed partial class LauncherForm : Form
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
    private bool powerActionRunning;

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
        settingsMenu.Items.Add("关机/重启前脚本...", null, delegate { EditPowerHooks(); });
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

}
