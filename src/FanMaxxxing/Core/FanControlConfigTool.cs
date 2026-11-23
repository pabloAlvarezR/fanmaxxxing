using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace FanMaxxxing.Core;

public sealed record FcControlInfo(string Id, string Name, bool? Enabled, bool ManualNow, double? ManualValue)
{
    public string Kind
    {
        get
        {
            var id = Id.ToLowerInvariant();
            if (id.Contains("gpu") || id.Contains("nvidia")) return "GPU";
            if (id.Contains("lpc") || id.Contains("superio") || id.Contains("motherboard")) return "Placa";
            if (id.Contains("plugin")) return "Plugin";
            return "Otro";
        }
    }
}

/// <summary>
/// Lee configuraciones de FanControl y genera una copia con todos los ventiladores en manual al 100 %.
/// No dependemos de la ruta exacta dentro del JSON: buscamos cualquier objeto que tenga
/// "ManualControl" y "ManualControlValue", así sobrevive a cambios de formato entre versiones.
/// </summary>
public static class FanControlConfigTool
{
    public const string MaxFileName = "FanMaxxxing_MAX.json";

    private static readonly JsonDocumentOptions DocOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 256,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static List<FcControlInfo> ListControls(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: DocOptions)
                   ?? throw new InvalidDataException("El archivo está vacío.");
        var list = new List<FcControlInfo>();
        int i = 0;
        foreach (var obj in FindControlObjects(root))
        {
            list.Add(new FcControlInfo(
                GetId(obj, i),
                GetName(obj, i),
                TryBool(obj, "Enable"),
                TryBool(obj, "ManualControl") ?? false,
                TryDouble(obj, "ManualControlValue")));
            i++;
        }
        return list;
    }

    public static List<FcControlInfo> ListControlsFromFile(string path) => ListControls(File.ReadAllText(path));

    /// <summary>Devuelve el JSON de la config MAX. <paramref name="boosted"/> = controles puestos al 100 %.</summary>
    public static string BuildMaxConfig(string json, ICollection<string> excludedIds, out int boosted, out int total)
    {
        var root = JsonNode.Parse(json, documentOptions: DocOptions)
                   ?? throw new InvalidDataException("El archivo está vacío.");
        var controls = FindControlObjects(root).ToList();
        total = controls.Count;
        boosted = 0;
        for (int i = 0; i < controls.Count; i++)
        {
            var obj = controls[i];
            if (excludedIds.Contains(GetId(obj, i))) continue;

            obj["ManualControl"] = true;
            obj["ManualControlValue"] = 100;
            if (obj.ContainsKey("Enable")) obj["Enable"] = true;
            boosted++;
        }
        return root.ToJsonString(WriteOptions);
    }

    private static IEnumerable<JsonObject> FindControlObjects(JsonNode root)
    {
        // DFS iterativo (sin recursión para no reventar la pila con JSON raros)
        var stack = new Stack<JsonNode>();
        var found = new List<JsonObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case JsonObject obj:
                    if (obj.ContainsKey("ManualControl") && obj.ContainsKey("ManualControlValue"))
                    {
                        found.Add(obj);
                        continue; // un control no contiene otros controles
                    }
                    // en orden inverso para que el resultado salga en el orden del archivo
                    var children = obj.Select(kv => kv.Value).Where(v => v != null).ToList();
                    for (int k = children.Count - 1; k >= 0; k--) stack.Push(children[k]!);
                    break;
                case JsonArray arr:
                    for (int k = arr.Count - 1; k >= 0; k--)
                        if (arr[k] != null) stack.Push(arr[k]!);
                    break;
            }
        }
        return found;
    }

    private static string GetId(JsonObject obj, int index)
    {
        var id = TryString(obj, "Identifier") ?? TryString(obj, "Id");
        if (!string.IsNullOrWhiteSpace(id)) return id!;
        var name = TryString(obj, "Name");
        return !string.IsNullOrWhiteSpace(name) ? "name:" + name : "#" + index;
    }

    private static string GetName(JsonObject obj, int index)
    {
        var nick = TryString(obj, "NickName");
        if (!string.IsNullOrWhiteSpace(nick)) return nick!;
        var name = TryString(obj, "Name");
        if (!string.IsNullOrWhiteSpace(name)) return name!;
        return "Control " + (index + 1);
    }

    private static string? TryString(JsonObject obj, string key)
    {
        try
        {
            return obj.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        }
        catch { return null; }
    }

    private static bool? TryBool(JsonObject obj, string key)
    {
        try
        {
            return obj.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<bool>(out var b) ? b : null;
        }
        catch { return null; }
    }

    private static double? TryDouble(JsonObject obj, string key)
    {
        try
        {
            return obj.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
        }
        catch { return null; }
    }
}
