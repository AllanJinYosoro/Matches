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

internal sealed class SearchClient
{
    private readonly string executable;
    private readonly object sync = new object();
    private Process running;

    internal SearchClient(string executablePath) { executable = executablePath; }

    internal List<string> Search(string query)
    {
        var launchables = RunSearch(query, true);
        var general = RunSearch(query, false);
        return RankResults(query, ParseResults(launchables + "\n" + general), 100);
    }

    private string RunSearch(string query, bool launchableOnly)
    {
        if (!File.Exists(executable)) throw new FileNotFoundException("es.exe 不存在", executable);
        var outputFile = Path.Combine(Path.GetTempPath(), "Matches-" + Guid.NewGuid().ToString("N") + ".txt");
        var info = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = BuildArguments(query, outputFile, launchableOnly),
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
            return File.Exists(outputFile) ? File.ReadAllText(outputFile, Encoding.UTF8) : String.Empty;
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

    internal static string BuildArguments(string query, string outputFile, bool launchableOnly = false)
    {
        // ponytail: separate 100 launchable and 500 general candidates; raise only if measured misses remain.
        var search = launchableOnly ? ApplicationQuery(query) : query;
        return "-instance Matches -n " + (launchableOnly ? "100" : "500") + " -full-path-and-name -export-txt " + QuoteArgument(outputFile) +
               " -utf8-bom " + QuoteArgument(search) + (launchableOnly ? " ext:lnk;exe;url;appref-ms;com;bat;cmd" : String.Empty);
    }

    internal static string ApplicationQuery(string query)
    {
        var pattern = new StringBuilder();
        foreach (var character in query.Trim().Trim('"'))
        {
            if (!Char.IsLetterOrDigit(character)) continue;
            if (pattern.Length > 0) pattern.Append('*');
            pattern.Append(character);
        }
        return pattern.Length == 0 ? query : pattern.Append('*').ToString();
    }

    internal static List<string> RankResults(string query, List<string> paths, int maximum)
    {
        var normalized = query.Trim().Trim('"');
        var preferred = String.Equals(normalized, "down", StringComparison.OrdinalIgnoreCase) ||
            String.Equals(normalized, "download", StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads") : null;
        if (preferred != null && !paths.Exists(delegate(string path)
            { return String.Equals(path, preferred, StringComparison.OrdinalIgnoreCase); })) paths.Insert(0, preferred);

        var order = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < paths.Count; index++)
        {
            if (!order.ContainsKey(paths[index])) order.Add(paths[index], index);
            scores[paths[index]] = preferred != null && String.Equals(paths[index], preferred, StringComparison.OrdinalIgnoreCase)
                ? Int32.MaxValue : RankScore(query, paths[index]);
        }
        paths.Sort(delegate(string left, string right)
        {
            var ranked = scores[right].CompareTo(scores[left]);
            return ranked != 0 ? ranked : order[left].CompareTo(order[right]);
        });

        var seenLaunchables = new HashSet<string>(StringComparer.Ordinal);
        var count = 0;
        for (var index = 0; index < paths.Count && count < maximum; index++)
        {
            var key = LaunchableNameKey(paths[index]);
            if (key != null && !seenLaunchables.Add(key)) continue;
            paths[count++] = paths[index];
        }
        if (paths.Count > count) paths.RemoveRange(count, paths.Count - count);
        return paths;
    }

    private static int RankScore(string query, string path)
    {
        query = query.Trim().Trim('"');
        var queryKey = SearchKey(query);
        var clean = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(clean);
        var extension = Path.GetExtension(name);
        var launchable = IsLaunchable(extension);
        var comparableName = SearchKey(launchable ? Path.GetFileNameWithoutExtension(name) : name);
        var score = queryKey.Length > 0 && String.Equals(comparableName, queryKey, StringComparison.Ordinal) ? 120 :
            queryKey.Length > 0 && comparableName.StartsWith(queryKey, StringComparison.Ordinal) ? 40 :
            queryKey.Length > 0 && comparableName.IndexOf(queryKey, StringComparison.Ordinal) >= 0 ? 20 : 0;
        if (launchable) score += 50;
        if (File.Exists(clean)) score += 50;

        var commonStart = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        var userStart = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        var commonDesktop = Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
        var userDesktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (IsUnder(path, commonStart) || IsUnder(path, userStart)) return score + 120;
        if (IsUnder(path, commonDesktop) || IsUnder(path, userDesktop)) return score + 100;

        if (IsDeepSystemPath(path) && !MentionsAny(query, "$Recycle.Bin", "System Volume Information", "Recovery",
            "Package Cache", "WER", "WinSxS", "Installer", "servicing", "assembly", "WindowsApps", "Temp", "Packages"))
            return score - 160;
        if (IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)) &&
            !MentionsAny(query, "ProgramData")) return score - 100;
        if (IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.Windows)) &&
            !MentionsAny(query, "Windows")) return score - 100;
        if ((IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData)) ||
            IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))) &&
            !MentionsAny(query, "AppData")) return score - 60;
        if ((IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)) ||
            IsUnder(path, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86))) &&
            !MentionsAny(query, "Program Files")) return score - 50;
        return score;
    }

    private static string LaunchableNameKey(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        var extension = Path.GetExtension(name);
        if (!String.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) &&
            !String.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase)) return null;
        var key = SearchKey(Path.GetFileNameWithoutExtension(name));
        return key.Length == 0 ? null : key;
    }

    private static string SearchKey(string value)
    {
        var key = new StringBuilder();
        foreach (var character in value)
            if (Char.IsLetterOrDigit(character)) key.Append(Char.ToUpperInvariant(character));
        return key.ToString();
    }

    private static bool IsLaunchable(string extension)
    {
        return String.Equals(extension, ".lnk", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(extension, ".url", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(extension, ".appref-ms", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(extension, ".com", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(extension, ".bat", StringComparison.OrdinalIgnoreCase) ||
               String.Equals(extension, ".cmd", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsDeepSystemPath(string path)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.GetPathRoot(path) ?? String.Empty;
        return IsUnder(path, Path.Combine(root, "$Recycle.Bin")) ||
               IsUnder(path, Path.Combine(root, "System Volume Information")) ||
               IsUnder(path, Path.Combine(root, "Recovery")) ||
               IsUnder(path, Path.Combine(programData, "Package Cache")) ||
               IsUnder(path, Path.Combine(programData, "Microsoft", "Windows", "WER")) ||
               IsUnder(path, Path.Combine(windows, "WinSxS")) ||
               IsUnder(path, Path.Combine(windows, "Installer")) ||
               IsUnder(path, Path.Combine(windows, "servicing")) ||
               IsUnder(path, Path.Combine(windows, "assembly")) ||
               IsUnder(path, Path.Combine(programFiles, "WindowsApps")) ||
               IsUnder(path, Path.Combine(localAppData, "Temp")) ||
               IsUnder(path, Path.Combine(localAppData, "Packages"));
    }

    private static bool IsUnder(string path, string directory)
    {
        if (directory.Length == 0) return false;
        directory = directory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return String.Equals(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), directory, StringComparison.OrdinalIgnoreCase) ||
               (path.Length > directory.Length && path.StartsWith(directory, StringComparison.OrdinalIgnoreCase) &&
                (path[directory.Length] == Path.DirectorySeparatorChar || path[directory.Length] == Path.AltDirectorySeparatorChar));
    }

    private static bool MentionsAny(string query, params string[] names)
    {
        foreach (var name in names)
            if (query.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) return true;
        return false;
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
