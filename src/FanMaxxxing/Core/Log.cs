using System.IO;
using System.Text;

namespace FanMaxxxing.Core;

/// <summary>
/// Logger mínimo, thread-safe, con rotación por tamaño (no crece sin límite).
/// </summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024; // 1 MB
    private static readonly object Sync = new();
    private static DateTime _lastErrorBurst = DateTime.MinValue;
    private static int _burstCount;

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message} :: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    /// <summary>Errores repetitivos (p. ej. en bucles de sensores): como mucho 5 por minuto.</summary>
    public static void ErrorThrottled(string message, Exception? ex = null)
    {
        lock (Sync)
        {
            var now = DateTime.UtcNow;
            if ((now - _lastErrorBurst).TotalSeconds > 60)
            {
                _lastErrorBurst = now;
                _burstCount = 0;
            }
            if (++_burstCount > 5) return;
        }
        Error(message, ex);
    }

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}";
        lock (Sync)
        {
            try
            {
                var file = AppPaths.LogFile;
                var fi = new FileInfo(file);
                if (fi.Exists && fi.Length > MaxBytes)
                {
                    var old = Path.Combine(AppPaths.DataDir, "log.old.txt");
                    File.Copy(file, old, overwrite: true);
                    File.Delete(file);
                }
                File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch
            {
                // El log nunca debe tumbar la app.
            }
        }
#if DEBUG
        System.Diagnostics.Debug.Write(line);
#endif
    }
}
