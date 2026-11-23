using System.IO;
using System.Text.Json;

namespace FanMaxxxing.Core;

/// <summary>
/// Fichero que existe solo mientras los ventiladores están al máximo. Si la app muere a mitad
/// (cuelgue, matar proceso, apagón), al volver a abrirla lo encontramos y devolvemos todo a la normalidad.
/// </summary>
public sealed class RecoveryMarker
{
    public BackendKind Backend { get; set; }
    public string? FanControlExe { get; set; }
    public string? NormalConfig { get; set; }
    public bool FanControlWasRunning { get; set; }
    public DateTime StartedUtc { get; set; } = DateTime.UtcNow;

    public static void Write(RecoveryMarker marker)
    {
        try
        {
            AppPaths.WriteAllTextAtomic(AppPaths.RecoveryMarkerFile, JsonSerializer.Serialize(marker, SettingsStore.JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo escribir el marcador de recuperación", ex);
        }
    }

    public static RecoveryMarker? Read()
    {
        try
        {
            if (!File.Exists(AppPaths.RecoveryMarkerFile)) return null;
            return JsonSerializer.Deserialize<RecoveryMarker>(File.ReadAllText(AppPaths.RecoveryMarkerFile), SettingsStore.JsonOptions);
        }
        catch (Exception ex)
        {
            Log.Error("Marcador de recuperación ilegible", ex);
            return null;
        }
    }

    public static void Delete()
    {
        try
        {
            if (File.Exists(AppPaths.RecoveryMarkerFile)) File.Delete(AppPaths.RecoveryMarkerFile);
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo borrar el marcador de recuperación", ex);
        }
    }
}
