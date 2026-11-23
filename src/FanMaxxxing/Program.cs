using FanMaxxxing.Core;

namespace FanMaxxxing;

/// <summary>
/// Punto de entrada propio: permite tareas "sin interfaz" (las usa el desinstalador)
/// sin llegar a crear la aplicación WPF.
/// </summary>
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args.Any(a => a.Equals("--uninstall-cleanup", StringComparison.OrdinalIgnoreCase)))
        {
            // Lo ejecuta el desinstalador (como SYSTEM): quitar las tareas programadas que creó la app.
            try
            {
                WindowsIntegration.RemoveAllTasks();
                return 0;
            }
            catch (Exception ex)
            {
                Log.Error("Limpieza de desinstalación", ex);
                return 1;
            }
        }

        var app = new App();
        app.InitializeComponent();
        return app.Run();
    }
}
