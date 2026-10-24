using System.Text.Encodings.Web;
using System.Text.Json;

namespace Rag.Evaluation.Human;

internal static class PilotJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping   // pilot files are read and edited by people
    };

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options) ?? throw new InvalidDataException($"{path} is empty.");
}
