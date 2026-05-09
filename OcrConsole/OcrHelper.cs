using System.Configuration;
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

internal static class SettingHelper
{

    public static string? GetSettingOrEnv(string appSettingKey, params string[] envKeys)
    {
        foreach (var envKey in envKeys)
        {
            var envValue = Environment.GetEnvironmentVariable(envKey);
            if (!string.IsNullOrWhiteSpace(envValue)) return envValue;
        }

        var settingValue = ConfigurationManager.AppSettings[appSettingKey];
        return string.IsNullOrWhiteSpace(settingValue) ? null : settingValue;
    }

    public static int ParseInt(string? text, int defaultValue)
    {
        if (int.TryParse(text, out var parsed)) return parsed;
        return defaultValue;
    }

}
