using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace OcrConsole;

internal static class JsonDisplayHelper
{
    private static readonly JsonSerializerOptions PrettyJsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string FormatForDisplay(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(PrettyJsonOptions) ?? json;
        }
        catch
        {
            return json;
        }
    }

    public static string NormalizeJsonString(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return string.Empty;

        try
        {
            var node = JsonNode.Parse(json);
            return node?.ToJsonString(PrettyJsonOptions) ?? json;
        }
        catch
        {
            return json;
        }
    }
}
