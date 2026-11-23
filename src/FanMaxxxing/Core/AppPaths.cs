using System.IO;

namespace FanMaxxxing.Core;

/// <summary>Rutas de datos de la aplicación (%LOCALAPPDATA%\FanMaxxxing).</summary>
public static class AppPaths
{
    public static string DataDir { get; } = InitDataDir();

    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string LogFile => Path.Combine(DataDir, "log.txt");
    public static string RecoveryMarkerFile => Path.Combine(DataDir, "boost-activo.json");

    public static string ExePath => Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "FanMaxxxing.exe");

    private static string InitDataDir()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FanMaxxxing");
        try
        {
            Directory.CreateDirectory(dir);
        }
        catch
        {
            // Último recurso: carpeta temporal (nunca debería pasar)
            dir = Path.Combine(Path.GetTempPath(), "FanMaxxxing");
            Directory.CreateDirectory(dir);
        }
        return dir;
    }

    /// <summary>Escribe un fichero de forma atómica (tmp + replace) para no dejarlo corrupto si se va la luz.</summary>
    public static void WriteAllTextAtomic(string path, string content, System.Text.Encoding? encoding = null)
    {
        encoding ??= new System.Text.UTF8Encoding(false);
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, "." + Path.GetFileName(path) + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var sw = new StreamWriter(fs, encoding))
            {
                sw.Write(content);
                sw.Flush();
                fs.Flush(true);
            }

            if (File.Exists(path))
                File.Replace(tmp, path, null, ignoreMetadataErrors: true);
            else
                File.Move(tmp, path);
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignorar */ }
        }
    }
}
