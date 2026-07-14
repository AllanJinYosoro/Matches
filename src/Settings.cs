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

internal sealed class PowerHookSettingsDialog : Form
{
    private readonly ListBox list = new ListBox();
    internal List<string> Scripts { get; private set; }

    internal PowerHookSettingsDialog(List<string> scripts)
    {
        Text = "关机/重启前脚本";
        ClientSize = new Size(620, 330);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);
        Controls.Add(new Label { Text = "脚本将从上到下依次执行；任一失败都会取消电源操作。", Location = new Point(16, 12), Size = new Size(570, 22) });
        list.Location = new Point(16, 40);
        list.Size = new Size(470, 236);
        list.HorizontalScrollbar = true;
        foreach (var script in scripts) list.Items.Add(script);
        Controls.Add(list);

        var add = new Button { Text = "添加...", Location = new Point(500, 40), Size = new Size(100, 28) };
        var remove = new Button { Text = "删除", Location = new Point(500, 76), Size = new Size(100, 28) };
        var up = new Button { Text = "上移", Location = new Point(500, 112), Size = new Size(100, 28) };
        var down = new Button { Text = "下移", Location = new Point(500, 148), Size = new Size(100, 28) };
        var openFolder = new Button { Text = "打开脚本目录", Location = new Point(500, 184), Size = new Size(100, 28) };
        add.Click += delegate
        {
            Directory.CreateDirectory(PowerHookStore.PrivateDirectory);
            using (var dialog = new OpenFileDialog())
            {
                dialog.Title = "选择电源操作前脚本";
                dialog.InitialDirectory = PowerHookStore.PrivateDirectory;
                dialog.Filter = "可执行脚本 (*.ps1;*.cmd;*.bat;*.py;*.exe)|*.ps1;*.cmd;*.bat;*.py;*.exe";
                dialog.Multiselect = true;
                if (dialog.ShowDialog(this) != DialogResult.OK) return;
                foreach (var file in dialog.FileNames) if (!list.Items.Contains(file)) list.Items.Add(file);
            }
        };
        remove.Click += delegate { if (list.SelectedIndex >= 0) list.Items.RemoveAt(list.SelectedIndex); };
        up.Click += delegate { MoveSelected(-1); };
        down.Click += delegate { MoveSelected(1); };
        openFolder.Click += delegate
        {
            Directory.CreateDirectory(PowerHookStore.PrivateDirectory);
            Process.Start("explorer.exe", SearchClient.QuoteArgument(PowerHookStore.PrivateDirectory));
        };
        Controls.Add(add);
        Controls.Add(remove);
        Controls.Add(up);
        Controls.Add(down);
        Controls.Add(openFolder);

        var okay = new Button { Text = "保存", Location = new Point(446, 288), Size = new Size(74, 28) };
        var cancel = new Button { Text = "取消", Location = new Point(528, 288), Size = new Size(74, 28), DialogResult = DialogResult.Cancel };
        okay.Click += delegate
        {
            Scripts = new List<string>();
            foreach (var item in list.Items) Scripts.Add((string)item);
            DialogResult = DialogResult.OK;
        };
        Controls.Add(okay);
        Controls.Add(cancel);
        AcceptButton = okay;
        CancelButton = cancel;
    }

    private void MoveSelected(int change)
    {
        var from = list.SelectedIndex;
        var to = from + change;
        if (from < 0 || to < 0 || to >= list.Items.Count) return;
        var item = list.Items[from];
        list.Items.RemoveAt(from);
        list.Items.Insert(to, item);
        list.SelectedIndex = to;
    }
}

internal static class PowerHookStore
{
    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Matches", "power-hooks.txt");

    internal static string PrivateDirectory
    {
        get
        {
            var application = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
            var root = String.Equals(application.Name, "dist", StringComparison.OrdinalIgnoreCase) && application.Parent != null
                ? application.Parent.FullName : application.FullName;
            return Path.Combine(root, "private_scripts");
        }
    }

    internal static List<string> Load()
    {
        var scripts = new List<string>();
        try
        {
            if (File.Exists(FilePath)) foreach (var line in File.ReadAllLines(FilePath, Encoding.UTF8))
                if (line.Trim().Length > 0) scripts.Add(line.Trim());
        }
        catch { }
        return scripts;
    }

    internal static void Save(List<string> scripts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
        File.WriteAllLines(FilePath, scripts.ToArray(), new UTF8Encoding(false));
    }
}

