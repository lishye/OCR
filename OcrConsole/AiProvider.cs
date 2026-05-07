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

internal sealed record AiProviderResponse(IReadOnlyDictionary<string, AiFieldCandidate> Candidates, string ProviderName);

internal interface IAiFieldResolver
{
    Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default);
}

internal static class AiResolverFactory
{
    public static IAiFieldResolver CreateFromConfig(string? providerOverride = null)
    {
        var enabled = ParseBool(ConfigurationManager.AppSettings["AiFallbackEnabled"], false);
        if (!enabled)
        {
            return new NoopAiFieldResolver();
        }

        var provider = (providerOverride ?? ConfigurationManager.AppSettings["AiProvider"] ?? "ollama").Trim();
        if (provider.Equals("mock", StringComparison.OrdinalIgnoreCase))
        {
            return new MockAiFieldResolver();
        }

        if (provider.Equals("ollama", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = ConfigurationManager.AppSettings["OllamaEndpoint"] ?? "http://127.0.0.1:11434";
            var model = ConfigurationManager.AppSettings["OllamaModel"] ?? "qwen2.5:7b";
            var timeoutSeconds = ParseInt(ConfigurationManager.AppSettings["OllamaTimeoutSeconds"], 20);

            return new OllamaAiFieldResolver(endpoint, model, TimeSpan.FromSeconds(Math.Max(5, timeoutSeconds)));
        }

        if (provider.Equals("bailian", StringComparison.OrdinalIgnoreCase))
        {
            var endpoint = ConfigurationManager.AppSettings["BailianEndpoint"] ?? "https://dashscope.aliyuncs.com/compatible-mode/v1";
            var model = ConfigurationManager.AppSettings["BailianModel"] ?? "qwen-plus";
            var apiKey = ConfigurationManager.AppSettings["BailianApiKey"] ?? string.Empty;
            var timeoutSeconds = ParseInt(ConfigurationManager.AppSettings["BailianTimeoutSeconds"], 20);

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

    private static bool ParseBool(string? text, bool defaultValue)
    {
        if (bool.TryParse(text, out var parsed)) return parsed;
        return defaultValue;
    }

    private static int ParseInt(string? text, int defaultValue)
    {
        if (int.TryParse(text, out var parsed)) return parsed;
        return defaultValue;
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

internal sealed class OllamaAiFieldResolver : IAiFieldResolver
{
    private readonly string _model;
    private readonly HttpClient _httpClient;

    public OllamaAiFieldResolver(string endpoint, string model, TimeSpan timeout)
    {
        _model = model;
        _httpClient = new HttpClient
        {
            BaseAddress = new Uri(endpoint.TrimEnd('/') + "/"),
            Timeout = timeout
        };
    }

    public async Task<AiProviderResponse?> ResolveAsync(AiProviderRequest request, CancellationToken cancellationToken = default)
    {
        var prompt = BuildPrompt(request);

        var payload = new
        {
            model = _model,
            prompt,
            stream = false,
            format = "json",
            options = new
            {
                temperature = 0.1,
                top_p = 0.9
            }
        };

        using var response = await _httpClient.PostAsJsonAsync("api/generate", payload, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
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

        var candidates = ParseCandidates(responseText);
        if (candidates.Count == 0)
        {
            return null;
        }

        return new AiProviderResponse(candidates, $"ollama:{_model}");
    }

    private static string BuildPrompt(AiProviderRequest request)
    {
        var barcodeText = string.Join(" | ", request.Barcodes.Select(b => b.Value));
        var aliRawText = request.AliRawStructuredFields is null
            ? "{}"
            : JsonSerializer.Serialize(request.AliRawStructuredFields);

                var sb = new StringBuilder();
                sb.AppendLine("You are an OCR post-processing assistant.");
                sb.AppendLine("Extract fields from OCR and barcode data.");
                sb.AppendLine("Return STRICT JSON only. No markdown.");
                sb.AppendLine();
                sb.AppendLine("Output schema:");
                sb.AppendLine("{");
                sb.AppendLine("  \"candidates\": {");
                sb.AppendLine("    \"PartNumber\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
                sb.AppendLine("    \"MPN\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
                sb.AppendLine("    \"Quantity\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
                sb.AppendLine("    \"LotNo\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
                sb.AppendLine("    \"Brand\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
                sb.AppendLine("    \"PO\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
                sb.AppendLine("    \"HuId\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"}");
                sb.AppendLine("  }");
                sb.AppendLine("}");
                sb.AppendLine();
                sb.AppendLine("Rules:");
                sb.AppendLine("- Quantity should be numeric only when possible.");
                sb.AppendLine("- Confidence range is 0..1.");
                sb.AppendLine("- Only fill fields you are reasonably sure about.");
                sb.AppendLine();
                sb.AppendLine("OCR text:");
                sb.AppendLine(request.Text);
                sb.AppendLine();
                sb.AppendLine("Barcode values:");
                sb.AppendLine(barcodeText);
                sb.AppendLine();
                sb.AppendLine("Ali structured fields:");
                sb.AppendLine(aliRawText);
                sb.AppendLine();
                sb.AppendLine("Current fields:");
                sb.AppendLine(JsonSerializer.Serialize(request.CurrentFields));

                return sb.ToString();
    }

    private static Dictionary<string, AiFieldCandidate> ParseCandidates(string responseText)
    {
        var map = new Dictionary<string, AiFieldCandidate>(StringComparer.OrdinalIgnoreCase);

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(responseText);
        }
        catch
        {
            var trimmed = responseText.Trim();
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var raw = trimmed[start..(end + 1)];
                try
                {
                    doc = JsonDocument.Parse(raw);
                }
                catch
                {
                    return map;
                }
            }
            else
            {
                return map;
            }
        }

        using (doc)
        {
            if (doc is null) return map;

            if (!doc.RootElement.TryGetProperty("candidates", out var candidatesNode) || candidatesNode.ValueKind != JsonValueKind.Object)
            {
                return map;
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

        return map;
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
        var prompt = BuildPrompt(request);
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
            return null;
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

        var candidates = ParseCandidates(responseText);
        if (candidates.Count == 0)
        {
            return null;
        }

        return new AiProviderResponse(candidates, $"bailian:{_model}");
    }

    private static string BuildPrompt(AiProviderRequest request)
    {
        var barcodeText = string.Join(" | ", request.Barcodes.Select(b => b.Value));
        var aliRawText = request.AliRawStructuredFields is null
            ? "{}"
            : JsonSerializer.Serialize(request.AliRawStructuredFields);

        var sb = new StringBuilder();
        sb.AppendLine("You are an OCR post-processing assistant.");
        sb.AppendLine("Extract fields from OCR and barcode data.");
        sb.AppendLine("Return STRICT JSON only. No markdown.");
        sb.AppendLine();
        sb.AppendLine("Output schema:");
        sb.AppendLine("{");
        sb.AppendLine("  \"candidates\": {");
        sb.AppendLine("    \"PartNumber\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
        sb.AppendLine("    \"MPN\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
        sb.AppendLine("    \"Quantity\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
        sb.AppendLine("    \"LotNo\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
        sb.AppendLine("    \"Brand\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
        sb.AppendLine("    \"PO\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"},");
        sb.AppendLine("    \"HuId\": {\"value\":\"\",\"confidence\":0.0,\"evidence\":\"\"}");
        sb.AppendLine("  }");
        sb.AppendLine("}");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- Quantity should be numeric only when possible.");
        sb.AppendLine("- Confidence range is 0..1.");
        sb.AppendLine("- Only fill fields you are reasonably sure about.");
        sb.AppendLine();
        sb.AppendLine("OCR text:");
        sb.AppendLine(request.Text);
        sb.AppendLine();
        sb.AppendLine("Barcode values:");
        sb.AppendLine(barcodeText);
        sb.AppendLine();
        sb.AppendLine("Ali structured fields:");
        sb.AppendLine(aliRawText);
        sb.AppendLine();
        sb.AppendLine("Current fields:");
        sb.AppendLine(JsonSerializer.Serialize(request.CurrentFields));

        return sb.ToString();
    }

    private static Dictionary<string, AiFieldCandidate> ParseCandidates(string responseText)
    {
        var map = new Dictionary<string, AiFieldCandidate>(StringComparer.OrdinalIgnoreCase);

        JsonDocument? doc = null;
        try
        {
            doc = JsonDocument.Parse(responseText);
        }
        catch
        {
            var trimmed = responseText.Trim();
            var start = trimmed.IndexOf('{');
            var end = trimmed.LastIndexOf('}');
            if (start >= 0 && end > start)
            {
                var raw = trimmed[start..(end + 1)];
                try
                {
                    doc = JsonDocument.Parse(raw);
                }
                catch
                {
                    return map;
                }
            }
            else
            {
                return map;
            }
        }

        using (doc)
        {
            if (doc is null) return map;

            if (!doc.RootElement.TryGetProperty("candidates", out var candidatesNode) || candidatesNode.ValueKind != JsonValueKind.Object)
            {
                return map;
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

        return map;
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

        return Task.FromResult<AiProviderResponse?>(new AiProviderResponse(map, "mock-ai-v1"));
    }
}
