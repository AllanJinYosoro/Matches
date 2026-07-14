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

internal sealed partial class LauncherForm
{
    private void SearchKeyDown(object sender, KeyEventArgs e)
    {
        var controlKey = e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey;
        if (!controlKey) shortcutControlPending = false;
        var shortcutHome = !webSearchMode && !folderMode && search.TextLength == 0;
        if (shortcutHome && (e.KeyCode == Keys.Left || e.KeyCode == Keys.Right ||
            e.KeyCode == Keys.Up || e.KeyCode == Keys.Down))
        {
            MoveShortcutSelection(e.KeyCode);
            e.Handled = true;
            e.SuppressKeyPress = true;
        }
        else if (shortcutHome && controlKey && selectedShortcutIndex >= 0)
        {
            shortcutControlPending = true;
            e.Handled = true;
        }
        else if (!webSearchMode && e.KeyCode == Keys.Down && results.Items.Count > 0)
        {
            var next = results.Items[Math.Min(1, results.Items.Count - 1)];
            next.Selected = true;
            next.Focused = true;
            results.Focus();
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

    private void SearchKeyUp(object sender, KeyEventArgs e)
    {
        var controlKey = e.KeyCode == Keys.ControlKey || e.KeyCode == Keys.LControlKey || e.KeyCode == Keys.RControlKey;
        if (!controlKey || !shortcutControlPending) return;
        shortcutControlPending = false;
        keyboardHook.CancelControlSequence();
        OpenSelectedShortcut();
        e.Handled = true;
        e.SuppressKeyPress = true;
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

    private void ResultsKeyPress(object sender, KeyPressEventArgs e)
    {
        if (Char.IsControl(e.KeyChar)) return;
        if (folderMode) SetWebSearchMode(false);
        search.Focus();
        search.SelectionStart = search.TextLength;
        search.SelectedText = e.KeyChar.ToString();
        e.Handled = true;
    }

    private string SelectedPath()
    {
        var result = SelectedResult();
        return result == null ? null : result.Path;
    }

    private SearchResult SelectedResult()
    {
        if (results.SelectedItems.Count > 0) return (SearchResult)results.SelectedItems[0].Tag;
        if (results.Items.Count > 0) return (SearchResult)results.Items[0].Tag;
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
        var result = SelectedResult();
        if (result == null) return;
        if (result.PowerAction != null) { ConfirmPowerAction(result.PowerAction); return; }
        try { Process.Start(new ProcessStartInfo(result.Path) { UseShellExecute = true }); Hide(); }
        catch (Exception ex) { status.Text = "无法打开：" + ex.Message; }
    }

    private void ConfirmPowerAction(string action)
    {
        if (powerActionRunning) return;
        var name = action == "restart" ? "重启" : "关机";
        var scripts = PowerHookStore.Load();
        var message = "确定要" + name + " Windows？" +
            (scripts.Count == 0 ? String.Empty : "\r\n将先按顺序执行 " + scripts.Count + " 个脚本。");
        if (MessageBox.Show(this, message, name, MessageBoxButtons.OKCancel,
            MessageBoxIcon.Warning) != DialogResult.OK) return;
        if (scripts.Count == 0) { ExecutePowerAction(action); return; }
        powerActionRunning = true;
        status.Text = "正在执行电源操作前脚本…";
        Task.Factory.StartNew(delegate { return PowerHookRunner.Run(scripts); }).ContinueWith(delegate(Task<string> task)
        {
            if (IsDisposed) return;
            BeginInvoke((Action)delegate
            {
                powerActionRunning = false;
                var error = task.IsFaulted ? task.Exception.GetBaseException().Message : task.Result;
                if (error != null)
                {
                    status.Text = "已取消" + name + "：" + error;
                    MessageBox.Show(this, status.Text, "Matches", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ExecutePowerAction(action);
            });
        });
    }

    private void ExecutePowerAction(string action)
    {
        var name = action == "restart" ? "重启" : "关机";
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "shutdown.exe"))
            {
                Arguments = PowerArguments(action),
                UseShellExecute = false,
                CreateNoWindow = true
            });
            Hide();
        }
        catch (Exception ex) { status.Text = "无法执行" + name + "：" + ex.Message; }
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
