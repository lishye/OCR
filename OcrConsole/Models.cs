using System.Configuration;
using System.Text.Json;

namespace OcrConsole;

internal sealed record OcrReadResult(
    string Text,
    ExtractedFields? MappedFields,
    IReadOnlyDictionary<string, string>? AliRawStructuredFields,
    string? AliRawStructuredJson);

internal sealed record BarcodeResult(string Format, string Value, string SourceVariant);

internal sealed record ExtractedFields(
    string? PartNumber,
    string? Description,
    string? Quantity,
    string? DateCode,
    string? LotNo,
    string? Supplier,
    string? Brand,
    string? MPN,
    string? PO,
    string? HuId);

internal sealed record ImageRecognitionResult(
    string FileName,
    string FilePath,
    string RelativePath,
    string OutputJsonPath,
    string OcrProvider,
    string? AiProvider,
    IReadOnlyList<BarcodeResult> Barcodes,
    string Text,
    ExtractedFields Fields,
    IReadOnlyDictionary<string, string>? AliRawStructuredFields,
    string? AliRawStructuredJson,
    IReadOnlyList<string> CorrectionNotes,
    IReadOnlyList<string> AppliedPreprocessing,
    string? Error);

internal sealed record ImageVariant(string Name, System.Drawing.Bitmap Bitmap);

internal sealed record ProcessedImage(IReadOnlyList<ImageVariant> Variants, IReadOnlyList<string> AppliedSteps) : IDisposable
{
    public void Dispose()
    {
        foreach (var v in Variants) v.Bitmap.Dispose();
    }
}

internal sealed record FieldRule(
    string Name,
    IReadOnlyList<string> AliKeys,
    string TextRegex,
    string BarcodeRegex,
    string Template,
    string DateFormat);

internal sealed record AppOptions(
    string InputDirectory,
    string OutputDirectory,
    OcrProvider Provider,
    string LanguageTag,
    string? AccessKeyId,
    string? AccessKeySecret,
    string AliEndpoint,
    string EasyOcrEndpoint,
    string PaddleEndpoint,
    int LocalOcrTimeoutSeconds,
    string LocalDbConnectionString,
    string TemplateName,
    IReadOnlyList<FieldRule> FieldRules)
{
    public static AppOptions FromConfigAndArgs(CliArgs args)
    {
        var input = args.Get("input") ?? args.Positionals.ElementAtOrDefault(0) ?? ConfigurationManager.AppSettings["InputDirectory"] ?? string.Empty;
        var output = args.Get("output") ?? args.Positionals.ElementAtOrDefault(1) ?? ConfigurationManager.AppSettings["OutputDirectory"] ?? string.Empty;
        var language = args.Get("lang") ?? args.Positionals.ElementAtOrDefault(2) ?? ConfigurationManager.AppSettings["OcrLanguage"] ?? "zh-Hans";

        var providerText = args.Get("ocr-provider") ?? ConfigurationManager.AppSettings["OcrProvider"];
        var provider = ParseProvider(providerText);

        var localDbConnectionString = ConfigurationManager.AppSettings["LocalDbConnectionString"]
            ?? "Data Source=(localdb)\\MSSQLLocalDB;Initial Catalog=OcrLocalDb;Integrated Security=True;TrustServerCertificate=True;";

        var templateName = args.Get("template") ?? ConfigurationManager.AppSettings["OcrTemplateName"] ?? provider.ToString();
        var localCompatEndpoint = args.Get("local-ocr-endpoint") ?? ConfigurationManager.AppSettings["LocalOcrEndpoint"];
        var easyOcrEndpoint = args.Get("easy-ocr-endpoint") ?? ConfigurationManager.AppSettings["EasyOcrEndpoint"] ?? localCompatEndpoint ?? "http://127.0.0.1:8000";
        var paddleEndpoint = args.Get("paddle-endpoint") ?? ConfigurationManager.AppSettings["PaddleEndpoint"] ?? localCompatEndpoint ?? "http://127.0.0.1:8001";

        var configuredRulesJson = ConfigurationManager.AppSettings["FieldRulesJson"];
        var configuredRules = ParseFieldRules(configuredRulesJson);

        return new AppOptions(
            InputDirectory: input,
            OutputDirectory: output,
            Provider: provider,
            LanguageTag: language,
            AccessKeyId: args.Get("ak") ?? ConfigurationManager.AppSettings["AccessKeyID"],
            AccessKeySecret: args.Get("sk") ?? ConfigurationManager.AppSettings["AccessKeySecret"],
            AliEndpoint: args.Get("endpoint") ?? ConfigurationManager.AppSettings["AliOcrEndpoint"] ?? "ocr-api.cn-hangzhou.aliyuncs.com",
            EasyOcrEndpoint: easyOcrEndpoint,
            PaddleEndpoint: paddleEndpoint,
            LocalOcrTimeoutSeconds: ParseInt(args.Get("local-ocr-timeout") ?? ConfigurationManager.AppSettings["LocalOcrTimeoutSeconds"], 300),
            LocalDbConnectionString: localDbConnectionString,
            TemplateName: templateName,
            FieldRules: configuredRules.Count == 0 ? TemplateFactory.Aliyun() : configuredRules);
    }

    private static int ParseInt(string? text, int defaultValue)
    {
        if (int.TryParse(text, out var parsed)) return parsed;
        return defaultValue;
    }

    private static OcrProvider ParseProvider(string? providerText)
    {
        if (string.IsNullOrWhiteSpace(providerText)) return OcrProvider.Aliyun;

        // Backward-compatible aliases for historical config values.
        if (string.Equals(providerText, "LocalPaddle", StringComparison.OrdinalIgnoreCase)) return OcrProvider.Paddle;
        if (string.Equals(providerText, "EasyOCR", StringComparison.OrdinalIgnoreCase)) return OcrProvider.EasyOcr;

        return Enum.TryParse<OcrProvider>(providerText, true, out var parsedProvider)
            ? parsedProvider
            : OcrProvider.Aliyun;
    }

    private static IReadOnlyList<FieldRule> ParseFieldRules(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            var rules = JsonSerializer.Deserialize<List<FieldRule>>(json);
            return rules?.Where(r => !string.IsNullOrWhiteSpace(r.Name)).ToList() ?? [];
        }
        catch
        {
            return [];
        }
    }
}

internal sealed class CliArgs
{
    public List<string> Positionals { get; } = [];
    public bool RunHeadless { get; private set; }

    private readonly Dictionary<string, string> _named = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string key) => _named.TryGetValue(key, out var value) ? value : null;

    public static CliArgs Parse(string[] args)
    {
        var result = new CliArgs();
        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];

            if (string.Equals(arg, "--run", StringComparison.OrdinalIgnoreCase))
            {
                result.RunHeadless = true;
                continue;
            }

            if (arg.StartsWith("--", StringComparison.Ordinal))
            {
                var token = arg[2..];
                var split = token.Split('=', 2);
                var key = split[0];
                var value = split.Length == 2
                    ? split[1]
                    : (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "true");
                result._named[key] = value;
                continue;
            }

            result.Positionals.Add(arg);
        }

        return result;
    }
}

internal enum OcrProvider
{
    Aliyun,
    Windows,
    Paddle,
    EasyOcr
}
