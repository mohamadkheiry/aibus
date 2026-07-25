using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;

namespace AiBus.Api;

public sealed class GatewayService(AppDbContext db, ApiKeyAuthenticator auth, SecretProtector secrets, IHttpClientFactory clients, ILogger<GatewayService> logger)
{
    public async Task ForwardRealtime(HttpContext context, CancellationToken ct)
    {
        if (!context.WebSockets.IsWebSocketRequest) { context.Response.StatusCode = 400; return; }
        var authenticated = await auth.Authenticate(context.Request, ct);
        if (authenticated is null) { context.Response.StatusCode = 401; return; }
        var (user, userKey) = authenticated.Value;
        var modelName = context.Request.Query["model"].ToString();
        var model = await db.Models.Include(x => x.Provider).SingleOrDefaultAsync(x => x.ModelId == modelName && x.IsActive && x.SupportsWebSocket && x.Provider!.IsActive, ct);
        if (model?.Provider is null || !HasModelAccess(userKey, modelName) || user.WalletUsd <= 0) { context.Response.StatusCode = 403; return; }
        var credential = await db.ProviderCredentials.Where(x => x.ProviderId == model.ProviderId && x.IsActive).OrderByDescending(x => x.RemainingBalanceUsd).FirstOrDefaultAsync(ct);
        if (credential is null) { context.Response.StatusCode = 503; return; }

        var baseUrl = model.Provider.BaseUrl.TrimEnd('/').Replace("https://", "wss://", StringComparison.OrdinalIgnoreCase).Replace("http://", "ws://", StringComparison.OrdinalIgnoreCase);
        var upstreamUrl = new Uri($"{baseUrl}/realtime?model={Uri.EscapeDataString(model.ModelId)}");
        using var upstream = new ClientWebSocket();
        upstream.Options.SetRequestHeader("Authorization", $"Bearer {secrets.Unprotect(credential.ProtectedApiKey)}");
        upstream.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1");
        try { await upstream.ConnectAsync(upstreamUrl, ct); }
        catch (Exception ex) { credential.LastError = ex.Message[..Math.Min(500, ex.Message.Length)]; await db.SaveChangesAsync(ct); context.Response.StatusCode = 502; return; }
        using var downstream = await context.WebSockets.AcceptWebSocketAsync();
        long inputTokens = 0, outputTokens = 0;
        var started = Stopwatch.StartNew();
        var downToUp = Relay(downstream, upstream, null, ct);
        var upToDown = Relay(upstream, downstream, text => ParseRealtimeUsage(text, ref inputTokens, ref outputTokens), ct);
        await Task.WhenAny(downToUp, upToDown);
        try { if (downstream.State == WebSocketState.Open) await downstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        try { if (upstream.State == WebSocketState.Open) await upstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        var cost = inputTokens / 1_000_000m * model.InputPricePerMillionUsd + outputTokens / 1_000_000m * model.OutputPricePerMillionUsd;
        await Record(user, userKey, model, credential, inputTokens, outputTokens, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, CancellationToken.None);
    }

    public async Task ForwardChat(HttpContext context, CancellationToken ct)
    {
        var authenticated = await auth.Authenticate(context.Request, ct);
        if (authenticated is null) { await WriteError(context, 401, "invalid_api_key", "کلید API نامعتبر یا غیرفعال است."); return; }
        var (user, userKey) = authenticated.Value;

        JsonDocument body;
        try { body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct); }
        catch { await WriteError(context, 400, "invalid_request", "بدنه JSON معتبر نیست."); return; }
        using (body)
        {
            if (!body.RootElement.TryGetProperty("model", out var modelProperty) || string.IsNullOrWhiteSpace(modelProperty.GetString()))
            { await WriteError(context, 400, "model_required", "فیلد model الزامی است."); return; }
            var modelName = modelProperty.GetString()!;
            var model = await db.Models.Include(x => x.Provider).SingleOrDefaultAsync(x => x.ModelId == modelName && x.IsActive && x.Provider!.IsActive, ct);
            if (model?.Provider is null) { await WriteError(context, 404, "model_not_found", "مدل فعال پیدا نشد."); return; }

            if (!HasModelAccess(userKey, modelName)) { await WriteError(context, 403, "model_denied", "این کلید به مدل انتخابی دسترسی ندارد."); return; }
            if (user.WalletUsd <= 0) { await WriteError(context, 402, "insufficient_balance", "موجودی کیف پول کافی نیست."); return; }
            if (userKey.RequestLimit.HasValue && userKey.RequestCount >= userKey.RequestLimit.Value) { await WriteError(context, 429, "request_limit", "سقف تعداد درخواست این کلید تمام شده است."); return; }
            if (userKey.SpendLimitUsd.HasValue && userKey.SpentUsd >= userKey.SpendLimitUsd.Value) { await WriteError(context, 429, "spend_limit", "سقف هزینه این کلید تمام شده است."); return; }

            var credentials = await db.ProviderCredentials.Where(x => x.ProviderId == model.ProviderId && x.IsActive)
                .OrderByDescending(x => x.RemainingBalanceUsd > x.AlertThresholdUsd).ThenBy(x => x.LastUsedAtUtc).ToListAsync(ct);
            if (credentials.Count == 0) { await WriteError(context, 503, "provider_unavailable", "کلید فعالی برای ارائه‌دهنده تنظیم نشده است."); return; }

            var stream = body.RootElement.TryGetProperty("stream", out var streamProperty) && streamProperty.ValueKind == JsonValueKind.True;
            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage? upstream = null;
            ProviderCredential? selected = null;
            foreach (var credential in credentials)
            {
                try
                {
                    upstream = await Send(model.Provider, credential, body.RootElement, stream, ct);
                    selected = credential;
                    if (upstream.IsSuccessStatusCode) break;
                    var errorBody = $"{(int)upstream.StatusCode}: {await upstream.Content.ReadAsStringAsync(ct)}";
                    credential.LastError = errorBody[..Math.Min(500, errorBody.Length)];
                    if (upstream.StatusCode is not (HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests)) break;
                    upstream.Dispose(); upstream = null;
                }
                catch (Exception ex) { credential.LastError = ex.Message[..Math.Min(500, ex.Message.Length)]; logger.LogWarning(ex, "Provider key {CredentialId} failed", credential.Id); }
            }
            if (upstream is null || selected is null) { await db.SaveChangesAsync(ct); await WriteError(context, 503, "provider_unavailable", "همه مسیرهای ارائه‌دهنده ناموفق بودند."); return; }
            using (upstream)
            {
                if (!upstream.IsSuccessStatusCode)
                {
                    context.Response.StatusCode = (int)upstream.StatusCode;
                    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                    await upstream.Content.CopyToAsync(context.Response.Body, ct);
                    await Record(user, userKey, model, selected, 0, 0, 0, stopwatch.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct);
                    return;
                }

                long inputTokens = 0, outputTokens = 0;
                if (stream && model.Provider.Protocol == "openai")
                {
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "text/event-stream; charset=utf-8";
                    context.Response.Headers.CacheControl = "no-cache";
                    await using var source = await upstream.Content.ReadAsStreamAsync(ct);
                    using var reader = new StreamReader(source);
                    while (!reader.EndOfStream && !ct.IsCancellationRequested)
                    {
                        var line = await reader.ReadLineAsync(ct) ?? "";
                        await context.Response.WriteAsync(line + "\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                        if (line.StartsWith("data: ") && line[6..] != "[DONE]") ParseUsage(line[6..], ref inputTokens, ref outputTokens);
                    }
                }
                else
                {
                    var responseText = await upstream.Content.ReadAsStringAsync(ct);
                    ParseUsage(responseText, ref inputTokens, ref outputTokens, model.Provider.Protocol);
                    if (model.Provider.Protocol == "anthropic") responseText = ConvertAnthropicResponse(responseText, model.ModelId);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(responseText, ct);
                }
                var cost = inputTokens / 1_000_000m * model.InputPricePerMillionUsd + outputTokens / 1_000_000m * model.OutputPricePerMillionUsd;
                await Record(user, userKey, model, selected, inputTokens, outputTokens, cost, stopwatch.ElapsedMilliseconds, "success", context.TraceIdentifier, ct);
            }
        }
    }

    private async Task<HttpResponseMessage> Send(AiProvider provider, ProviderCredential credential, JsonElement body, bool stream, CancellationToken ct)
    {
        var client = clients.CreateClient("providers");
        var url = provider.BaseUrl.TrimEnd('/') + (provider.Protocol == "anthropic" ? "/messages" : "/chat/completions");
        var request = new HttpRequestMessage(HttpMethod.Post, url);
        var apiKey = secrets.Unprotect(credential.ProtectedApiKey);
        if (provider.Protocol == "anthropic")
        {
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Content = new StringContent(ConvertToAnthropicRequest(body, stream), Encoding.UTF8, "application/json");
        }
        else
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
            var payload = body.GetRawText();
            if (stream)
            {
                var node = JsonNode.Parse(payload)!.AsObject();
                node["stream_options"] = new JsonObject { ["include_usage"] = true };
                payload = node.ToJsonString();
            }
            request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        }
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task Record(AppUser user, UserApiKey key, AiModel model, ProviderCredential credential, long input, long output, decimal cost, long durationMs, string status, string traceId, CancellationToken ct)
    {
        if (status == "success")
        {
            user.WalletUsd = Math.Max(0, user.WalletUsd - cost);
            key.RequestCount++;
            key.SpentUsd += cost;
            key.LastUsedAtUtc = DateTime.UtcNow;
            credential.RequestCount++;
            credential.LastUsedAtUtc = DateTime.UtcNow;
            credential.LastError = null;
            if (credential.InitialBalanceUsd > 0) credential.RemainingBalanceUsd = Math.Max(0, credential.RemainingBalanceUsd - cost);
        }
        db.UsageRecords.Add(new UsageRecord { UserId = user.Id, UserApiKeyId = key.Id, ModelId = model.Id, ProviderCredentialId = credential.Id, ModelName = model.ModelId, ProviderName = model.Provider!.Name, InputTokens = input, OutputTokens = output, CostUsd = cost, DurationMs = (int)Math.Min(int.MaxValue, durationMs), Status = status, TraceId = traceId });
        await db.SaveChangesAsync(ct);
    }

    private static bool HasModelAccess(UserApiKey key, string model)
    {
        string[] rules;
        try { rules = JsonSerializer.Deserialize<string[]>(key.ModelRulesJson) ?? []; } catch { rules = []; }
        return key.AccessMode switch { "allow" => rules.Contains(model), "deny" => !rules.Contains(model), _ => true };
    }

    private static async Task Relay(WebSocket source, WebSocket destination, Action<string>? inspect, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (!ct.IsCancellationRequested && source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await source.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close) return;
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var bytes = message.ToArray();
            if (result.MessageType == WebSocketMessageType.Text && inspect is not null) inspect(Encoding.UTF8.GetString(bytes));
            await destination.SendAsync(bytes, result.MessageType, true, ct);
        }
    }

    private static void ParseRealtimeUsage(string json, ref long input, ref long output)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("usage", out var usage)) return;
            if (usage.TryGetProperty("input_tokens", out var i)) input += i.GetInt64();
            if (usage.TryGetProperty("output_tokens", out var o)) output += o.GetInt64();
        }
        catch { }
    }

    private static void ParseUsage(string json, ref long input, ref long output, string protocol = "openai")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("usage", out var usage)) return;
            if (protocol == "anthropic")
            {
                if (usage.TryGetProperty("input_tokens", out var i)) input = i.GetInt64();
                if (usage.TryGetProperty("output_tokens", out var o)) output = o.GetInt64();
            }
            else
            {
                if (usage.TryGetProperty("prompt_tokens", out var i)) input = i.GetInt64();
                if (usage.TryGetProperty("completion_tokens", out var o)) output = o.GetInt64();
            }
        }
        catch { }
    }

    private static string ConvertToAnthropicRequest(JsonElement input, bool stream)
    {
        var model = input.GetProperty("model").GetString();
        var messages = input.GetProperty("messages");
        var system = messages.EnumerateArray().FirstOrDefault(x => x.TryGetProperty("role", out var r) && r.GetString() == "system");
        var filtered = messages.EnumerateArray().Where(x => !x.TryGetProperty("role", out var r) || r.GetString() != "system").Select(x => JsonSerializer.Deserialize<object>(x.GetRawText())).ToArray();
        return JsonSerializer.Serialize(new { model, messages = filtered, system = system.ValueKind == JsonValueKind.Object && system.TryGetProperty("content", out var c) ? c.GetString() : null, max_tokens = input.TryGetProperty("max_tokens", out var mt) ? mt.GetInt32() : 4096, stream });
    }

    private static string ConvertAnthropicResponse(string input, string model)
    {
        using var doc = JsonDocument.Parse(input);
        var root = doc.RootElement;
        var text = root.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array
            ? string.Concat(content.EnumerateArray().Where(x => x.TryGetProperty("text", out _)).Select(x => x.GetProperty("text").GetString())) : "";
        var inputTokens = root.TryGetProperty("usage", out var usage) && usage.TryGetProperty("input_tokens", out var i) ? i.GetInt64() : 0;
        var outputTokens = root.TryGetProperty("usage", out usage) && usage.TryGetProperty("output_tokens", out var o) ? o.GetInt64() : 0;
        return JsonSerializer.Serialize(new { id = root.TryGetProperty("id", out var id) ? id.GetString() : Guid.NewGuid().ToString(), @object = "chat.completion", created = DateTimeOffset.UtcNow.ToUnixTimeSeconds(), model, choices = new[] { new { index = 0, message = new { role = "assistant", content = text }, finish_reason = "stop" } }, usage = new { prompt_tokens = inputTokens, completion_tokens = outputTokens, total_tokens = inputTokens + outputTokens } });
    }

    private static async Task WriteError(HttpContext context, int status, string code, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsJsonAsync(new { error = new { message, type = code, code } });
    }
}
