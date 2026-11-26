using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace FanMaxxxing.Core;

public sealed record UpdateInfo(Version Version, string Tag, string PageUrl, string? MsiUrl, string? MsiName, long MsiSize);

/// <summary>
/// Busca versiones nuevas en las Releases de GitHub del repositorio.
/// Para publicar una actualización: crear una Release con etiqueta vX.Y.Z y adjuntar los .msi.
/// Si haces un fork, cambia <see cref="Repo"/> por tu "usuario/repositorio".
/// </summary>
public static class UpdateChecker
{
    public const string DefaultRepo = "pabloAlvarezR/fanmaxxxing";

    /// <summary>Repositorio consultado (en Debug se puede cambiar con --update-repo para pruebas).</summary>
    public static string Repo { get; set; } = DefaultRepo;
    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    // Un único HttpClient para toda la vida de la app (evita agotar sockets).
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("FanMaxxxing", CurrentVersion.ToString(3)));
        c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return c;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Devuelve la actualización si hay una versión más nueva; null si no la hay o no se pudo consultar.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync($"https://api.github.com/repos/{Repo}/releases/latest", ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                Log.Info($"Actualizaciones: GitHub respondió {(int)resp.StatusCode}");
                return null;
            }
            await using var s = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(s, cancellationToken: ct).ConfigureAwait(false);
            var root = doc.RootElement;
            if (root.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
            if (root.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

            var tag = root.GetProperty("tag_name").GetString() ?? "";
            if (!TryParseVersion(tag, out var latest)) return null;
            if (latest <= CurrentVersion)
            {
                Log.Info($"Actualizaciones: al día ({CurrentVersion} ≥ {latest})");
                return null;
            }

            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? ReleasesPage : ReleasesPage;
            var (url, name, size) = PickMsi(root);
            Log.Info($"Actualizaciones: nueva versión {latest} ({name ?? "sin MSI"})");
            return new UpdateInfo(latest, tag, page, url, name, size);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            Log.Info("Actualizaciones: no se pudo consultar (" + ex.GetType().Name + ")");
            return null;
        }
    }

    public static bool TryParseVersion(string tag, out Version version)
    {
        var t = tag.Trim().TrimStart('v', 'V');
        int dash = t.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) t = t[..dash];
        if (Version.TryParse(t, out var v))
        {
            version = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            return true;
        }
        version = new Version(0, 0, 0);
        return false;
    }

    /// <summary>Elige el MSI del idioma actual (…-es.msi), si no el inglés, si no el primero.</summary>
    private static (string? Url, string? Name, long Size) PickMsi(JsonElement root)
    {
        if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array) return (null, null, 0);
        var msis = assets.EnumerateArray()
            .Select(a => (Name: a.GetProperty("name").GetString() ?? "",
                          Url: a.GetProperty("browser_download_url").GetString() ?? "",
                          Size: a.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0))
            .Where(a => a.Name.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) && a.Url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (msis.Count == 0) return (null, null, 0);
        var pick = msis.FirstOrDefault(m => m.Name.Contains("-" + Loc.Current + ".", StringComparison.OrdinalIgnoreCase));
        if (pick.Name == null || pick.Name.Length == 0) pick = msis.FirstOrDefault(m => m.Name.Contains("-en.", StringComparison.OrdinalIgnoreCase));
        if (pick.Name == null || pick.Name.Length == 0) pick = msis[0];
        return (pick.Url, pick.Name, pick.Size);
    }

    /// <summary>Descarga el MSI a %TEMP% con progreso (0..1). Devuelve la ruta.</summary>
    public static async Task<string> DownloadAsync(UpdateInfo info, IProgress<double>? progress, CancellationToken ct)
    {
        if (info.MsiUrl == null || info.MsiName == null) throw new InvalidOperationException("La release no tiene instalador .msi");
        var dir = Path.Combine(Path.GetTempPath(), "FanMaxxxing-update");
        Directory.CreateDirectory(dir);
        var safeName = string.Concat(info.MsiName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)));
        var target = Path.Combine(dir, safeName);
        var partial = target + ".part";

        using (var resp = await Http.GetAsync(info.MsiUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false))
        {
            resp.EnsureSuccessStatusCode();
            long total = resp.Content.Headers.ContentLength ?? info.MsiSize;
            await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            await using var dst = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            var buffer = new byte[81920];
            long read = 0;
            int n;
            while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                read += n;
                if (total > 0) progress?.Report(Math.Min(1, (double)read / total));
            }
            if (info.MsiSize > 0 && read != info.MsiSize)
                throw new IOException($"Descarga incompleta ({read} de {info.MsiSize} bytes)");
        }
        File.Move(partial, target, overwrite: true);
        Log.Info($"Actualización descargada: {target}");
        return target;
    }

    public static void LaunchInstaller(string msiPath)
    {
        var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "msiexec.exe"), $"/i \"{msiPath}\"")
        {
            UseShellExecute = true,
        };
        Process.Start(psi)?.Dispose();
    }

    public static void OpenPage(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { Log.Warn("No se pudo abrir el navegador: " + ex.Message); }
    }
}
