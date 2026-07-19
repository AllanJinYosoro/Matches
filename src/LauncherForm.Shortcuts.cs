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
    private void RebuildShortcuts()
    {
        selectedShortcutIndex = -1;
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

    internal static int MoveShortcutSelection(int current, int count, int columns, Keys key)
    {
        if (count == 0) return -1;
        if (current < 0) return 0;
        columns = Math.Max(1, columns);
        if (key == Keys.Left) return Math.Max(0, current - 1);
        if (key == Keys.Right) return Math.Min(count - 1, current + 1);
        if (key == Keys.Up) return Math.Max(0, current - columns);
        if (key == Keys.Down) return Math.Min(count - 1, current + columns);
        return current;
    }

    private void MoveShortcutSelection(Keys key)
    {
        if (shortcuts.Controls.Count == 0) return;
        var tile = shortcuts.Controls[0];
        var columns = Math.Max(1, (shortcuts.ClientSize.Width - shortcuts.Padding.Horizontal) /
            (tile.Width + tile.Margin.Horizontal));
        SelectShortcut(MoveShortcutSelection(selectedShortcutIndex, shortcuts.Controls.Count, columns, key));
        status.Text = "按 Ctrl 打开：" + shortcutItems[selectedShortcutIndex].Name;
    }

    private void SelectShortcut(int index)
    {
        selectedShortcutIndex = index >= 0 && index < shortcuts.Controls.Count ? index : -1;
        for (var i = 0; i < shortcuts.Controls.Count; i++)
        {
            var tile = shortcuts.Controls[i] as ShortcutTile;
            if (tile != null) tile.Selected = i == selectedShortcutIndex;
        }
        if (selectedShortcutIndex >= 0) shortcuts.ScrollControlIntoView(shortcuts.Controls[selectedShortcutIndex]);
    }

    private void OpenSelectedShortcut()
    {
        if (selectedShortcutIndex >= 0 && selectedShortcutIndex < shortcutItems.Count)
            OpenShortcut(shortcutItems[selectedShortcutIndex]);
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

    private void AddRegisteredApplication()
    {
        using (var dialog = new RegisteredApplicationDialog(RegisteredApplicationStore.Load()))
        {
            if (dialog.ShowDialog(this) == DialogResult.OK) AddShortcut(dialog.Shortcut);
        }
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

    private void ToggleAutoStart()
    {
        try
        {
            var enabled = !AutoStart.IsEnabled();
            AutoStart.SetEnabled(enabled);
            autoStartItem.Checked = enabled;
            status.Text = enabled ? "已启用开机自动启动" : "已关闭开机自动启动";
        }
        catch (Exception ex)
        {
            status.Text = "无法修改开机自动启动：" + ex.Message;
        }
    }

    private void EditPowerHooks()
    {
        using (var dialog = new PowerHookSettingsDialog(PowerHookStore.Load()))
        {
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            PowerHookStore.Save(dialog.Scripts);
            status.Text = "电源操作前脚本已保存";
        }
    }

    private void UpdateShortcutHint()
    {
        shortcutHint.Text = HotkeyStore.Format(hotkeys.Locate) + "  打开所在文件夹    " + HotkeyStore.Format(hotkeys.Copy) +
            "  复制路径    " + HotkeyStore.Format(hotkeys.Codex) + "  在当前目录打开 Codex Terminal";
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

}
