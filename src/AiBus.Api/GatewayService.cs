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
        if (model?.Provider is null) { context.Response.StatusCode = 404; return; }
        if (!await CheckAccess(context, user, userKey, model, ct)) return;
        var credential = await db.ProviderCredentials.Where(x => x.ProviderId == model.ProviderId && x.IsActive).OrderByDescending(x => (double)x.RemainingBalanceUsd).FirstOrDefaultAsync(ct);
        if (credential is null) { context.Response.StatusCode = 503; return; }

        // OpenAI transcription sockets use intent=transcription without a URL model. The
        // selected transcription model is configured inside session.update and billed here.
        IReadOnlyDictionary<string, string> realtimeQuery = new Dictionary<string, string>
        {
            [model.ServiceType == "speech_to_text" ? "intent" : "model"] =
                model.ServiceType == "speech_to_text" ? "transcription" : model.ModelId
        };
        var upstreamUrl = BuildUpstreamUri(model.Provider, model, true, realtimeQuery);
        using var upstream = new ClientWebSocket();
        ConfigureWebSocketAuthentication(upstream, model.Provider, secrets.Unprotect(credential.ProtectedApiKey));
        try { await upstream.ConnectAsync(upstreamUrl, ct); }
        catch (Exception ex) { credential.LastError = ex.Message[..Math.Min(500, ex.Message.Length)]; await db.SaveChangesAsync(ct); context.Response.StatusCode = 502; return; }
        var requestedProtocols = context.Request.Headers.SecWebSocketProtocol.ToString();
        using var downstream = requestedProtocols.Contains("aibus-realtime", StringComparison.Ordinal)
            ? await context.WebSockets.AcceptWebSocketAsync("aibus-realtime")
            : await context.WebSockets.AcceptWebSocketAsync();
        long inputTokens = 0, outputTokens = 0, inputAudioTokens = 0, outputAudioTokens = 0;
        var started = Stopwatch.StartNew();
        var downToUp = Relay(downstream, upstream, null, ct);
        var upToDown = Relay(upstream, downstream, text => ParseRealtimeUsage(text, ref inputTokens, ref outputTokens, ref inputAudioTokens, ref outputAudioTokens), ct);
        await Task.WhenAny(downToUp, upToDown);
        try { if (downstream.State == WebSocketState.Open) await downstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        try { if (upstream.State == WebSocketState.Open) await upstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        var cost = EstimateRealtimeCost(model, inputTokens, outputTokens, inputAudioTokens, outputAudioTokens);
        await Record(user, userKey, model, credential, inputTokens, outputTokens, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, CancellationToken.None);
    }

    public async Task ForwardSpeech(HttpContext context, CancellationToken ct)
    {
        var authenticated = await auth.Authenticate(context.Request, ct);
        if (authenticated is null) { await WriteError(context, 401, "invalid_api_key", "کلید API نامعتبر یا غیرفعال است."); return; }
        var (user, userKey) = authenticated.Value;
        JsonDocument body;
        try { body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct); }
        catch { await WriteError(context, 400, "invalid_request", "بدنه JSON معتبر نیست."); return; }
        using (body)
        {
            var modelName = body.RootElement.TryGetProperty("model", out var modelProperty) ? modelProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(modelName)) { await WriteError(context, 400, "model_required", "فیلد model الزامی است."); return; }
            var model = await db.Models.Include(x => x.Provider).SingleOrDefaultAsync(x => x.ModelId == modelName && x.IsActive && x.Provider!.IsActive, ct);
            if (model?.Provider is null || model.ServiceType is not ("text_to_speech" or "voice_clone" or "voice_design")) { await WriteError(context, 404, "model_not_found", "مدل فعال متن‌به‌صوت پیدا نشد."); return; }
            if (!await CheckAccess(context, user, userKey, model, ct)) return;

            var credentials = await ActiveCredentials(model.ProviderId, ct);
            if (credentials.Count == 0) { await WriteError(context, 503, "provider_unavailable", "کلید فعالی برای ارائه‌دهنده تنظیم نشده است."); return; }
            var started = Stopwatch.StartNew();
            foreach (var credential in credentials)
            {
                try
                {
                    using var upstream = await SendJson(model, credential, body.RootElement, ct);
                    if (!upstream.IsSuccessStatusCode)
                    {
                        var error = await upstream.Content.ReadAsStringAsync(ct);
                        credential.LastError = $"{(int)upstream.StatusCode}: {error}"[..Math.Min(500, $"{(int)upstream.StatusCode}: {error}".Length)];
                        if (upstream.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests) continue;
                        await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct);
                        context.Response.StatusCode = (int)upstream.StatusCode; context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                        await context.Response.WriteAsync(error, ct); return;
                    }
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
                    if (upstream.Content.Headers.ContentDisposition is not null) context.Response.Headers.ContentDisposition = upstream.Content.Headers.ContentDisposition.ToString();
                    await upstream.Content.CopyToAsync(context.Response.Body, ct);
                    var input = body.RootElement.TryGetProperty("input", out var inputProperty) ? inputProperty.GetString() ?? "" : body.RootElement.TryGetProperty("text", out var textProperty) ? textProperty.GetString() ?? "" : "";
                    var cost = EstimateMediaCost(model, input.Length, 0);
                    await Record(user, userKey, model, credential, input.Length, 0, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, ct);
                    return;
                }
                catch (Exception ex) { credential.LastError = ex.Message[..Math.Min(500, ex.Message.Length)]; logger.LogWarning(ex, "Speech provider key {CredentialId} failed", credential.Id); }
            }
            await db.SaveChangesAsync(ct); await WriteError(context, 503, "provider_unavailable", "همه مسیرهای ارائه‌دهنده ناموفق بودند.");
        }
    }

    public async Task ForwardTranscription(HttpContext context, CancellationToken ct)
    {
        var authenticated = await auth.Authenticate(context.Request, ct);
        if (authenticated is null) { await WriteError(context, 401, "invalid_api_key", "کلید API نامعتبر یا غیرفعال است."); return; }
        var (user, userKey) = authenticated.Value;
        if (!context.Request.HasFormContentType) { await WriteError(context, 400, "multipart_required", "فایل صوتی باید با multipart/form-data ارسال شود."); return; }
        var form = await context.Request.ReadFormAsync(ct);
        var modelName = form["model"].ToString();
        var file = form.Files.GetFile("file");
        if (string.IsNullOrWhiteSpace(modelName)) { await WriteError(context, 400, "model_required", "فیلد model الزامی است."); return; }
        if (file is null || file.Length == 0) { await WriteError(context, 400, "file_required", "فایل صوتی الزامی است."); return; }
        var model = await db.Models.Include(x => x.Provider).SingleOrDefaultAsync(x => x.ModelId == modelName && x.IsActive && x.Provider!.IsActive, ct);
        if (model?.Provider is null || model.ServiceType is not ("speech_to_text" or "translation" or "audio_understanding")) { await WriteError(context, 404, "model_not_found", "مدل فعال صوت‌به‌متن پیدا نشد."); return; }
        if (!await CheckAccess(context, user, userKey, model, ct)) return;
        var credentials = await ActiveCredentials(model.ProviderId, ct);
        if (credentials.Count == 0) { await WriteError(context, 503, "provider_unavailable", "کلید فعالی برای ارائه‌دهنده تنظیم نشده است."); return; }

        await using var source = file.OpenReadStream();
        using var audio = new MemoryStream(); await source.CopyToAsync(audio, ct); var audioBytes = audio.ToArray();
        var durationSeconds = decimal.TryParse(form["aibus_duration_seconds"].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration) ? Math.Max(0, duration) : 0;
        var started = Stopwatch.StartNew();
        foreach (var credential in credentials)
        {
            try
            {
                using var upstream = await SendMultipart(model, credential, form, file, audioBytes, ct);
                var responseBody = await upstream.Content.ReadAsByteArrayAsync(ct);
                if (!upstream.IsSuccessStatusCode)
                {
                    var error = Encoding.UTF8.GetString(responseBody);
                    credential.LastError = $"{(int)upstream.StatusCode}: {error}"[..Math.Min(500, $"{(int)upstream.StatusCode}: {error}".Length)];
                    if (upstream.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests) continue;
                    await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct);
                    context.Response.StatusCode = (int)upstream.StatusCode; context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json"; await context.Response.Body.WriteAsync(responseBody, ct); return;
                }
                context.Response.StatusCode = 200; context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json"; await context.Response.Body.WriteAsync(responseBody, ct);
                long inputTokens = 0, outputTokens = 0; ParseUsage(Encoding.UTF8.GetString(responseBody), ref inputTokens, ref outputTokens);
                var cost = inputTokens + outputTokens > 0 ? EstimateTokenCost(model, inputTokens, outputTokens) : EstimateMediaCost(model, 0, durationSeconds);
                await Record(user, userKey, model, credential, inputTokens > 0 ? inputTokens : (long)Math.Ceiling(durationSeconds), outputTokens, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, ct);
                return;
            }
            catch (Exception ex) { credential.LastError = ex.Message[..Math.Min(500, ex.Message.Length)]; logger.LogWarning(ex, "Transcription provider key {CredentialId} failed", credential.Id); }
        }
        await db.SaveChangesAsync(ct); await WriteError(context, 503, "provider_unavailable", "همه مسیرهای ارائه‌دهنده ناموفق بودند.");
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
                .OrderByDescending(x => (double)x.RemainingBalanceUsd > (double)x.AlertThresholdUsd).ThenBy(x => x.LastUsedAtUtc).ToListAsync(ct);
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

    public async Task ForwardJsonEndpoint(HttpContext context, CancellationToken ct)
    {
        var authenticated = await auth.Authenticate(context.Request, ct);
        if (authenticated is null) { await WriteError(context, 401, "invalid_api_key", "کلید API نامعتبر یا غیرفعال است."); return; }
        var (user, userKey) = authenticated.Value;

        JsonDocument body;
        try { body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: ct); }
        catch { await WriteError(context, 400, "invalid_request", "بدنه JSON معتبر نیست."); return; }
        using (body)
        {
            var modelName = body.RootElement.TryGetProperty("model", out var modelProperty) ? modelProperty.GetString() : null;
            if (string.IsNullOrWhiteSpace(modelName)) { await WriteError(context, 400, "model_required", "فیلد model الزامی است."); return; }
            var model = await db.Models.Include(x => x.Provider).SingleOrDefaultAsync(x => x.ModelId == modelName && x.IsActive && x.Provider!.IsActive, ct);
            if (model?.Provider is null) { await WriteError(context, 404, "model_not_found", "مدل فعال پیدا نشد."); return; }
            if (!string.Equals(model.EndpointPath.TrimEnd('/'), context.Request.Path.Value?.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
            { await WriteError(context, 400, "endpoint_mismatch", $"این مدل باید از مسیر {model.EndpointPath} فراخوانی شود."); return; }
            if (!await CheckAccess(context, user, userKey, model, ct)) return;

            var credentials = await ActiveCredentials(model.ProviderId, ct);
            if (credentials.Count == 0) { await WriteError(context, 503, "provider_unavailable", "کلید فعالی برای ارائه‌دهنده تنظیم نشده است."); return; }
            var stream = body.RootElement.TryGetProperty("stream", out var streamProperty) && streamProperty.ValueKind == JsonValueKind.True;
            var started = Stopwatch.StartNew();
            foreach (var credential in credentials)
            {
                try
                {
                    using var upstream = await SendJson(model, credential, body.RootElement, ct);
                    if (!upstream.IsSuccessStatusCode)
                    {
                        var error = await upstream.Content.ReadAsStringAsync(ct);
                        var credentialError = $"{(int)upstream.StatusCode}: {error}";
                        credential.LastError = credentialError[..Math.Min(500, credentialError.Length)];
                        if (upstream.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.PaymentRequired or HttpStatusCode.TooManyRequests) continue;
                        await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct);
                        context.Response.StatusCode = (int)upstream.StatusCode;
                        context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
                        await context.Response.WriteAsync(error, ct);
                        return;
                    }

                    long inputTokens = 0, outputTokens = 0;
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? (stream ? "text/event-stream; charset=utf-8" : "application/json; charset=utf-8");
                    if (stream)
                    {
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
                        ParseUsage(responseText, ref inputTokens, ref outputTokens);
                        await context.Response.WriteAsync(responseText, ct);
                    }
                    var cost = EstimateTokenCost(model, inputTokens, outputTokens);
                    if (cost == 0 && model.ServiceType == "video_generation" && TryGetDecimal(body.RootElement, "seconds", out var seconds))
                        cost = EstimateMediaCost(model, 0, seconds);
                    await Record(user, userKey, model, credential, inputTokens, outputTokens, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, CancellationToken.None);
                    return;
                }
                catch (Exception ex)
                {
                    credential.LastError = ex.Message[..Math.Min(500, ex.Message.Length)];
                    logger.LogWarning(ex, "JSON provider key {CredentialId} failed", credential.Id);
                }
            }
            await db.SaveChangesAsync(ct);
            await WriteError(context, 503, "provider_unavailable", "همه مسیرهای ارائه‌دهنده ناموفق بودند.");
        }
    }

    private async Task<bool> CheckAccess(HttpContext context, AppUser user, UserApiKey key, AiModel model, CancellationToken ct)
    {
        if (!HasModelAccess(key, model.ModelId)) { await WriteError(context, 403, "model_denied", "این کلید به مدل انتخابی دسترسی ندارد."); return false; }
        if (user.WalletUsd <= 0) { await WriteError(context, 402, "insufficient_balance", "موجودی کیف پول کافی نیست."); return false; }
        if (key.RequestLimit.HasValue && key.RequestCount >= key.RequestLimit.Value) { await WriteError(context, 429, "request_limit", "سقف تعداد درخواست این کلید تمام شده است."); return false; }
        if (key.SpendLimitUsd.HasValue && key.SpentUsd >= key.SpendLimitUsd.Value) { await WriteError(context, 429, "spend_limit", "سقف هزینه این کلید تمام شده است."); return false; }
        return true;
    }

    private Task<List<ProviderCredential>> ActiveCredentials(Guid providerId, CancellationToken ct) =>
        db.ProviderCredentials.Where(x => x.ProviderId == providerId && x.IsActive)
            .OrderByDescending(x => (double)x.RemainingBalanceUsd > (double)x.AlertThresholdUsd).ThenBy(x => x.LastUsedAtUtc).ToListAsync(ct);

    private async Task<HttpResponseMessage> SendJson(AiModel model, ProviderCredential credential, JsonElement body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUpstreamUri(model.Provider!, model, false, MediaQuery(model, body)));
        ConfigureAuthentication(request, model.Provider!, secrets.Unprotect(credential.ProtectedApiKey));
        var payload = body.GetRawText();
        if (model.Provider!.Protocol == "elevenlabs" && body.TryGetProperty("input", out var input))
            payload = JsonSerializer.Serialize(new { text = input.GetString(), model_id = model.ModelId });
        else if (model.Provider.Protocol == "deepgram" && body.TryGetProperty("input", out input))
            payload = JsonSerializer.Serialize(new { text = input.GetString() });
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");
        return await clients.CreateClient("providers").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private async Task<HttpResponseMessage> SendMultipart(AiModel model, ProviderCredential credential, IFormCollection form, IFormFile file, byte[] audio, CancellationToken ct)
    {
        var query = new Dictionary<string, string>();
        if (model.Provider!.Protocol == "deepgram") query["model"] = model.ModelId;
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUpstreamUri(model.Provider, model, false, query));
        ConfigureAuthentication(request, model.Provider, secrets.Unprotect(credential.ProtectedApiKey));
        if (model.Provider.Protocol == "deepgram")
        {
            request.Content = new ByteArrayContent(audio);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
        }
        else
        {
            var multipart = new MultipartFormDataContent();
            foreach (var field in form.Where(x => x.Key != "aibus_duration_seconds"))
                foreach (var value in field.Value) multipart.Add(new StringContent(value ?? ""), field.Key);
            var audioContent = new ByteArrayContent(audio);
            audioContent.Headers.ContentType = new MediaTypeHeaderValue(string.IsNullOrWhiteSpace(file.ContentType) ? "application/octet-stream" : file.ContentType);
            multipart.Add(audioContent, "file", file.FileName);
            request.Content = multipart;
        }
        return await clients.CreateClient("providers").SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    private static Dictionary<string, string> MediaQuery(AiModel model, JsonElement body)
    {
        var query = new Dictionary<string, string>();
        if (model.Provider!.Protocol == "deepgram") query["model"] = model.ModelId;
        if (model.Provider.Protocol == "google" && body.TryGetProperty("language", out var language)) query["languageCode"] = language.GetString() ?? "fa-IR";
        return query;
    }

    private static Uri BuildUpstreamUri(AiProvider provider, AiModel model, bool webSocket, IReadOnlyDictionary<string, string>? query = null)
    {
        var baseUri = new Uri(provider.BaseUrl);
        var path = model.EndpointPath.StartsWith('/') ? model.EndpointPath : "/" + model.EndpointPath;
        if (provider.Protocol == "elevenlabs" && model.ServiceType == "text_to_speech" && path.TrimEnd('/').EndsWith("text-to-speech", StringComparison.OrdinalIgnoreCase))
            path = path.TrimEnd('/') + "/JBFqnCBsd6RMkjVDRZzb";
        var builder = new UriBuilder(baseUri) { Path = path, Query = "" };
        if (webSocket) builder.Scheme = builder.Scheme == "https" ? "wss" : "ws";
        if (webSocket) builder.Port = -1;
        if (query is { Count: > 0 }) builder.Query = string.Join('&', query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return builder.Uri;
    }

    private static void ConfigureAuthentication(HttpRequestMessage request, AiProvider provider, string apiKey)
    {
        switch (provider.Protocol)
        {
            case "elevenlabs": request.Headers.TryAddWithoutValidation("xi-api-key", apiKey); break;
            case "deepgram": request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey); break;
            case "assemblyai": request.Headers.TryAddWithoutValidation("authorization", apiKey); break;
            default: request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey); break;
        }
    }

    private static void ConfigureWebSocketAuthentication(ClientWebSocket socket, AiProvider provider, string apiKey)
    {
        switch (provider.Protocol)
        {
            case "elevenlabs": socket.Options.SetRequestHeader("xi-api-key", apiKey); break;
            case "deepgram": socket.Options.SetRequestHeader("Authorization", $"Token {apiKey}"); break;
            case "assemblyai": socket.Options.SetRequestHeader("Authorization", apiKey); break;
            default: socket.Options.SetRequestHeader("Authorization", $"Bearer {apiKey}"); break;
        }
    }

    private static decimal EstimateMediaCost(AiModel model, int characters, decimal durationSeconds)
    {
        try
        {
            var prices = JsonSerializer.Deserialize<List<PriceComponent>>(model.PricingDetailsJson) ?? [];
            var priced = prices.FirstOrDefault(x => x.PriceUsd.HasValue && (characters > 0 ? x.Unit.Contains("characters") : x.Unit is "second" or "minute" or "hour"));
            if (priced?.PriceUsd is not decimal price) return 0;
            return priced.Unit switch
            {
                "million_characters" => characters / 1_000_000m * price,
                "ten_thousand_characters" => characters / 10_000m * price,
                "thousand_characters" => characters / 1_000m * price,
                "second" => durationSeconds * price,
                "minute" => durationSeconds / 60m * price,
                "hour" => durationSeconds / 3600m * price,
                _ => 0
            };
        }
        catch { return 0; }
    }

    private static decimal EstimateTokenCost(AiModel model, long inputTokens, long outputTokens) =>
        inputTokens / 1_000_000m * model.InputPricePerMillionUsd + outputTokens / 1_000_000m * model.OutputPricePerMillionUsd;

    private static decimal EstimateRealtimeCost(AiModel model, long inputTokens, long outputTokens, long inputAudioTokens, long outputAudioTokens)
    {
        try
        {
            var prices = JsonSerializer.Deserialize<List<PriceComponent>>(model.PricingDetailsJson) ?? [];
            var audioInputPrice = prices.FirstOrDefault(x => x.Unit == "million_audio_tokens" && x.Label.Contains("ورودی"))?.PriceUsd ?? 0;
            var audioOutputPrice = prices.FirstOrDefault(x => x.Unit == "million_audio_tokens" && x.Label.Contains("خروجی"))?.PriceUsd ?? 0;
            var textInput = Math.Max(0, inputTokens - inputAudioTokens); var textOutput = Math.Max(0, outputTokens - outputAudioTokens);
            return textInput / 1_000_000m * model.InputPricePerMillionUsd + textOutput / 1_000_000m * model.OutputPricePerMillionUsd + inputAudioTokens / 1_000_000m * audioInputPrice + outputAudioTokens / 1_000_000m * audioOutputPrice;
        }
        catch { return EstimateTokenCost(model, inputTokens, outputTokens); }
    }

    private sealed record PriceComponent(string Label, string Unit, decimal? PriceUsd, string? Note);

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

    private static void ParseRealtimeUsage(string json, ref long input, ref long output, ref long inputAudio, ref long outputAudio)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("usage", out var usage)) return;
            if (usage.TryGetProperty("input_tokens", out var i)) input += i.GetInt64();
            if (usage.TryGetProperty("output_tokens", out var o)) output += o.GetInt64();
            if (usage.TryGetProperty("input_token_details", out var inputDetails) && inputDetails.TryGetProperty("audio_tokens", out var ia)) inputAudio += ia.GetInt64();
            if (usage.TryGetProperty("output_token_details", out var outputDetails) && outputDetails.TryGetProperty("audio_tokens", out var oa)) outputAudio += oa.GetInt64();
        }
        catch { }
    }

    private static void ParseUsage(string json, ref long input, ref long output, string protocol = "openai")
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("usage", out var usage))
            {
                if (!root.TryGetProperty("response", out var response) || !response.TryGetProperty("usage", out usage)) return;
            }
            if (protocol == "anthropic")
            {
                if (usage.TryGetProperty("input_tokens", out var i)) input = i.GetInt64();
                if (usage.TryGetProperty("output_tokens", out var o)) output = o.GetInt64();
            }
            else
            {
                if (usage.TryGetProperty("prompt_tokens", out var i) || usage.TryGetProperty("input_tokens", out i)) input = i.GetInt64();
                if (usage.TryGetProperty("completion_tokens", out var o) || usage.TryGetProperty("output_tokens", out o)) output = o.GetInt64();
            }
        }
        catch { }
    }

    private static bool TryGetDecimal(JsonElement body, string propertyName, out decimal value)
    {
        value = 0;
        if (!body.TryGetProperty(propertyName, out var property)) return false;
        if (property.ValueKind == JsonValueKind.Number) return property.TryGetDecimal(out value);
        return property.ValueKind == JsonValueKind.String && decimal.TryParse(property.GetString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out value);
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
