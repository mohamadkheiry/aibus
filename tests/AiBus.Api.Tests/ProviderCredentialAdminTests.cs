using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AiBus.Api;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AiBus.Api.Tests;

public sealed class ProviderCredentialAdminTests(TestAppFactory factory) : IClassFixture<TestAppFactory>
{
    private readonly HttpClient _client = factory.CreateClient();

    [Fact]
    public async Task Only_super_admin_can_update_or_delete_provider_credentials()
    {
        var credentialId = await CreateCredential("authorization", 25m, 20m, 5m);

        var anonymousUpdate = await _client.PutAsJsonAsync($"/api/admin/credentials/{credentialId}/balance", new
        {
            initialBalanceUsd = 30m,
            remainingBalanceUsd = 30m,
            alertThresholdUsd = 4m
        });
        Assert.Equal(HttpStatusCode.Unauthorized, anonymousUpdate.StatusCode);

        var ordinaryUser = await LoginAs("09125550101");
        using (var updateRequest = Authorized(HttpMethod.Put, $"/api/admin/credentials/{credentialId}/balance", ordinaryUser.Token, new
               {
                   initialBalanceUsd = 30m,
                   remainingBalanceUsd = 30m,
                   alertThresholdUsd = 4m
               }))
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(updateRequest)).StatusCode);

        using (var deleteRequest = Authorized(HttpMethod.Delete, $"/api/admin/credentials/{credentialId}", ordinaryUser.Token))
            Assert.Equal(HttpStatusCode.Forbidden, (await _client.SendAsync(deleteRequest)).StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var unchanged = await db.ProviderCredentials.AsNoTracking().SingleAsync(x => x.Id == credentialId);
        Assert.Equal(25m, unchanged.InitialBalanceUsd);
        Assert.Equal(20m, unchanged.RemainingBalanceUsd);
    }

    [Fact]
    public async Task Super_admin_can_update_balances_without_clearing_real_quota_state_and_legacy_payload_is_supported()
    {
        var credentialId = await CreateCredential("balance-update", 50m, 0m, 7m, quotaExhausted: true);
        var admin = await LoginAs(SuperAdministrators.PrimaryMobile);

        using (var updateRequest = Authorized(HttpMethod.Put, $"/api/admin/credentials/{credentialId}/balance", admin.Token, new
               {
                   initialBalanceUsd = 120.75m,
                   remainingBalanceUsd = 95.125m,
                   alertThresholdUsd = 12.5m
               }))
        {
            var updateResponse = await _client.SendAsync(updateRequest);
            Assert.Equal(HttpStatusCode.NoContent, updateResponse.StatusCode);
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var updated = await db.ProviderCredentials.AsNoTracking().SingleAsync(x => x.Id == credentialId);
            Assert.Equal(120.75m, updated.InitialBalanceUsd);
            Assert.Equal(95.125m, updated.RemainingBalanceUsd);
            Assert.Equal(12.5m, updated.AlertThresholdUsd);
            Assert.Equal("raw quota diagnostic", updated.LastError);
            Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode, updated.LastErrorCode);
            Assert.NotNull(updated.LastErrorAtUtc);

            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x =>
                x.Action == "provider_credential.balance.update" && x.EntityId == credentialId.ToString());
            Assert.Contains("120.75", audit.DetailsJson);
            Assert.Contains("95.125", audit.DetailsJson);
            Assert.DoesNotContain("provider-secret", audit.DetailsJson);
        }

        using (var compatibleRequest = Authorized(HttpMethod.Put, $"/api/admin/credentials/{credentialId}/balance", admin.Token, new
               {
                   remainingBalanceUsd = 80.5m,
                   alertThresholdUsd = 11m
               }))
            Assert.Equal(HttpStatusCode.NoContent, (await _client.SendAsync(compatibleRequest)).StatusCode);

        foreach (var invalidBody in new object[]
                 {
                     new { initialBalanceUsd = -1m, remainingBalanceUsd = 0m, alertThresholdUsd = 0m },
                     new { initialBalanceUsd = 1m, remainingBalanceUsd = 1_000_000_001m, alertThresholdUsd = 0m },
                     new { initialBalanceUsd = 1m, remainingBalanceUsd = 1m, alertThresholdUsd = -0.01m },
                     new { initialBalanceUsd = 10m, remainingBalanceUsd = 10.01m, alertThresholdUsd = 1m },
                     new { initialBalanceUsd = 0m, remainingBalanceUsd = 0.01m, alertThresholdUsd = 0m }
                 })
        {
            using var invalidRequest = Authorized(HttpMethod.Put, $"/api/admin/credentials/{credentialId}/balance", admin.Token, invalidBody);
            var invalidResponse = await _client.SendAsync(invalidRequest);
            Assert.Equal(HttpStatusCode.BadRequest, invalidResponse.StatusCode);
            using var error = JsonDocument.Parse(await invalidResponse.Content.ReadAsStringAsync());
            Assert.Equal("invalid_credential_balance", error.RootElement.GetProperty("code").GetString());
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var unchanged = await db.ProviderCredentials.AsNoTracking().SingleAsync(x => x.Id == credentialId);
            Assert.Equal(120.75m, unchanged.InitialBalanceUsd);
            Assert.Equal(80.5m, unchanged.RemainingBalanceUsd);
            Assert.Equal(11m, unchanged.AlertThresholdUsd);
            Assert.Equal(ProviderErrorMapper.QuotaExhaustedCode, unchanged.LastErrorCode);

            var providerId = unchanged.ProviderId;
            using var invalidCreate = Authorized(HttpMethod.Post, $"/api/admin/providers/{providerId}/credentials", admin.Token, new
            {
                label = "invalid-invariant",
                apiKey = "must-not-be-stored",
                isActive = true,
                initialBalanceUsd = 5m,
                remainingBalanceUsd = 6m,
                alertThresholdUsd = 1m
            });
            var invalidCreateResponse = await _client.SendAsync(invalidCreate);
            Assert.Equal(HttpStatusCode.BadRequest, invalidCreateResponse.StatusCode);
            Assert.False(await db.ProviderCredentials.AnyAsync(x => x.Label == "invalid-invariant"));
        }
    }

    [Fact]
    public async Task Super_admin_delete_preserves_usage_history_and_audit_does_not_contain_secret()
    {
        var credentialId = await CreateCredential("delete-with-history", 10m, 8m, 2m);
        Guid usageId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            var model = await db.Models.FirstAsync();
            var key = new UserApiKey
            {
                UserId = user.Id,
                Name = "provider-delete-history",
                KeyHash = Hashing.Sha256($"delete-history-{Guid.NewGuid():N}"),
                KeyPrefix = "delete-histo"
            };
            var usage = new UsageRecord
            {
                UserId = user.Id,
                UserApiKeyId = key.Id,
                ModelId = model.Id,
                ProviderCredentialId = credentialId,
                ModelName = model.ModelId,
                ProviderName = "history-provider",
                InputTokens = 10,
                OutputTokens = 5,
                CostUsd = 0.01m,
                DurationMs = 20,
                TraceId = $"delete-history-{Guid.NewGuid():N}"
            };
            db.UserApiKeys.Add(key);
            db.UsageRecords.Add(usage);
            await db.SaveChangesAsync();
            usageId = usage.Id;
        }

        var admin = await LoginAs(SuperAdministrators.PrimaryMobile);
        using (var missingConfirmation = Authorized(HttpMethod.Delete, $"/api/admin/credentials/{credentialId}", admin.Token))
        {
            var missingResponse = await _client.SendAsync(missingConfirmation);
            Assert.Equal(HttpStatusCode.BadRequest, missingResponse.StatusCode);
            using var error = JsonDocument.Parse(await missingResponse.Content.ReadAsStringAsync());
            Assert.Equal("credential_delete_confirmation_required", error.RootElement.GetProperty("code").GetString());
        }
        using (var wrongConfirmation = Authorized(HttpMethod.Delete, $"/api/admin/credentials/{credentialId}", admin.Token, new { confirmation = "DELETE" }))
        {
            var wrongResponse = await _client.SendAsync(wrongConfirmation);
            Assert.Equal(HttpStatusCode.BadRequest, wrongResponse.StatusCode);
            using var guardScope = factory.Services.CreateScope();
            Assert.True(await guardScope.ServiceProvider.GetRequiredService<AppDbContext>().ProviderCredentials.AnyAsync(x => x.Id == credentialId));
        }

        using var inFlightScope = factory.Services.CreateScope();
        var inFlightDb = inFlightScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var selectedBeforeDelete = await inFlightDb.ProviderCredentials.SingleAsync(x => x.Id == credentialId);
        var inFlightUser = await inFlightDb.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
        var inFlightKey = await inFlightDb.UserApiKeys.SingleAsync(x => x.Name == "provider-delete-history");
        var inFlightModel = await inFlightDb.Models.FirstAsync();

        using var deleteRequest = Authorized(HttpMethod.Delete, $"/api/admin/credentials/{credentialId}", admin.Token, new { confirmation = "حذف" });
        var deleteResponse = await _client.SendAsync(deleteRequest);
        Assert.Equal(HttpStatusCode.NoContent, deleteResponse.StatusCode);
        Assert.Empty(await deleteResponse.Content.ReadAsByteArrayAsync());

        selectedBeforeDelete.RequestCount++;
        selectedBeforeDelete.LastUsedAtUtc = DateTime.UtcNow;
        var lateUsage = new UsageRecord
        {
            UserId = inFlightUser.Id,
            UserApiKeyId = inFlightKey.Id,
            ModelId = inFlightModel.Id,
            ProviderCredentialId = credentialId,
            ModelName = inFlightModel.ModelId,
            ProviderName = "history-provider",
            InputTokens = 20,
            OutputTokens = 10,
            CostUsd = 0.02m,
            DurationMs = 25,
            TraceId = $"late-record-{Guid.NewGuid():N}"
        };
        inFlightDb.UsageRecords.Add(lateUsage);
        await inFlightDb.SaveChangesAsync();

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        var tombstone = await verificationDb.ProviderCredentials.AsNoTracking().SingleAsync(x => x.Id == credentialId);
        Assert.False(tombstone.IsActive);
        Assert.Equal("", tombstone.ProtectedApiKey);
        Assert.Equal(1, tombstone.RequestCount);
        var retainedUsage = await verificationDb.UsageRecords.AsNoTracking().SingleAsync(x => x.Id == usageId);
        Assert.Equal(credentialId, retainedUsage.ProviderCredentialId);
        Assert.Equal(0.01m, retainedUsage.CostUsd);
        var retainedLateUsage = await verificationDb.UsageRecords.AsNoTracking().SingleAsync(x => x.Id == lateUsage.Id);
        Assert.Equal(credentialId, retainedLateUsage.ProviderCredentialId);
        Assert.Equal(0.02m, retainedLateUsage.CostUsd);
        var audit = await verificationDb.AuditLogs.AsNoTracking().SingleAsync(x =>
            x.Action == "provider_credential.delete" && x.EntityId == credentialId.ToString());
        Assert.Contains("delete-with-history", audit.DetailsJson);
        Assert.Contains("\"retainedUsageRecords\":1", audit.DetailsJson);
        Assert.Contains("\"deletionMode\":\"tombstone\"", audit.DetailsJson);
        Assert.DoesNotContain("provider-secret", audit.DetailsJson);

        using (var providersRequest = Authorized(HttpMethod.Get, "/api/admin/providers", admin.Token))
        {
            var providersResponse = await _client.SendAsync(providersRequest);
            providersResponse.EnsureSuccessStatusCode();
            using var providers = JsonDocument.Parse(await providersResponse.Content.ReadAsStringAsync());
            Assert.DoesNotContain(providers.RootElement.EnumerateArray().SelectMany(x => x.GetProperty("credentials").EnumerateArray()), x =>
                x.GetProperty("id").GetGuid() == credentialId);
        }

        using (var deletedBalanceUpdate = Authorized(HttpMethod.Put, $"/api/admin/credentials/{credentialId}/balance", admin.Token, new
               {
                   initialBalanceUsd = 10m,
                   remainingBalanceUsd = 5m,
                   alertThresholdUsd = 1m
               }))
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(deletedBalanceUpdate)).StatusCode);
        using (var deletedTest = Authorized(HttpMethod.Post, $"/api/admin/credentials/{credentialId}/test", admin.Token))
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(deletedTest)).StatusCode);

        using var repeatDelete = Authorized(HttpMethod.Delete, $"/api/admin/credentials/{credentialId}", admin.Token, new { confirmation = "حذف" });
        Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(repeatDelete)).StatusCode);
    }

    private async Task<Guid> CreateCredential(string label, decimal initial, decimal remaining, decimal threshold, bool quotaExhausted = false)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var secrets = scope.ServiceProvider.GetRequiredService<SecretProtector>();
        var provider = await db.Providers.FirstAsync();
        var credential = new ProviderCredential
        {
            ProviderId = provider.Id,
            Label = label,
            ProtectedApiKey = secrets.Protect("provider-secret-" + Guid.NewGuid().ToString("N")),
            InitialBalanceUsd = initial,
            RemainingBalanceUsd = remaining,
            AlertThresholdUsd = threshold,
            LastError = quotaExhausted ? "raw quota diagnostic" : null,
            LastErrorCode = quotaExhausted ? ProviderErrorMapper.QuotaExhaustedCode : null,
            LastErrorAtUtc = quotaExhausted ? DateTime.UtcNow : null
        };
        db.ProviderCredentials.Add(credential);
        await db.SaveChangesAsync();
        return credential.Id;
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
}
