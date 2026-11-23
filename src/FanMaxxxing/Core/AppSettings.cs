using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanMaxxxing.Core;

public enum BackendKind
{
    /// <summary>FanControl si está instalado; si no, control directo.</summary>
    Auto,
    /// <summary>Cambia la configuración de FanControl (Rem0o) por línea de comandos.</summary>
    FanControl,
    /// <summary>Control directo con LibreHardwareMonitor (experimental).</summary>
    Direct,
    /// <summary>Nada real: temperaturas simuladas, para probar la app.</summary>
    Simulation,
}

public enum AfterAction
{
    Nothing,
    Shutdown,
    Hibernate,
    Sleep,
}

public sealed class Stats
{
    public int TotalCooldowns { get; set; }
    public double TotalDegreesDropped { get; set; }
    public double BestDrop { get; set; }
    public int NightsSaved { get; set; }
    public double TotalCoolingSeconds { get; set; }
}

public sealed class AppSettings
{
    // --- Enfriamiento ---
    public int DurationMinutes { get; set; } = 5;
    public AfterAction AfterAction { get; set; } = AfterAction.Nothing;
    public bool SmartStop { get; set; } = true;
    public double SmartStopCpuTemp { get; set; } = 45;
    public double SmartStopGpuTemp { get; set; } = 45;
    public int SmartStopMinSeconds { get; set; } = 90;
    public bool TurnOffDisplay { get; set; }

    // --- Apagado ---
    public int PowerCountdownSeconds { get; set; } = 30;
    public bool ForceCloseApps { get; set; }

    // --- Motor ---
    public BackendKind Backend { get; set; } = BackendKind.Auto;
    public string? FanControlExePath { get; set; }
    public string? NormalConfigPath { get; set; }
    public bool UseCustomMaxConfig { get; set; }
    public string? CustomMaxConfigPath { get; set; }
    public List<string> ExcludedControlIds { get; set; } = new();
    public bool CloseFanControlIfItWasClosed { get; set; } = true;

    // --- Extras ---
    public bool SoundEnabled { get; set; } = true;
    public bool ReducedAnimations { get; set; }
    public bool CloseToTray { get; set; } = true;
    public bool FirstRunDone { get; set; }

    public Stats Stats { get; set; } = new();

    public AppSettings Clone()
    {
        var json = JsonSerializer.Serialize(this, SettingsStore.JsonOptions);
        return JsonSerializer.Deserialize<AppSettings>(json, SettingsStore.JsonOptions) ?? new AppSettings();
    }

    /// <summary>Corrige valores fuera de rango (por si alguien edita el JSON a mano).</summary>
    public void Sanitize()
    {
        DurationMinutes = Math.Clamp(DurationMinutes, 1, 30);
        SmartStopCpuTemp = Math.Clamp(SmartStopCpuTemp, 25, 90);
        SmartStopGpuTemp = Math.Clamp(SmartStopGpuTemp, 25, 90);
        SmartStopMinSeconds = Math.Clamp(SmartStopMinSeconds, 30, 1800);
        PowerCountdownSeconds = Math.Clamp(PowerCountdownSeconds, 5, 300);
        ExcludedControlIds ??= new();
        Stats ??= new();
        if (!Enum.IsDefined(Backend)) Backend = BackendKind.Auto;
        if (!Enum.IsDefined(AfterAction)) AfterAction = AfterAction.Nothing;
    }
}

public static class SettingsStore
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly object Sync = new();

    public static AppSettings Load()
    {
        lock (Sync)
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var json = File.ReadAllText(AppPaths.SettingsFile);
                    var s = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions) ?? new AppSettings();
                    s.Sanitize();
                    return s;
                }
            }
            catch (Exception ex)
            {
                Log.Error("No se pudieron leer los ajustes; se usan los de serie", ex);
                try
                {
                    File.Copy(AppPaths.SettingsFile, AppPaths.SettingsFile + ".corrupto", overwrite: true);
                }
                catch { /* ignorar */ }
            }
            var fresh = new AppSettings();
            fresh.Sanitize();
            return fresh;
        }
    }

    public static void Save(AppSettings settings)
    {
        lock (Sync)
        {
            try
            {
                settings.Sanitize();
                var json = JsonSerializer.Serialize(settings, JsonOptions);
                AppPaths.WriteAllTextAtomic(AppPaths.SettingsFile, json);
            }
            catch (Exception ex)
            {
                Log.Error("No se pudieron guardar los ajustes", ex);
            }
        }
    }
}
