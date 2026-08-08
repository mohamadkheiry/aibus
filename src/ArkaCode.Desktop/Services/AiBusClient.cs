using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace ArkaCode.Services;

public sealed class AiBusClient
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AiModel>> GetModelsAsync(string baseUrl, string apiKey, CancellationToken ct)
    {
        using var request = CreateRequest(HttpMethod.Get, $"{Normalize(baseUrl)}/v1/models", apiKey);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.GetProperty("data").EnumerateArray()
            .Select(item => new AiModel(
                item.GetProperty("id").GetString() ?? "",
                item.TryGetProperty("display_name", out var display) ? display.GetString() ?? "" : item.GetProperty("id").GetString() ?? "",
                item.TryGetProperty("owned_by", out var owner) ? owner.GetString() ?? "AiBus" : "AiBus",
                item.TryGetProperty("endpoint_path", out var endpoint) ? endpoint.GetString() ?? "/v1/chat/completions" : "/v1/chat/completions"))
            .Where(model => !string.IsNullOrWhiteSpace(model.Id) && IsAgentEndpoint(model.EndpointPath))
            .OrderBy(model => model.Provider).ThenBy(model => model.DisplayName)
            .ToList();
    }

    private static bool IsAgentEndpoint(string endpointPath) =>
        endpointPath.TrimEnd('/').EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase) ||
        endpointPath.TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase);

    public async Task<AgentResult> RunAgentAsync(string baseUrl, string apiKey, AiModel model, string effort, string task, IReadOnlyList<WorkspaceFile> files, CancellationToken ct)
    {
        var system = """
You are ArkaCode, a careful senior software-engineering agent. Analyze the supplied workspace and task. Return ONLY valid JSON with this shape:
{"summary":"short result","explanation":"concise technical explanation","operations":[{"path":"relative/path","action":"write","content":"complete file content"}]}
Never use absolute paths, never use ../, and include only files that must change. Keep existing behavior unless the task asks otherwise. The user will review every operation before it is applied.
""";
        var context = string.Join("\n\n", files.Select(file => $"--- FILE: {file.RelativePath} ---\n{file.Content}"));
        var prompt = $"Reasoning level: {effort}\n\nUSER TASK:\n{task}\n\nWORKSPACE SNAPSHOT:\n{context}";
        var usesResponses = model.EndpointPath.TrimEnd('/').EndsWith("/responses", StringComparison.OrdinalIgnoreCase);
        object payload = usesResponses
            ? new { model = model.Id, input = new[] { new { role = "system", content = system }, new { role = "user", content = prompt } }, reasoning = new { effort }, stream = false }
            : new { model = model.Id, messages = new[] { new { role = "system", content = system }, new { role = "user", content = prompt } }, stream = false };
        using var request = CreateRequest(HttpMethod.Post, $"{Normalize(baseUrl)}{(usesResponses ? "/v1/responses" : "/v1/chat/completions")}", apiKey);
        request.Content = JsonContent.Create(payload, options: JsonOptions);
        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);
        EnsureSuccess(response, body);
        var text = ExtractText(body);
        var clean = StripFence(text);
        try
        {
            using var doc = JsonDocument.Parse(clean);
            var root = doc.RootElement;
            var operations = root.TryGetProperty("operations", out var ops)
                ? ops.EnumerateArray().Select(op => new FileOperation(
                    op.GetProperty("path").GetString() ?? "",
                    op.GetProperty("content").GetString() ?? "",
                    op.TryGetProperty("action", out var action) ? action.GetString() ?? "write" : "write")).ToList()
                : [];
            return new AgentResult(root.TryGetProperty("summary", out var summary) ? summary.GetString() ?? "Agent completed" : "Agent completed", root.TryGetProperty("explanation", out var explanation) ? explanation.GetString() ?? "" : "", operations, text);
        }
        catch (JsonException)
        {
            return new AgentResult("پاسخ عامل دریافت شد", text, [], text);
        }
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string apiKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
        request.Headers.UserAgent.ParseAdd("ArkaCode/1.0");
        return request;
    }

    private static string Normalize(string value) => value.Trim().TrimEnd('/');

    private static void EnsureSuccess(HttpResponseMessage response, string body)
    {
        if (response.IsSuccessStatusCode) return;
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var message = root.TryGetProperty("error", out var error) && error.TryGetProperty("message", out var nested)
                ? nested.GetString()
                : root.TryGetProperty("message", out var direct) ? direct.GetString() : null;
            throw new InvalidOperationException(message ?? $"AiBus request failed ({(int)response.StatusCode}).");
        }
        catch (JsonException) { throw new InvalidOperationException($"AiBus request failed ({(int)response.StatusCode})."); }
    }

    internal static string ExtractText(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (root.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
        {
            var choice = choices[0];
            if (choice.TryGetProperty("message", out var message) && message.TryGetProperty("content", out var content)) return ContentText(content);
        }
        if (root.TryGetProperty("output_text", out var direct)) return direct.GetString() ?? "";
        if (root.TryGetProperty("output", out var output))
            return string.Join("\n", output.EnumerateArray().SelectMany(item => item.TryGetProperty("content", out var content) ? content.EnumerateArray() : []).Select(part => part.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "").Where(text => text.Length > 0));
        return body;
    }

    private static string ContentText(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => content.GetString() ?? "",
        JsonValueKind.Array => string.Join("\n", content.EnumerateArray().Select(item => item.TryGetProperty("text", out var text) ? text.GetString() ?? "" : "").Where(text => text.Length > 0)),
        _ => ""
    };

    private static string StripFence(string text)
    {
        var value = text.Trim();
        if (!value.StartsWith("```", StringComparison.Ordinal)) return value;
        var firstLine = value.IndexOf('\n');
        var lastFence = value.LastIndexOf("```", StringComparison.Ordinal);
        return firstLine >= 0 && lastFence > firstLine ? value[(firstLine + 1)..lastFence].Trim() : value;
    }
}
