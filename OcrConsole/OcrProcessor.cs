using System.Diagnostics;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Configuration;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using AlibabaCloud.OpenApiClient.Models;
using AlibabaCloud.SDK.Ocr_api20210707;
using AlibabaCloud.SDK.Ocr_api20210707.Models;
using AlibabaCloud.TeaUtil.Models;
using OpenCvSharp;
using OpenCvSharp.Extensions;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using ZXing;

namespace OcrConsole;

internal sealed class OcrProcessor
{
    private readonly LocalDbStore _store;
    private readonly IAiFieldResolver _aiResolver;
    private readonly string? _aiProvider;
    private readonly bool _aiDebugLogEnabled;
    private static readonly JsonSerializerOptions RawAliJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    public OcrProcessor(LocalDbStore store, IAiFieldResolver? aiResolver = null, string? aiProvider = null)
    {
        _store = store;
        _aiResolver = aiResolver ?? AiResolverFactory.CreateFromConfig();
        _aiProvider = string.IsNullOrWhiteSpace(aiProvider) ? null : aiProvider;
        _aiDebugLogEnabled = ParseBool(ConfigurationManager.AppSettings["AiDebugLogEnabled"], Debugger.IsAttached);
    }

    public async Task ProcessAsync(AppOptions options, IProgress<string>? progress = null, ProcessingControl? control = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(options.InputDirectory) || string.IsNullOrWhiteSpace(options.OutputDirectory))
            throw new InvalidOperationException("输入目录和输出目录不能为空。");

        var inputDirectory = Path.GetFullPath(options.InputDirectory);
        var outputDirectory = Path.GetFullPath(options.OutputDirectory);

        if (!Directory.Exists(inputDirectory))
            throw new DirectoryNotFoundException($"输入目录不存在: {inputDirectory}");

        Directory.CreateDirectory(outputDirectory);

        Client? aliClient = null;
        if (options.Provider == OcrProvider.Aliyun)
        {
            if (string.IsNullOrWhiteSpace(options.AccessKeyId) || string.IsNullOrWhiteSpace(options.AccessKeySecret))
                throw new InvalidOperationException("使用 Aliyun OCR 时必须在 App.config 或命令行提供 AccessKeyID/AccessKeySecret。");

            aliClient = CreateAliOcrClient(options.AccessKeyId!, options.AccessKeySecret!, options.AliEndpoint);
        }

