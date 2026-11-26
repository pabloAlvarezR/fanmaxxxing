using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FanMaxxxing.Core;

/// <summary>
/// Paquete de idioma: se carga de Strings/&lt;código&gt;.json (recurso incrustado).
/// Para añadir un idioma basta con copiar en.json, traducirlo y registrarlo en <see cref="Loc.Languages"/>.
/// </summary>
public sealed class LangPack
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("strings")] public Dictionary<string, string> Strings { get; set; } = new();
    [JsonPropertyName("memes")] public Dictionary<string, List<string[]>> Memes { get; set; } = new();
    [JsonPropertyName("tags")] public List<string> Tags { get; set; } = new();
    [JsonPropertyName("ranks")] public List<string> Ranks { get; set; } = new();
}

/// <summary>Traducciones. El idioma se fija al arrancar (cambiarlo requiere reiniciar la app).</summary>
public static class Loc
{
    /// <summary>Idiomas disponibles (código, nombre nativo). El primero que coincida con Windows se usa en «automático».</summary>
    public static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),
        ("es", "Español"),
        ("fr", "Français"),
        ("de", "Deutsch"),
        ("it", "Italiano"),
        ("pt", "Português"),
        ("zh", "简体中文"),
    };

    private static LangPack _pack = new();
    private static LangPack _fallback = new();

    public static string Current { get; private set; } = "en";
    public static LangPack Pack => _pack;

    /// <summary>Idioma del sistema si está soportado; si no, inglés.</summary>
    public static string SystemLanguage()
    {
        var two = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.ToLowerInvariant();
        return Languages.Any(l => l.Code == two) ? two : "en";
    }

    public static string Resolve(string? setting) =>
        !string.IsNullOrWhiteSpace(setting) && Languages.Any(l => l.Code == setting) ? setting! : SystemLanguage();

    public static void Init(string? setting)
    {
        Current = Resolve(setting);
        _fallback = LoadPack("en") ?? new LangPack();
        _pack = Current == "en" ? _fallback : LoadPack(Current) ?? _fallback;
        Log.Info($"Idioma: {Current}");
    }

    private static LangPack? LoadPack(string code)
    {
        try
        {
            var asm = Assembly.GetExecutingAssembly();
            using var s = asm.GetManifestResourceStream($"FanMaxxxing.Strings.{code}.json");
            if (s == null) return null;
            return JsonSerializer.Deserialize<LangPack>(s, new JsonSerializerOptions
            {
                ReadCommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (Exception ex)
        {
            Log.Error($"No se pudo cargar el idioma {code}", ex);
            return null;
        }
    }

    /// <summary>Texto traducido (o el inglés, o la propia clave si falta).</summary>
    public static string T(string key)
    {
        if (_pack.Strings.TryGetValue(key, out var v)) return v;
        if (_fallback.Strings.TryGetValue(key, out var f)) return f;
        return key;
    }

    /// <summary>Texto traducido con parámetros {0}, {1}…</summary>
    public static string F(string key, params object?[] args)
    {
        try { return string.Format(CultureInfo.CurrentCulture, T(key), args); }
        catch (FormatException) { return T(key); }
    }

    public static List<string[]> MemePool(string mood)
    {
        if (_pack.Memes.TryGetValue(mood, out var list) && list.Count > 0) return list;
        if (_fallback.Memes.TryGetValue(mood, out var fb) && fb.Count > 0) return fb;
        return new List<string[]> { new[] { "FANMAXXXING", "" } };
    }

    public static List<string> Tags => _pack.Tags.Count > 0 ? _pack.Tags : _fallback.Tags;
    public static List<string> Ranks => _pack.Ranks.Count >= 7 ? _pack.Ranks : _fallback.Ranks;
}
