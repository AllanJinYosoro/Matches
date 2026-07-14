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
