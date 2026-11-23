namespace FanMaxxxing.Core;

/// <summary>
/// Argumentos soportados:
///   --start                 empieza a enfriar nada más abrir
///   --minutes N             duración (1-30)
///   --then shutdown|hibernate|sleep|nothing
///   --tray                  arranca minimizado en la bandeja
///   --sim                   modo simulación (no toca hardware)
///   --no-elevate            no pedir permisos de administrador
/// </summary>
public sealed class LaunchOptions
{
    public bool StartNow { get; set; }
    public int? Minutes { get; set; }
    public AfterAction? Then { get; set; }
    public bool Tray { get; set; }
    public bool Simulation { get; set; }
    public bool NoElevate { get; set; }
    public bool FromTask { get; set; }

    /// <summary>Solo depuración: guarda una captura de la ventana en esta ruta tras <see cref="SnapDelay"/> s y sale.</summary>
    public string? SnapPath { get; set; }
    public double SnapDelay { get; set; } = 4;

    /// <summary>Solo depuración: duración del enfriamiento en segundos.</summary>
    public double? DebugSeconds { get; set; }

    /// <summary>Solo depuración: encadena enfriamientos sin fin y registra memoria/handles (busca fugas).</summary>
    public bool DebugLoop { get; set; }

    /// <summary>Solo depuración: la captura es de la ventana de Ajustes.</summary>
    public bool SnapSettings { get; set; }

    public static LaunchOptions Parse(IReadOnlyList<string> args)
    {
        var o = new LaunchOptions();
        for (int i = 0; i < args.Count; i++)
        {
            var a = args[i].Trim().ToLowerInvariant();
            string? NextValue() => i + 1 < args.Count ? args[++i] : null;
            switch (a)
            {
                case "--start":
                case "/start":
                    o.StartNow = true;
                    break;
                case "--minutes":
                case "--min":
                case "-m":
                    if (int.TryParse(NextValue(), out var m)) o.Minutes = Math.Clamp(m, 1, 30);
                    break;
                case "--then":
                case "--after":
                    o.Then = (NextValue() ?? "").ToLowerInvariant() switch
                    {
                        "shutdown" or "apagar" or "off" => AfterAction.Shutdown,
                        "hibernate" or "hibernar" => AfterAction.Hibernate,
                        "sleep" or "suspender" => AfterAction.Sleep,
                        "nothing" or "nada" or "none" => AfterAction.Nothing,
                        _ => null,
                    };
                    break;
                case "--tray":
                    o.Tray = true;
                    break;
                case "--sim":
                case "--simulation":
                    o.Simulation = true;
                    break;
                case "--no-elevate":
                    o.NoElevate = true;
                    break;
                case "--from-task":
                    o.FromTask = true;
                    break;
#if DEBUG
                case "--snap":
                    o.SnapPath = NextValue();
                    break;
                case "--snap-delay":
                    if (double.TryParse(NextValue(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d))
                        o.SnapDelay = d;
                    break;
                case "--debug-loop":
                    o.DebugLoop = true;
                    break;
                case "--snap-settings":
                    o.SnapSettings = true;
                    break;
                case "--debug-seconds":
                    if (double.TryParse(NextValue(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var ds))
                        o.DebugSeconds = ds;
                    break;
#endif
            }
        }
        return o;
    }

    public List<string> ToArgs()
    {
        var list = new List<string>();
        if (StartNow) list.Add("--start");
        if (Minutes is int m) { list.Add("--minutes"); list.Add(m.ToString()); }
        if (Then is AfterAction t)
        {
            list.Add("--then");
            list.Add(t switch
            {
                AfterAction.Shutdown => "shutdown",
                AfterAction.Hibernate => "hibernate",
                AfterAction.Sleep => "sleep",
                _ => "nothing",
            });
        }
        if (Tray) list.Add("--tray");
        if (Simulation) list.Add("--sim");
        if (NoElevate) list.Add("--no-elevate");
        return list;
    }
}
