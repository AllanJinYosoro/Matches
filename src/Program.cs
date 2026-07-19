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

internal static class Program
{
    private static Mutex mutex;

    [STAThread]
    internal static int Main(string[] args)
    {
        if (args.Length == 1 && args[0] == "--self-test") return SelfTest();
        var startHidden = args.Length == 1 && args[0] == "--startup";

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
        using (var form = new LauncherForm(startHidden))
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
            if (arguments.Contains("name:") || !arguments.Contains("-n 500") || arguments.Contains("-sort") || !arguments.Contains("\"Matches\"")) return 1;
            if (SearchClient.ApplicationQuery("drawio") != "d*r*a*w*i*o*") return 1;
            var launchableArguments = SearchClient.BuildArguments("drawio", "C:\\temp\\result.txt", true);
            if (!launchableArguments.Contains("-n 100") || !launchableArguments.Contains("\"d*r*a*w*i*o*\"") ||
                !launchableArguments.Contains(" ext:lnk;exe;url;appref-ms;com;bat;cmd") || launchableArguments.Contains("-sort")) return 1;
            var ranked = SearchClient.RankResults("MonoCloud", new List<string>
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MonoCloud data"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "WinSxS", "MonoCloud system.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "MonoCloud", "MonoCloud app.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "MonoCloud.lnk")
            }, 100);
            if (!ranked[0].EndsWith("MonoCloud.lnk", StringComparison.OrdinalIgnoreCase) ||
                ranked[ranked.Count - 1].IndexOf("WinSxS", StringComparison.OrdinalIgnoreCase) < 0) return 1;
            var drawioShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs", "draw.io.lnk");
            var drawioFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "diagram.drawio");
            var drawioRanked = SearchClient.RankResults("drawio", new List<string>
            {
                drawioFile,
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "draw.io.lnk"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "draw.io", "draw.io.exe"),
                drawioShortcut
            }, 100);
            if (drawioRanked.Count != 2 || drawioRanked[0] != drawioShortcut || drawioRanked[1] != drawioFile) return 1;
            var explicitProgramData = SearchClient.RankResults("ProgramData", new List<string>
            {
                Path.Combine(Path.GetTempPath(), "ProgramData.txt"),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)
            }, 100);
            if (explicitProgramData[0] != Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)) return 1;
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
            var downRanked = SearchClient.RankResults("down", new List<string> { Path.Combine(Path.GetTempPath(), "down.exe") }, 1);
            var downloadRanked = SearchClient.RankResults("download", new List<string>(), 1);
            if (downRanked.Count != 1 || downRanked[0] != downloads ||
                downloadRanked.Count != 1 || downloadRanked[0] != downloads) return 1;
            var rankedFile = Path.GetTempFileName();
            try
            {
                var fileFirst = SearchClient.RankResults(String.Empty, new List<string> { Path.GetTempPath(), rankedFile }, 2);
                if (fileFirst[0] != rankedFile) return 1;
            }
            finally { File.Delete(rankedFile); }
            if (!LauncherForm.IsUninstallQuery(" 卸载 ") || !LauncherForm.IsUninstallQuery("XIEZAI") ||
                LauncherForm.IsUninstallQuery("卸载程序")) return 1;
            if (LauncherForm.PowerActionFor(" 关机 ") != "shutdown" || LauncherForm.PowerActionFor("GUANJI") != "shutdown" ||
                LauncherForm.PowerActionFor("重启") != "restart" || LauncherForm.PowerActionFor("ChOnGqI") != "restart" ||
                LauncherForm.PowerActionFor("重新启动") != null || LauncherForm.PowerArguments("restart") != "/r /t 0") return 1;
            using (var shutdown = Ui.PowerImage("shutdown", 32))
            using (var restart = Ui.PowerImage("restart", 32))
                if (shutdown.Size != new Size(32, 32) || restart.Size != new Size(32, 32) ||
                    ((Bitmap)shutdown).GetPixel(16, 4).ToArgb() == ((Bitmap)restart).GetPixel(16, 4).ToArgb()) return 1;
            using (var uninstall = Ui.UninstallImage(32))
                if (uninstall.Size != new Size(32, 32) || ((Bitmap)uninstall).GetPixel(16, 9).A == 0) return 1;
            if (!File.Exists(Path.Combine(Environment.SystemDirectory, "appwiz.cpl"))) return 1;
            var row = new Rectangle(0, 0, 770, 54);
            if (LauncherForm.ResultActionAt(new Point(710, 24), row) != 1 ||
                LauncherForm.ResultActionAt(new Point(740, 24), row) != 2 ||
                LauncherForm.ResultActionAt(new Point(100, 24), row) != 0) return 1;
            if (LauncherForm.MoveShortcutSelection(-1, 3, 2, Keys.Left) != 0 ||
                LauncherForm.MoveShortcutSelection(0, 3, 2, Keys.Right) != 1 ||
                LauncherForm.MoveShortcutSelection(0, 3, 2, Keys.Down) != 2 ||
                LauncherForm.MoveShortcutSelection(2, 3, 2, Keys.Up) != 0 ||
                LauncherForm.MoveShortcutSelection(0, 0, 2, Keys.Down) != -1) return 1;
            var saved = ShortcutStore.Serialize(new ShortcutItem("测试", "C:\\目录\\程序.exe"));
            ShortcutItem restored;
            if (!ShortcutStore.TryParse(saved, out restored) || restored.Name != "测试" || restored.Target != "C:\\目录\\程序.exe") return 1;
            if (!LauncherForm.WebSearchUrl("bili 火柴").StartsWith("https://search.bilibili.com/", StringComparison.Ordinal) ||
                !LauncherForm.WebSearchUrl("火柴").StartsWith("https://www.google.com/search", StringComparison.Ordinal)) return 1;
            var defaults = ShortcutStore.Defaults();
            if (defaults.Count != 2 || defaults[0].Name != "桌面" || defaults[1].Name != "下载") return 1;
            var registered = RegisteredApplicationStore.Load();
            for (var index = 0; index < registered.Count; index++)
                if (!File.Exists(registered[index].Target) || (index > 0 &&
                    StringComparer.CurrentCultureIgnoreCase.Compare(registered[index - 1].Name, registered[index].Name) > 0)) return 1;
            if (LauncherForm.ListFolder(AppDomain.CurrentDomain.BaseDirectory).Count == 0) return 1;
            if (Ui.WebsiteIconPath("https://chatgpt.com/a") != Ui.WebsiteIconPath("https://chatgpt.com/b") ||
                Ui.WebsiteIconPath("C:\\temp") != null) return 1;
            if (!LauncherForm.CodexTerminalArguments("C:\\a b", "codex.exe").Contains("\"C:\\a b\"")) return 1;
            HotkeySettings testedHotkeys;
            var controlL = (Keys.Control | Keys.L).ToString();
            if (!HotkeyStore.TryParse(new[] { controlL, "F5", "F6" }, out testedHotkeys) ||
                testedHotkeys.Locate != (Keys.Control | Keys.L) || HotkeyStore.Format(testedHotkeys.Locate) != "Ctrl+L" ||
                HotkeyStore.TryParse(new[] { controlL, controlL, "F6" }, out testedHotkeys)) return 1;
            if (PowerHookRunner.StartInfo("C:\\test.ps1").FileName != "powershell.exe" ||
                PowerHookRunner.StartInfo("C:\\test.txt") != null || !PowerHookRunner.SelfTest()) return 1;
            if (AutoStart.CommandFor("C:\\Program Files\\Matches\\Matches.exe") !=
                "\"C:\\Program Files\\Matches\\Matches.exe\" --startup") return 1;
            return 0;
        }
        catch { return 1; }
    }
}

internal static class AutoStart
{
    private const string KeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Matches";

    internal static string CommandFor(string executable)
    {
        return SearchClient.QuoteArgument(executable) + " --startup";
    }

    internal static bool IsEnabled()
    {
        try
        {
            using (var key = Registry.CurrentUser.OpenSubKey(KeyPath))
                return key != null && String.Equals(key.GetValue(ValueName) as string,
                    CommandFor(Application.ExecutablePath), StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    internal static void SetEnabled(bool enabled)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(KeyPath))
        {
            if (enabled) key.SetValue(ValueName, CommandFor(Application.ExecutablePath), RegistryValueKind.String);
            else key.DeleteValue(ValueName, false);
        }
    }
}
