using System.Diagnostics;
using System.IO;

namespace FanMaxxxing.Core;

public static class PowerActions
{
    private static bool _keepAwake;

    /// <summary>
    /// Evita que Windows se suspenda mientras enfriamos (si se suspende a mitad, los ventiladores se paran
    /// con el calor dentro, que es justo lo que queremos evitar). Llamar siempre desde el hilo de UI.
    /// </summary>
    public static void KeepSystemAwake(bool on)
    {
        if (_keepAwake == on) return;
        _keepAwake = on;
        var flags = on
            ? Native.ExecutionState.Continuous | Native.ExecutionState.SystemRequired
            : Native.ExecutionState.Continuous;
        if (Native.SetThreadExecutionState(flags) == 0)
            Log.Warn("SetThreadExecutionState falló");
    }

    /// <summary>Apaga la pantalla (se enciende al mover el ratón). No afecta al enfriamiento.</summary>
    public static void TurnOffDisplay(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.PostMessage(hwnd, Native.WM_SYSCOMMAND, (IntPtr)Native.SC_MONITORPOWER, (IntPtr)2);
    }

    public static string Describe(AfterAction a) => a switch
    {
        AfterAction.Shutdown => Loc.T("verb.shutdown"),
        AfterAction.Hibernate => Loc.T("verb.hibernate"),
        AfterAction.Sleep => Loc.T("verb.sleep"),
        _ => Loc.T("verb.nothing"),
    };

    /// <summary>Ejecuta la acción de energía. Devuelve null si ha ido bien o un mensaje de error.</summary>
    public static string? Execute(AfterAction action, bool forceCloseApps)
    {
        Log.Info($"Acción de energía: {action} (forzar={forceCloseApps})");
        try
        {
            switch (action)
            {
                case AfterAction.Shutdown:
                    return RunShutdownExe(forceCloseApps ? "/s /t 0 /f" : "/s /t 0");
                case AfterAction.Hibernate:
                    return RunShutdownExe("/h");
                case AfterAction.Sleep:
                    if (!Native.SetSuspendState(false, false, false))
                        return Loc.T("power.sleepfail");
                    return null;
                default:
                    return null;
            }
        }
        catch (Exception ex)
        {
            Log.Error("Fallo en acción de energía", ex);
            return ex.Message;
        }
    }

    private static string? RunShutdownExe(string args)
    {
        var exe = Path.Combine(Environment.SystemDirectory, "shutdown.exe");
        var psi = new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        using var p = Process.Start(psi);
        if (p == null) return "No se pudo lanzar shutdown.exe";
        // Leer las salidas antes de esperar para evitar bloqueos por buffer lleno.
        var errTask = p.StandardError.ReadToEndAsync();
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(15000)) return null; // sigue en curso: Windows ya está apagando
        var err = (errTask.Wait(1000) ? errTask.Result : "") + (outTask.Wait(1000) ? outTask.Result : "");
        if (p.ExitCode != 0)
        {
            Log.Warn($"shutdown.exe {args} -> código {p.ExitCode}: {err}");
            return string.IsNullOrWhiteSpace(err) ? $"shutdown.exe devolvió {p.ExitCode}" : err.Trim();
        }
        return null;
    }
}
