using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        var root = AppContext.BaseDirectory;
        var script = Path.Combine(root, "ytdl-manager-gui.ps1");

        if (!File.Exists(script))
        {
            MessageBox.Show(
                "Не найден ytdl-manager-gui.ps1 рядом с VideoDownloader.exe.",
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var pwsh = FindPwsh();
        if (pwsh == null)
        {
            MessageBox.Show(
                "Нужен PowerShell 7 (pwsh.exe). Установи PowerShell 7 и запусти приложение снова.",
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = pwsh,
            WorkingDirectory = root,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        psi.ArgumentList.Add("-NoProfile");
        psi.ArgumentList.Add("-ExecutionPolicy");
        psi.ArgumentList.Add("Bypass");
        psi.ArgumentList.Add("-WindowStyle");
        psi.ArgumentList.Add("Hidden");
        psi.ArgumentList.Add("-STA");
        psi.ArgumentList.Add("-File");
        psi.ArgumentList.Add(script);

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        try
        {
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось запустить GUI: " + ex.Message,
                "Video Downloader",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
        }
    }

    private static string? FindPwsh()
    {
        var path = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in path.Split(Path.PathSeparator).Where(x => !string.IsNullOrWhiteSpace(x)))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "pwsh.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }

        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var powershellRoot = Path.Combine(programFiles, "PowerShell");
        if (Directory.Exists(powershellRoot))
        {
            var candidate = Directory.GetDirectories(powershellRoot)
                .OrderByDescending(x => x)
                .Select(x => Path.Combine(x, "pwsh.exe"))
                .FirstOrDefault(File.Exists);
            if (candidate != null) return candidate;
        }

        return null;
    }
}