internal static class PowerHookRunner
{
    internal static bool SelfTest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "Matches-hook-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            var output = Path.Combine(directory, "order.txt");
            var first = Path.Combine(directory, "1.cmd");
            var second = Path.Combine(directory, "2.cmd");
            var failure = Path.Combine(directory, "fail.cmd");
            var afterFailure = Path.Combine(directory, "3.cmd");
            File.WriteAllText(first, "@echo off\r\necho 1 >" + SearchClient.QuoteArgument(output) + "\r\n", Encoding.ASCII);
            File.WriteAllText(second, "@echo off\r\necho 2 >>" + SearchClient.QuoteArgument(output) + "\r\n", Encoding.ASCII);
            File.WriteAllText(failure, "@exit /b 5\r\n", Encoding.ASCII);
            File.WriteAllText(afterFailure, "@echo off\r\necho 3 >>" + SearchClient.QuoteArgument(output) + "\r\n", Encoding.ASCII);
            var error = Run(new List<string> { first, second });
            var failureError = Run(new List<string> { failure, afterFailure });
            var lines = File.Exists(output) ? File.ReadAllLines(output) : new string[0];
            return error == null && failureError != null && lines.Length == 2 &&
                   lines[0].Trim() == "1" && lines[1].Trim() == "2";
        }
        finally
        {
            try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
            catch { }
        }
    }

    internal static ProcessStartInfo StartInfo(string script)
    {
        var extension = Path.GetExtension(script).ToLowerInvariant();
        var info = new ProcessStartInfo { UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(script) ?? Environment.CurrentDirectory };
        if (extension == ".ps1") { info.FileName = "powershell.exe"; info.Arguments = "-NoProfile -ExecutionPolicy Bypass -File " + SearchClient.QuoteArgument(script); }
        else if (extension == ".cmd" || extension == ".bat") { info.FileName = "cmd.exe"; info.Arguments = "/d /c " + SearchClient.QuoteArgument(script); }
        else if (extension == ".py") { info.FileName = "python.exe"; info.Arguments = SearchClient.QuoteArgument(script); }
        else if (extension == ".exe") info.FileName = script;
        else return null;
        return info;
    }

    internal static string Run(List<string> scripts)
    {
        // ponytail: hooks intentionally have no timeout; add per-script limits if unattended hooks can hang.
        foreach (var script in scripts)
        {
            if (!File.Exists(script)) return "脚本不存在：" + script;
            var info = StartInfo(script);
            if (info == null) return "不支持的脚本类型：" + script;
            try
            {
                using (var process = Process.Start(info))
                {
                    process.WaitForExit();
                    if (process.ExitCode != 0) return Path.GetFileName(script) + " 返回退出码 " + process.ExitCode;
                }
            }
            catch (Exception ex) { return Path.GetFileName(script) + " 执行失败：" + ex.Message; }
        }
        return null;
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
    private readonly HotkeyBox locate;
    private readonly HotkeyBox copy;
    private readonly HotkeyBox codex;
    internal HotkeySettings Settings { get; private set; }

    internal HotkeySettingsDialog(HotkeySettings current)
    {
        locate = new HotkeyBox(current.Locate);
        copy = new HotkeyBox(current.Copy);
        codex = new HotkeyBox(current.Codex);
        Text = "快捷键设置";
        ClientSize = new Size(390, 216);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        Font = new Font("Microsoft YaHei UI", 9F);

        Controls.Add(new Label { Text = "点击输入框，然后直接按下新的快捷键", Location = new Point(18, 12), Size = new Size(350, 22) });
        AddRow("打开所在文件夹", locate, 42);
        AddRow("复制路径", copy, 82);
        AddRow("打开 Codex Terminal", codex, 122);
        var okay = new Button { Text = "保存", Location = new Point(214, 171), Size = new Size(74, 28) };
        var cancel = new Button { Text = "取消", Location = new Point(296, 171), Size = new Size(74, 28), DialogResult = DialogResult.Cancel };
        okay.Click += delegate
        {
            var value = new HotkeySettings(locate.Hotkey, copy.Hotkey, codex.Hotkey);
            if (!HotkeyStore.IsValid(value.Locate) || !HotkeyStore.IsValid(value.Copy) || !HotkeyStore.IsValid(value.Codex))
            {
                MessageBox.Show(this, "字母、数字或方向键需要搭配 Ctrl、Alt 或 Shift。", "Matches", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
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

    private void AddRow(string text, HotkeyBox box, int top)
    {
        Controls.Add(new Label { Text = text, Location = new Point(18, top + 5), Size = new Size(190, 22) });
        box.Location = new Point(220, top);
        box.Size = new Size(150, 25);
        Controls.Add(box);
    }
}

internal sealed class HotkeyBox : TextBox
{
    internal Keys Hotkey { get; private set; }

    internal HotkeyBox(Keys hotkey)
    {
        ReadOnly = true;
        BackColor = SystemColors.Window;
        ShortcutsEnabled = false;
        Hotkey = hotkey;
        Text = HotkeyStore.Format(hotkey);
        TextAlign = HorizontalAlignment.Center;
    }

    protected override bool ProcessCmdKey(ref Message message, Keys keyData)
    {
        if (keyData == Keys.Enter || keyData == Keys.Escape || keyData == Keys.Tab || keyData == (Keys.Shift | Keys.Tab))
            return base.ProcessCmdKey(ref message, keyData);
        var key = keyData & Keys.KeyCode;
        if (key == Keys.ControlKey || key == Keys.ShiftKey || key == Keys.Menu) return true;
        Hotkey = keyData;
        Text = HotkeyStore.Format(keyData);
        SelectAll();
        return true;
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
            !Enum.TryParse(lines[2], out codex) || !IsValid(locate) || !IsValid(copy) || !IsValid(codex) ||
            locate == copy || locate == codex || copy == codex) return false;
        settings = new HotkeySettings(locate, copy, codex);
        return true;
    }

    internal static bool IsValid(Keys hotkey)
    {
        var key = hotkey & Keys.KeyCode;
        if (key == Keys.None || key == Keys.ControlKey || key == Keys.LControlKey || key == Keys.RControlKey ||
            key == Keys.ShiftKey || key == Keys.LShiftKey || key == Keys.RShiftKey || key == Keys.Menu ||
            key == Keys.LMenu || key == Keys.RMenu) return false;
        return (key >= Keys.F1 && key <= Keys.F24) || (hotkey & Keys.Modifiers) != Keys.None;
    }

    internal static string Format(Keys hotkey)
    {
        var parts = new List<string>();
        if ((hotkey & Keys.Control) != 0) parts.Add("Ctrl");
        if ((hotkey & Keys.Alt) != 0) parts.Add("Alt");
        if ((hotkey & Keys.Shift) != 0) parts.Add("Shift");
        var key = hotkey & Keys.KeyCode;
        parts.Add(key >= Keys.D0 && key <= Keys.D9 ? ((int)key - (int)Keys.D0).ToString() : key.ToString());
        return String.Join("+", parts.ToArray());
    }
}
