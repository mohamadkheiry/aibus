using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiBus.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiBus.Api.Tests;

public sealed class UserApiKeyLimitTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Limits_endpoint_requires_authentication_and_hides_keys_owned_by_another_user()
    {
        var owner = await LoginAs("09126660101");
        var otherUser = await LoginAs("09126660102");
        var key = await CreateKey("09126660101", isActive: true);

        var anonymous = await _client.PutAsJsonAsync($"/api/keys/{key.Id}/limits", new
        {
            requestLimit = 20,
            spendLimitUsd = 4m
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using (var otherRequest = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}/limits", otherUser.Token, new
               {
                   requestLimit = 20,
                   spendLimitUsd = 4m
               }))
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(otherRequest)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unchanged = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == key.Id);
        Assert.Equal(50, unchanged.RequestLimit);
        Assert.Equal(10m, unchanged.SpendLimitUsd);
        Assert.Equal(12, unchanged.RequestCount);
        Assert.Equal(3.5m, unchanged.SpentUsd);
        Assert.False(await db.AuditLogs.AnyAsync(x => x.Action == "user_api_key.limits.update" && x.EntityId == key.Id.ToString()));
        Assert.False(string.IsNullOrWhiteSpace(owner.Token));
    }

    [Fact]
    public async Task Owner_can_lower_or_remove_limits_without_resetting_usage_counters()
    {
        const string mobile = "09126660201";
        var owner = await LoginAs(mobile);
        var key = await CreateKey(mobile, isActive: true);

        using (var lowerRequest = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}/limits", owner.Token, new
               {
                   requestLimit = 5,
                   spendLimitUsd = 2m
               }))
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(lowerRequest)).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var updated = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == key.Id);
            Assert.Equal(5, updated.RequestLimit);
            Assert.Equal(2m, updated.SpendLimitUsd);
            Assert.Equal(12, updated.RequestCount);
            Assert.Equal(3.5m, updated.SpentUsd);
            Assert.Equal(key.LastUsedAtUtc, updated.LastUsedAtUtc);
            Assert.Equal("limit-regression", updated.Name);
            Assert.True(updated.IsActive);
            Assert.Equal("allow", updated.AccessMode);
            Assert.Equal(JsonSerializer.Serialize(new[] { key.ModelId }), updated.ModelRulesJson);

            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x =>
                x.Action == "user_api_key.limits.update" && x.EntityId == key.Id.ToString());
            Assert.Contains("\"RequestLimit\":50", audit.DetailsJson);
            Assert.Contains("\"RequestLimit\":5", audit.DetailsJson);
            Assert.Contains("\"SpendLimitUsd\":2", audit.DetailsJson);
            Assert.DoesNotContain(key.RawApiKey, audit.DetailsJson);
        }

        var requestLimited = await CallGateway(key.RawApiKey, key.ModelId);
        Assert.Equal(HttpStatusCode.TooManyRequests, requestLimited.StatusCode);
        Assert.Contains("request_limit", await requestLimited.Content.ReadAsStringAsync());

        using (var spendOnlyRequest = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}/limits", owner.Token, new
               {
                   requestLimit = (int?)null,
                   spendLimitUsd = 2m
               }))
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(spendOnlyRequest)).StatusCode);

        var spendLimited = await CallGateway(key.RawApiKey, key.ModelId);
        Assert.Equal(HttpStatusCode.TooManyRequests, spendLimited.StatusCode);
        Assert.Contains("spend_limit", await spendLimited.Content.ReadAsStringAsync());

        using (var unlimitedRequest = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}/limits", owner.Token, new
               {
                   requestLimit = (int?)null,
                   spendLimitUsd = (decimal?)null
               }))
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(unlimitedRequest)).StatusCode);

        var unlimited = await CallGateway(key.RawApiKey, key.ModelId);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, unlimited.StatusCode);
        Assert.DoesNotContain("request_limit", await unlimited.Content.ReadAsStringAsync());

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var final = await verificationDb.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == key.Id);
        Assert.Null(final.RequestLimit);
        Assert.Null(final.SpendLimitUsd);
        Assert.Equal(12, final.RequestCount);
        Assert.Equal(3.5m, final.SpentUsd);
        Assert.Equal(3, await verificationDb.AuditLogs.CountAsync(x =>
            x.Action == "user_api_key.limits.update" && x.EntityId == key.Id.ToString()));
    }

    [Fact]
    public async Task Invalid_limits_are_rejected_consistently_and_inactive_keys_remain_inactive()
    {
        const string mobile = "09126660301";
        var owner = await LoginAs(mobile);
        var key = await CreateKey(mobile, isActive: false);

        foreach (var invalidBody in new object[]
                 {
                     new { requestLimit = -1, spendLimitUsd = (decimal?)1m },
                     new { requestLimit = 1_000_000_001, spendLimitUsd = (decimal?)1m },
                     new { requestLimit = 1, spendLimitUsd = (decimal?)-0.01m },
                     new { requestLimit = 1, spendLimitUsd = (decimal?)1_000_000_001m }
                 })
        {
            using var invalidRequest = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}/limits", owner.Token, invalidBody);
            var invalidResponse = await _client.SendAsync(invalidRequest);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            using var error = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
            Assert.Equal("invalid_api_key_limits", error.RootElement.GetProperty("code").GetString());
        }

        using (var validInactiveRequest = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}/limits", owner.Token, new
               {
                   requestLimit = 0,
                   spendLimitUsd = 0m
               }))
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(validInactiveRequest)).StatusCode);

        using (var oldRouteBypass = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}", owner.Token, new
               {
                   name = "attempted-bypass",
                   isActive = true,
                   requestLimit = -1,
                   spendLimitUsd = 1m,
                   accessMode = "all",
                   modelRules = Array.Empty<string>()
               }))
        {
            var bypassResponse = await _client.SendAsync(oldRouteBypass);
            Assert.Equal(HttpStatusCode.BadRequest, bypassResponse.StatusCode);
        }

        using (var validOldRoute = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}", owner.Token, new
               {
                   name = "legacy-route-audited",
                   isActive = false,
                   requestLimit = 2,
                   spendLimitUsd = 1m,
                   accessMode = "allow",
                   modelRules = new[] { key.ModelId }
               }))
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(validOldRoute)).StatusCode);

        using (var invalidCreate = Authorized(HttpMethod.Post, "/api/keys", owner.Token, new
               {
                   name = "invalid-create",
                   requestLimit = 1,
                   spendLimitUsd = -1m,
                   accessMode = "all",
                   modelRules = Array.Empty<string>()
               }))
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(invalidCreate)).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var unchanged = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == key.Id);
            Assert.Equal(2, unchanged.RequestLimit);
            Assert.Equal(1m, unchanged.SpendLimitUsd);
            Assert.Equal(12, unchanged.RequestCount);
            Assert.Equal(3.5m, unchanged.SpentUsd);
            Assert.False(unchanged.IsActive);
            Assert.Equal("legacy-route-audited", unchanged.Name);
            Assert.False(await db.UserApiKeys.AnyAsync(x => x.Name == "invalid-create"));
            var audits = await db.AuditLogs.AsNoTracking().Where(x =>
                x.Action == "user_api_key.limits.update" && x.EntityId == key.Id.ToString()).ToListAsync();
            Assert.Equal(2, audits.Count);
            var legacyAudit = Assert.Single(audits, x => x.DetailsJson.Contains("\"source\":\"full_update\""));
            Assert.Contains("\"RequestLimit\":0", legacyAudit.DetailsJson);
            Assert.Contains("\"RequestLimit\":2", legacyAudit.DetailsJson);
            Assert.Contains("\"SpendLimitUsd\":1", legacyAudit.DetailsJson);
            Assert.DoesNotContain(key.RawApiKey, legacyAudit.DetailsJson);
        }

        using (var deleteRequest = Authorized(HttpMethod.Delete, $"/api/keys/{key.Id}", owner.Token))
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(deleteRequest)).StatusCode);
        using (var archivedRequest = Authorized(HttpMethod.Put, $"/api/keys/{key.Id}/limits", owner.Token, new
               {
                   requestLimit = 10,
                   spendLimitUsd = 2m
               }))
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(archivedRequest)).StatusCode);
    }

    private async Task<TestKey> CreateKey(string mobile, bool isActive)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var user = await db.Users.SingleAsync(x => x.Mobile == mobile);
        var model = await db.Models.FirstAsync(x => x.IsActive && x.Provider!.IsActive);
        var raw = $"aibus_limit_test_{Guid.NewGuid():N}";
        var lastUsedAtUtc = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);
        var key = new UserApiKey
        {
            UserId = user.Id,
            Name = "limit-regression",
            KeyHash = Hashing.Sha256(raw),
            KeyPrefix = raw[..12],
            ProtectedApiKey = secrets.Protect(raw),
            IsActive = isActive,
            RequestLimit = 50,
            SpendLimitUsd = 10m,
            RequestCount = 12,
            SpentUsd = 3.5m,
            AccessMode = "allow",
            ModelRulesJson = JsonSerializer.Serialize(new[] { model.ModelId }),
            LastUsedAtUtc = lastUsedAtUtc
        };
        user.WalletUsd = 100m;
        db.UserApiKeys.Add(key);
        await db.SaveChangesAsync();
        return new(key.Id, raw, model.ModelId, lastUsedAtUtc);
    }

    private async Task<HttpResponseMessage> CallGateway(string rawApiKey, string modelId)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawApiKey);
        request.Content = JsonContent.Create(new { model = modelId, messages = new[] { new { role = "user", content = "limit test" } } });
        return await _client.SendAsync(request);
    }

    private async Task<LoginResponse> LoginAs(string mobile)
    {
        var otpResponse = await _client.PostAsJsonAsync("/api/auth/request-otp", new { mobile });
        otpResponse.EnsureSuccessStatusCode();
        var otp = await otpResponse.Content.ReadFromJsonAsync<OtpResponse>();
        var loginResponse = await _client.PostAsJsonAsync("/api/auth/verify-otp", new { mobile, code = otp!.DebugCode });
        loginResponse.EnsureSuccessStatusCode();
        return (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;
    }

    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }

    private sealed record OtpResponse(string? DebugCode);
    private sealed record LoginResponse(string Token);
    private sealed record TestKey(Guid Id, string RawApiKey, string ModelId, DateTime LastUsedAtUtc);
}
