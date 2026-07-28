using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using AiBus.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AiBus.Api.Tests;

public sealed class ScriptedProviderHandler : HttpMessageHandler
{
    private Func<HttpRequestMessage, HttpResponseMessage> _responder = _ =>
        JsonResponse(HttpStatusCode.ServiceUnavailable, new { error = new { message = "No test response configured" } });

    public ConcurrentQueue<string> CalledApiKeys { get; } = new();

    public void Reset(Func<HttpRequestMessage, HttpResponseMessage> responder)
    {
        while (CalledApiKeys.TryDequeue(out _)) { }
        _responder = responder;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        CalledApiKeys.Enqueue(request.Headers.Authorization?.Parameter ?? "");
        return Task.FromResult(_responder(request));
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

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite($"Data Source={_dbPath}"));
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

public sealed class ProviderQuotaTests(QuotaGatewayFactory factory) : IClassFixture<QuotaGatewayFactory>
{
    private const string RawQuotaMarker = "UPSTREAM-SECRET-QUOTA-MARKER";
    private readonly HttpClient _client = factory.CreateClient();

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
    public async Task Quota_failure_is_sanitized_marks_key_and_never_charges_user()
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

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Contains(ProviderErrorMapper.QuotaExhaustedCode, responseText);
        Assert.Contains("هزینه‌ای از کیف پول شما کسر نشد", responseText);
        Assert.DoesNotContain(RawQuotaMarker, responseText);
        Assert.DoesNotContain("platform.openai.com", responseText);
        Assert.DoesNotContain("exceeded your current quota", responseText, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == setup.UserId);
        var userKey = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        var providerKey = await db.ProviderCredentials.AsNoTracking().SingleAsync(x => x.Id == setup.CredentialIds[0]);
        var usage = await db.UsageRecords.AsNoTracking().SingleAsync(x => x.UserApiKeyId == setup.UserApiKeyId);

        Assert.Equal(setup.StartingWalletUsd, user.WalletUsd);
        Assert.Equal(0, userKey.RequestCount);
        Assert.Equal(0, userKey.SpentUsd);
        Assert.Equal(0, providerKey.RemainingBalanceUsd);
        Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode, providerKey.LastErrorCode);
        Assert.NotNull(providerKey.LastErrorAtUtc);
        Assert.Contains(RawQuotaMarker, providerKey.LastError);
        Assert.Equal("failed", usage.Status);
        Assert.Equal(0, usage.CostUsd);
    }

    [Fact]
    public async Task Sse_quota_event_without_optional_space_is_sanitized_and_not_charged()
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
        Assert.Contains(ProviderErrorMapper.QuotaExhaustedCode, responseText);
        Assert.Contains("data: [DONE]", responseText);
        Assert.DoesNotContain(RawQuotaMarker, responseText);
        Assert.DoesNotContain("exceeded your current quota", responseText, StringComparison.OrdinalIgnoreCase);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(x => x.Id == setup.UserId);
        var userKey = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == setup.UserApiKeyId);
        var usage = await db.UsageRecords.AsNoTracking().SingleAsync(x => x.UserApiKeyId == setup.UserApiKeyId);

        Assert.Equal(setup.StartingWalletUsd, user.WalletUsd);
        Assert.Equal(0, userKey.RequestCount);
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

    private sealed record GatewaySetup(
        Guid UserId,
        Guid UserApiKeyId,
        string ModelId,
        string RawUserApiKey,
        decimal StartingWalletUsd,
        Guid[] CredentialIds);
}
