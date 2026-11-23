using System.ComponentModel;
using System.Diagnostics;
using System.Security.Principal;

namespace FanMaxxxing.Core;

public static class Elevation
{
    public static bool IsAdmin { get; } = CheckAdmin();

    private static bool CheckAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public enum RelaunchResult { Started, Declined, Failed }

    /// <summary>Relanza este mismo exe como administrador (UAC) con los argumentos dados.</summary>
    public static RelaunchResult RelaunchAsAdmin(IEnumerable<string> args)
    {
        try
        {
            var psi = new ProcessStartInfo(AppPaths.ExePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WorkingDirectory = AppContext.BaseDirectory,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi);
            return p != null ? RelaunchResult.Started : RelaunchResult.Failed;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED: el usuario dijo que no al UAC
            return RelaunchResult.Declined;
        }
        catch (Exception ex)
        {
            Log.Error("No se pudo relanzar como administrador", ex);
            return RelaunchResult.Failed;
        }
    }
}
