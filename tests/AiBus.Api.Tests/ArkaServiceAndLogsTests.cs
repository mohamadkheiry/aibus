using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AiBus.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiBus.Api.Tests;

public sealed class ArkaServiceAndLogsTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Arka_is_listed_as_keyless_provider_and_rejects_provider_keys()
    {
        Guid providerId;
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            providerId = (await db.Providers.AsNoTracking().SingleAsync(x => x.Slug == "arka")).Id;
            var admin = await db.Users.AsNoTracking().SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            token = scope.ServiceProvider.GetRequiredService<TokenService>().Create(admin);
        }

        using (var providersRequest = Authorized(HttpMethod.Get, "/api/admin/providers", token))
        {
            var providersResponse = await _client.SendAsync(providersRequest);
            providersResponse.EnsureSuccessStatusCode();
            using var providers = JsonDocument.Parse(await providersResponse.Content.ReadAsStringAsync());
            var arka = providers.RootElement.EnumerateArray().Single(x => x.GetProperty("slug").GetString() == "arka");
            Assert.False(arka.GetProperty("requiresApiKey").GetBoolean());
            Assert.Empty(arka.GetProperty("credentials").EnumerateArray());
        }

        using var createKey = Authorized(HttpMethod.Post, $"/api/admin/providers/{providerId}/credentials", token, new
        {
            label = "must-be-rejected",
            apiKey = "must-not-be-stored",
            isActive = true,
            initialBalanceUsd = 0,
            remainingBalanceUsd = 0,
            alertThresholdUsd = 0
        });
        var createKeyResponse = await _client.SendAsync(createKey);
        Assert.Equal(HttpStatusCode.BadRequest, createKeyResponse.StatusCode);
        using var error = JsonDocument.Parse(await createKeyResponse.Content.ReadAsStringAsync());
        Assert.Equal("arka_provider_key_not_required", error.RootElement.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Admin_can_publish_arka_service_with_explicit_io_and_private_upstream_route()
    {
        Guid providerId;
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            providerId = (await db.Providers.AsNoTracking().SingleAsync(x => x.Slug == "arka")).Id;
            var admin = await db.Users.AsNoTracking().SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            token = scope.ServiceProvider.GetRequiredService<TokenService>().Create(admin);
        }

        var modelId = $"arka-integration-{Guid.NewGuid():N}";
        using var create = Authorized(HttpMethod.Post, "/api/admin/models", token, new
        {
            providerId,
            modelId,
            displayName = "ARKA Integration",
            modality = "text-image-video",
            inputModalities = new[] { "text", "image" },
            outputModality = "video",
            inputPricePerMillionUsd = 1.25m,
            outputPricePerMillionUsd = 4.5m,
            cachedInputPricePerMillionUsd = (decimal?)null,
            contextWindow = 128000,
            supportsStreaming = false,
            supportsWebSocket = false,
            isActive = true,
            pricingSourceUrl = "",
            testPayloadJson = JsonSerializer.Serialize(new { model = modelId, prompt = "test" }),
            serviceType = "video_generation",
            endpointPath = "/v1/videos",
            upstreamBaseUrl = "https://arka-upstream.example/v1",
            upstreamPath = "/video/generations",
            region = "internal",
            isPreview = false,
            pricingComponents = new[] { new { label = "ورودی", unit = "million_text_tokens", priceUsd = 1.25m, note = "" } },
            pricingNotes = "تعرفه داخلی"
        });
        var response = await _client.SendAsync(create);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var list = Authorized(HttpMethod.Get, "/api/admin/models", token);
        using var catalog = JsonDocument.Parse(await (await _client.SendAsync(list)).Content.ReadAsStringAsync());
        var saved = catalog.RootElement.EnumerateArray().Single(x => x.GetProperty("modelId").GetString() == modelId);
        Assert.Equal("arka", saved.GetProperty("providerSlug").GetString());
        Assert.Equal(new[] { "text", "image" }, saved.GetProperty("inputModalities").EnumerateArray().Select(x => x.GetString()).ToArray());
        Assert.Equal("video", saved.GetProperty("outputModality").GetString());
        Assert.Equal("https://arka-upstream.example/v1", saved.GetProperty("upstreamBaseUrl").GetString());
        Assert.Equal("/video/generations", saved.GetProperty("upstreamPath").GetString());
    }

    [Fact]
    public async Task Request_logs_and_user_report_are_available_without_body_storage()
    {
        string token;
        const string forbiddenBody = "THIS-MUST-NEVER-BE-STORED-AS-A-LOG-BODY";
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            token = scope.ServiceProvider.GetRequiredService<TokenService>().Create(admin);
            var model = await db.Models.FirstAsync();
            var key = new UserApiKey { UserId = admin.Id, Name = "privacy-test", KeyHash = Hashing.Sha256(Guid.NewGuid().ToString()), KeyPrefix = "aibus_privacy" };
            db.UserApiKeys.Add(key);
            db.UsageRecords.Add(new UsageRecord
            {
                UserId = admin.Id, UserApiKeyId = key.Id, ModelId = model.Id, ModelName = model.ModelId,
                ProviderName = "ARKA", InputTokens = 10, OutputTokens = 20, CostUsd = .01m,
                DurationMs = 123, Status = "failed", HttpStatus = 429, EndpointPath = "/v1/chat/completions",
                TraceId = $"privacy-{Guid.NewGuid():N}"
            });
            await db.SaveChangesAsync();

            var connection = db.Database.GetDbConnection();
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(\"UsageRecords\")";
            await using var reader = await command.ExecuteReaderAsync();
            var columns = new List<string>();
            while (await reader.ReadAsync()) columns.Add(reader.GetString(1));
            Assert.DoesNotContain(columns, x => x.Contains("Body", StringComparison.OrdinalIgnoreCase)
                || x.Contains("RequestContent", StringComparison.OrdinalIgnoreCase)
                || x.Contains("ResponseContent", StringComparison.OrdinalIgnoreCase));
        }

        using (var logsRequest = Authorized(HttpMethod.Get, "/api/admin/logs?page=1&pageSize=30&search=privacy-test", token))
        {
            var logsResponse = await _client.SendAsync(logsRequest);
            logsResponse.EnsureSuccessStatusCode();
            var json = await logsResponse.Content.ReadAsStringAsync();
            Assert.Contains("\"bodyStored\":false", json);
            Assert.DoesNotContain(forbiddenBody, json);
            using var document = JsonDocument.Parse(json);
            Assert.Equal(429, document.RootElement.GetProperty("items")[0].GetProperty("httpStatus").GetInt32());
        }

        using (var reportRequest = Authorized(HttpMethod.Get, "/api/admin/users/report?sort=requests", token))
        {
            var reportResponse = await _client.SendAsync(reportRequest);
            reportResponse.EnsureSuccessStatusCode();
            Assert.Equal("text/csv", reportResponse.Content.Headers.ContentType?.MediaType);
            Assert.Contains(SuperAdministrators.PrimaryMobile, await reportResponse.Content.ReadAsStringAsync());
        }
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
