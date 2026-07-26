using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

        using (var catalogRequest = Authorized(HttpMethod.Get, "/api/models", result.Token))
        {
            var catalogResponse = await _client.SendAsync(catalogRequest);
            catalogResponse.EnsureSuccessStatusCode();
            using var catalog = JsonDocument.Parse(await catalogResponse.Content.ReadAsStringAsync());
            var models = catalog.RootElement.EnumerateArray().ToArray();
            Assert.True(models.Length >= 90, $"Expected the full AI catalog, received {models.Length} services.");
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "speech_to_text");
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "text_to_speech");
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "speech_to_speech" && x.GetProperty("supportsWebSocket").GetBoolean());
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "realtime_translation");
            Assert.Contains(models, x => x.GetProperty("pricingComponents").EnumerateArray().Any(p => p.GetProperty("unit").GetString() == "minute"));
            Assert.Contains(models, x => x.GetProperty("pricingComponents").EnumerateArray().Any(p => p.GetProperty("unit").GetString() == "million_audio_tokens"));
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
    public async Task User_and_super_admin_can_exchange_support_ticket_messages()
    {
        var userOtpResponse = await _client.PostAsJsonAsync("/api/auth/request-otp", new { mobile = "09123456789" });
        userOtpResponse.EnsureSuccessStatusCode();
        var userOtp = await userOtpResponse.Content.ReadFromJsonAsync<OtpResponse>();
        var userLoginResponse = await _client.PostAsJsonAsync("/api/auth/verify-otp", new { mobile = "09123456789", code = userOtp!.DebugCode });
        userLoginResponse.EnsureSuccessStatusCode();
        var userLogin = await userLoginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        using var createRequest = Authorized(HttpMethod.Post, "/api/tickets", userLogin!.Token, new { subject = "خطای فراخوانی مدل", category = "technical", priority = "high", message = "هنگام فراخوانی API خطای آزمایشی دریافت می‌کنم." });
        var createResponse = await _client.SendAsync(createRequest);
        createResponse.EnsureSuccessStatusCode();
        using var created = JsonDocument.Parse(await createResponse.Content.ReadAsStringAsync());
        var ticketId = created.RootElement.GetProperty("id").GetString();
        Assert.False(string.IsNullOrWhiteSpace(ticketId));

        var adminOtpResponse = await _client.PostAsJsonAsync("/api/auth/request-otp", new { mobile = "09015909044" });
        adminOtpResponse.EnsureSuccessStatusCode();
        var adminOtp = await adminOtpResponse.Content.ReadFromJsonAsync<OtpResponse>();
        var adminLoginResponse = await _client.PostAsJsonAsync("/api/auth/verify-otp", new { mobile = "09015909044", code = adminOtp!.DebugCode });
        adminLoginResponse.EnsureSuccessStatusCode();
        var adminLogin = await adminLoginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        using var adminListRequest = Authorized(HttpMethod.Get, "/api/admin/tickets?status=open", adminLogin!.Token);
        var adminListResponse = await _client.SendAsync(adminListRequest);
        adminListResponse.EnsureSuccessStatusCode();
        var listJson = await adminListResponse.Content.ReadAsStringAsync();
        Assert.Contains("خطای فراخوانی مدل", listJson);

        using var replyRequest = Authorized(HttpMethod.Post, $"/api/admin/tickets/{ticketId}/messages", adminLogin.Token, new { message = "پاسخ پشتیبانی آزمایشی ثبت شد." });
        (await _client.SendAsync(replyRequest)).EnsureSuccessStatusCode();
        using var resolveRequest = Authorized(HttpMethod.Put, $"/api/admin/tickets/{ticketId}", adminLogin.Token, new { status = "resolved", priority = "high" });
        (await _client.SendAsync(resolveRequest)).EnsureSuccessStatusCode();

        using var userDetailRequest = Authorized(HttpMethod.Get, $"/api/tickets/{ticketId}", userLogin.Token);
        var userDetailResponse = await _client.SendAsync(userDetailRequest);
        userDetailResponse.EnsureSuccessStatusCode();
        using var detail = JsonDocument.Parse(await userDetailResponse.Content.ReadAsStringAsync());
        Assert.Equal("resolved", detail.RootElement.GetProperty("status").GetString());
        Assert.Equal(2, detail.RootElement.GetProperty("messages").GetArrayLength());
        Assert.True(detail.RootElement.GetProperty("messages")[1].GetProperty("isStaff").GetBoolean());
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
    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