        var imageExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp"
        };

        var imageFiles = Directory
            .EnumerateFiles(inputDirectory, "*.*", SearchOption.AllDirectories)
            .Where(path => imageExtensions.Contains(Path.GetExtension(path)))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (imageFiles.Count == 0)
        {
            progress?.Report("未找到可识别的图片文件。");
            return;
        }

        var providerTemplateName = options.Provider.ToString();
        var providerRules = _store.GetTemplateRules(providerTemplateName);
        var activeRules = providerRules.Count > 0 ? providerRules : options.FieldRules;
        var activeTemplateName = providerRules.Count > 0 ? providerTemplateName : "默认";

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        foreach (var imageFile in imageFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (control is not null)
            {
                await control.WaitIfPausedAsync(cancellationToken);
            }

            progress?.Report($"正在识别: {imageFile}");
            var relativePath = Path.GetRelativePath(inputDirectory, imageFile);
            var outputFile = Path.Combine(outputDirectory, Path.ChangeExtension(relativePath, ".json"));
            Directory.CreateDirectory(Path.GetDirectoryName(outputFile)!);

            var sw = Stopwatch.StartNew();
            try
            {
                var isDetailedLog = options.LogVerbosity == LogVerbosity.Detailed;
                if (control is not null)
                {
                    await control.WaitIfPausedAsync(cancellationToken);
                }
                using var processedImage = PreprocessImage(imageFile);
                progress?.Report($"  预处理: {sw.ElapsedMilliseconds}ms");
                sw.Restart();

                if (control is not null)
                {
                    await control.WaitIfPausedAsync(cancellationToken);
                }
                var barcodeInputVariants = processedImage.Variants
                    .Where(v => !v.Name.StartsWith("ocr-", StringComparison.OrdinalIgnoreCase))
                    .Select(v => v.Name)
                    .ToArray();
                progress?.Report($"  条码识别-输入: Provider={options.BarcodeProvider}, 变体=[{string.Join(", ", barcodeInputVariants)}]");
                var barcodes = ReadBarcodes(processedImage, options.BarcodeProvider);
                progress?.Report($"  条码识别: {sw.ElapsedMilliseconds}ms");
                progress?.Report($"  条码识别-输出: {FormatBarcodeResults(barcodes, isDetailedLog)}");
                //progress?.Report(string.Empty);
                sw.Restart();

                if (control is not null)
                {
                    await control.WaitIfPausedAsync(cancellationToken);
                }
                progress?.Report($"  OCR识别-输入: Provider={options.Provider}, Image={Path.GetFileName(imageFile)}, Lang={options.LanguageTag}, Barcodes={barcodes.Count}");
                var ocr = await ReadOcrAsync(processedImage, imageFile, options, aliClient);
                progress?.Report($"  OCR识别: {sw.ElapsedMilliseconds}ms");
                progress?.Report($"  OCR识别-输出: {FormatOcrOutput(ocr, isDetailedLog)}");
                //progress?.Report(string.Empty);

                var values = ResolveValues(ocr.AliRawStructuredFields, ocr.Text, barcodes, activeRules);
                var extracted = BuildFields(values);
                //progress?.Report($"  模板: {activeTemplateName}");

                var merged = MergeFields(ocr.MappedFields, extracted);
                var corrected = ApplyBarcodeCorrections(merged, barcodes, out var notes);
                var mergedNotes = notes.ToList();
                var shouldUseAiProvider = ShouldUseAiProvider(corrected);
                if (shouldUseAiProvider)
                {
                    progress?.Report($"  AI补全-输入: Provider={_aiProvider ?? "none"}, Current={FormatFieldsForLog(corrected, isDetailedLog)}, OCR={Preview(ocr.Text, isDetailedLog ? null : 180)}, Barcodes={FormatBarcodeValues(barcodes, isDetailedLog)}");
                    sw.Restart();
                }
                else
                {
                    progress?.Report($"  AI补全-输入: 已跳过，Current={FormatFieldsForLog(corrected, isDetailedLog)}");
                }

                var aiApply = await ApplyAiProviderAsync(ocr, barcodes, corrected, mergedNotes, progress, isDetailedLog, cancellationToken);
                var finalized = aiApply.Fields;
                if (shouldUseAiProvider)
                {
                    progress?.Report($"  AI补全: {sw.ElapsedMilliseconds}ms");
                }
                progress?.Report($"  AI补全-输出: {aiApply.Summary}");
                //progress?.Report(string.Empty);

                var result = new ImageRecognitionResult(
                    FileName: Path.GetFileName(imageFile),
                    FilePath: imageFile,
                    RelativePath: relativePath,
                    OutputJsonPath: outputFile,
                    OcrProvider: options.Provider.ToString(),
                    AiProvider: _aiProvider,
                    Barcodes: barcodes,
                    Text: ocr.Text,
                    Fields: finalized,
                    AliRawStructuredFields: ocr.AliRawStructuredFields,
                    AliRawStructuredJson: ocr.AliRawStructuredJson,
                    CorrectionNotes: mergedNotes,
                    AppliedPreprocessing: processedImage.AppliedSteps,
                    AiDebug: aiApply.Debug,
                    Error: null);

                var resultJson = JsonSerializer.Serialize(result, jsonOptions);
                await File.WriteAllTextAsync(outputFile, resultJson);
                _store.SaveOcrResult(result, resultJson);
            }
            catch (Exception ex)
            {
                var errorResult = new ImageRecognitionResult(
                    FileName: Path.GetFileName(imageFile),
                    FilePath: imageFile,
                    RelativePath: relativePath,
                    OutputJsonPath: outputFile,
                    OcrProvider: options.Provider.ToString(),
                    AiProvider: _aiProvider,
                    Barcodes: [],
                    Text: string.Empty,
                    Fields: EmptyFields,
                    AliRawStructuredFields: null,
                    AliRawStructuredJson: null,
                    CorrectionNotes: [],
                    AppliedPreprocessing: [],
                    AiDebug: null,
                    Error: ex.Message);

                var errorJson = JsonSerializer.Serialize(errorResult, jsonOptions);
                await File.WriteAllTextAsync(outputFile, errorJson);
                _store.SaveOcrResult(errorResult, errorJson);
            }
        }

        progress?.Report($"识别完成，结果已写入目录: {outputDirectory}");
    }

    private async Task<AiApplyResult> ApplyAiProviderAsync(
        OcrReadResult ocr,
        IReadOnlyList<BarcodeResult> barcodes,
        ExtractedFields current,
        List<string> notes,
        IProgress<string>? progress,
        bool isDetailedLog,
        CancellationToken cancellationToken)
    {
        if (!ShouldUseAiProvider(current)) return new AiApplyResult(current, null, "跳过: 关键字段已齐全");

        var response = await _aiResolver.ResolveAsync(
            new AiProviderRequest(
                Text: ocr.Text,
                Barcodes: barcodes,
                AliRawStructuredFields: ocr.AliRawStructuredFields,
                CurrentFields: current),
            cancellationToken);

        var debug = BuildAiDebugInfo(response);

        if (response is null || response.Candidates.Count == 0)
        {
            notes.Add("AI兜底: 未产出可用候选");
            return new AiApplyResult(current, debug, "无可用候选");
        }

        var updated = current;
        var accepted = new List<string>();
        foreach (var kv in response.Candidates)
        {
            if (kv.Value.Confidence < 0.7 || string.IsNullOrWhiteSpace(kv.Value.Value)) continue;
            updated = SetFieldIfEmptyOrLowConfidence(updated, kv.Key, kv.Value.Value!, notes, kv.Value, response.ProviderName);
            accepted.Add($"{kv.Key}={Preview(kv.Value.Value, isDetailedLog ? null : 30)}(conf={kv.Value.Confidence:0.00})");
        }

        var summary = accepted.Count > 0
            ? $"Provider={response.ProviderName}, Candidates={response.Candidates.Count}, Accepted={accepted.Count}[{string.Join(", ", accepted)}], Final={FormatFieldsForLog(updated, isDetailedLog)}"
            : $"Provider={response.ProviderName}, Candidates={response.Candidates.Count}, Accepted=0, Final={FormatFieldsForLog(updated, isDetailedLog)}";

        if (progress is not null && debug is not null)
        {
            progress.Report($"  AI调试: PromptLen={debug.Prompt?.Length ?? 0}, RawLen={debug.RawResponse?.Length ?? 0}");
        }

        return new AiApplyResult(updated, debug, summary);
    }

    private AiDebugInfo? BuildAiDebugInfo(AiProviderResponse? response)
    {
        if (!_aiDebugLogEnabled || response is null) return null;
        return new AiDebugInfo(response.ProviderName, response.Prompt, response.RawResponse);
    }

    private static bool ParseBool(string? text, bool defaultValue)
    {
        if (bool.TryParse(text, out var parsed)) return parsed;
        return defaultValue;
    }

    private sealed record AiApplyResult(ExtractedFields Fields, AiDebugInfo? Debug, string Summary);

    private static string FormatBarcodeResults(IReadOnlyList<BarcodeResult> barcodes, bool isDetailedLog)
    {
        if (barcodes.Count == 0) return "Count=0";

        var rows = isDetailedLog ? barcodes : barcodes.Take(6);
        var previewItems = rows.Select(b => $"{b.Format}:{Preview(b.Value, isDetailedLog ? null : 24)}@{b.SourceVariant}");

        var suffix = !isDetailedLog && barcodes.Count > 6 ? $", ...(+{barcodes.Count - 6})" : string.Empty;
        return $"Count={barcodes.Count}, Items=[{string.Join(", ", previewItems)}{suffix}]";
    }

    private static string FormatBarcodeValues(IReadOnlyList<BarcodeResult> barcodes, bool isDetailedLog)
    {
        if (barcodes.Count == 0) return "none";
        var values = (isDetailedLog ? barcodes : barcodes.Take(6)).Select(b => Preview(b.Value, isDetailedLog ? null : 24));
        var suffix = !isDetailedLog && barcodes.Count > 6 ? $", ...(+{barcodes.Count - 6})" : string.Empty;
        return $"[{string.Join(", ", values)}{suffix}]";
    }

    private static string FormatOcrOutput(OcrReadResult ocr, bool isDetailedLog)
    {
        var rawFieldCount = ocr.AliRawStructuredFields?.Count ?? 0;
        var mapped = ocr.MappedFields is null ? "none" : FormatFieldsForLog(ocr.MappedFields, isDetailedLog);
        return $"TextLen={ocr.Text.Length}, Text={Preview(ocr.Text, isDetailedLog ? null : 180)}, Mapped={mapped}, RawFieldCount={rawFieldCount}";
    }

    private static string FormatFieldsForLog(ExtractedFields fields, bool isDetailedLog)
    {
        var items = new List<string>();
        AddIfNotEmpty(items, "PartNumber", fields.PartNumber, isDetailedLog);
        AddIfNotEmpty(items, "MPN", fields.MPN, isDetailedLog);
        AddIfNotEmpty(items, "Quantity", fields.Quantity, isDetailedLog);
        AddIfNotEmpty(items, "DateCode", fields.DateCode, isDetailedLog);
        AddIfNotEmpty(items, "LotNo", fields.LotNo, isDetailedLog);
        AddIfNotEmpty(items, "Brand", fields.Brand, isDetailedLog);
        AddIfNotEmpty(items, "Supplier", fields.Supplier, isDetailedLog);
        AddIfNotEmpty(items, "PO", fields.PO, isDetailedLog);
        AddIfNotEmpty(items, "HuId", fields.HuId, isDetailedLog);

        return items.Count == 0 ? "empty" : string.Join(", ", items);
    }

    private static void AddIfNotEmpty(List<string> items, string key, string? value, bool isDetailedLog)
    {
        if (string.IsNullOrWhiteSpace(value)) return;
        items.Add($"{key}={Preview(value, isDetailedLog ? null : 30)}");
    }

    private static string Preview(string? text, int? max)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var normalized = NormalizeWhitespace(text);
        if (max is null || max <= 0) return normalized;
        if (normalized.Length <= max) return normalized;
        return normalized[..max.Value] + "...";
    }

    private static bool ShouldUseAiProvider(ExtractedFields fields)
    {
        return string.IsNullOrWhiteSpace(fields.PartNumber)
            || string.IsNullOrWhiteSpace(fields.MPN)
            || string.IsNullOrWhiteSpace(fields.Quantity)
            || string.IsNullOrWhiteSpace(fields.LotNo)
            || string.IsNullOrWhiteSpace(fields.Brand);
    }

    private static ExtractedFields SetFieldIfEmptyOrLowConfidence(
        ExtractedFields current,
        string field,
        string candidate,
        List<string> notes,
        AiFieldCandidate meta,
        string provider)
    {
        string? existing = field switch
        {
            "PartNumber" => current.PartNumber,
            "MPN" => current.MPN,
            "Quantity" => current.Quantity,
            "LotNo" => current.LotNo,
            "Brand" => current.Brand,
            "PO" => current.PO,
            "HuId" => current.HuId,
            _ => null
        };

        if (!string.IsNullOrWhiteSpace(existing)) return current;

        notes.Add($"AI兜底[{provider}] {field}={candidate} (conf={meta.Confidence:0.00}, {meta.Evidence})");

        return field switch
        {
            "PartNumber" => current with { PartNumber = candidate },
            "MPN" => current with { MPN = candidate },
            "Quantity" => current with { Quantity = NormalizeQuantityValue(candidate) },
            "LotNo" => current with { LotNo = candidate },
            "Brand" => current with { Brand = candidate },
            "PO" => current with { PO = candidate },
            "HuId" => current with { HuId = candidate },
            _ => current
        };
    }

    private static readonly ExtractedFields EmptyFields = new(null, null, null, null, null, null, null, null, null, null);

    private static Client CreateAliOcrClient(string accessKeyId, string accessKeySecret, string endpoint)
    {
        var config = new Config
        {
            AccessKeyId = accessKeyId,
            AccessKeySecret = accessKeySecret,
            Endpoint = string.IsNullOrWhiteSpace(endpoint) ? "ocr-api.cn-hangzhou.aliyuncs.com" : endpoint
        };
        return new Client(config);
    }

    private static async Task<OcrReadResult> ReadOcrAsync(ProcessedImage image, string imagePath, AppOptions options, Client? aliClient)
    {
        return options.Provider switch
        {
            OcrProvider.Windows => await ReadWindowsTextAsync(image, options.LanguageTag),
            OcrProvider.Aliyun when aliClient is not null => await ReadAliTextAsync(imagePath, aliClient),
            OcrProvider.Paddle => await ReadLocalHttpOcrTextAsync(imagePath, options.PaddleEndpoint, options.LocalOcrTimeoutSeconds),
            _ => throw new InvalidOperationException("OCR 配置无效。")
        };
    }

    private static async Task<OcrReadResult> ReadLocalHttpOcrTextAsync(string imagePath, string endpoint, int timeoutSeconds)
    {
        var baseUrl = string.IsNullOrWhiteSpace(endpoint) ? "http://127.0.0.1:8000" : endpoint.TrimEnd('/');

        using var http = new HttpClient
        {
            BaseAddress = new Uri(baseUrl + "/"),
            Timeout = TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds))
        };

        using var form = new MultipartFormDataContent();
        await using var stream = File.OpenRead(imagePath);
        using var fileContent = new StreamContent(stream);
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(fileContent, "file", Path.GetFileName(imagePath));

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync("ocr", form);
        }
        catch (TaskCanceledException ex)
        {
            throw new InvalidOperationException(
                $"本地 OCR 请求超时（{Math.Max(5, timeoutSeconds)} 秒）：{baseUrl}/ocr。请确认服务已启动，或提高 LocalOcrTimeoutSeconds。",
                ex);
        }
        response.EnsureSuccessStatusCode();

        var raw = await response.Content.ReadAsStringAsync();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return new OcrReadResult(string.Empty, null, null, null);
        }

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        var text = root.TryGetProperty("text", out var textNode) && textNode.ValueKind == JsonValueKind.String
            ? textNode.GetString() ?? string.Empty
            : string.Empty;

        Dictionary<string, string>? kvMap = null;
        if (root.TryGetProperty("fields", out var fieldsNode) && fieldsNode.ValueKind == JsonValueKind.Object)
        {
            kvMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in fieldsNode.EnumerateObject())
            {
                var value = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()
                    : property.Value.ToString();

                if (!string.IsNullOrWhiteSpace(value))
                {
                    kvMap[property.Name] = value!;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text) && kvMap is not null && kvMap.Count > 0)
        {
            text = string.Join(" ", kvMap.Select(x => $"{x.Key}: {x.Value}"));
        }

        return new OcrReadResult(NormalizeWhitespace(text), null, kvMap, raw);
    }

    private static async Task<OcrReadResult> ReadAliTextAsync(string imagePath, Client client)
    {
        await using var body = File.OpenRead(imagePath);
        var request = new RecognizeGeneralStructureRequest { Body = body };
        var response = await client.RecognizeGeneralStructureWithOptionsAsync(request, new RuntimeOptions());
        return ExtractAliResult(response);
    }

    private static OcrReadResult ExtractAliResult(RecognizeGeneralStructureResponse response)
    {
        var json = JsonSerializer.Serialize(response, RawAliJsonOptions);
        var root = JsonNode.Parse(json);
        if (root is null) return new OcrReadResult(string.Empty, null, null, null);

        var kvMap = ExtractAliKvMap(root);
        var text = kvMap.Count > 0
            ? NormalizeWhitespace(string.Join(" ", kvMap.Select(x => $"{x.Key}: {x.Value}")))
            : NormalizeWhitespace(root.ToJsonString(RawAliJsonOptions));

        return new OcrReadResult(text, null, kvMap, root.ToJsonString(RawAliJsonOptions));
    }

    private static async Task<OcrReadResult> ReadWindowsTextAsync(ProcessedImage processedImage, string languageTag)
    {
        var language = new Language(string.IsNullOrWhiteSpace(languageTag) ? "zh-Hans" : languageTag);
        var engine = OcrEngine.TryCreateFromLanguage(language) ?? OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null)
            throw new InvalidOperationException($"无法创建 Windows OCR 引擎，语言代码: {languageTag}");

        var candidates = new List<string>();
        foreach (var name in new[] { "ocr-original", "ocr-threshold" })
        {
            var variant = processedImage.Variants.FirstOrDefault(v => v.Name == name);
            if (variant is null) continue;

            using var stream = new MemoryStream();
            variant.Bitmap.Save(stream, ImageFormat.Bmp);
            stream.Position = 0;
            using var ras = stream.AsRandomAccessStream();
            var decoder = await BitmapDecoder.CreateAsync(ras);
            var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
            var result = await engine.RecognizeAsync(bitmap);
            var text = NormalizeWhitespace(result.Text);
            if (!string.IsNullOrWhiteSpace(text)) candidates.Add(text);
        }

        var best = candidates.OrderByDescending(x => x.Length).FirstOrDefault() ?? string.Empty;
        return new OcrReadResult(best, null, null, null);
    }

    private static Dictionary<string, string> ExtractAliKvMap(JsonNode root)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root["Body"]?["Data"]?["SubImages"] is not JsonArray subImages) return map;

        foreach (var sub in subImages)
        {
            if (sub?["KvInfo"]?["Data"] is not JsonObject kvData) continue;
            foreach (var p in kvData)
            {
                if (p.Value is JsonValue jv && jv.TryGetValue<string>(out var txt))
                    map[p.Key] = txt;
                else if (p.Value is not null)
                    map[p.Key] = p.Value.ToJsonString();
            }
        }

        return map;
    }

    private static Dictionary<string, string?> ResolveValues(
        IReadOnlyDictionary<string, string>? aliMap,
        string? text,
        IReadOnlyList<BarcodeResult> barcodes,
        IReadOnlyList<FieldRule> rules)
    {
        var resolved = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var joinedBarcodes = string.Join(" ", barcodes.Select(b => b.Value));

        foreach (var rule in rules)
        {
            string? value = null;
            var preferBarcode = PrefersBarcode(rule.Name);

            if (aliMap is not null)
            {
                foreach (var key in rule.AliKeys)
                {
                    if (aliMap.TryGetValue(key, out var aliValue) && !string.IsNullOrWhiteSpace(aliValue))
                    {
                        value = aliValue;
                        break;
                    }
                }
            }

            if (preferBarcode)
            {
                if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(rule.BarcodeRegex) && !string.IsNullOrWhiteSpace(joinedBarcodes))
                    value = MatchGroup1(joinedBarcodes, rule.BarcodeRegex);

                if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(rule.TextRegex) && !string.IsNullOrWhiteSpace(text))
                    value = MatchGroup1(text, rule.TextRegex);
            }
            else
            {
                if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(rule.TextRegex) && !string.IsNullOrWhiteSpace(text))
                    value = MatchGroup1(text, rule.TextRegex);

                if (string.IsNullOrWhiteSpace(value) && !string.IsNullOrWhiteSpace(rule.BarcodeRegex) && !string.IsNullOrWhiteSpace(joinedBarcodes))
                    value = MatchGroup1(joinedBarcodes, rule.BarcodeRegex);
            }

            if (!string.IsNullOrWhiteSpace(rule.Template))
            {
                var templated = ApplyTemplate(rule.Template, resolved, aliMap);
                if (!string.IsNullOrWhiteSpace(templated))
                    value = string.IsNullOrWhiteSpace(value) ? templated : $"{value} {templated}";
            }

            value = NormalizeWhitespace(value);
            value = NormalizeRuleValue(rule.Name, value);
            if (!string.IsNullOrWhiteSpace(rule.DateFormat))
                value = NormalizeDate(value, rule.DateFormat);

            resolved[rule.Name] = value;
        }

        return resolved;
    }

    private static string? ApplyTemplate(string template, IReadOnlyDictionary<string, string?> resolved, IReadOnlyDictionary<string, string>? aliMap)
    {
        return Regex.Replace(template, "\\{([^{}]+)\\}", m =>
        {
            var key = m.Groups[1].Value;
            if (resolved.TryGetValue(key, out var v) && !string.IsNullOrWhiteSpace(v)) return v;
            if (aliMap is not null && aliMap.TryGetValue(key, out var ali) && !string.IsNullOrWhiteSpace(ali)) return ali;
            return string.Empty;
        });
    }

    private static string? MatchGroup1(string input, string pattern)
    {
        var match = Regex.Match(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        if (!match.Success) return null;

        if (match.Groups.Count > 1)
        {
            for (var i = 1; i < match.Groups.Count; i++)
            {
                if (!string.IsNullOrWhiteSpace(match.Groups[i].Value))
                {
                    return match.Groups[i].Value;
                }
            }
        }

        return match.Value;
    }

    private static string? NormalizeRuleValue(string fieldName, string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;

        return fieldName switch
        {
            "Quantity" => NormalizeQuantityValue(value),
            "DateCode" => Regex.Replace(value, "(?<=\\d)\\s+(?=\\d)", string.Empty),
            "PartNumber" or "MPN" or "LotNo" => Regex.Replace(value, "(?<=[A-Z0-9])\\s+(?=[A-Z0-9])", string.Empty, RegexOptions.IgnoreCase),
            _ => value
        };
    }

    private static bool PrefersBarcode(string fieldName)
    {
        return fieldName is "PartNumber" or "MPN" or "LotNo";
    }

    private static string? NormalizeDate(string? value, string dateFormat)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var digitsOnly = Regex.Replace(value, "[^0-9]", string.Empty);

        if (digitsOnly.Length == 4 && int.TryParse(digitsOnly[..2], out var yy) && int.TryParse(digitsOnly[2..], out var isoWeek2))
        {
            var year = yy >= 70 ? 1900 + yy : 2000 + yy;
            if (isoWeek2 is >= 1 and <= 53)
            {
                var monday = ISOWeek.ToDateTime(year, isoWeek2, System.DayOfWeek.Monday);
                return monday.ToString(dateFormat);
            }
        }

        if (digitsOnly.Length == 6)
        {
            if (DateTime.TryParseExact(digitsOnly, "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ymd6))
                return ymd6.ToString(dateFormat);

            if (int.TryParse(digitsOnly[..4], out var isoYear4) && int.TryParse(digitsOnly[4..], out var isoWeek2B) && isoYear4 is >= 1900 and <= 2100 && isoWeek2B is >= 1 and <= 53)
            {
                var monday = ISOWeek.ToDateTime(isoYear4, isoWeek2B, System.DayOfWeek.Monday);
                return monday.ToString(dateFormat);
            }
        }

        if (digitsOnly.Length == 8 && DateTime.TryParseExact(digitsOnly, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var ymd8))
            return ymd8.ToString(dateFormat);

        if (DateTime.TryParse(value, out var parsed))
            return parsed.ToString(dateFormat);

        return NormalizeWhitespace(value);
    }

    private static ExtractedFields BuildFields(IReadOnlyDictionary<string, string?> values)
    {
        string? Get(string key) => values.TryGetValue(key, out var v) ? NormalizeWhitespace(v) : null;

        return new ExtractedFields(
            PartNumber: Get("PartNumber"),
            Description: Get("Description"),
            Quantity: Get("Quantity"),
            DateCode: Get("DateCode"),
            LotNo: Get("LotNo"),
            Supplier: Get("Supplier"),
            Brand: Get("Brand"),
            MPN: Get("MPN"),
            PO: Get("PO"),
            HuId: Get("HuId"));
    }

    private static ExtractedFields MergeFields(ExtractedFields? preferred, ExtractedFields fallback)
    {
        if (preferred is null) return fallback;

        return new ExtractedFields(
            PartNumber: Pick(preferred.PartNumber, fallback.PartNumber),
            Description: Pick(preferred.Description, fallback.Description),
            Quantity: Pick(preferred.Quantity, fallback.Quantity),
            DateCode: Pick(preferred.DateCode, fallback.DateCode),
            LotNo: Pick(preferred.LotNo, fallback.LotNo),
            Supplier: Pick(preferred.Supplier, fallback.Supplier),
            Brand: Pick(preferred.Brand, fallback.Brand),
            MPN: Pick(preferred.MPN, fallback.MPN),
            PO: Pick(preferred.PO, fallback.PO),
            HuId: Pick(preferred.HuId, fallback.HuId));
    }

    private sealed record BarcodeCorrectionRule(
        string Name,
        Regex Pattern,
        Func<Match, IReadOnlyDictionary<string, string?>> BuildCandidates);

    private sealed class BarcodeCorrectionContext
    {
        private readonly List<string> _notes;

        public BarcodeCorrectionContext(ExtractedFields fields, List<string> notes)
        {
            Current = fields;
            _notes = notes;
        }

        public ExtractedFields Current { get; private set; }

        public void CorrectField(string field, string? barcodeValue)
        {
            var value = NormalizeWhitespace(barcodeValue);
            if (field == "Quantity") value = NormalizeQuantityValue(value);
            if (string.IsNullOrWhiteSpace(value)) return;

            var currentValue = GetFieldValue(field);
            var currentNormalized = field == "Quantity"
                ? NormalizeQuantityValue(currentValue)
                : NormalizeWhitespace(currentValue);

            if (string.IsNullOrWhiteSpace(currentValue))
            {
                SetFieldValue(field, value);
                _notes.Add($"{field} 由条码补全: {value}");
                return;
            }

            if (!string.Equals(currentNormalized, value, StringComparison.OrdinalIgnoreCase))
            {
                SetFieldValue(field, value);
                _notes.Add($"{field} 被条码纠正: {currentValue} -> {value}");
            }
        }

        private string? GetFieldValue(string field)
        {
            return field switch
            {
                "PartNumber" => Current.PartNumber,
                "Description" => Current.Description,
                "Quantity" => Current.Quantity,
                "DateCode" => Current.DateCode,
                "LotNo" => Current.LotNo,
                "Supplier" => Current.Supplier,
                "Brand" => Current.Brand,
                "MPN" => Current.MPN,
                "PO" => Current.PO,
                "HuId" => Current.HuId,
                _ => null
            };
        }

        private void SetFieldValue(string field, string value)
        {
            Current = field switch
            {
                "PartNumber" => Current with { PartNumber = value },
                "Description" => Current with { Description = value },
                "Quantity" => Current with { Quantity = value },
                "DateCode" => Current with { DateCode = value },
                "LotNo" => Current with { LotNo = value },
                "Supplier" => Current with { Supplier = value },
                "Brand" => Current with { Brand = value },
                "MPN" => Current with { MPN = value },
                "PO" => Current with { PO = value },
                "HuId" => Current with { HuId = value },
                _ => Current
            };
        }
    }

    private static readonly IReadOnlyList<BarcodeCorrectionRule> BarcodeCorrectionRules =
    [
        new(
            "QR_HASH_7",
            new Regex("^([^#]+)#([^#]+)#([^#]+)#([^#]+)#([^#]+)#([^#]+)#([^#]+)$", RegexOptions.Compiled | RegexOptions.CultureInvariant),
            m => new Dictionary<string, string?>
            {
                ["PartNumber"] = m.Groups[1].Value,
                ["MPN"] = m.Groups[2].Value,
                ["Quantity"] = Regex.Match(m.Groups[3].Value, "\\d+").Value,
                ["DateCode"] = NormalizeDate(m.Groups[4].Value, "yyyy-MM-dd"),
                ["LotNo"] = m.Groups[5].Value,
                ["Brand"] = m.Groups[6].Value,
                ["Supplier"] = m.Groups[7].Value
            }),
        new(
            "CODE39_3N2",
            new Regex("^3N2\\s*([A-Z0-9\\-_/]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            m => new Dictionary<string, string?>
            {
                ["LotNo"] = m.Groups[1].Value
            }),
        new(
            "CODE39_3N1",
            new Regex("^3N1\\s*([A-Z0-9\\-_/]+)\\s+([0-9]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            m => new Dictionary<string, string?>
            {
                ["MPN"] = m.Groups[1].Value,
                ["PartNumber"] = m.Groups[1].Value,
                ["Quantity"] = m.Groups[2].Value
            }),
        new(
            "CODE39_1P",
            new Regex("^1P\\s*([A-Z0-9\\-_/]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            m => new Dictionary<string, string?>
            {
                ["PartNumber"] = m.Groups[1].Value,
                ["MPN"] = m.Groups[1].Value
            }),
        new(
            "MODEL_TOKEN",
            new Regex("^(KEMC[0-9A-Z]+|EEHZE[0-9A-Z]+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            m => new Dictionary<string, string?>
            {
                ["PartNumber"] = m.Groups[1].Value,
                ["MPN"] = m.Groups[1].Value
            })
    ];

    private static ExtractedFields ApplyBarcodeCorrections(ExtractedFields fields, IReadOnlyList<BarcodeResult> barcodes, out IReadOnlyList<string> notes)
    {
        var noteList = new List<string>();
        var context = new BarcodeCorrectionContext(fields with { }, noteList);

        var barcodeValues = barcodes
            .Select(b => NormalizeWhitespace(b.Value))
            .Where(v => !string.IsNullOrWhiteSpace(v))
            .ToList();

        foreach (var value in barcodeValues)
        {
            foreach (var rule in BarcodeCorrectionRules)
            {
                var match = rule.Pattern.Match(value);
                if (!match.Success) continue;

                var candidates = rule.BuildCandidates(match);
                foreach (var kv in candidates)
                {
                    context.CorrectField(kv.Key, kv.Value);
                }
            }
        }

        ApplyNumericHeuristics(context, barcodeValues);

        notes = noteList;
        return context.Current;
    }

    private static void ApplyNumericHeuristics(BarcodeCorrectionContext context, IReadOnlyList<string> barcodeValues)
    {
        var numericValues = barcodeValues
            .Where(v => Regex.IsMatch(v, "^[0-9]+$"))
            .ToList();

        var poCandidate = numericValues.FirstOrDefault(v => v.Length == 10);
        if (!string.IsNullOrWhiteSpace(poCandidate))
        {
            context.CorrectField("PO", poCandidate);
        }

        var huIdCandidate = numericValues.FirstOrDefault(v => v.Length == 9);
        if (!string.IsNullOrWhiteSpace(huIdCandidate))
        {
            context.CorrectField("HuId", huIdCandidate);
        }

        var qtyCandidate = numericValues
            .Where(v => v.Length is >= 1 and <= 6)
            .Select(v => int.TryParse(v, out var n) ? n : -1)
            .Where(n => n is > 0 and < 1000000)
            .OrderByDescending(n => n)
            .FirstOrDefault();

        if (qtyCandidate > 0)
        {
            context.CorrectField("Quantity", qtyCandidate.ToString(CultureInfo.InvariantCulture));
        }

        var dcCandidate = numericValues.FirstOrDefault(v => v.Length == 4);
        if (!string.IsNullOrWhiteSpace(dcCandidate))
        {
            context.CorrectField("DateCode", NormalizeDate(dcCandidate, "yyyy-MM-dd"));
        }
    }

    private static string NormalizeQuantityValue(string? value)
    {
        var normalized = NormalizeWhitespace(value);
        if (string.IsNullOrWhiteSpace(normalized)) return string.Empty;

        var match = Regex.Match(normalized, "\\d+");
        return match.Success ? match.Value : normalized;
    }

    private static string Pick(string? preferred, string? fallback) =>
        string.IsNullOrWhiteSpace(preferred) ? NormalizeWhitespace(fallback) : NormalizeWhitespace(preferred);

    private static string NormalizeWhitespace(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;
        return Regex.Replace(input, "\\s+", " ").Trim();
    }

    private static ProcessedImage PreprocessImage(string imagePath)
    {
        const int MaxBarcodeWidth = 2000;
        const int MaxOcrWidth = 1500;

        using var fileImage = (System.Drawing.Bitmap)System.Drawing.Image.FromFile(imagePath);

        var barcodeBase = fileImage.Width > MaxBarcodeWidth
            ? ScaleBitmapF(fileImage, (float)MaxBarcodeWidth / fileImage.Width)
            : new System.Drawing.Bitmap(fileImage);

        using var barcodeGray = ToGrayscaleBitmap(barcodeBase);
        var barcodeThreshold = BinarizeGrayscale(barcodeGray, 128);

        var variants = new List<ImageVariant>
        {
            new("original", barcodeBase),
            new("threshold-128", barcodeThreshold),
        };

        if (barcodeBase.Width < 1000)
        {
            variants.Add(new("upscaled-original", ScaleBitmap(barcodeBase, 2)));
            variants.Add(new("upscaled-threshold", ScaleBitmap(barcodeThreshold, 2)));
        }

        var ocrBase = fileImage.Width > MaxOcrWidth
            ? ScaleBitmapF(fileImage, (float)MaxOcrWidth / fileImage.Width)
            : new System.Drawing.Bitmap(fileImage);
        using var ocrGray = ToGrayscaleBitmap(ocrBase);
        var ocrThreshold = BinarizeGrayscale(ocrGray, 128);
        variants.Add(new("ocr-original", ocrBase));
        variants.Add(new("ocr-threshold", ocrThreshold));

        var steps = new List<string>();
        if (fileImage.Width > MaxBarcodeWidth) steps.Add($"barcode-downscale-{MaxBarcodeWidth}px");
        steps.Add("threshold-128");
        if (barcodeBase.Width < 1000) steps.Add("barcode-upscale-2x");
        if (fileImage.Width > MaxOcrWidth) steps.Add($"ocr-downscale-{MaxOcrWidth}px");

        return new ProcessedImage(variants, steps);
    }

    private static IReadOnlyList<BarcodeResult> ReadBarcodes(ProcessedImage processedImage, BarcodeProvider provider)
    {
        return provider switch
        {
            BarcodeProvider.ZXing => ReadBarcodesByZxing(processedImage),
            BarcodeProvider.WechatQrCode => ReadBarcodesByWechatAndZxingFallback(processedImage),
            _ => ReadBarcodesByZxing(processedImage)
        };
    }

    private static IReadOnlyList<BarcodeResult> ReadBarcodesByWechatAndZxingFallback(ProcessedImage processedImage)
    {
        var wechatResults = ReadBarcodesByWechatQrCode(processedImage);
        var zxingNonQrResults = ReadBarcodesByZxing(processedImage, includeQrFormats: false);

        var merged = new List<BarcodeResult>(wechatResults.Count + zxingNonQrResults.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in wechatResults)
        {
            var key = $"{item.Format}:{item.Value}";
            if (seen.Add(key)) merged.Add(item);
        }

        foreach (var item in zxingNonQrResults)
        {
            var key = $"{item.Format}:{item.Value}";
            if (seen.Add(key)) merged.Add(item);
        }

        return merged;
    }

    private static IReadOnlyList<BarcodeResult> ReadBarcodesByZxing(ProcessedImage processedImage, bool includeQrFormats = true)
    {
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var barcodeResults = new List<BarcodeResult>();

        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = { TryHarder = true, TryInverted = true, ReturnCodabarStartEnd = true }
        };

        var qrReader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options =
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = [BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417]
            }
        };

        var qrVariantNames = new HashSet<string> { "original", "threshold-128", "upscaled-original" };

        foreach (var variant in processedImage.Variants.Where(v => !v.Name.StartsWith("ocr-", StringComparison.OrdinalIgnoreCase)))
        {
            Collect(reader.DecodeMultiple(variant.Bitmap), variant.Name);
            if (includeQrFormats && qrVariantNames.Contains(variant.Name))
                Collect(qrReader.DecodeMultiple(variant.Bitmap), variant.Name + "+qr");
        }

        return barcodeResults;

        void Collect(Result[]? results, string source)
        {
            if (results is null || results.Length == 0) return;
            foreach (var item in results)
            {
                if (!includeQrFormats && IsQrLikeFormat(item.BarcodeFormat)) continue;

                var value = NormalizeWhitespace(item.Text);
                var key = $"{item.BarcodeFormat}:{value}";
                if (string.IsNullOrWhiteSpace(value) || !values.Add(key)) continue;
                barcodeResults.Add(new BarcodeResult(item.BarcodeFormat.ToString(), value, source));
            }
        }
    }

    private static bool IsQrLikeFormat(BarcodeFormat format)
    {
        return format is BarcodeFormat.QR_CODE;
            // or BarcodeFormat.DATA_MATRIX
            // or BarcodeFormat.AZTEC
            // or BarcodeFormat.PDF_417
            // or BarcodeFormat.MAXICODE;
    }

    private static IReadOnlyList<BarcodeResult> ReadBarcodesByWechatQrCode(ProcessedImage processedImage)
    {
        var modelDir = ResolveWechatQrCodeModelDirectory();
        if (string.IsNullOrWhiteSpace(modelDir))
        {
            throw new DirectoryNotFoundException("未找到 WechatQrCode 模型目录。请在 App.config 设置 WechatQrCodeModelDirectory，或将模型放到 ../wechatqrcode/models、models/wechatqrcode。\n需要文件: detect.prototxt, detect.caffemodel, sr.prototxt, sr.caffemodel");
        }

        var detectProto = Path.Combine(modelDir, "detect.prototxt");
        var detectModel = Path.Combine(modelDir, "detect.caffemodel");
        var srProto = Path.Combine(modelDir, "sr.prototxt");
        var srModel = Path.Combine(modelDir, "sr.caffemodel");

        var requiredFiles = new[] { detectProto, detectModel, srProto, srModel };
        var missing = requiredFiles.Where(path => !File.Exists(path)).ToList();
        if (missing.Count > 0)
        {
            throw new FileNotFoundException($"WechatQrCode 模型文件缺失: {string.Join(", ", missing)}");
        }

        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var barcodeResults = new List<BarcodeResult>();

        using var reader = WeChatQRCode.Create(detectProto, detectModel, srProto, srModel);
        foreach (var variant in processedImage.Variants.Where(v => !v.Name.StartsWith("ocr-", StringComparison.OrdinalIgnoreCase)))
        {
            using var mat = BitmapConverter.ToMat(variant.Bitmap);
            reader.DetectAndDecode(mat, out _, out var results);
            Collect(results, variant.Name + "+wechat");
        }

        return barcodeResults;

        void Collect(string[]? results, string source)
        {
            if (results is null || results.Length == 0) return;
            foreach (var item in results)
            {
                var value = NormalizeWhitespace(item);
                var key = $"WECHAT_QRCODE:{value}";
                if (string.IsNullOrWhiteSpace(value) || !values.Add(key)) continue;
                barcodeResults.Add(new BarcodeResult("WECHAT_QRCODE", value, source));
            }
        }
    }

    private static string? ResolveWechatQrCodeModelDirectory()
    {
        var configured = ConfigurationManager.AppSettings["WechatQrCodeModelDirectory"];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var configuredPath = Path.GetFullPath(configured);
            if (Directory.Exists(configuredPath)) return configuredPath;
        }

        var candidates = new List<string>();
        var baseDir = AppContext.BaseDirectory;
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "wechatqrcode", "models")));
        candidates.Add(Path.GetFullPath(Path.Combine(baseDir, "..", "..", "..", "..", "models", "wechatqrcode")));

        var current = new DirectoryInfo(Environment.CurrentDirectory);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "OCR.sln")))
            {
                candidates.Add(Path.Combine(current.FullName, "models", "wechatqrcode"));
                break;
            }
            current = current.Parent;
        }

        return candidates.FirstOrDefault(Directory.Exists);
    }

    private static System.Drawing.Bitmap ToGrayscaleBitmap(System.Drawing.Bitmap source)
    {
        var result = new System.Drawing.Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        using var graphics = System.Drawing.Graphics.FromImage(result);
        var colorMatrix = new System.Drawing.Imaging.ColorMatrix(
        [
            [0.299f, 0.299f, 0.299f, 0, 0],
            [0.587f, 0.587f, 0.587f, 0, 0],
            [0.114f, 0.114f, 0.114f, 0, 0],
            [0, 0, 0, 1, 0],
            [0, 0, 0, 0, 1]
        ]);

        using var attributes = new System.Drawing.Imaging.ImageAttributes();
        attributes.SetColorMatrix(colorMatrix);
        graphics.DrawImage(source, new System.Drawing.Rectangle(0, 0, result.Width, result.Height), 0, 0, source.Width, source.Height, System.Drawing.GraphicsUnit.Pixel, attributes);
        return result;
    }

    private static System.Drawing.Bitmap BinarizeGrayscale(System.Drawing.Bitmap grayscale, int threshold)
    {
        var width = grayscale.Width;
        var height = grayscale.Height;
        var result = new System.Drawing.Bitmap(width, height, PixelFormat.Format24bppRgb);
        var rect = new System.Drawing.Rectangle(0, 0, width, height);
        var srcData = grayscale.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        var dstData = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);

        try
        {
            var stride = srcData.Stride;
            var total = Math.Abs(stride) * height;
            var src = new byte[total];
            Marshal.Copy(srcData.Scan0, src, 0, total);
            var dst = new byte[total];

            for (var y = 0; y < height; y++)
            {
                var row = y * stride;
                for (var x = 0; x < width; x++)
                {
                    var idx = row + x * 3;
                    var v = src[idx] >= threshold ? (byte)255 : (byte)0;
                    dst[idx] = v;
                    dst[idx + 1] = v;
                    dst[idx + 2] = v;
                }
            }

            Marshal.Copy(dst, 0, dstData.Scan0, total);
        }
        finally
        {
            grayscale.UnlockBits(srcData);
            result.UnlockBits(dstData);
        }

        return result;
    }

    private static System.Drawing.Bitmap ScaleBitmap(System.Drawing.Bitmap source, int scale)
    {
        var scaled = new System.Drawing.Bitmap(source.Width * scale, source.Height * scale, PixelFormat.Format24bppRgb);
        using var g = System.Drawing.Graphics.FromImage(scaled);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new System.Drawing.Rectangle(0, 0, scaled.Width, scaled.Height));
        return scaled;
    }

    private static System.Drawing.Bitmap ScaleBitmapF(System.Drawing.Bitmap source, float scale)
    {
        var w = Math.Max(1, (int)(source.Width * scale));
        var h = Math.Max(1, (int)(source.Height * scale));
        var scaled = new System.Drawing.Bitmap(w, h, PixelFormat.Format24bppRgb);
        using var g = System.Drawing.Graphics.FromImage(scaled);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.DrawImage(source, new System.Drawing.Rectangle(0, 0, w, h));
        return scaled;
    }
}
