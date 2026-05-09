using System.Text.RegularExpressions;
using System.Configuration;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;

namespace OcrConsole;

internal sealed record AiProviderRequest(
    string Text,
    IReadOnlyList<BarcodeResult> Barcodes,
    IReadOnlyDictionary<string, string>? AliRawStructuredFields,
    ExtractedFields CurrentFields);

internal sealed record AiFieldCandidate(string? Value, double Confidence, string Evidence);

internal sealed record AiProviderResponse(
    IReadOnlyDictionary<string, AiFieldCandidate> Candidates,
    string ProviderName,
    string? Prompt = null,
    string? RawResponse = null);

internal interface IAiFieldResolver
{
    Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default);
}

internal static class AiResolverFactory
{
    private static readonly string[] CandidateFieldNames =
    [
        "PartNumber",
        "Quantity",
        "LotNo",
        "DateCode",
    ];

    public static IAiFieldResolver CreateFromConfig(string? providerOverride = null)
    {
        var provider = (providerOverride ?? ConfigurationManager.AppSettings["AiProvider"] ?? "ollama").Trim();
        if (provider.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            return new MockAiFieldResolver();
        }

        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = ConfigurationManager.AppSettings["OllamaEndpoint"] ?? "http://127.0.0.1:11434";
            var model = ConfigurationManager.AppSettings["OllamaModel"] ?? "qwen2.5:7b";
            var timeoutSeconds = SettingHelper.ParseInt(ConfigurationManager.AppSettings["OllamaTimeoutSeconds"], 45);
            var keepAlive = ConfigurationManager.AppSettings["OllamaKeepAlive"] ?? "30m";
            var numPredict = SettingHelper.ParseInt(ConfigurationManager.AppSettings["OllamaNumPredict"], 512);
            var numCtx = SettingHelper.ParseInt(ConfigurationManager.AppSettings["OllamaNumCtx"], 1024);
            var numThread = SettingHelper.ParseInt(ConfigurationManager.AppSettings["OllamaNumThread"], 16);
            var numBatch = SettingHelper.ParseInt(ConfigurationManager.AppSettings["OllamaNumBatch"], 128);

            numPredict = Math.Clamp(numPredict, 16, 1024);
            numCtx = Math.Clamp(numCtx, 512, 4096);
            numThread = Math.Clamp(numThread, 1, 64);
            numBatch = Math.Clamp(numBatch, 16, 1024);

            return new OllamaAiFieldResolver(
                endpoint,
                model,
                TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)),
                keepAlive,
                numPredict,
                numCtx,
                numThread,
                numBatch);
        }

        if (provider.Equals("openvino", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = ConfigurationManager.AppSettings["OpenVinoEndpoint"] ?? "http://127.0.0.1:8000";
            var timeoutSeconds = SettingHelper.ParseInt(ConfigurationManager.AppSettings["OpenVinoTimeoutSeconds"], 60);
            var maxNewTokens = SettingHelper.ParseInt(ConfigurationManager.AppSettings["OpenVinoMaxNewTokens"], 256);

            return new OpenVinoAiFieldResolver(endpoint, TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)), maxNewTokens);
        }

        if (provider.Equals("bailian", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = ConfigurationManager.AppSettings["BailianEndpoint"] ?? "https://dashscope.aliyuncs.com/compatible-mode/v1";
            var model = ConfigurationManager.AppSettings["BailianModel"] ?? "qwen-plus";
            var apiKey = SettingHelper.GetSettingOrEnv("BailianApiKey", "OCR_BAILIAN_API_KEY") ?? string.Empty;
            var timeoutSeconds = SettingHelper.ParseInt(ConfigurationManager.AppSettings["BailianTimeoutSeconds"], 20);

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return new NoopAiFieldResolver();
            }

            return new BailianAiFieldResolver(endpoint, model, apiKey, TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
        }

        if (!provider.Equals("ollama", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("bailian", StringComparison.OrdinalIgnoreCase) &&
            !provider.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            return new NoopAiFieldResolver();
        }

        return new NoopAiFieldResolver();
    }

    internal static string BuildAiPrompt0(AiProviderRequest request)
    {
        var barcodeText = string.Join(" | ", request.Barcodes.Select(b => b.Value));
        var aliRawText = request.AliRawStructuredFields is null
            ? "{}"
            : JsonSerializer.Serialize(request.AliRawStructuredFields);

        var sb = new StringBuilder();
        sb.AppendLine("You are an OCR post-processing assistant.");
        sb.AppendLine("Extract fields from OCR and barcode data.");
        sb.AppendLine("Return STRICT JSON only. No markdown. Unknown value return null.");
        sb.AppendLine();
        sb.AppendLine("Output schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"PartNumber\": \"\",");
        sb.AppendLine("  \"Quantity\": \"\",");
        sb.AppendLine("  \"LotNo\": \"\",");
        sb.AppendLine("  \"DateCode\": \"\"");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Quantity should be numeric only when possible.");
        sb.AppendLine("- Only fill fields you are reasonably sure about.");
        sb.AppendLine("- Stop immediately after the closing JSON brace. No extra notes.");
        sb.AppendLine();
        sb.AppendLine("Barcode values:");
        sb.AppendLine(barcodeText);
        sb.AppendLine();
        sb.AppendLine("OCR values:");
        if (aliRawText == "{}")
        {
            sb.AppendLine(request.Text);
        }
        else
        {
            sb.AppendLine(aliRawText);
        }
        //sb.AppendLine("Current fields:");
        //sb.AppendLine(JsonSerializer.Serialize(request.CurrentFields));

        return sb.ToString();
    }

    internal static string BuildAiPrompt(AiProviderRequest request)
    {
        var barcodeText = string.Join(" | ", request.Barcodes.Select(b => b.Value));
        var aliRawText = request.AliRawStructuredFields is null
            ? "{}"
            : JsonSerializer.Serialize(request.AliRawStructuredFields);

        var sb = new StringBuilder();

        sb.AppendLine("Extract electronic component data. Output ONLY JSON.");
        sb.AppendLine("Fields: PartNumber, Quantity, LotNo, DateCode");
        sb.AppendLine();
        sb.AppendLine("Output format: {\"PartNumber\":\"value\",\"Quantity\":\"value\",\"LotNo\":\"value\",\"DateCode\":\"value\"}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Quantity should be numeric only when possible.");
        sb.AppendLine("- Only fill fields you are reasonably sure about.");
        sb.AppendLine("- Stop immediately after the closing JSON brace. No extra notes.");
        sb.AppendLine("- LotNo: extract from OCR or Barcode if available"); 
        sb.AppendLine("- Skip fields with empty values (omit from output).");
        sb.AppendLine();
        sb.AppendLine("Input:");
        sb.AppendLine($"Barcode: {barcodeText}");
        sb.AppendLine($"OCR: {(aliRawText == "{}" ? request.Text : aliRawText)}");
        sb.AppendLine();
        sb.AppendLine("JSON Result Only:");

        return sb.ToString();
    }


    internal static Dictionary<string, AiFieldCandidate> ParseAiCandidates(string responseText)
    {
        var map = new Dictionary<string, AiFieldCandidate>(StringComparer.OrdinalIgnoreCase);

        if (TryParseCandidatesAsJson(responseText, map))
        {
            return map;
        }

        // Fallback: salvage field-level candidates from partially broken JSON.
        TryParseCandidatesByFieldRegex(responseText, map);
        return map;
    }

    internal static string NormalizeModelResponse(string responseText)
    {
        var text = (responseText ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstLf = text.IndexOf('\n');
            if (firstLf >= 0)
            {
                text = text[(firstLf + 1)..];
            }

            var fenceEnd = text.LastIndexOf("```", StringComparison.Ordinal);
            if (fenceEnd >= 0)
            {
                text = text[..fenceEnd].Trim();
            }
        }

        if (TryExtractFirstCompleteJsonObject(text, out var json))
        {
            return json;
        }

        return text;
    }

    private static bool TryExtractFirstCompleteJsonObject(string text, out string json)
    {
        json = string.Empty;
        var start = text.IndexOf('{');
        if (start < 0)
        {
            return false;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;

        for (var i = start; i < text.Length; i++)
        {
            var ch = text[i];

            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (ch == '\\')
                {
                    escaped = true;
                }
                else if (ch == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (ch == '"')
            {
                inString = true;
                continue;
            }

            if (ch == '{')
            {
                depth++;
                continue;
            }

            if (ch == '}')
            {
                depth--;
                if (depth == 0)
                {
                    json = text[start..(i + 1)];
                    return true;
                }
            }
        }

        return false;
    }

    private static bool TryParseCandidatesAsJson(string responseText, Dictionary<string, AiFieldCandidate> map)
    {
        JsonDocument? doc = null;

        try
        {
            doc = JsonDocument.Parse(responseText);
        }
        catch
        {
            if (!TryExtractFirstCompleteJsonObject(responseText, out var raw))
            {
                return false;
            }

            try
            {
                doc = JsonDocument.Parse(raw);
            }
            catch
            {
                return false;
            }
        }

        using (doc)
        {
            if (doc is null) return false;

            // Preferred schema: flat JSON with 3 string fields.
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var fieldName in CandidateFieldNames)
                {
                    if (!doc.RootElement.TryGetProperty(fieldName, out var node))
                    {
                        continue;
                    }

                    string? value = node.ValueKind switch
                    {
                        JsonValueKind.String => node.GetString(),
                        JsonValueKind.Number => node.ToString(),
                        JsonValueKind.Null => null,
                        _ => node.ToString()
                    };

                    if (string.IsNullOrWhiteSpace(value))
                    {
                        continue;
                    }

                    map[fieldName] = new AiFieldCandidate(value, 0.85, "flat-json");
                }

                if (map.Count > 0)
                {
                    return true;
                }
            }

            if (!doc.RootElement.TryGetProperty("candidates", out var candidatesNode) || candidatesNode.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            foreach (var field in candidatesNode.EnumerateObject())
            {
                if (field.Value.ValueKind != JsonValueKind.Object) continue;

                var value = field.Value.TryGetProperty("value", out var valueNode) ? valueNode.GetString() : null;
                var confidence = field.Value.TryGetProperty("confidence", out var confNode) && confNode.TryGetDouble(out var c) ? c : 0;
                var evidence = field.Value.TryGetProperty("evidence", out var evNode) ? (evNode.GetString() ?? string.Empty) : string.Empty;

                if (string.IsNullOrWhiteSpace(value)) continue;
                map[field.Name] = new AiFieldCandidate(value, Math.Clamp(confidence, 0, 1), evidence);
            }
        }

        return map.Count > 0;
    }

    private static void TryParseCandidatesByFieldRegex(string responseText, Dictionary<string, AiFieldCandidate> map)
    {
        foreach (var fieldName in CandidateFieldNames)
        {
            var flatFieldPattern = $"\\\"{Regex.Escape(fieldName)}\\\"\\s*:\\s*(\\\"(?<fv>(?:\\\\.|[^\\\"])*)\\\"|(?<fn>-?[0-9]+(?:\\.[0-9]+)?)|null)";
            var flatFieldMatch = Regex.Match(responseText, flatFieldPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (flatFieldMatch.Success)
            {
                string? flatValue = null;
                if (flatFieldMatch.Groups["fv"].Success)
                {
                    flatValue = UnescapeJsonString(flatFieldMatch.Groups["fv"].Value);
                }
                else if (flatFieldMatch.Groups["fn"].Success)
                {
                    flatValue = flatFieldMatch.Groups["fn"].Value;
                }

                if (!string.IsNullOrWhiteSpace(flatValue))
                {
                    map[fieldName] = new AiFieldCandidate(flatValue, 0.8, "flat-json-regex");
                    continue;
                }
            }

            var fieldPattern = $"\\\"{Regex.Escape(fieldName)}\\\"\\s*:\\s*\\{{(?<obj>.*?)\\}}";
            var fieldMatch = Regex.Match(responseText, fieldPattern, RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!fieldMatch.Success)
            {
                continue;
            }

            var objText = fieldMatch.Groups["obj"].Value;
            var valueMatch = Regex.Match(objText, "\\\"value\\\"\\s*:\\s*(\\\"(?<v>(?:\\\\.|[^\\\"])*)\\\"|null)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!valueMatch.Success)
            {
                continue;
            }

            string? value = null;
            if (valueMatch.Value.IndexOf("null", StringComparison.OrdinalIgnoreCase) < 0)
            {
                value = UnescapeJsonString(valueMatch.Groups["v"].Value);
            }

            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            var confidence = 0d;
            var confMatch = Regex.Match(objText, "\\\"confidence\\\"\\s*:\\s*(?<c>-?[0-9]+(?:\\.[0-9]+)?)", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (confMatch.Success)
            {
                _ = double.TryParse(confMatch.Groups["c"].Value, out confidence);
            }

            var evidence = string.Empty;
            var evMatch = Regex.Match(objText, "\\\"evidence\\\"\\s*:\\s*\\\"(?<e>(?:\\\\.|[^\\\"])*)\\\"", RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (evMatch.Success)
            {
                evidence = UnescapeJsonString(evMatch.Groups["e"].Value);
            }

            map[fieldName] = new AiFieldCandidate(value, Math.Clamp(confidence, 0, 1), evidence);
        }
    }

    private static string UnescapeJsonString(string text)
    {
        try
        {
            return JsonSerializer.Deserialize<string>($"\"{text}\"") ?? text;
        }
        catch
        {
            return text.Replace("\\\\\"", "\"").Replace("\\\\", "\\");
        }
    }


}

internal sealed class OllamaAiFieldResolver : IAiFieldResolver
{
    private readonly string _model;
    private readonly HttpClient _httpClient;
    private readonly string _keepAlive;
    private readonly int _numPredict;
    private readonly int _numCtx;
    private readonly int _numThread;
    private readonly int _numBatch;

    public OllamaAiFieldResolver(
        string endpoint,
        string model,
        TimeSpan timeout,
        string keepAlive,
        int numPredict,
        int numCtx,
        int numThread,
        int numBatch)
    {
        _model = model;
        _keepAlive = string.IsNullOrWhiteSpace(keepAlive) ? "30m" : keepAlive.Trim();
        _numPredict = numPredict;
        _numCtx = numCtx;
        _numThread = numThread;
        _numBatch = numBatch;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = timeout
        };
    }

    public async Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = AiResolverFactory.BuildAiPrompt(request);

        var payload = new
        {
            model = _model,
            prompt,
            stream = false,
            format = "json",
            keep_alive = _keepAlive,
            options = new
            {
                num_predict = _numPredict,
                num_ctx = _numCtx,
                num_thread = _numThread,
                num_batch = _numBatch,
                temperature = 0.1,
                top_p = 0.9
            },
            // extra_body = new
            // {
            //     enable_thinking = false
            // }
        };

        using var response = await _httpClient.PostAsJsonAsync("api/generate", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AiProviderResponse(
                new Dictionary<string, AiFieldCandidate>(StringComparer.OrdinalIgnoreCase),
                $"ollama:{_model}",
                prompt,
                $"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var rootDoc = JsonDocument.Parse(body);
        if (!rootDoc.RootElement.TryGetProperty("response", out var responseNode))
        {
            return null;
        }

        var responseText = responseNode.GetString();
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var normalizedResponseText = AiResolverFactory.NormalizeModelResponse(responseText);
        var candidates = AiResolverFactory.ParseAiCandidates(normalizedResponseText);
        return new AiProviderResponse(candidates, $"ollama:{_model}", prompt, normalizedResponseText);
    }
}

internal sealed class OpenVinoAiFieldResolver : IAiFieldResolver
{
    private readonly HttpClient _httpClient;
    private readonly int _maxNewTokens;

    public OpenVinoAiFieldResolver(string endpoint, TimeSpan timeout, int maxNewTokens)
    {
        _maxNewTokens = maxNewTokens;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = timeout
        };
    }

    public async Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = AiResolverFactory.BuildAiPrompt(request);

        var payload = new
        {
            prompt,
            max_new_tokens = _maxNewTokens,
            temperature = 0.1
        };

        using var response = await _httpClient.PostAsJsonAsync("ai/generate", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AiProviderResponse(
                new Dictionary<string, AiFieldCandidate>(StringComparer.OrdinalIgnoreCase),
                "openvino",
                prompt,
                $"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var rootDoc = JsonDocument.Parse(body);
        if (!rootDoc.RootElement.TryGetProperty("response", out var responseNode))
        {
            return null;
        }

        var responseText = responseNode.GetString();
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var normalizedResponseText = AiResolverFactory.NormalizeModelResponse(responseText);
        var candidates = AiResolverFactory.ParseAiCandidates(normalizedResponseText);
        return new AiProviderResponse(candidates, "openvino", prompt, normalizedResponseText);
    }
}

internal sealed class NoopAiFieldResolver : IAiFieldResolver
{
    public Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<AiProviderResponse?>(null);
    }
}

internal sealed class BailianAiFieldResolver : IAiFieldResolver
{
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public BailianAiFieldResolver(string endpoint, string model, string apiKey, TimeSpan timeout)
    {
        _model = model;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = timeout
        };
        _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public async Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = AiResolverFactory.BuildAiPrompt(request);
        var payload = new
        {
            model = _model,
            messages = new[]
            {
                new { role = "user", content = prompt }
            },
            stream = false,
            temperature = 0.1,
            top_p = 0.9
        };

        using var response = await _httpClient.PostAsJsonAsync("chat/completions", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            return new AiProviderResponse(
                new Dictionary<string, AiFieldCandidate>(StringComparer.OrdinalIgnoreCase),
                $"bailian:{_model}",
                prompt,
                $"HTTP {(int)response.StatusCode}: {errorBody}");
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        using var rootDoc = JsonDocument.Parse(body);
        if (!rootDoc.RootElement.TryGetProperty("choices", out var choicesNode) ||
            choicesNode.ValueKind != JsonValueKind.Array ||
            choicesNode.GetArrayLength() == 0)
        {
            return null;
        }

        var firstChoice = choicesNode[0];
        if (!firstChoice.TryGetProperty("message", out var messageNode) ||
            !messageNode.TryGetProperty("content", out var contentNode))
        {
            return null;
        }

        var responseText = contentNode.ValueKind == JsonValueKind.String
            ? contentNode.GetString()
            : contentNode.ToString();

        if (string.IsNullOrWhiteSpace(responseText))
        {
            return null;
        }

        var normalizedResponseText = AiResolverFactory.NormalizeModelResponse(responseText);
        var candidates = AiResolverFactory.ParseAiCandidates(normalizedResponseText);
        return new AiProviderResponse(candidates, $"bailian:{_model}", prompt, normalizedResponseText);
    }
}

// A runnable local fallback provider that mimics AI-style field suggestion with confidence/evidence output.
internal sealed class MockAiFieldResolver : IAiFieldResolver
{
    public Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var map = new Dictionary<string, AiFieldCandidate>(StringComparer.OrdinalIgnoreCase);
        var text = request.Text ?? string.Empty;
        var barcodeValues = request.Barcodes.Select(x => x.Value).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();

        var modelBarcode = barcodeValues.FirstOrDefault(v => Regex.IsMatch(v, "(KEMC[0-9A-Z]+|EEHZE[0-9A-Z]+)", RegexOptions.IgnoreCase));
        if (!string.IsNullOrWhiteSpace(modelBarcode))
        {
            var m = Regex.Match(modelBarcode, "(KEMC[0-9A-Z]+|EEHZE[0-9A-Z]+)", RegexOptions.IgnoreCase);
            if (m.Success)
            {
                map["MPN"] = new AiFieldCandidate(m.Value, 0.86, "mock-ai: barcode model token");
                map["PartNumber"] = new AiFieldCandidate(m.Value, 0.82, "mock-ai: barcode model token");
            }
        }

        var qtyText = Regex.Match(text, "(?:QTY|Quantity|数量)\\s*[:：]?\\s*([0-9]{1,6})", RegexOptions.IgnoreCase);
        if (qtyText.Success)
        {
            map["Quantity"] = new AiFieldCandidate(qtyText.Groups[1].Value, 0.9, "mock-ai: text quantity regex");
        }
        else
        {
            var qtyBarcode = barcodeValues.Select(v => Regex.Match(v, "\\b([0-9]{3,6})\\b")).FirstOrDefault(x => x.Success);
            if (qtyBarcode is not null && qtyBarcode.Success)
            {
                map["Quantity"] = new AiFieldCandidate(qtyBarcode.Groups[1].Value, 0.72, "mock-ai: barcode numeric fallback");
            }
        }

        var lotText = Regex.Match(text, "(?:Lot\\s*No|LotNo|Serial\\s*No|批次号|序列号)\\s*[:：]?\\s*([A-Z0-9\\-_/]+)", RegexOptions.IgnoreCase);
        if (lotText.Success)
        {
            map["LotNo"] = new AiFieldCandidate(lotText.Groups[1].Value, 0.78, "mock-ai: text lot regex");
        }

        var poBarcode = barcodeValues.FirstOrDefault(v => Regex.IsMatch(v, "^[0-9]{10}$"));
        if (!string.IsNullOrWhiteSpace(poBarcode))
        {
            map["PO"] = new AiFieldCandidate(poBarcode, 0.84, "mock-ai: 10-digit barcode");
        }

        var huIdBarcode = barcodeValues.FirstOrDefault(v => Regex.IsMatch(v, "^[0-9]{9}$"));
        if (!string.IsNullOrWhiteSpace(huIdBarcode))
        {
            map["HuId"] = new AiFieldCandidate(huIdBarcode, 0.84, "mock-ai: 9-digit barcode");
        }

        // Brand: 从条码前缀或 OCR 文本推断品牌
        var brandText = Regex.Match(text, @"\b(KEMET|Panasonic|Murata|Samsung|TDK|Vishay|Yageo|Bourns|Molex)\b", RegexOptions.IgnoreCase);
        if (brandText.Success)
        {
            map["Brand"] = new AiFieldCandidate(brandText.Value, 0.88, "mock-ai: brand keyword in OCR text");
        }
        else if (barcodeValues.Any(v => Regex.IsMatch(v, "^KEMC", RegexOptions.IgnoreCase)))
        {
            map["Brand"] = new AiFieldCandidate("KEMET", 0.82, "mock-ai: KEMC barcode prefix → KEMET");
        }
        else if (barcodeValues.Any(v => Regex.IsMatch(v, "^EEHZE", RegexOptions.IgnoreCase)))
        {
            map["Brand"] = new AiFieldCandidate("Panasonic", 0.82, "mock-ai: EEHZE barcode prefix → Panasonic");
        }

        if (map.Count == 0)
        {
            return Task.FromResult<AiProviderResponse?>(null);
        }

        return Task.FromResult<AiProviderResponse?>(new AiProviderResponse(map, "mock-ai-v1", null, JsonSerializer.Serialize(map)));
    }
}
