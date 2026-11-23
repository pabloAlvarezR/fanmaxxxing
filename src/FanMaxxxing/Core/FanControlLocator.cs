using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace FanMaxxxing.Core;

/// <summary>Encuentra la instalación de FanControl (Rem0o) y sus configuraciones.</summary>
public static class FanControlLocator
{
    public const string ProcessName = "FanControl";

    public static bool IsRunning() => IsRunning(out _);

    public static bool IsRunning(out string? exePath)
    {
        exePath = null;
        var procs = Process.GetProcessesByName(ProcessName);
        try
        {
            foreach (var p in procs)
            {
                exePath ??= Native.TryGetProcessPath(p.Id);
            }
            return procs.Length > 0;
        }
        finally
        {
            foreach (var p in procs) p.Dispose();
        }
    }

    public static string? FindExe(string? configured)
    {
        if (IsValidExe(configured)) return configured;

        if (IsRunning(out var running) && IsValidExe(running)) return running;

        foreach (var candidate in RegistryCandidates().Concat(CommonCandidates()))
        {
            if (IsValidExe(candidate)) return candidate;
        }
        return null;
    }

    public static bool IsValidExe(string? path)
        => !string.IsNullOrWhiteSpace(path)
           && path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
           && File.Exists(path);

    public static string ConfigDir(string exe) => Path.Combine(Path.GetDirectoryName(exe)!, "Configurations");

    /// <summary>Configuraciones del usuario (sin la nuestra), de la más reciente a la más antigua.</summary>
    public static IReadOnlyList<string> ListConfigs(string? exe)
    {
        if (!IsValidExe(exe)) return Array.Empty<string>();
        try
        {
            var dir = ConfigDir(exe!);
            if (!Directory.Exists(dir)) return Array.Empty<string>();
            return new DirectoryInfo(dir)
                .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.Equals(FanControlConfigTool.MaxFileName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Select(f => f.FullName)
                .ToList();
        }
        catch (Exception ex)
        {
            Log.Warn($"No se pudieron listar las configs de FanControl: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// La config "normal" a restaurar: la elegida en ajustes o, en automático, la modificada más
    /// recientemente (FanControl guarda la config activa cada vez que cambia algo).
    /// </summary>
    public static string? ResolveNormalConfig(string? exe, string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return configured;
        return ListConfigs(exe).FirstOrDefault();
    }

    private static IEnumerable<string> RegistryCandidates()
    {
        var results = new List<string>();
        (RegistryKey root, string path)[] roots =
        {
            (Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
            (Registry.CurrentUser, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
        };
        foreach (var (root, path) in roots)
        {
            try
            {
                using var key = root.OpenSubKey(path);
                if (key == null) continue;
                foreach (var name in key.GetSubKeyNames())
                {
                    try
                    {
                        using var sub = key.OpenSubKey(name);
                        var display = sub?.GetValue("DisplayName") as string;
                        if (display == null) continue;
                        var norm = display.Replace(" ", "");
                        if (!norm.StartsWith("FanControl", StringComparison.OrdinalIgnoreCase)) continue;

                        if (sub!.GetValue("InstallLocation") is string loc && !string.IsNullOrWhiteSpace(loc))
                            results.Add(Path.Combine(loc.Trim('"'), "FanControl.exe"));
                        if (sub.GetValue("DisplayIcon") is string icon && !string.IsNullOrWhiteSpace(icon))
                        {
                            var iconPath = icon.Split(',')[0].Trim('"');
                            if (iconPath.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) results.Add(iconPath);
                        }
                    }
                    catch { /* clave rara: siguiente */ }
                }
            }
            catch { /* sin permisos: siguiente */ }
        }
        return results;
    }

    private static IEnumerable<string> CommonCandidates()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string pf86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string user = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        var list = new List<string>
        {
            Path.Combine(pf86, "FanControl", "FanControl.exe"),
            Path.Combine(pf, "FanControl", "FanControl.exe"),
            Path.Combine(local, "Programs", "FanControl", "FanControl.exe"),
            Path.Combine(user, "scoop", "apps", "fancontrol", "current", "FanControl.exe"),
            Path.Combine(desktop, "FanControl", "FanControl.exe"),
            Path.Combine(user, "Downloads", "FanControl", "FanControl.exe"),
            @"C:\FanControl\FanControl.exe",
            @"D:\FanControl\FanControl.exe",
        };

        // winget (instalación portable)
        try
        {
            var winget = Path.Combine(local, "Microsoft", "WinGet", "Packages");
            if (Directory.Exists(winget))
            {
                foreach (var dir in Directory.EnumerateDirectories(winget, "Rem0o.FanControl*"))
                    list.Add(Path.Combine(dir, "FanControl.exe"));
            }
        }
        catch { /* ignorar */ }

        return list;
    }
}
