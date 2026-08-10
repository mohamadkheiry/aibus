using System.Collections.Concurrent;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiBus.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiBus.Api.Tests;

public sealed class ScriptedProviderHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder = (_, _) =>
        Task.FromResult(JsonResponse(HttpStatusCode.ServiceUnavailable, new { error = new { message = "No test response configured" } }));

    public ConcurrentQueue<string> CalledApiKeys { get; } = new();
    public ConcurrentQueue<Uri> CalledUris { get; } = new();

    public void Reset(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        while (CalledApiKeys.TryDequeue(out _)) { }
        while (CalledUris.TryDequeue(out _)) { }
        _responder = (request, _) => Task.FromResult(responder(request));
    }

    public void ResetAsync(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
    {
        while (CalledApiKeys.TryDequeue(out _)) { }
        while (CalledUris.TryDequeue(out _)) { }
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CalledApiKeys.Enqueue(request.Headers.Authorization?.Parameter ?? "");
        if (request.RequestUri is not null) CalledUris.Enqueue(request.RequestUri);
        return _responder(request, cancellationToken);
    }

    public static HttpResponseMessage JsonResponse(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
    };
}

public sealed class QuotaGatewayFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aibus-quota-tests-{Guid.NewGuid():N}.db");
    public ScriptedProviderHandler ProviderHandler { get; } = new();
    public SpendReservationRaceInterceptor SpendRaceInterceptor { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddSingleton(SpendRaceInterceptor);
            services.AddDbContext<AppDbContext>((serviceProvider, options) => options
                .UseSqlite($"Data Source={_dbPath}")
                .AddInterceptors(serviceProvider.GetRequiredService<SpendReservationRaceInterceptor>()));
            services.AddSingleton(ProviderHandler);
            services.AddHttpClient("providers").ConfigurePrimaryHttpMessageHandler(serviceProvider =>
                serviceProvider.GetRequiredService<ScriptedProviderHandler>());
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            try { File.Delete(_dbPath + suffix); } catch (IOException) { }
    }
}

public sealed class SpendReservationRaceInterceptor : DbCommandInterceptor
{
    private int _armed;
    private TaskCompletionSource<bool> _paused = CompletedSignal();
    private TaskCompletionSource<bool> _release = CompletedSignal();

    public void Arm()
    {
        _paused = NewSignal();
        _release = NewSignal();
        Volatile.Write(ref _armed, 1);
    }

    public Task WaitUntilPaused(TimeSpan timeout) => _paused.Task.WaitAsync(timeout);
    public void Release() => _release.TrySetResult(true);

    public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        if (command.CommandText.Contains("ProviderCredentials", StringComparison.Ordinal)
            && Interlocked.CompareExchange(ref _armed, 0, 1) == 1)
        {
            _paused.TrySetResult(true);
            await _release.Task.WaitAsync(cancellationToken);
        }
        return result;
    }

    private static TaskCompletionSource<bool> NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource<bool> CompletedSignal()
    {
        var signal = NewSignal();
        signal.SetResult(true);
        return signal;
    }
}

public sealed class ProviderQuotaTests(QuotaGatewayFactory factory) : IClassFixture<QuotaGatewayFactory>
{
    private const string RawQuotaMarker = "UPSTREAM-SECRET-QUOTA-MARKER";
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Arka_model_test_needs_no_upstream_key_and_does_not_audit_response_body()
    {
        const string responseMarker = "TRANSIENT-UPSTREAM-TEST-BODY";
        factory.ProviderHandler.Reset(_ => ScriptedProviderHandler.JsonResponse(HttpStatusCode.OK, new { result = responseMarker }));
        Guid modelId;
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = await db.Providers.SingleAsync(x => x.Slug == "arka");
            var model = new AiModel
            {
                ProviderId = provider.Id, ModelId = $"arka-route-{Guid.NewGuid():N}", DisplayName = "ARKA Route Test",
                EndpointPath = "/v1/chat/completions", UpstreamBaseUrl = "https://private.arka.example/root/v1",
                UpstreamPath = "/custom/chat", TestPayloadJson = "{\"model\":\"arka-route\",\"messages\":[]}",
                InputModalitiesJson = "[\"text\"]", OutputModality = "text"
            };
            db.Models.Add(model);
            await db.SaveChangesAsync();
            modelId = model.Id;
            var admin = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            token = scope.ServiceProvider.GetRequiredService<TokenService>().Create(admin);
        }

