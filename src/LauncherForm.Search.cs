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

    internal static string PowerActionFor(string query)
    {
        query = query.Trim();
        if (query == "关机") return "shutdown";
        if (query == "重启") return "restart";
        return null;
    }

    internal static string PowerArguments(string action)
    {
        return action == "restart" ? "/r /t 0" : "/s /t 0";
    }

    private void ShowResults(List<string> paths, string query)
    {
        results.BeginUpdate();
        ClearResults();
        var powerAction = PowerActionFor(query);
        if (powerAction != null)
        {
            var path = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
            var name = powerAction == "restart" ? "重启" : "关机";
            var item = new ListViewItem(name);
            item.Tag = new SearchResult(path, name, "Windows 电源操作", powerAction);
            results.Items.Add(item);
        }
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
        if (result.Image == null) result.Image = result.PowerAction == null
            ? Ui.GetImage(result.Path, 32) : Ui.PowerImage(result.PowerAction, 32);
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

}
