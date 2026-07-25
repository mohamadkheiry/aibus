using System.Net;
using System.Net.Http.Json;
using AiBus.Api;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace AiBus.Api.Tests;

public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aibus-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.RemoveAll<AppDbContext>();
            services.AddDbContext<AppDbContext>(o => o.UseSqlite($"Data Source={_dbPath}"));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { if (File.Exists(_dbPath)) File.Delete(_dbPath); } catch (IOException) { /* OS releases SQLite WAL shortly after host shutdown. */ }
    }
}

public sealed class GatewayTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Health_endpoint_is_ready()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Super_admin_can_login_with_development_otp()
    {
        var otp = await _client.PostAsJsonAsync("/api/auth/request-otp", new { mobile = "09015909044" });
        otp.EnsureSuccessStatusCode();
        var requested = await otp.Content.ReadFromJsonAsync<OtpResponse>();
        Assert.NotNull(requested?.DebugCode);

        var verify = await _client.PostAsJsonAsync("/api/auth/verify-otp", new { mobile = "09015909044", code = requested!.DebugCode });
        verify.EnsureSuccessStatusCode();
        var result = await verify.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(result?.Token));
        Assert.Equal(Roles.SuperAdmin, result?.User.Role);

        const string rawApiKey = "aibus_integration_test_key";
        string modelId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(x => x.Mobile == "09015909044");
            var model = await db.Models.FirstAsync();
            var key = new UserApiKey
            {
                UserId = user.Id,
                Name = "تست یکپارچگی",
                KeyHash = Hashing.Sha256(rawApiKey),
                KeyPrefix = rawApiKey[..12],
                SpentUsd = 1.234567m,
                RequestCount = 2
            };
            user.WalletUsd = 12.345678m;
            db.UserApiKeys.Add(key);
            db.UsageRecords.Add(new UsageRecord
            {
                UserId = user.Id,
                UserApiKeyId = key.Id,
                ModelId = model.Id,
                ModelName = model.ModelId,
                ProviderName = "integration",
                InputTokens = 123,
                OutputTokens = 45,
                CostUsd = 0.012345m,
                DurationMs = 321,
                TraceId = "integration-trace"
            });
            await db.SaveChangesAsync();
            modelId = model.ModelId;
        }

        var endpoints = new[]
        {
            "/api/me",
            "/api/models",
            "/api/keys",
            "/api/dashboard",
            "/api/dashboard?from=2020-01-01&to=2030-01-01",
            "/api/usage?page=1&pageSize=20",
            "/api/wallet/quote?amountUsd=10",
            "/api/admin/settings",
            "/api/admin/providers",
            "/api/admin/models",
            "/api/admin/users",
            "/api/admin/users?sort=wallet",
            "/api/admin/users?sort=requests",
            $"/api/admin/users/{result!.User.Id}/keys",
            "/api/admin/dashboard",
            "/api/admin/visits?page=1&pageSize=20"
        };
        foreach (var endpoint in endpoints)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.Token);
            var response = await _client.SendAsync(request);
            Assert.True(response.IsSuccessStatusCode, $"{endpoint} returned {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        }

        using var gatewayRequest = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        gatewayRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawApiKey);
        gatewayRequest.Content = JsonContent.Create(new { model = modelId, messages = new[] { new { role = "user", content = "test" } } });
        var gateway = await _client.SendAsync(gatewayRequest);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, gateway.StatusCode);
    }

    [Fact]
    public async Task Gateway_requires_a_user_api_key()
    {
        var response = await _client.PostAsJsonAsync("/v1/chat/completions", new { model = "gpt-5.6-luna", messages = new[] { new { role = "user", content = "hello" } } });
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public void Hashing_is_stable_and_keys_are_prefixed()
    {
        Assert.Equal(Hashing.Sha256("secret"), Hashing.Sha256("secret"));
        Assert.NotEqual(Hashing.Sha256("secret"), Hashing.Sha256("other"));
        Assert.StartsWith("aibus_", Hashing.RandomApiKey());
    }

    private sealed record OtpResponse(string? DebugCode);
    private sealed record LoginResponse(string Token, LoginUser User);
    private sealed record LoginUser(string Id, string Role);
}
