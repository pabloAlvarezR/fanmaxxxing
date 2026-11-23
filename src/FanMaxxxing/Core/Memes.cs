namespace FanMaxxxing.Core;

public enum MemeMood { IdleHot, IdleCool, Cooling, Done, Night, Error }

public readonly record struct Meme(string Top, string Bottom);

/// <summary>Contenido humorístico. Formato clásico: texto arriba / texto abajo.</summary>
public static class Memes
{
    private static readonly Random Rng = new();

    private static readonly Meme[] IdleHot =
    {
        new("CUANDO CIERRAS EL JUEGO", "PERO LA GPU SIGUE A 80 GRADOS"),
        new("YO: ME VOY A DORMIR", "MI PC: ¿Y EL CALOR RESIDUAL QUÉ, LISTO?"),
        new("ESTO ESTÁ BIEN", "(TODO ESTÁ ARDIENDO)"),
        new("NADIE:", "MI CPU A LAS 3 AM: MODO SAUNA"),
        new("SESIÓN DE 6 HORAS", "PLACA BASE A LA PARRILLA"),
        new("TU TORRE AHORA MISMO", "UNA FREIDORA DE AIRE CON RGB"),
        new("APAGAR EL PC EN CALIENTE", "NI DE BROMA, BRO"),
        new("SE PUEDEN HACER TOSTADAS", "ENCIMA DE TU GRÁFICA"),
        new("EL CALOR RESIDUAL", "ESPERANDO A QUE APAGUES: :)"),
    };

    private static readonly Meme[] IdleCool =
    {
        new("TODO FRESQUITO", "PERO NUNCA SE SABE"),
        new("PREPARADO PARA MAXXEAR", "CUANDO TÚ DIGAS, JEFE"),
        new("TEMPERATURAS DE PINGÜINO", "RESPETO MÁXIMO"),
        new("ESTOY TAN TRANQUILO", "QUE PODRÍA SER UN SCREENSAVER"),
        new("ESPERANDO ÓRDENES", "MIENTRAS GIRO SUAVECITO"),
    };

    private static readonly Meme[] Cooling =
    {
        new("VENTILADORES AL 100%", "EL VECINO CREE QUE ESTÁS DESPEGANDO"),
        new("TORRE DE CONTROL", "DESPEGUE AUTORIZADO"),
        new("TU GPU AHORA MISMO", "RESPIRANDO POR PRIMERA VEZ EN 6 HORAS"),
        new("LA NASA HA LLAMADO", "QUIERE SU TÚNEL DE VIENTO DE VUELTA"),
        new("TEMPERATURAS BAJANDO", "STONKS PERO AL REVÉS"),
        new("ESTO NO ES UN PC", "ES UNA TURBINA CON RGB"),
        new("AIRE ACONDICIONADO", "EDICIÓN GAMER LOW COST"),
        new("SE VIENE", "EL INVIERNO (DENTRO DE TU TORRE)"),
        new("MAMÁ: ¿QUÉ ES ESE RUIDO?", "YO: CIENCIA, MAMÁ. CIENCIA."),
        new("POV:", "ERES UN VENTILADOR A 3000 RPM A LAS 3 AM"),
        new("EL CALOR RESIDUAL", "HA ABANDONADO EL CHAT"),
        new("BRRRRRRRRRRRRR", "BRRRRRRRRRRRRRRRRRRRR"),
        new("NO ES RUIDO", "ES EL SONIDO DE LA VICTORIA"),
        new("MODO HURACÁN", "NIVEL: SECADOR DE PELO DE DIOS"),
        new("PELUSAS DEL DISIPADOR", "VOLANDO HACIA LA LIBERTAD"),
    };

    private static readonly Meme[] Done =
    {
        new("MISIÓN CUMPLIDA", "PC MÁS FRESCO QUE TU EX"),
        new("CALOR RESIDUAL", "ELIMINADO"),
        new("YA PUEDES APAGAR", "SIN COCINAR LA PLACA"),
        new("GG WP", "TEMPERATURAS NORMALIZADAS"),
        new("TU PC TE DA LAS GRACIAS", "(SI PUDIERA HABLAR)"),
    };

    private static readonly Meme[] Night =
    {
        new("BUENAS NOCHES, GAMER", "MAÑANA MÁS (O ESTA NOCHE, NO JUZGO)"),
        new("APAGANDO", "QUE SUEÑES CON 240 FPS"),
        new("A DORMIR", "LOS DOS, TÚ Y LA GRÁFICA"),
    };

    private static readonly Meme[] Error =
    {
        new("ALGO HA FALLADO", "NO ES UN BUG, ES UNA FEATURE"),
        new("ERROR 404", "FRÍO NO ENCONTRADO"),
    };

    private static readonly string[] DogeWords =
    {
        "wow", "much frío", "such rpm", "very turbina", "so fresquito", "amaze",
        "much aire", "wow 0 calor", "such viento", "very pingüino", "much brrr", "so huracán",
    };

    private static int _lastIndex = -1;

    public static Meme Next(MemeMood mood)
    {
        var pool = mood switch
        {
            MemeMood.IdleHot => IdleHot,
            MemeMood.IdleCool => IdleCool,
            MemeMood.Cooling => Cooling,
            MemeMood.Done => Done,
            MemeMood.Night => Night,
            _ => Error,
        };
        int i;
        do { i = Rng.Next(pool.Length); } while (pool.Length > 1 && i == _lastIndex);
        _lastIndex = i;
        return pool[i];
    }

    public static string NextDoge() => DogeWords[Rng.Next(DogeWords.Length)];

    public static (string Rank, string Next) RankFor(int cooldowns)
    {
        (int min, string name)[] ranks =
        {
            (0, "Tostadora"), (1, "Microondas"), (3, "Ventilador de pie"), (7, "Aire acondicionado"),
            (15, "Nevera industrial"), (30, "Pingüino emperador"), (60, "Cero absoluto"),
        };
        int idx = 0;
        for (int i = 0; i < ranks.Length; i++) if (cooldowns >= ranks[i].min) idx = i;
        int left = idx + 1 < ranks.Length ? ranks[idx + 1].min - cooldowns : 0;
        string next = idx + 1 < ranks.Length
            ? $"{left} {(left == 1 ? "enfriamiento" : "enfriamientos")} para «{ranks[idx + 1].name}»"
            : "Rango máximo. Eres leyenda.";
        return (ranks[idx].name, next);
    }

    public static (string Title, string Subtitle) Achievement(CooldownResult r)
    {
        if (r.BestDrop is not double drop)
            return ("Enfriamiento a ciegas", "Sin sensores, pero con mucha fe");
        return drop switch
        {
            >= 30 => ("Glaciar supremo", $"-{drop:0} °C. Eso no es enfriar, es criogenizar"),
            >= 20 => ("Pingüino de élite", $"-{drop:0} °C de calor residual eliminado"),
            >= 10 => ("Brisa fresquita", $"-{drop:0} °C. Tu placa base te quiere"),
            >= 3 => ("Algo es algo", $"-{drop:0} °C. Ya estaba fresquito"),
            _ => ("Ya estaba frío, máquina", "No había calor que quitar, pero bien visto"),
        };
    }
}
