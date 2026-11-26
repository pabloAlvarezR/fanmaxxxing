namespace FanMaxxxing.Core;

public enum MemeMood { IdleHot, IdleCool, Cooling, Done, Night, Error }

public readonly record struct Meme(string Top, string Bottom);

/// <summary>
/// Memes, etiquetas, rangos y logros. El contenido sale del paquete de idioma (Strings/*.json),
/// así cada idioma tiene sus propios memes y no traducciones literales.
/// </summary>
public static class Memes
{
    private static readonly Random Rng = new();
    private static int _lastIndex = -1;

    public static Meme Next(MemeMood mood)
    {
        var pool = Loc.MemePool(mood.ToString());
        int i;
        do { i = Rng.Next(pool.Count); } while (pool.Count > 1 && i == _lastIndex);
        _lastIndex = i;
        var m = pool[i];
        return new Meme(m.Length > 0 ? m[0] : "", m.Length > 1 ? m[1] : "");
    }

    public static string NextTag()
    {
        var tags = Loc.Tags;
        return tags.Count == 0 ? "FAAAH" : tags[Rng.Next(tags.Count)];
    }

    private static readonly int[] RankThresholds = { 0, 1, 3, 7, 15, 30, 60 };

    public static (string Rank, string Next) RankFor(int cooldowns)
    {
        var names = Loc.Ranks;
        int idx = 0;
        for (int i = 0; i < RankThresholds.Length; i++) if (cooldowns >= RankThresholds[i]) idx = i;
        if (idx + 1 >= RankThresholds.Length) return (names[idx], Loc.T("rank.max"));
        int left = RankThresholds[idx + 1] - cooldowns;
        return (names[idx], Loc.F(left == 1 ? "rank.next.one" : "rank.next.many", left, names[idx + 1]));
    }

    public static (string Title, string Subtitle) Achievement(CooldownResult r)
    {
        if (r.BestDrop is not double drop)
            return (Loc.T("ach.blind.title"), Loc.T("ach.blind.sub"));
        string k = drop switch
        {
            >= 30 => "ach.30",
            >= 20 => "ach.20",
            >= 10 => "ach.10",
            >= 3 => "ach.3",
            _ => "ach.0",
        };
        return (Loc.T(k + ".title"), Loc.F(k + ".sub", drop.ToString("0")));
    }
}