        using var request = Authorized(HttpMethod.Post, $"/api/admin/models/{modelId}/test", token);
        var response = await _client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        Assert.Contains(responseMarker, await response.Content.ReadAsStringAsync());
        Assert.Equal("https://private.arka.example/root/v1/custom/chat", factory.ProviderHandler.CalledUris.Single().ToString().TrimEnd('/'));
        Assert.Equal("", factory.ProviderHandler.CalledApiKeys.Single());

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var audit = await verificationDb.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "model.upstream.test" && x.EntityId == modelId.ToString());
        Assert.DoesNotContain(responseMarker, audit.DetailsJson);
    }

    [Fact]
    public async Task Arka_gateway_works_without_provider_credential_and_records_keyless_usage()
    {
        factory.ProviderHandler.Reset(_ => ScriptedProviderHandler.JsonResponse(HttpStatusCode.OK, new
        {
            id = "arka-keyless-response",
            choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } },
            usage = new { prompt_tokens = 100, completion_tokens = 50, total_tokens = 150 }
        }));
        var rawUserKey = $"aibus_arka_{Guid.NewGuid():N}";
        string modelId;
        Guid userKeyId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var provider = await db.Providers.SingleAsync(x => x.Slug == "arka");
            var user = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            user.WalletUsd = 10m;
            modelId = $"arka-keyless-{Guid.NewGuid():N}";
            var model = new AiModel
            {
                ProviderId = provider.Id,
                ModelId = modelId,
                DisplayName = "ARKA Keyless Gateway",
                EndpointPath = "/v1/chat/completions",
                UpstreamBaseUrl = "https://private.arka.example/v1",
                UpstreamPath = "/chat/completions",
                InputPricePerMillionUsd = 1m,
                OutputPricePerMillionUsd = 2m,
                InputModalitiesJson = "[\"text\"]",
                OutputModality = "text"
            };
            var userKey = new UserApiKey
            {
                UserId = user.Id,
                Name = "ARKA keyless test",
                KeyHash = Hashing.Sha256(rawUserKey),
                KeyPrefix = rawUserKey[..Math.Min(14, rawUserKey.Length)]
            };
            db.Models.Add(model);
            db.UserApiKeys.Add(userKey);
            await db.SaveChangesAsync();
            userKeyId = userKey.Id;
        }

        using var gatewayRequest = GatewayRequest(rawUserKey, modelId);
        var gatewayResponse = await _client.SendAsync(gatewayRequest);
        gatewayResponse.EnsureSuccessStatusCode();
        Assert.Equal("", factory.ProviderHandler.CalledApiKeys.Single());
        Assert.Equal("https://private.arka.example/v1/chat/completions", factory.ProviderHandler.CalledUris.Single().ToString().TrimEnd('/'));

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var usage = await verificationDb.UsageRecords.AsNoTracking().SingleAsync(x => x.UserApiKeyId == userKeyId);
        Assert.Null(usage.ProviderCredentialId);
        Assert.Equal("success", usage.Status);
        Assert.Equal(0.0002m, usage.CostUsd);
    }

    [Fact]
    public void Classifier_distinguishes_quota_from_temporary_rate_limit()
    {
        var quota = ProviderErrorMapper.Classify(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"type\":\"insufficient_quota\",\"message\":\"You exceeded your current quota\"}}");
        var rate = ProviderErrorMapper.Classify(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"rate_limit_exceeded\",\"message\":\"Too many requests\"}}");
        var genericQuotaLimit = ProviderErrorMapper.Classify(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"code\":\"RESOURCE_EXHAUSTED\",\"message\":\"You exceeded your current quota for requests per minute\"}}");
        var exceptionQuota = ProviderErrorMapper.Classify(new HttpRequestException("billing_hard_limit_reached"));

        Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode, quota.Code);
        Assert.Equal(ProviderErrorMapper.RateLimitedCode, rate.Code);
        Assert.Equal(429, rate.PublicStatus);
        Assert.Equal(ProviderErrorMapper.RateLimitedCode, genericQuotaLimit.Code);
        Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode, exceptionQuota.Code);
    }

    [Fact]
    public void In_band_classifier_ignores_successful_null_errors_and_emits_standard_realtime_errors()
    {
        Assert.False(ProviderErrorMapper.TryClassifyInBand("{\"error\":null,\"data\":[1]}", out _));
        Assert.False(ProviderErrorMapper.TryClassifyInBand("[1,2,3]", out _));

        var failure = ProviderErrorMapper.Classify(HttpStatusCode.TooManyRequests,
            "{\"error\":{\"type\":\"insufficient_quota\"}}");
        using var realtime = JsonDocument.Parse(ProviderErrorMapper.PublicRealtimeErrorJson(failure, "OpenAI"));
        Assert.Equal("error", realtime.RootElement.GetProperty("type").GetString());
        Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode,
            realtime.RootElement.GetProperty("error").GetProperty("code").GetString());
    }

    [Fact]
    public async Task Quota_failure_is_forwarded_marks_key_and_never_charges_user_or_persists_body()
    {
        factory.ProviderHandler.Reset(_ => ScriptedProviderHandler.JsonResponse(HttpStatusCode.TooManyRequests, new
        {
            error = new
            {
                type = "insufficient_quota",
                code = "insufficient_quota",
                message = $"{RawQuotaMarker}: You exceeded your current quota. https://platform.openai.com/private"
            }
        }));
        var setup = await CreateGatewayIdentity(("quota-only", DateTime.UtcNow.AddDays(-1)));

        using var request = GatewayRequest(setup.RawUserApiKey, setup.ModelId);
        var response = await _client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains(RawQuotaMarker, responseText);
        Assert.Contains("platform.openai.com/private", responseText);
        Assert.Contains("exceeded your current quota", responseText, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == setup.UserId);
        var userKey = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        var providerKey = await db.ProviderCredentials.AsNoTracking().SingleAsync(x => x.Id == setup.CredentialIds[0]);
        var usage = await db.UsageRecords.AsNoTracking().SingleAsync(x => x.UserApiKeyId == setup.UserApiKeyId);

        Assert.Equal(setup.StartingWalletUsd, user.WalletUsd);
        Assert.Equal(1, userKey.RequestCount);
        Assert.Equal(0, userKey.SpentUsd);
        Assert.Equal(0, providerKey.RemainingBalanceUsd);
        Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode, providerKey.LastErrorCode);
        Assert.NotNull(providerKey.LastErrorAtUtc);
        Assert.Equal($"{ProviderErrorMapper.QuotaExhaustedCode} ({ProviderFailureKind.QuotaExhausted})", providerKey.LastError);
        Assert.DoesNotContain(RawQuotaMarker, providerKey.LastError);
        Assert.Equal("failed", usage.Status);
        Assert.Equal(0, usage.CostUsd);
    }

    [Fact]
    public async Task Sse_quota_event_without_optional_space_is_forwarded_and_not_charged()
    {
        factory.ProviderHandler.Reset(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                $"data:{{\"error\":{{\"type\":\"insufficient_quota\",\"message\":\"{RawQuotaMarker}: You exceeded your current quota\"}}}}\n\n",
                Encoding.UTF8,
                "text/event-stream")
        });
        var setup = await CreateGatewayIdentity(("stream-quota", DateTime.UtcNow.AddDays(-1)));

        using var request = GatewayRequest(setup.RawUserApiKey, setup.ModelId, stream: true);
        var response = await _client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("data: [DONE]", responseText);
        Assert.Contains(RawQuotaMarker, responseText);
        Assert.Contains("exceeded your current quota", responseText, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == setup.UserId);
        var userKey = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        var usage = await db.UsageRecords.AsNoTracking().SingleAsync(x => x.UserApiKeyId == setup.UserApiKeyId);

        Assert.Equal(setup.StartingWalletUsd, user.WalletUsd);
        Assert.Equal(1, userKey.RequestCount);
        Assert.Equal(0, userKey.SpentUsd);
        Assert.Equal("failed", usage.Status);
        Assert.Equal(0, usage.CostUsd);
    }

    [Fact]
    public async Task Quota_key_fails_over_to_next_key_and_charges_exactly_once()
    {
        factory.ProviderHandler.Reset(request => request.Headers.Authorization?.Parameter switch
        {
            "upstream-quota" => ScriptedProviderHandler.JsonResponse(HttpStatusCode.TooManyRequests, new
            {
                error = new { type = "insufficient_quota", message = "You exceeded your current quota" }
            }),
            "upstream-healthy" => ScriptedProviderHandler.JsonResponse(HttpStatusCode.OK, new
            {
                id = "chatcmpl-test",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1000, completion_tokens = 500, total_tokens = 1500 }
            }),
            _ => ScriptedProviderHandler.JsonResponse(HttpStatusCode.Unauthorized, new { error = new { message = "unexpected key" } })
        });
        var setup = await CreateGatewayIdentity(
            ("upstream-quota", DateTime.UtcNow.AddDays(-2)),
            ("upstream-healthy", DateTime.UtcNow.AddDays(-1)));

        using var request = GatewayRequest(setup.RawUserApiKey, setup.ModelId);
        var response = await _client.SendAsync(request);
        var responseText = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("chatcmpl-test", responseText);
        Assert.Equal(new[] { "upstream-quota", "upstream-healthy" }, factory.ProviderHandler.CalledApiKeys.ToArray());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == setup.UserId);
        var userKey = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        var credentials = await db.ProviderCredentials.AsNoTracking().Where(x => setup.CredentialIds.Contains(x.Id)).ToListAsync();
        var quotaKey = credentials.Single(x => x.Label == "upstream-quota");
        var healthyKey = credentials.Single(x => x.Label == "upstream-healthy");
        var usages = await db.UsageRecords.AsNoTracking().Where(x => x.UserApiKeyId == setup.UserApiKeyId).ToListAsync();

        Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode, quotaKey.LastErrorCode);
        Assert.Equal(0, quotaKey.RemainingBalanceUsd);
        Assert.Null(healthyKey.LastErrorCode);
        Assert.Equal(1, healthyKey.RequestCount);
        Assert.Equal(1, userKey.RequestCount);
        Assert.True(userKey.SpentUsd > 0);
        Assert.Equal(setup.StartingWalletUsd - userKey.SpentUsd, user.WalletUsd);
        var usage = Assert.Single(usages);
        Assert.Equal("success", usage.Status);
        Assert.True(usage.CostUsd > 0);
    }

    [Fact]
    public async Task Request_limit_reservation_admits_exactly_one_concurrent_upstream_request()
    {
        var upstreamEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpstream = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        factory.ProviderHandler.ResetAsync(async (_, cancellationToken) =>
        {
            upstreamEntered.TrySetResult(true);
            await releaseUpstream.Task.WaitAsync(cancellationToken);
            return ScriptedProviderHandler.JsonResponse(HttpStatusCode.OK, new
            {
                id = "chatcmpl-concurrency",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1000, completion_tokens = 500, total_tokens = 1500 }
            });
        });
        var setup = await CreateGatewayIdentity(("upstream-blocking", DateTime.UtcNow.AddDays(-1)));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.UserApiKeys.Where(x => x.Id == setup.UserApiKeyId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.RequestLimit, 1));
        }

        using var firstRequest = GatewayRequest(setup.RawUserApiKey, setup.ModelId);
        var firstTask = _client.SendAsync(firstRequest);
        await upstreamEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));

        HttpResponseMessage secondResponse;
        try
        {
            using var secondRequest = GatewayRequest(setup.RawUserApiKey, setup.ModelId);
            secondResponse = await _client.SendAsync(secondRequest).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HttpStatusCode.TooManyRequests, secondResponse.StatusCode);
            Assert.Contains("request_limit", await secondResponse.Content.ReadAsStringAsync());
            Assert.Equal(new[] { "upstream-blocking" }, factory.ProviderHandler.CalledApiKeys.ToArray());
        }
        finally
        {
            releaseUpstream.TrySetResult(true);
        }
        using (secondResponse)
        using (var firstResponse = await firstTask.WaitAsync(TimeSpan.FromSeconds(10)))
        {
            Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
            Assert.Contains("chatcmpl-concurrency", await firstResponse.Content.ReadAsStringAsync());
        }

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userKey = await verificationDb.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        Assert.Equal(1, userKey.RequestCount);
        Assert.True(userKey.SpentUsd > 0);
        Assert.NotNull(userKey.LastUsedAtUtc);
        Assert.Equal(1, await verificationDb.UsageRecords.CountAsync(x => x.UserApiKeyId == setup.UserApiKeyId));
    }

    [Fact]
    public async Task Atomic_reservation_rejects_stale_auth_when_concurrent_completion_reaches_spend_limit()
    {
        factory.ProviderHandler.Reset(_ => ScriptedProviderHandler.JsonResponse(HttpStatusCode.OK, new
        {
            id = "must-not-reach-upstream",
            choices = Array.Empty<object>(),
            usage = new { prompt_tokens = 0, completion_tokens = 0, total_tokens = 0 }
        }));
        var setup = await CreateGatewayIdentity(("upstream-spend-race", DateTime.UtcNow.AddDays(-1)));
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            await db.UserApiKeys.Where(x => x.Id == setup.UserApiKeyId)
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.SpendLimitUsd, 1m)
                    .SetProperty(x => x.SpentUsd, 0m));
        }

        factory.SpendRaceInterceptor.Arm();
        using var request = GatewayRequest(setup.RawUserApiKey, setup.ModelId);
        var responseTask = _client.SendAsync(request);
        try
        {
            await factory.SpendRaceInterceptor.WaitUntilPaused(TimeSpan.FromSeconds(10));
            using var completionScope = factory.Services.CreateScope();
            var completionDb = completionScope.ServiceProvider.GetRequiredService<AppDbContext>();
            await completionDb.UserApiKeys.Where(x => x.Id == setup.UserApiKeyId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.SpentUsd, 1m));
        }
        finally
        {
            factory.SpendRaceInterceptor.Release();
        }

        using var response = await responseTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Contains("spend_limit", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.ProviderHandler.CalledApiKeys);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var userKey = await verificationDb.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        Assert.Equal(0, userKey.RequestCount);
        Assert.Equal(1m, userKey.SpentUsd);
        Assert.False(await verificationDb.UsageRecords.AnyAsync(x => x.UserApiKeyId == setup.UserApiKeyId));
    }

    [Fact]
    public async Task Concurrent_successes_atomically_update_all_billing_and_provider_totals()
    {
        var bothUpstreamRequestsEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseUpstream = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var upstreamCount = 0;
        factory.ProviderHandler.ResetAsync(async (_, cancellationToken) =>
        {
            if (Interlocked.Increment(ref upstreamCount) == 2) bothUpstreamRequestsEntered.TrySetResult(true);
            await releaseUpstream.Task.WaitAsync(cancellationToken);
            return ScriptedProviderHandler.JsonResponse(HttpStatusCode.OK, new
            {
                id = "chatcmpl-concurrent-success",
                choices = new[] { new { index = 0, message = new { role = "assistant", content = "OK" }, finish_reason = "stop" } },
                usage = new { prompt_tokens = 1000, completion_tokens = 500, total_tokens = 1500 }
            });
        });
        var setup = await CreateGatewayIdentity(("upstream-concurrent-success", DateTime.UtcNow.AddDays(-1)));
        decimal expectedCost;
        const decimal startingProviderBalance = 20m;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var model = await db.Models.AsNoTracking().SingleAsync(x => x.ModelId == setup.ModelId);
            expectedCost = 1000m / 1_000_000m * model.InputPricePerMillionUsd
                + 500m / 1_000_000m * model.OutputPricePerMillionUsd;
            await db.ProviderCredentials.Where(x => x.Id == setup.CredentialIds[0])
                .ExecuteUpdateAsync(update => update
                    .SetProperty(x => x.LastError, "stale provider error")
                    .SetProperty(x => x.LastErrorCode, ProviderErrorMapper.QuotaExhaustedCode)
                    .SetProperty(x => x.LastErrorAtUtc, DateTime.UtcNow));
        }

        using var firstRequest = GatewayRequest(setup.RawUserApiKey, setup.ModelId);
        using var secondRequest = GatewayRequest(setup.RawUserApiKey, setup.ModelId);
        var firstTask = _client.SendAsync(firstRequest);
        var secondTask = _client.SendAsync(secondRequest);
        try
        {
            await bothUpstreamRequestsEntered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            releaseUpstream.TrySetResult(true);
        }
        using var firstResponse = await firstTask.WaitAsync(TimeSpan.FromSeconds(10));
        using var secondResponse = await secondTask.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(HttpStatusCode.OK, firstResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, secondResponse.StatusCode);
        Assert.Equal(2, factory.ProviderHandler.CalledApiKeys.Count);

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await verificationDb.Users.AsNoTracking().SingleAsync(x => x.Id == setup.UserId);
        var userKey = await verificationDb.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        var providerKey = await verificationDb.ProviderCredentials.AsNoTracking().SingleAsync(x => x.Id == setup.CredentialIds[0]);
        var usages = await verificationDb.UsageRecords.AsNoTracking().Where(x => x.UserApiKeyId == setup.UserApiKeyId).ToListAsync();
        var expectedTotal = expectedCost * 2;
        Assert.Equal(2, userKey.RequestCount);
        Assert.Equal(expectedTotal, userKey.SpentUsd);
        Assert.Equal(setup.StartingWalletUsd - expectedTotal, user.WalletUsd);
        Assert.Equal(2, providerKey.RequestCount);
        Assert.Equal(startingProviderBalance - expectedTotal, providerKey.RemainingBalanceUsd);
        Assert.NotNull(providerKey.LastUsedAtUtc);
        Assert.Null(providerKey.LastError);
        Assert.Null(providerKey.LastErrorCode);
        Assert.Null(providerKey.LastErrorAtUtc);
        Assert.Equal(2, usages.Count);
        Assert.All(usages, usage => Assert.Equal(expectedCost, usage.CostUsd));
        Assert.Equal(expectedTotal, usages.Sum(x => x.CostUsd));
    }

    private async Task<GatewaySetup> CreateGatewayIdentity(params (string ApiKey, DateTime LastUsedAtUtc)[] providerKeys)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var protector = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        await db.ProviderCredentials.Where(x => x.Provider!.Slug == "openai" && x.IsActive)
            .ExecuteUpdateAsync(update => update.SetProperty(x => x.IsActive, false));
        var provider = await db.Providers.SingleAsync(x => x.Slug == "openai");
        var model = await db.Models.SingleAsync(x => x.ProviderId == provider.Id && x.ModelId == "gpt-5.6-sol");
        var suffix = Guid.NewGuid().ToString("N");
        var rawUserApiKey = $"aibus_test_{suffix}";
        const decimal wallet = 25m;
        var user = new AppUser { Mobile = "09" + suffix[..9], DisplayName = "Quota integration", WalletUsd = wallet };
        var userKey = new UserApiKey
        {
            User = user,
            Name = "Quota integration key",
            KeyHash = Hashing.Sha256(rawUserApiKey),
            KeyPrefix = rawUserApiKey[..12]
        };
        db.Users.Add(user);
        db.UserApiKeys.Add(userKey);
        var credentials = providerKeys.Select(item => new ProviderCredential
        {
            ProviderId = provider.Id,
            Label = item.ApiKey,
            ProtectedApiKey = protector.Protect(item.ApiKey),
            InitialBalanceUsd = 20m,
            RemainingBalanceUsd = 20m,
            AlertThresholdUsd = 1m,
            LastUsedAtUtc = item.LastUsedAtUtc,
            IsActive = true
        }).ToArray();
        db.ProviderCredentials.AddRange(credentials);
        await db.SaveChangesAsync();
        return new(user.Id, userKey.Id, model.ModelId, rawUserApiKey, wallet, credentials.Select(x => x.Id).ToArray());
    }

    private static HttpRequestMessage GatewayRequest(string apiKey, string model, bool stream = false)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(new
        {
            model,
            messages = new[] { new { role = "user", content = "test" } },
            stream
        }), Encoding.UTF8, "application/json");
        return request;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }

    private sealed record GatewaySetup(
        Guid UserId,
        Guid UserApiKeyId,
        string ModelId,
        string RawUserApiKey,
        decimal StartingWalletUsd,
        Guid[] CredentialIds);
}
