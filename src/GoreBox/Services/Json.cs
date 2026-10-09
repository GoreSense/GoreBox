using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GoreBox.Services;

/// <summary>Чтение/запись json-файлов с атомарной заменой и бэкапом.</summary>
public static class Json
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver(),
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            var text = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(text)) return null;
            return JsonSerializer.Deserialize<T>(text, Options);
        }
        catch
        {
            try
            {
                var bak = path + ".bak";
                if (File.Exists(bak))
                    return JsonSerializer.Deserialize<T>(File.ReadAllText(bak), Options);
            }
            catch { /* ignore */ }
            return null;
        }
    }

    public static void Write<T>(string path, T value)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var tmp = path + ".tmp";
        var json = JsonSerializer.Serialize(value, Options);
        File.WriteAllText(tmp, json);

        if (File.Exists(path))
        {
            try { File.Copy(path, path + ".bak", true); } catch { /* ignore */ }
        }
        File.Move(tmp, path, true);
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}
