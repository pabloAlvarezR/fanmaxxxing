using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text;

namespace FanMaxxxing.Core;

/// <summary>
/// Tareas programadas (para arrancar con Windows y para abrir como admin sin UAC)
/// y accesos directos en el escritorio.
/// </summary>
public static class WindowsIntegration
{
    public const string StartupTask = @"FanMaxxxing\Inicio";
    public const string ElevatedTask = @"FanMaxxxing\SinUAC";
    private static string PendingArgsFile => Path.Combine(AppPaths.DataDir, "args-pendientes.txt");

    // ---------- Tareas programadas ----------

    public static bool TaskExists(string name) => RunSchtasks($"/query /tn \"{name}\"", out _) == 0;

    public static string? CreateTask(string name, string arguments, bool atLogon)
    {
        string sid;
        using (var id = WindowsIdentity.GetCurrent()) sid = id.User?.Value ?? id.Name;

        string triggers = atLogon
            ? $"<Triggers><LogonTrigger><Enabled>true</Enabled><UserId>{SecurityElement.Escape(sid)}</UserId><Delay>PT5S</Delay></LogonTrigger></Triggers>"
            : "<Triggers />";

        var xml = $"""
            <?xml version="1.0" encoding="UTF-16"?>
            <Task version="1.2" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
              <RegistrationInfo>
                <Description>FanMaxxxing: enfriador post-vicio</Description>
              </RegistrationInfo>
              {triggers}
              <Principals>
                <Principal id="Author">
                  <UserId>{SecurityElement.Escape(sid)}</UserId>
                  <LogonType>InteractiveToken</LogonType>
                  <RunLevel>HighestAvailable</RunLevel>
                </Principal>
              </Principals>
              <Settings>
                <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
                <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
                <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
                <AllowHardTerminate>true</AllowHardTerminate>
                <StartWhenAvailable>false</StartWhenAvailable>
                <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
                <IdleSettings>
                  <StopOnIdleEnd>false</StopOnIdleEnd>
                  <RestartOnIdle>false</RestartOnIdle>
                </IdleSettings>
                <AllowStartOnDemand>true</AllowStartOnDemand>
                <Enabled>true</Enabled>
                <Hidden>false</Hidden>
                <RunOnlyIfIdle>false</RunOnlyIfIdle>
                <WakeToRun>false</WakeToRun>
                <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
                <Priority>5</Priority>
              </Settings>
              <Actions Context="Author">
                <Exec>
                  <Command>"{SecurityElement.Escape(AppPaths.ExePath)}"</Command>
                  <Arguments>{SecurityElement.Escape(arguments)}</Arguments>
                  <WorkingDirectory>{SecurityElement.Escape(AppContext.BaseDirectory.TrimEnd('\\'))}</WorkingDirectory>
                </Exec>
              </Actions>
            </Task>
            """;

        var tmp = Path.Combine(Path.GetTempPath(), "fanmaxxxing-task-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            File.WriteAllText(tmp, xml, Encoding.Unicode);
            int code = RunSchtasks($"/create /tn \"{name}\" /xml \"{tmp}\" /f", out var output);
            if (code != 0)
            {
                Log.Warn($"schtasks /create {name} -> {code}: {output}");
                return string.IsNullOrWhiteSpace(output) ? $"schtasks devolvió {code}" : output.Trim();
            }
            Log.Info($"Tarea creada: {name}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo crear la tarea programada", ex);
            return ex.Message;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignorar */ }
        }
    }

    /// <summary>
    /// Borra todas las tareas de FanMaxxxing y su carpeta en el Programador de tareas (lo usa el desinstalador).
    /// Usa la API COM del Programador porque schtasks.exe no sabe borrar carpetas.
    /// </summary>
    public static void RemoveAllTasks()
    {
        object? service = null, folder = null, root = null;
        try
        {
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Programador de tareas no disponible");
            service = Activator.CreateInstance(type)!;
            dynamic svc = service;
            svc.Connect();
            try
            {
                folder = svc.GetFolder(@"\FanMaxxxing");
            }
            catch (Exception ex) when (ex is COMException or FileNotFoundException or DirectoryNotFoundException)
            {
                Log.Info("Limpieza: no hay tareas de FanMaxxxing");
                return;
            }
            dynamic f = folder!;
            foreach (var name in new[] { "Inicio", "SinUAC" })
            {
                try
                {
                    f.DeleteTask(name, 0);
                    Log.Info($"Limpieza: tarea {name} borrada");
                }
                catch (Exception ex) when (ex is COMException or FileNotFoundException) { /* no existía */ }
            }
            root = svc.GetFolder(@"\");
            try
            {
                ((dynamic)root!).DeleteFolder("FanMaxxxing", 0);
            }
            catch (Exception ex) when (ex is COMException or IOException or UnauthorizedAccessException)
            {
                Log.Warn("Limpieza: no se pudo borrar la carpeta de tareas: " + ex.Message);
            }
        }
        finally
        {
            foreach (var o in new[] { root, folder, service })
                if (o != null && Marshal.IsComObject(o)) Marshal.FinalReleaseComObject(o);
        }
    }

    public static string? DeleteTask(string name)
    {
        if (!TaskExists(name)) return null;
        int code = RunSchtasks($"/delete /tn \"{name}\" /f", out var output);
        return code == 0 ? null : output.Trim();
    }

    /// <summary>
    /// Desde una instancia sin permisos: deja los argumentos en un fichero y lanza la tarea elevada
    /// (Windows la ejecuta como admin sin preguntar). Devuelve true si se lanzó.
    /// </summary>
    public static bool TryLaunchElevatedViaTask(IEnumerable<string> args)
    {
        try
        {
            if (!TaskExists(ElevatedTask)) return false;
            File.WriteAllText(PendingArgsFile, string.Join("\n", args), Encoding.UTF8);
            return RunSchtasks($"/run /tn \"{ElevatedTask}\"", out _) == 0;
        }
        catch (Exception ex)
        {
            Log.Warn("No se pudo usar la tarea elevada: " + ex.Message);
            return false;
        }
    }

    /// <summary>Recoge (y borra) los argumentos que dejó la instancia sin permisos. Caducan a los 2 minutos.</summary>
    public static string[] TakePendingArgs()
    {
        try
        {
            var fi = new FileInfo(PendingArgsFile);
            if (!fi.Exists) return Array.Empty<string>();
            bool fresh = (DateTime.UtcNow - fi.LastWriteTimeUtc).TotalMinutes < 2;
            var lines = File.ReadAllLines(PendingArgsFile);
            fi.Delete();
            return fresh ? lines.Where(l => !string.IsNullOrWhiteSpace(l)).ToArray() : Array.Empty<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static int RunSchtasks(string args, out string output)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "schtasks.exe"), args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var p = Process.Start(psi);
        if (p == null) { output = "No se pudo lanzar schtasks"; return -1; }
        var o = p.StandardOutput.ReadToEndAsync();
        var e = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(15000))
        {
            try { p.Kill(); } catch { /* ignorar */ }
            output = "schtasks no respondió";
            return -1;
        }
        output = (o.Wait(1000) ? o.Result : "") + (e.Wait(1000) ? e.Result : "");
        return p.ExitCode;
    }

    // ---------- Accesos directos ----------

    public static string? CreateDesktopShortcut(string fileName, string arguments, string description)
    {
        object? shell = null, shortcut = null;
        try
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            var path = Path.Combine(desktop, fileName + ".lnk");
            var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell no disponible");
            shell = Activator.CreateInstance(type)!;
            dynamic sh = shell;
            shortcut = sh.CreateShortcut(path);
            dynamic lnk = shortcut!;
            lnk.TargetPath = AppPaths.ExePath;
            lnk.Arguments = arguments;
            lnk.WorkingDirectory = AppContext.BaseDirectory;
            lnk.Description = description;
            lnk.IconLocation = AppPaths.ExePath + ",0";
            lnk.Save();
            Log.Info($"Acceso directo creado: {path}");
            return null;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo crear el acceso directo", ex);
            return ex.Message;
        }
        finally
        {
            if (shortcut != null && Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            if (shell != null && Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }
    }
}
