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
        var credentials = await ActiveCredentials(model.Provider, ct);
        if (credentials.Count == 0) { await WriteError(context, 503, ProviderErrorMapper.UnavailableCode, "این سرویس موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد."); return; }
        if (!await ReserveRequestSlot(context, userKey, ct)) return;

        // OpenAI transcription sockets use intent=transcription without a URL model. The
        // selected transcription model is configured inside session.update and billed here.
        IReadOnlyDictionary<string, string> realtimeQuery = new Dictionary<string, string>
        {
            [model.ServiceType == "speech_to_text" ? "intent" : "model"] =
                model.ServiceType == "speech_to_text" ? "transcription" : model.ModelId
        };
        var upstreamUrl = BuildUpstreamUri(model.Provider, model, true, realtimeQuery);
        var failures = new List<ProviderFailure>();
        ClientWebSocket? upstream = null;
        ProviderCredential? credential = null;
        foreach (var candidate in credentials)
        {
            var socket = new ClientWebSocket();
            ConfigureWebSocketAuthentication(socket, model.Provider, model, secrets.Unprotect(candidate.ProtectedApiKey));
            try
            {
                await socket.ConnectAsync(upstreamUrl, ct);
                upstream = socket;
                credential = candidate;
                break;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                socket.Dispose();
                var failure = ProviderErrorMapper.Classify(ex);
                failures.Add(failure);
                ProviderErrorMapper.Apply(candidate, failure);
                logger.LogWarning(ex, "Realtime provider key {CredentialId} failed during handshake", candidate.Id);
            }
        }
        if (upstream is null || credential is null)
        {
            var failure = ProviderErrorMapper.MostRelevant(failures);
            await Record(user, userKey, model, credentials.Last(), 0, 0, 0, 0, "failed", context.TraceIdentifier, CancellationToken.None);
            await WriteProviderError(context, failure, model.Provider.Name);
            return;
        }
        using (upstream)
        {
        var requestedProtocols = context.Request.Headers.SecWebSocketProtocol.ToString();
        using var downstream = requestedProtocols.Contains("aibus-realtime", StringComparison.Ordinal)
            ? await context.WebSockets.AcceptWebSocketAsync("aibus-realtime")
            : await context.WebSockets.AcceptWebSocketAsync();
        long inputTokens = 0, outputTokens = 0, inputAudioTokens = 0, outputAudioTokens = 0;
        ProviderFailure? realtimeFailure = null;
        var started = Stopwatch.StartNew();
        var downToUp = Relay(downstream, upstream, null, ct);
        var upToDown = Relay(upstream, downstream, text =>
        {
            ParseRealtimeUsage(text, ref inputTokens, ref outputTokens, ref inputAudioTokens, ref outputAudioTokens);
            if (!ProviderErrorMapper.TryClassifyInBand(text, out var failure) || failure is null) return text;
            realtimeFailure = failure;
            ProviderErrorMapper.Apply(credential, failure);
            return text;
        }, ct);
        var completed = await Task.WhenAny(downToUp, upToDown);
        try
        {
            var relayResult = await completed;
            if (ReferenceEquals(completed, upToDown)
                && relayResult.CloseStatus is not null and not WebSocketCloseStatus.NormalClosure)
            {
                var shouldNotifyDownstream = realtimeFailure is null;
                realtimeFailure ??= ProviderErrorMapper.Classify(
                    HttpStatusCode.BadGateway,
                    $"WebSocket close {(int)relayResult.CloseStatus.Value}: {relayResult.CloseDescription}");
                ProviderErrorMapper.Apply(credential, realtimeFailure);
                if (shouldNotifyDownstream && downstream.State == WebSocketState.Open)
                {
                    try
                    {
                        var safeError = Encoding.UTF8.GetBytes(
                            ProviderErrorMapper.PublicRealtimeErrorJson(realtimeFailure, model.Provider.Name));
                        await downstream.SendAsync(safeError, WebSocketMessageType.Text, true, CancellationToken.None);
                    }
                    catch (Exception sendException)
                    {
                        logger.LogDebug(sendException, "Could not deliver the sanitized realtime close error to the client");
                    }
                }
            }
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            realtimeFailure ??= ProviderErrorMapper.Classify(ex);
            ProviderErrorMapper.Apply(credential, realtimeFailure);
            logger.LogWarning(ex, "Realtime relay failed for provider key {CredentialId}", credential.Id);
        }
        try { if (downstream.State == WebSocketState.Open) await downstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        try { if (upstream.State == WebSocketState.Open) await upstream.CloseAsync(WebSocketCloseStatus.NormalClosure, "closed", CancellationToken.None); } catch { }
        if (realtimeFailure is not null)
        {
            await Record(user, userKey, model, credential, inputTokens, outputTokens, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None);
        }
        else
        {
            var cost = EstimateRealtimeCost(model, inputTokens, outputTokens, inputAudioTokens, outputAudioTokens);
            await Record(user, userKey, model, credential, inputTokens, outputTokens, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, CancellationToken.None);
        }
        }
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

            var credentials = await ActiveCredentials(model.Provider, ct);
            if (credentials.Count == 0) { await WriteError(context, 503, ProviderErrorMapper.UnavailableCode, "این سرویس موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد."); return; }
            if (!await ReserveRequestSlot(context, userKey, ct)) return;
            var started = Stopwatch.StartNew();
            var failures = new List<ProviderFailure>();
            ProviderCredential? lastAttempt = null;
            foreach (var credential in credentials)
            {
                lastAttempt = credential;
                try
                {
                    using var upstream = await SendJson(model, credential, body.RootElement, ct);
                    if (!upstream.IsSuccessStatusCode)
                    {
                        var error = await upstream.Content.ReadAsStringAsync(ct);
                        var failure = ProviderErrorMapper.Classify(upstream.StatusCode, error);
                        failures.Add(failure);
                        ProviderErrorMapper.Apply(credential, failure);
                        if (failure.ShouldFailover && credential != credentials[^1]) continue;
                        await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct, (int)upstream.StatusCode);
                        await WriteUpstreamError(context, upstream.StatusCode, error, upstream.Content.Headers.ContentType?.ToString());
                        return;
                    }
                    byte[]? bufferedBody = null;
                    if (upstream.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        bufferedBody = await upstream.Content.ReadAsByteArrayAsync(ct);
                        var applicationBody = Encoding.UTF8.GetString(bufferedBody);
                        if (ProviderErrorMapper.TryClassifyInBand(applicationBody, out var applicationFailure) && applicationFailure is not null)
                        {
                            failures.Add(applicationFailure);
                            ProviderErrorMapper.Apply(credential, applicationFailure);
                            if (credential != credentials[^1]) continue;
                            await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct, 200);
                            context.Response.StatusCode = 200;
                            context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
                            await context.Response.Body.WriteAsync(bufferedBody, ct);
                            return;
                        }
                    }
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "audio/mpeg";
                    if (upstream.Content.Headers.ContentDisposition is not null) context.Response.Headers.ContentDisposition = upstream.Content.Headers.ContentDisposition.ToString();
                    if (bufferedBody is null) await upstream.Content.CopyToAsync(context.Response.Body, ct);
                    else await context.Response.Body.WriteAsync(bufferedBody, ct);
                    var input = body.RootElement.TryGetProperty("input", out var inputProperty) ? inputProperty.GetString() ?? "" : body.RootElement.TryGetProperty("text", out var textProperty) ? textProperty.GetString() ?? "" : "";
                    var cost = EstimateMediaCost(model, input.Length, 0);
                    if (cost == 0) cost = EstimateRequestCost(model);
                    await Record(user, userKey, model, credential, input.Length, 0, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, ct);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var failure = ProviderErrorMapper.Classify(ex);
                    failures.Add(failure);
                    ProviderErrorMapper.Apply(credential, failure);
                    logger.LogWarning(ex, "Speech provider key {CredentialId} failed", credential.Id);
                }
            }
            var terminalFailure = ProviderErrorMapper.MostRelevant(failures);
            if (lastAttempt is not null) await Record(user, userKey, model, lastAttempt, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None);
            else await db.SaveChangesAsync(CancellationToken.None);
            await WriteProviderError(context, terminalFailure, model.Provider.Name);
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
        var credentials = await ActiveCredentials(model.Provider, ct);
        if (credentials.Count == 0) { await WriteError(context, 503, ProviderErrorMapper.UnavailableCode, "این سرویس موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد."); return; }

        await using var source = file.OpenReadStream();
        using var audio = new MemoryStream(); await source.CopyToAsync(audio, ct); var audioBytes = audio.ToArray();
        if (!await ReserveRequestSlot(context, userKey, ct)) return;
        var durationSeconds = decimal.TryParse(form["aibus_duration_seconds"].ToString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var duration) ? Math.Max(0, duration) : 0;
        var started = Stopwatch.StartNew();
        var failures = new List<ProviderFailure>();
        ProviderCredential? lastAttempt = null;
        foreach (var credential in credentials)
        {
            lastAttempt = credential;
            try
            {
                using var upstream = await SendMultipart(model, credential, form, file, audioBytes, ct);
                var responseBody = await upstream.Content.ReadAsByteArrayAsync(ct);
                if (!upstream.IsSuccessStatusCode)
                {
                    var error = Encoding.UTF8.GetString(responseBody);
                    var failure = ProviderErrorMapper.Classify(upstream.StatusCode, error);
                    failures.Add(failure);
                    ProviderErrorMapper.Apply(credential, failure);
                    if (failure.ShouldFailover && credential != credentials[^1]) continue;
                    await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct, (int)upstream.StatusCode);
                    await WriteUpstreamError(context, upstream.StatusCode, responseBody, upstream.Content.Headers.ContentType?.ToString());
                    return;
                }
                var responseText = Encoding.UTF8.GetString(responseBody);
                if (ProviderErrorMapper.TryClassifyInBand(responseText, out var applicationFailure) && applicationFailure is not null)
                {
                    failures.Add(applicationFailure);
                    ProviderErrorMapper.Apply(credential, applicationFailure);
                    if (credential != credentials[^1]) continue;
                    await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct, 200);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
                    await context.Response.Body.WriteAsync(responseBody, ct);
                    return;
                }
                context.Response.StatusCode = 200; context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json"; await context.Response.Body.WriteAsync(responseBody, ct);
                long inputTokens = 0, outputTokens = 0; ParseUsage(responseText, ref inputTokens, ref outputTokens);
                var cost = inputTokens + outputTokens > 0 ? EstimateTokenCost(model, inputTokens, outputTokens) : EstimateMediaCost(model, 0, durationSeconds);
                if (cost == 0) cost = EstimateRequestCost(model);
                await Record(user, userKey, model, credential, inputTokens > 0 ? inputTokens : (long)Math.Ceiling(durationSeconds), outputTokens, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, ct);
                return;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                var failure = ProviderErrorMapper.Classify(ex);
                failures.Add(failure);
                ProviderErrorMapper.Apply(credential, failure);
                logger.LogWarning(ex, "Transcription provider key {CredentialId} failed", credential.Id);
            }
        }
        var terminalFailure = ProviderErrorMapper.MostRelevant(failures);
        if (lastAttempt is not null) await Record(user, userKey, model, lastAttempt, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None);
        else await db.SaveChangesAsync(CancellationToken.None);
        await WriteProviderError(context, terminalFailure, model.Provider.Name);
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

            if (!await CheckAccess(context, user, userKey, model, ct)) return;

            var credentials = await ActiveCredentials(model.Provider, ct);
            if (credentials.Count == 0) { await WriteError(context, 503, ProviderErrorMapper.UnavailableCode, "این سرویس موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد."); return; }
            if (!await ReserveRequestSlot(context, userKey, ct)) return;

            var stream = body.RootElement.TryGetProperty("stream", out var streamProperty) && streamProperty.ValueKind == JsonValueKind.True;
            var stopwatch = Stopwatch.StartNew();
            HttpResponseMessage? upstream = null;
            string? bufferedResponse = null;
            ProviderCredential? selected = null;
            ProviderCredential? lastAttempt = null;
            var failures = new List<ProviderFailure>();
            foreach (var credential in credentials)
            {
                lastAttempt = credential;
                try
                {
                    upstream = await Send(model, credential, body.RootElement, stream, ct);
                    if (upstream.IsSuccessStatusCode)
                    {
                        if (!stream)
                        {
                            bufferedResponse = await upstream.Content.ReadAsStringAsync(ct);
                            if (ProviderErrorMapper.TryClassifyInBand(bufferedResponse, out var applicationFailure) && applicationFailure is not null)
                            {
                                failures.Add(applicationFailure);
                                ProviderErrorMapper.Apply(credential, applicationFailure);
                                if (credential != credentials[^1])
                                {
                                    upstream.Dispose(); upstream = null; bufferedResponse = null;
                                    continue;
                                }
                                await Record(user, userKey, model, credential, 0, 0, 0, stopwatch.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct, 200);
                                context.Response.StatusCode = 200;
                                context.Response.ContentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json; charset=utf-8";
                                await context.Response.WriteAsync(bufferedResponse, ct);
                                upstream.Dispose();
                                return;
                            }
                        }
                        selected = credential;
                        break;
                    }
                    var errorBody = await upstream.Content.ReadAsStringAsync(ct);
                    var failure = ProviderErrorMapper.Classify(upstream.StatusCode, errorBody);
                    failures.Add(failure);
                    ProviderErrorMapper.Apply(credential, failure);
                    bufferedResponse = errorBody;
                    if (!failure.ShouldFailover || credential == credentials[^1]) { selected = credential; break; }
                    upstream.Dispose(); upstream = null;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    upstream?.Dispose(); upstream = null;
                    var failure = ProviderErrorMapper.Classify(ex);
                    failures.Add(failure);
                    ProviderErrorMapper.Apply(credential, failure);
                    logger.LogWarning(ex, "Provider key {CredentialId} failed", credential.Id);
                }
            }
            if (upstream is null || selected is null)
            {
                var terminalFailure = ProviderErrorMapper.MostRelevant(failures);
                if (lastAttempt is not null) await Record(user, userKey, model, lastAttempt, 0, 0, 0, stopwatch.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None);
                else await db.SaveChangesAsync(CancellationToken.None);
                await WriteProviderError(context, terminalFailure, model.Provider.Name);
                return;
            }
            using (upstream)
            {
                if (!upstream.IsSuccessStatusCode)
                {
                    var errorBody = bufferedResponse ?? await upstream.Content.ReadAsStringAsync(ct);
                    await Record(user, userKey, model, selected, 0, 0, 0, stopwatch.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct, (int)upstream.StatusCode);
                    await WriteUpstreamError(context, upstream.StatusCode, errorBody, upstream.Content.Headers.ContentType?.ToString());
                    return;
                }

                long inputTokens = 0, outputTokens = 0;
                ProviderFailure? streamFailure = null;
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
                        var hasSseData = TryGetSseData(line, out var ssePayload);
                        if (hasSseData && ssePayload != "[DONE]"
                            && ProviderErrorMapper.TryClassifyInBand(ssePayload, out var failure) && failure is not null)
                        {
                            streamFailure = failure;
                            ProviderErrorMapper.Apply(selected, failure);
                            await context.Response.WriteAsync(line + "\n\ndata: [DONE]\n\n", ct);
                            await context.Response.Body.FlushAsync(ct);
                            break;
                        }
                        await context.Response.WriteAsync(line + "\n", ct);
                        await context.Response.Body.FlushAsync(ct);
                        if (hasSseData && ssePayload != "[DONE]") ParseUsage(ssePayload, ref inputTokens, ref outputTokens);
                    }
                }
                else
                {
                    var responseText = bufferedResponse ?? await upstream.Content.ReadAsStringAsync(ct);
                    ParseUsage(responseText, ref inputTokens, ref outputTokens, model.Provider.Protocol);
                    if (model.Provider.Protocol == "anthropic") responseText = ConvertAnthropicResponse(responseText, model.ModelId);
                    context.Response.StatusCode = 200;
                    context.Response.ContentType = "application/json; charset=utf-8";
                    await context.Response.WriteAsync(responseText, ct);
                }
                if (streamFailure is not null)
                {
                    await Record(user, userKey, model, selected, inputTokens, outputTokens, 0, stopwatch.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None);
                    return;
                }
                var cost = inputTokens / 1_000_000m * model.InputPricePerMillionUsd + outputTokens / 1_000_000m * model.OutputPricePerMillionUsd;
                if (cost == 0) cost = EstimateRequestCost(model);
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

            var credentials = await ActiveCredentials(model.Provider, ct);
            if (credentials.Count == 0) { await WriteError(context, 503, ProviderErrorMapper.UnavailableCode, "این سرویس موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد."); return; }
            if (!await ReserveRequestSlot(context, userKey, ct)) return;
            var stream = body.RootElement.TryGetProperty("stream", out var streamProperty) && streamProperty.ValueKind == JsonValueKind.True;
            var started = Stopwatch.StartNew();
            var failures = new List<ProviderFailure>();
            ProviderCredential? lastAttempt = null;
            foreach (var credential in credentials)
            {
                lastAttempt = credential;
                try
                {
                    using var upstream = await SendJson(model, credential, body.RootElement, ct);
                    if (!upstream.IsSuccessStatusCode)
                    {
                        var error = await upstream.Content.ReadAsStringAsync(ct);
                        var failure = ProviderErrorMapper.Classify(upstream.StatusCode, error);
                        failures.Add(failure);
                        ProviderErrorMapper.Apply(credential, failure);
                        if (failure.ShouldFailover && credential != credentials[^1]) continue;
                        await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, ct, (int)upstream.StatusCode);
                        await WriteUpstreamError(context, upstream.StatusCode, error, upstream.Content.Headers.ContentType?.ToString());
                        return;
                    }

                    long inputTokens = 0, outputTokens = 0;
                    ProviderFailure? streamFailure = null;
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
                            var hasSseData = TryGetSseData(line, out var ssePayload);
                            if (hasSseData && ssePayload != "[DONE]"
                                && ProviderErrorMapper.TryClassifyInBand(ssePayload, out var failure) && failure is not null)
                            {
                                streamFailure = failure;
                                ProviderErrorMapper.Apply(credential, failure);
                                await context.Response.WriteAsync(line + "\n\ndata: [DONE]\n\n", ct);
                                await context.Response.Body.FlushAsync(ct);
                                break;
                            }
                            await context.Response.WriteAsync(line + "\n", ct);
                            await context.Response.Body.FlushAsync(ct);
                            if (hasSseData && ssePayload != "[DONE]") ParseUsage(ssePayload, ref inputTokens, ref outputTokens);
                        }
                    }
                    else
                    {
                        var responseText = await upstream.Content.ReadAsStringAsync(ct);
                        if (ProviderErrorMapper.TryClassifyInBand(responseText, out var applicationFailure) && applicationFailure is not null)
                        {
                            failures.Add(applicationFailure);
                            ProviderErrorMapper.Apply(credential, applicationFailure);
                            if (credential != credentials[^1]) continue;
                            await Record(user, userKey, model, credential, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None, 200);
                            await context.Response.WriteAsync(responseText, ct);
                            return;
                        }
                        ParseUsage(responseText, ref inputTokens, ref outputTokens);
                        await context.Response.WriteAsync(responseText, ct);
                    }
                    if (streamFailure is not null)
                    {
                        await Record(user, userKey, model, credential, inputTokens, outputTokens, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None);
                        return;
                    }
                    var cost = EstimateTokenCost(model, inputTokens, outputTokens);
                    if (cost == 0 && model.ServiceType == "video_generation" && TryGetDecimal(body.RootElement, "seconds", out var seconds))
                        cost = EstimateMediaCost(model, 0, seconds);
                    if (cost == 0) cost = EstimateRequestCost(model);
                    await Record(user, userKey, model, credential, inputTokens, outputTokens, cost, started.ElapsedMilliseconds, "success", context.TraceIdentifier, CancellationToken.None);
                    return;
                }
                catch (Exception ex) when (!ct.IsCancellationRequested)
                {
                    var failure = ProviderErrorMapper.Classify(ex);
                    failures.Add(failure);
                    ProviderErrorMapper.Apply(credential, failure);
                    logger.LogWarning(ex, "JSON provider key {CredentialId} failed", credential.Id);
                }
            }
            var terminalFailure = ProviderErrorMapper.MostRelevant(failures);
            if (lastAttempt is not null) await Record(user, userKey, model, lastAttempt, 0, 0, 0, started.ElapsedMilliseconds, "failed", context.TraceIdentifier, CancellationToken.None);
            else await db.SaveChangesAsync(CancellationToken.None);
            await WriteProviderError(context, terminalFailure, model.Provider.Name);
        }
    }

    private async Task<bool> CheckAccess(HttpContext context, AppUser user, UserApiKey key, AiModel model, CancellationToken ct)
    {
        if (!HasModelAccess(key, model.ModelId)) { await WriteError(context, 403, "model_denied", "این کلید به مدل انتخابی دسترسی ندارد."); return false; }
        if (user.WalletUsd <= 0) { await WriteError(context, 402, "insufficient_balance", "موجودی کیف پول کافی نیست."); return false; }
        // This early check avoids provider discovery for an already exhausted key. The
        // conditional reservation below remains the concurrency-safe source of truth.
        if (key.RequestLimit.HasValue && key.RequestCount >= key.RequestLimit.Value) { await WriteError(context, 429, "request_limit", "سقف تعداد درخواست این کلید تمام شده است."); return false; }
        if (key.SpendLimitUsd.HasValue && key.SpentUsd >= key.SpendLimitUsd.Value) { await WriteError(context, 429, "spend_limit", "سقف هزینه این کلید تمام شده است."); return false; }
        return true;
    }

    private async Task<bool> ReserveRequestSlot(HttpContext context, UserApiKey key, CancellationToken ct)
    {
        var admittedAtUtc = DateTime.UtcNow;
        var affected = await db.UserApiKeys
            .Where(x => x.Id == key.Id && x.IsActive
                && (!x.RequestLimit.HasValue || x.RequestCount < x.RequestLimit.Value)
                && (!x.SpendLimitUsd.HasValue || x.SpentUsd < x.SpendLimitUsd.Value))
            .ExecuteUpdateAsync(update => update
                .SetProperty(x => x.RequestCount, x => x.RequestCount + 1)
                .SetProperty(x => x.LastUsedAtUtc, admittedAtUtc), ct);
        if (affected == 1) return true;

        var state = await db.UserApiKeys.AsNoTracking().Where(x => x.Id == key.Id)
            .Select(x => new { x.IsActive, x.RequestLimit, x.RequestCount, x.SpendLimitUsd, x.SpentUsd }).SingleOrDefaultAsync(ct);
        if (state is null || !state.IsActive)
            await WriteError(context, 401, "invalid_api_key", "کلید API نامعتبر یا غیرفعال است.");
        else if (state.RequestLimit.HasValue && state.RequestCount >= state.RequestLimit.Value)
            await WriteError(context, 429, "request_limit", "سقف تعداد درخواست این کلید تمام شده است.");
        else if (state.SpendLimitUsd.HasValue && state.SpentUsd >= state.SpendLimitUsd.Value)
            await WriteError(context, 429, "spend_limit", "سقف هزینه این کلید تمام شده است.");
        else
            await WriteError(context, 409, "request_not_admitted", "درخواست به‌دلیل تغییر هم‌زمان وضعیت کلید پذیرفته نشد؛ دوباره تلاش کنید.");
        return false;
    }

    private async Task<List<ProviderCredential>> ActiveCredentials(AiProvider provider, CancellationToken ct)
    {
        if (provider.Slug == "arka")
        {
            return [new ProviderCredential
            {
                Id = Guid.Empty,
                ProviderId = provider.Id,
                Provider = provider,
                Label = "مسیر داخلی ARKA",
                IsActive = true
            }];
        }
        return await db.ProviderCredentials.Where(x => x.ProviderId == provider.Id && x.IsActive && x.ProtectedApiKey != "")
            .OrderBy(x => x.LastErrorCode == ProviderErrorMapper.QuotaExhaustedCode)
            .ThenByDescending(x => (double)x.RemainingBalanceUsd > (double)x.AlertThresholdUsd)
            .ThenBy(x => x.LastUsedAtUtc).ToListAsync(ct);
    }

    private async Task<HttpResponseMessage> SendJson(AiModel model, ProviderCredential credential, JsonElement body, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUpstreamUri(model.Provider!, model, false, MediaQuery(model, body)));
        ConfigureAuthentication(request, model.Provider!, model, secrets.Unprotect(credential.ProtectedApiKey));
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
        ConfigureAuthentication(request, model.Provider, model, secrets.Unprotect(credential.ProtectedApiKey));
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
        var baseUri = new Uri(string.IsNullOrWhiteSpace(model.UpstreamBaseUrl) ? provider.BaseUrl : model.UpstreamBaseUrl);
        var upstreamPath = string.IsNullOrWhiteSpace(model.UpstreamPath) ? model.EndpointPath : model.UpstreamPath;
        var path = upstreamPath.StartsWith('/') ? upstreamPath : "/" + upstreamPath;
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        if (basePath.Length > 0 && !path.Equals(basePath, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase))
            path = basePath + "/" + path.TrimStart('/');
        if (provider.Protocol == "elevenlabs" && model.ServiceType == "text_to_speech" && path.TrimEnd('/').EndsWith("text-to-speech", StringComparison.OrdinalIgnoreCase))
            path = path.TrimEnd('/') + "/JBFqnCBsd6RMkjVDRZzb";
        var builder = new UriBuilder(baseUri) { Path = path, Query = "" };
        if (webSocket) builder.Scheme = builder.Scheme == "https" ? "wss" : "ws";
        if (webSocket) builder.Port = -1;
        if (query is { Count: > 0 }) builder.Query = string.Join('&', query.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));
        return builder.Uri;
    }

    private static void ConfigureAuthentication(HttpRequestMessage request, AiProvider provider, AiModel model, string apiKey)
    {
        if (provider.Slug == "arka") return;
        if (provider.Slug == "gemini" && model.UpstreamPath.StartsWith("/v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            request.Headers.TryAddWithoutValidation("x-goog-api-key", apiKey);
            return;
        }
        switch (provider.Protocol)
        {
            case "elevenlabs": request.Headers.TryAddWithoutValidation("xi-api-key", apiKey); break;
            case "deepgram": request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey); break;
            case "assemblyai": request.Headers.TryAddWithoutValidation("authorization", apiKey); break;
            default: request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey); break;
        }
    }

    private static void ConfigureWebSocketAuthentication(ClientWebSocket socket, AiProvider provider, AiModel model, string apiKey)
    {
        if (provider.Slug == "arka") return;
        if (provider.Slug == "gemini" && model.UpstreamPath.StartsWith("/v1beta/", StringComparison.OrdinalIgnoreCase))
        {
            socket.Options.SetRequestHeader("x-goog-api-key", apiKey);
            return;
        }
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

    private static decimal EstimateRequestCost(AiModel model)
    {
        try
        {
            var prices = JsonSerializer.Deserialize<List<PriceComponent>>(model.PricingDetailsJson) ?? [];
            return prices.FirstOrDefault(x => x.PriceUsd.HasValue && x.Unit is "request" or "message" or "image" or "video" or "voice")?.PriceUsd ?? 0;
        }
        catch { return 0; }
    }

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

    private async Task<HttpResponseMessage> Send(AiModel model, ProviderCredential credential, JsonElement body, bool stream, CancellationToken ct)
    {
        var provider = model.Provider!;
        var client = clients.CreateClient("providers");
        var url = provider.Protocol == "anthropic"
            ? provider.BaseUrl.TrimEnd('/') + "/messages"
            : BuildUpstreamUri(provider, model, false).ToString();
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
            if (provider.Slug != "arka") request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
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

    private async Task Record(AppUser user, UserApiKey key, AiModel model, ProviderCredential credential, long input, long output, decimal cost, long durationMs, string status, string traceId, CancellationToken ct, int? httpStatus = null)
    {
        var credentialId = credential.Id == Guid.Empty ? (Guid?)null : credential.Id;
        var usage = new UsageRecord { UserId = user.Id, UserApiKeyId = key.Id, ModelId = model.Id, ProviderCredentialId = credentialId, ModelName = model.ModelId, ProviderName = model.Provider!.Name, InputTokens = input, OutputTokens = output, CostUsd = cost, DurationMs = (int)Math.Min(int.MaxValue, durationMs), Status = status, TraceId = traceId, EndpointPath = model.EndpointPath, HttpStatus = httpStatus ?? (status == "success" ? 200 : 502) };
        if (status == "success")
        {
            var completedAtUtc = DateTime.UtcNow;
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            await db.UserApiKeys.Where(x => x.Id == key.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.SpentUsd, x => x.SpentUsd + cost), ct);
            await db.Users.Where(x => x.Id == user.Id)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.WalletUsd,
                    x => x.WalletUsd >= cost ? x.WalletUsd - cost : 0m), ct);
            if (credentialId.HasValue)
                await db.ProviderCredentials.Where(x => x.Id == credentialId.Value)
                    .ExecuteUpdateAsync(update => update
                        .SetProperty(x => x.RequestCount, x => x.RequestCount + 1)
                        .SetProperty(x => x.LastUsedAtUtc, completedAtUtc)
                        .SetProperty(x => x.RemainingBalanceUsd, x => x.InitialBalanceUsd <= 0
                            ? x.RemainingBalanceUsd
                            : x.RemainingBalanceUsd >= cost ? x.RemainingBalanceUsd - cost : 0m)
                        .SetProperty(x => x.LastError, (string?)null)
                        .SetProperty(x => x.LastErrorCode, (string?)null)
                        .SetProperty(x => x.LastErrorAtUtc, (DateTime?)null), ct);
            db.UsageRecords.Add(usage);
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return;
        }
        db.UsageRecords.Add(usage);
        await db.SaveChangesAsync(ct);
    }

    private static bool HasModelAccess(UserApiKey key, string model)
    {
        string[] rules;
        try { rules = JsonSerializer.Deserialize<string[]>(key.ModelRulesJson) ?? []; } catch { rules = []; }
        return key.AccessMode switch { "allow" => rules.Contains(model), "deny" => !rules.Contains(model), _ => true };
    }

    private sealed record WebSocketRelayResult(WebSocketCloseStatus? CloseStatus, string? CloseDescription);

    private static async Task<WebSocketRelayResult> Relay(WebSocket source, WebSocket destination, Func<string, string>? transform, CancellationToken ct)
    {
        var buffer = new byte[32 * 1024];
        while (!ct.IsCancellationRequested && source.State == WebSocketState.Open && destination.State == WebSocketState.Open)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await source.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return new(result.CloseStatus, result.CloseStatusDescription);
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            var bytes = message.ToArray();
            if (result.MessageType == WebSocketMessageType.Text && transform is not null)
                bytes = Encoding.UTF8.GetBytes(transform(Encoding.UTF8.GetString(bytes)));
            await destination.SendAsync(bytes, result.MessageType, true, ct);
        }
        return new(null, null);
    }

    private static bool TryGetSseData(string line, out string payload)
    {
        payload = "";
        if (!line.StartsWith("data:", StringComparison.Ordinal)) return false;
        payload = line[5..].TrimStart();
        return true;
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

    private static async Task WriteProviderError(HttpContext context, ProviderFailure failure, string providerName)
    {
        context.Response.StatusCode = failure.PublicStatus;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(ProviderErrorMapper.PublicError(failure, providerName));
    }

    private static async Task WriteUpstreamError(HttpContext context, HttpStatusCode status, string body, string? contentType)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/json; charset=utf-8" : contentType;
        await context.Response.WriteAsync(body);
    }

    private static async Task WriteUpstreamError(HttpContext context, HttpStatusCode status, byte[] body, string? contentType)
    {
        context.Response.StatusCode = (int)status;
        context.Response.ContentType = string.IsNullOrWhiteSpace(contentType) ? "application/json; charset=utf-8" : contentType;
        await context.Response.Body.WriteAsync(body);
    }
}
