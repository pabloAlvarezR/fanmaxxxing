namespace FanMaxxxing.Core;

public enum MemeMood { IdleHot, IdleCool, Cooling, Done, Night, Error }

public readonly record struct Meme(string Top, string Bottom);

/// <summary>
/// Contenido humorístico (memes de 2025-2026). Formato clásico: texto arriba / remate abajo.
/// Referencias: FAAAH, 6 7, cooked / let him cook, aura, -maxxing, frame-mogging, la peace,
/// say wallahi bro, Mbappé special, "why would I deceive you?", "he is 20 years old",
/// IM GOOD BRO, canon event, ROW!, kinda chic, locked in, Tung Tung Sahur, brainrot soviético.
/// </summary>
public static class Memes
{
    private static readonly Random Rng = new();

    private static readonly Meme[] IdleHot =
    {
        new("CUANDO MIRAS LA TEMPERATURA DE LA GPU", "FAAAAAAH"),
        new("TU GRÁFICA DESPUÉS DE 6 HORAS DE VICIO", "COOKED. MUY COOKED."),
        new("¿APAGAR EL PC A 80 GRADOS?", "SAY WALLAHI BRO"),
        new("CALOR RESIDUAL EN LA PLACA BASE", "CANON EVENT DE TODO GAMER"),
        new("APAGAR EL PC EN CALIENTE", "-1000 AURA"),
        new("YO: ME VOY A DORMIR CON LA TORRE ASÍ", "LA TORRE: IM GOOD BRO"),
        new("«SE ENFRÍA SOLO, TRANQUI»", "¿POR QUÉ IBA A ENGAÑARTE?"),
        new("LA CPU MARCANDO SESENTA Y...", "6 7"),
        new("TU CPU AHORA MISMO", "A UN GRADO DEL CRASH OUT"),
    };

    private static readonly Meme[] IdleCool =
    {
        new("TEMPERATURAS PERFECTAS", "LA PEACE"),
        new("PC FRESQUITO SIN HACER NADA", "KINDA CHIC"),
        new("¿QUÉ HA HECHO HOY EL VENTILADOR?", "EL ESPECIAL DE MBAPPÉ: NADA"),
        new("ESPERANDO ÓRDENES", "MODO NPC ACTIVADO"),
        new("TODO A 40 GRADOS", "DU BIST GUT GENUG"),
    };

    private static readonly Meme[] Cooling =
    {
        new("VENTILADORES AL 100%", "LOCKED IN"),
        new("EL CALOR RESIDUAL VIENDO ESTO", "FAAAAAAAH"),
        new("NO ES LOOKSMAXXING", "ES FANMAXXING"),
        new("TUS VENTILADORES AHORA MISMO", "ROW! ROW! ROW!"),
        new("CADA GRADO QUE BAJA", "+100 AURA"),
        new("TUNG TUNG TUNG TUNG", "TURBINA SAHUR"),
        new("DÉJALE QUE ENFRÍE", "LET HIM COOL"),
        new("EL CAMARADA BRDYSHCH", "HA PROHIBIDO EL CALOR RESIDUAL"),
        new("SOMEBODY GET THESE GRADOS", "OUT OF HERE"),
        new("MAMÁ: ¿QUÉ ES ESE RUIDO?", "YO: MI ERA FANMAXXING"),
        new("EL VECINO OYENDO LA TURBINA", "SAY WALLAHI QUE ES UN PC"),
        new("LA GPU ANTES: COOKED", "LA GPU AHORA: LA PEACE"),
        new("ESTOS VENTILADORES", "FRAME-MOGGEAN A UN AIRE ACONDICIONADO"),
    };

    private static readonly Meme[] Done =
    {
        new("TEMPERATURAS NORMALIZADAS", "+1000 AURA"),
        new("ESTE PC BAJÓ 30 GRADOS EN 5 MINUTOS", "Y TIENE 20 AÑOS"),
        new("EL CALOR RESIDUAL", "HA SIDO ELIMINADO. LA PEACE."),
        new("TU PC AL LADO DEL DE TU COLEGA", "FRAME-MOGGING TÉRMICO"),
        new("GG", "TU PLACA BASE TE DEBE UNA"),
    };

    private static readonly Meme[] Night =
    {
        new("BUENAS NOCHES, GAMER", "SLEEPMAXXING ACTIVADO"),
        new("A DORMIR", "MAÑANA, LOCK IN OTRA VEZ"),
        new("APAGANDO", "LA PEACE"),
    };

    private static readonly Meme[] Error =
    {
        new("ALGO HA FALLADO", "FAAAAAAAH"),
        new("ERROR", "SKILL ISSUE (MÍO, NO TUYO)"),
    };

    /// <summary>Etiquetas flotantes estilo texto de TikTok durante el enfriamiento.</summary>
    private static readonly string[] Tags =
    {
        "FAAAH", "+1000 aura", "locked in", "6 7", "la peace", "W", "let him cool", "say wallahi",
        "ROW!", "fanmaxxing", "frame-mog", "tung tung sahur", "kinda chic", "-15 °C", "not cooked",
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

    public static string NextTag() => Tags[Rng.Next(Tags.Length)];

    public static (string Rank, string Next) RankFor(int cooldowns)
    {
        (int min, string name)[] ranks =
        {
            (0, "Cooked"), (1, "Medio cooked"), (3, "NPC del aire"), (7, "Locked in"),
            (15, "Aura farmer"), (30, "Frame-mogger térmico"), (60, "La peace absoluta"),
        };
        int idx = 0;
        for (int i = 0; i < ranks.Length; i++) if (cooldowns >= ranks[i].min) idx = i;
        int left = idx + 1 < ranks.Length ? ranks[idx + 1].min - cooldowns : 0;
        string next = idx + 1 < ranks.Length
            ? $"{left} {(left == 1 ? "enfriamiento" : "enfriamientos")} para «{ranks[idx + 1].name}»"
            : "Rango máximo. Aura infinita.";
        return (ranks[idx].name, next);
    }

    public static (string Title, string Subtitle) Achievement(CooldownResult r)
    {
        if (r.BestDrop is not double drop)
            return ("Enfriamiento a ciegas", "Sin sensores, pero say wallahi que ha enfriado");
        return drop switch
        {
            >= 30 => ("Aura infinita", $"-{drop:0} °C. Frame-mogging térmico nivel dios"),
            >= 20 => ("+1000 aura", $"-{drop:0} °C de calor residual eliminado"),
            >= 10 => ("Locked in", $"-{drop:0} °C. Tu placa base dice gracias"),
            >= 3 => ("Kinda chic", $"-{drop:0} °C. Ya estaba fresquito, pero bien"),
            _ => ("El especial de Mbappé", "Calor eliminado: nada. Ya estaba frío"),
        };
    }
}
