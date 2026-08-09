using System.Net;
using System.Net.Http.Json;
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

public sealed class TestAppFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aibus-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.ConfigureLogging(logging => logging.ClearProviders());
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

public sealed class BootstrapOtpFactory : WebApplicationFactory<Program>
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"aibus-bootstrap-tests-{Guid.NewGuid():N}.db");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        builder.UseSetting("Jwt:Key", "aibus-bootstrap-integration-test-key-at-least-32-characters");
        builder.UseSetting("Bootstrap:ExposeSuperAdminOtp", "true");
        builder.UseSetting("Development:ExposeOtp", "false");
        builder.ConfigureLogging(logging => logging.ClearProviders());
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
    public async Task Catalog_snapshot_preserves_administrator_overrides_after_restart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aibus-catalog-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        try
        {
            await using (var db = new AppDbContext(options))
            {
                await SeedData.Initialize(db);
                var model = await db.Models.SingleAsync(x => x.ModelId == "gemini-3.1-flash-image");
                model.OutputPricePerMillionUsd = 999m;
                model.PricingNotes = "administrator override";
                var snapshot = await db.Settings.SingleAsync(x => x.Key == "catalog.model_snapshot");
                snapshot.Value = "2026-07-27";
                await db.SaveChangesAsync();
            }

            await using (var restarted = new AppDbContext(options))
            {
                await SeedData.Initialize(restarted);
                var model = await restarted.Models.AsNoTracking().SingleAsync(x => x.ModelId == "gemini-3.1-flash-image");
                Assert.Equal(999m, model.OutputPricePerMillionUsd);
                Assert.Equal("administrator override", model.PricingNotes);
                Assert.Equal("https://generativelanguage.googleapis.com", model.UpstreamBaseUrl);
                Assert.StartsWith("/v1beta/", model.UpstreamPath);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(dbPath + suffix); } catch (IOException) { /* SQLite can release WAL shortly after disposal. */ }
        }
    }

    [Fact]
    public async Task Catalog_refresh_applies_known_official_price_changes_only_to_unmodified_models()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), $"aibus-catalog-price-{Guid.NewGuid():N}.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={dbPath}").Options;
        try
        {
            await using (var db = new AppDbContext(options))
            {
                await SeedData.Initialize(db);
                var model = await db.Models.SingleAsync(x => x.ModelId == "gpt-5.6-terra");
                model.InputPricePerMillionUsd = 2.5m;
                model.OutputPricePerMillionUsd = 15m;
                model.CachedInputPricePerMillionUsd = .25m;
                model.PricingDetailsJson = JsonSerializer.Serialize(new[]
                {
                    new { Label = "ورودی تا 272K", Unit = "million_text_tokens", PriceUsd = (decimal?)2.5m, Note = (string?)null },
                    new { Label = "ورودی Cache تا 272K", Unit = "million_text_tokens", PriceUsd = (decimal?).25m, Note = (string?)null },
                    new { Label = "خروجی تا 272K", Unit = "million_text_tokens", PriceUsd = (decimal?)15m, Note = (string?)null },
                    new { Label = "ورودی بیش از 272K", Unit = "million_text_tokens", PriceUsd = (decimal?)5m, Note = (string?)null },
                    new { Label = "ورودی Cache بیش از 272K", Unit = "million_text_tokens", PriceUsd = (decimal?).5m, Note = (string?)null },
                    new { Label = "خروجی بیش از 272K", Unit = "million_text_tokens", PriceUsd = (decimal?)22.5m, Note = (string?)null }
                });
                (await db.Settings.SingleAsync(x => x.Key == "catalog.model_snapshot")).Value = "2026-07-27";
                await db.SaveChangesAsync();
            }

            await using (var refreshed = new AppDbContext(options))
            {
                await SeedData.Initialize(refreshed);
                var model = await refreshed.Models.AsNoTracking().SingleAsync(x => x.ModelId == "gpt-5.6-terra");
                Assert.Equal(2m, model.InputPricePerMillionUsd);
                Assert.Equal(12m, model.OutputPricePerMillionUsd);
                Assert.Equal(.2m, model.CachedInputPricePerMillionUsd);
                Assert.Equal(new DateTime(2026, 8, 8, 0, 0, 0, DateTimeKind.Utc), model.PriceSyncedAtUtc);
            }
        }
        finally
        {
            foreach (var suffix in new[] { "", "-wal", "-shm" })
                try { File.Delete(dbPath + suffix); } catch (IOException) { /* SQLite can release WAL shortly after disposal. */ }
        }
    }

    [Fact]
    public async Task Health_endpoint_is_ready()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Central_settings_are_validated_encrypted_and_applied_to_billing()
    {
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var admin = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            token = scope.ServiceProvider.GetRequiredService<TokenService>().Create(admin);
        }

        var payload = new
        {
            dollarRateIrr = 910000,
            feePercent = 7.5m,
            minimumTopUpUsd = 5m,
            maximumTopUpUsd = 2500m,
            smsEnabled = false,
            smsApiKey = "test-sms-secret-value",
            smsTemplateId = 176898,
            otpExpiryMinutes = 3,
            otpRequestLimit = 6,
            otpWindowMinutes = 15,
            otpMaxAttempts = 4,
            zarinpalMerchantId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
            paymentCallbackUrl = "https://aibus.example/api/wallet/callback",
            sessionLifetimeHours = 24,
            requireHttpsCallback = true,
            allowAdminImpersonation = true
        };
        using (var update = Authorized(HttpMethod.Put, "/api/admin/settings", token, payload))
        {
            var response = await _client.SendAsync(update);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        }

        using (var get = Authorized(HttpMethod.Get, "/api/admin/settings", token))
        {
            var response = await _client.SendAsync(get);
            response.EnsureSuccessStatusCode();
            using var settings = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(910000, settings.RootElement.GetProperty("dollarRateIrr").GetInt64());
            Assert.Equal(5m, settings.RootElement.GetProperty("minimumTopUpUsd").GetDecimal());
            Assert.Equal(24, settings.RootElement.GetProperty("sessionLifetimeHours").GetInt32());
            Assert.True(settings.RootElement.GetProperty("paymentConfigured").GetBoolean());
            Assert.True(settings.RootElement.GetProperty("callbackSecure").GetBoolean());
        }

        using (var quote = Authorized(HttpMethod.Get, "/api/wallet/quote?amountUsd=2", token))
        {
            var response = await _client.SendAsync(quote);
            response.EnsureSuccessStatusCode();
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal(5m, result.RootElement.GetProperty("amountUsd").GetDecimal());
            Assert.Equal(5m, result.RootElement.GetProperty("minimumTopUpUsd").GetDecimal());
            Assert.Equal(2500m, result.RootElement.GetProperty("maximumTopUpUsd").GetDecimal());
        }

        using (var tooSmall = Authorized(HttpMethod.Post, "/api/wallet/topup", token, new { amountUsd = 2m }))
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(tooSmall)).StatusCode);

        using (var invalid = Authorized(HttpMethod.Put, "/api/admin/settings", token, new
        {
            payload.dollarRateIrr, payload.feePercent, payload.minimumTopUpUsd, payload.maximumTopUpUsd,
            payload.smsEnabled, payload.smsApiKey, payload.smsTemplateId, payload.otpExpiryMinutes,
            payload.otpRequestLimit, payload.otpWindowMinutes, payload.otpMaxAttempts,
            payload.zarinpalMerchantId, paymentCallbackUrl = "http://insecure.example/callback",
            payload.sessionLifetimeHours, requireHttpsCallback = true, payload.allowAdminImpersonation
        }))
            Assert.Equal(HttpStatusCode.BadRequest, (await _client.SendAsync(invalid)).StatusCode);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var protectedSms = await db.Settings.SingleAsync(x => x.Key == "sms.api_key");
            Assert.True(protectedSms.IsSecret);
            Assert.NotEqual(payload.smsApiKey, protectedSms.Value);
            Assert.Contains(await db.AuditLogs.ToListAsync(), x => x.Action == "settings.update");
        }
    }

    [Fact]
    public async Task Production_bootstrap_otp_is_exposed_only_for_configured_super_admins()
    {
        using var bootstrapFactory = new BootstrapOtpFactory();
        using var bootstrapClient = bootstrapFactory.CreateClient();

        foreach (var mobile in SuperAdministrators.Mobiles)
        {
            var response = await bootstrapClient.PostAsJsonAsync("/api/auth/request-otp", new { mobile });
            response.EnsureSuccessStatusCode();
            var otp = await response.Content.ReadFromJsonAsync<OtpResponse>();
            Assert.NotNull(otp?.DebugCode);
        }

        var ordinaryResponse = await bootstrapClient.PostAsJsonAsync("/api/auth/request-otp", new { mobile = "09120000000" });
        ordinaryResponse.EnsureSuccessStatusCode();
        var ordinaryOtp = await ordinaryResponse.Content.ReadFromJsonAsync<OtpResponse>();
        Assert.Null(ordinaryOtp?.DebugCode);
    }

    [Fact]
    public async Task Roles_can_be_changed_live_except_for_the_primary_super_admin()
    {
        string primaryToken;
        string delegatedToken;
        Guid primaryId;
        Guid delegatedId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var primary = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            var delegated = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.RequestedAdditionalMobile);
            Assert.Equal(Roles.SuperAdmin, primary.Role);
            Assert.Equal(Roles.SuperAdmin, delegated.Role);
            var tokens = scope.ServiceProvider.GetRequiredService<TokenService>();
            primaryToken = tokens.Create(primary);
            delegatedToken = tokens.Create(delegated);
            primaryId = primary.Id;
            delegatedId = delegated.Id;
        }

        using (var demote = Authorized(HttpMethod.Put, $"/api/admin/users/{delegatedId}/role", primaryToken, new { role = Roles.User }))
        {
            var response = await _client.SendAsync(demote);
            response.EnsureSuccessStatusCode();
        }

        using (var scope = factory.Services.CreateScope())
            await SeedData.Initialize(scope.ServiceProvider.GetRequiredService<AppDbContext>());

        using (var staleAdminRequest = Authorized(HttpMethod.Get, "/api/admin/settings", delegatedToken))
        {
            var response = await _client.SendAsync(staleAdminRequest);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using (var demotePrimary = Authorized(HttpMethod.Put, $"/api/admin/users/{primaryId}/role", primaryToken, new { role = Roles.User }))
        {
            var response = await _client.SendAsync(demotePrimary);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        }

        using (var promote = Authorized(HttpMethod.Put, $"/api/admin/users/{delegatedId}/role", primaryToken, new { role = Roles.SuperAdmin }))
            (await _client.SendAsync(promote)).EnsureSuccessStatusCode();

        using var verificationScope = factory.Services.CreateScope();
        var verificationDb = verificationScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(Roles.SuperAdmin, (await verificationDb.Users.FindAsync(delegatedId))!.Role);
        Assert.Contains(await verificationDb.AuditLogs.ToListAsync(), x => x.Action == "user.role.update" && x.EntityId == delegatedId.ToString());
    }

    [Fact]
    public async Task User_can_set_or_clear_an_optional_nickname()
    {
        string token;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleOrDefaultAsync(x => x.Mobile == "09351112233")
                       ?? new AppUser { Mobile = "09351112233", DisplayName = "کاربر 2233" };
            if (db.Entry(user).State == EntityState.Detached) db.Users.Add(user);
            await db.SaveChangesAsync();
            token = scope.ServiceProvider.GetRequiredService<TokenService>().Create(user);
        }

        using (var rename = Authorized(HttpMethod.Put, "/api/me", token, new { displayName = "  محمد  " }))
        {
            var response = await _client.SendAsync(rename);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("محمد", body.RootElement.GetProperty("displayName").GetString());
        }

        using (var clear = Authorized(HttpMethod.Put, "/api/me", token, new { displayName = "   " }))
        {
            var response = await _client.SendAsync(clear);
            response.EnsureSuccessStatusCode();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.Equal("کاربر 2233", body.RootElement.GetProperty("displayName").GetString());
        }
    }

    [Fact]
    public async Task Super_admin_can_login_with_development_otp()
    {
        var otp = await _client.PostAsJsonAsync("/api/auth/request-otp", new { mobile = SuperAdministrators.PrimaryMobile });
        otp.EnsureSuccessStatusCode();
        var requested = await otp.Content.ReadFromJsonAsync<OtpResponse>();
        Assert.NotNull(requested?.DebugCode);

        var verify = await _client.PostAsJsonAsync("/api/auth/verify-otp", new { mobile = SuperAdministrators.PrimaryMobile, code = requested!.DebugCode });
        verify.EnsureSuccessStatusCode();
        var result = await verify.Content.ReadFromJsonAsync<LoginResponse>();
        Assert.False(string.IsNullOrWhiteSpace(result?.Token));
        Assert.Equal(Roles.SuperAdmin, result?.User.Role);

        const string rawApiKey = "aibus_integration_test_key";
        string modelId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var user = await db.Users.SingleAsync(x => x.Mobile == SuperAdministrators.PrimaryMobile);
            var model = await db.Models
                .Where(x => x.IsActive && x.Provider!.IsActive)
                .OrderBy(x => x.ModelId)
                .FirstAsync();
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
            Assert.True(models.Length >= 300, $"Expected the full vendor-verified AI catalog, received {models.Length} active services.");
            Assert.True(models.Select(x => x.GetProperty("provider").GetProperty("slug").GetString()).Distinct().Count() >= 17);
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "speech_to_text");
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "text_to_speech");
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "speech_to_speech" && x.GetProperty("supportsWebSocket").GetBoolean());
            Assert.Contains(models, x => x.GetProperty("serviceType").GetString() == "realtime_translation");
            Assert.Contains(models, x => x.GetProperty("modelId").GetString() == "gpt-5.6-sol");
            Assert.Contains(models, x => x.GetProperty("modelId").GetString() == "gemini-3.6-flash");
            Assert.Contains(models, x => x.GetProperty("modelId").GetString() == "claude-fable-5");
            Assert.Contains(models, x => x.GetProperty("modelId").GetString() == "qwen3.7-max");
            Assert.Contains(models, x => x.GetProperty("modelId").GetString() == "universal-3-5-pro");
            Assert.Contains(models, x => x.GetProperty("modelId").GetString() == "gpt-image-2" && x.GetProperty("endpointPath").GetString() == "/v1/images/generations");
            Assert.Contains(models, x => x.GetProperty("modelId").GetString() == "text-embedding-3-small" && x.GetProperty("endpointPath").GetString() == "/v1/embeddings");
            Assert.Contains(models, x => x.GetProperty("pricingComponents").EnumerateArray().Any(p => p.GetProperty("unit").GetString() == "minute"));
            Assert.Contains(models, x => x.GetProperty("pricingComponents").EnumerateArray().Any(p => p.GetProperty("unit").GetString() == "million_audio_tokens"));
            Assert.DoesNotContain(models, x => x.GetProperty("modelId").GetString() is "gpt-image-1.5" or "sora-2" or "eleven_turbo_v2_5");
            var glm52 = Assert.Single(models, x => x.GetProperty("modelId").GetString() == "glm-5.2");
            Assert.Equal(1.4m, glm52.GetProperty("inputPricePerMillionUsd").GetDecimal());
            Assert.Equal(4.4m, glm52.GetProperty("outputPricePerMillionUsd").GetDecimal());
            Assert.Equal(200000, glm52.GetProperty("contextWindow").GetInt32());
            var nativeGemini = Assert.Single(models, x => x.GetProperty("modelId").GetString() == "gemini-3.1-flash-image");
            Assert.Equal("/v1/images/generations", nativeGemini.GetProperty("endpointPath").GetString());
            Assert.Equal("https://generativelanguage.googleapis.com", nativeGemini.GetProperty("upstreamBaseUrl").GetString());
            Assert.StartsWith("/v1beta/", nativeGemini.GetProperty("upstreamPath").GetString());
            Assert.All(models, model =>
            {
                Assert.StartsWith("https://", model.GetProperty("pricingSourceUrl").GetString());
                Assert.True(model.GetProperty("inputPricePerMillionUsd").GetDecimal() >= 0);
                Assert.True(model.GetProperty("outputPricePerMillionUsd").GetDecimal() >= 0);
            });
        }

        using (var modelsRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            modelsRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rawApiKey);
            var modelsResponse = await _client.SendAsync(modelsRequest);
            modelsResponse.EnsureSuccessStatusCode();
            using var modelsDocument = JsonDocument.Parse(await modelsResponse.Content.ReadAsStringAsync());
            var modelItem = Assert.Single(modelsDocument.RootElement.GetProperty("data").EnumerateArray(), item => item.GetProperty("id").GetString() == modelId);
            Assert.False(string.IsNullOrWhiteSpace(modelItem.GetProperty("display_name").GetString()));
            Assert.StartsWith("/v1/", modelItem.GetProperty("endpoint_path").GetString());
            Assert.False(string.IsNullOrWhiteSpace(modelItem.GetProperty("service_type").GetString()));
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

        var speech = await _client.PostAsJsonAsync("/v1/audio/speech", new { model = "tts-1", input = "hello", voice = "alloy" });
        Assert.Equal(HttpStatusCode.Unauthorized, speech.StatusCode);

        foreach (var endpoint in new[] { "/v1/responses", "/v1/embeddings", "/v1/moderations", "/v1/images/generations", "/v1/videos" })
        {
            var specialized = await _client.PostAsJsonAsync(endpoint, new { model = "test-model", input = "hello" });
            Assert.Equal(HttpStatusCode.Unauthorized, specialized.StatusCode);
        }

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent("whisper-1"), "model");
        form.Add(new ByteArrayContent([1, 2, 3, 4]), "file", "sample.wav");
        var transcription = await _client.PostAsync("/v1/audio/transcriptions", form);
        Assert.Equal(HttpStatusCode.Unauthorized, transcription.StatusCode);
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

        var adminOtpResponse = await _client.PostAsJsonAsync("/api/auth/request-otp", new { mobile = SuperAdministrators.PrimaryMobile });
        adminOtpResponse.EnsureSuccessStatusCode();
        var adminOtp = await adminOtpResponse.Content.ReadFromJsonAsync<OtpResponse>();
        var adminLoginResponse = await _client.PostAsJsonAsync("/api/auth/verify-otp", new { mobile = SuperAdministrators.PrimaryMobile, code = adminOtp!.DebugCode });
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
    public async Task User_api_keys_can_be_revealed_backfilled_and_rotated_without_leaking_plaintext()
    {
        async Task<LoginResponse> LoginAs(string mobile)
        {
            var otpResponse = await _client.PostAsJsonAsync("/api/auth/request-otp", new { mobile });
            otpResponse.EnsureSuccessStatusCode();
            var otp = await otpResponse.Content.ReadFromJsonAsync<OtpResponse>();
            using var loginRequest = new HttpRequestMessage(HttpMethod.Post, "/api/auth/verify-otp")
            {
                Content = JsonContent.Create(new { mobile, code = otp!.DebugCode })
            };
            var loginResponse = await _client.SendAsync(loginRequest);
            loginResponse.EnsureSuccessStatusCode();
            return (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!;
        }

        var owner = await LoginAs("09124440001");
        var otherUser = await LoginAs("09124440002");

        using var createRequest = Authorized(HttpMethod.Post, "/api/keys", owner.Token, new
        {
            name = "کلید قابل نمایش",
            requestLimit = 120,
            spendLimitUsd = 9.75m,
            accessMode = "all",
            modelRules = Array.Empty<string>()
        });
        var createResponse = await _client.SendAsync(createRequest);
        createResponse.EnsureSuccessStatusCode();
        Assert.True(createResponse.Headers.CacheControl?.NoStore == true);
        var created = await createResponse.Content.ReadFromJsonAsync<ApiKeySecretResponse>();
        Assert.NotNull(created);
        Assert.StartsWith("aibus_", created!.ApiKey);

        using (var listRequest = Authorized(HttpMethod.Get, "/api/keys", owner.Token))
        {
            var listResponse = await _client.SendAsync(listRequest);
            listResponse.EnsureSuccessStatusCode();
            var json = await listResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(created.ApiKey, json);
            using var list = JsonDocument.Parse(json);
            var listed = Assert.Single(list.RootElement.EnumerateArray(), x => x.GetProperty("id").GetGuid() == created.Id);
            Assert.True(listed.GetProperty("canReveal").GetBoolean());
            Assert.Equal(created.KeyPrefix, listed.GetProperty("keyPrefix").GetString());
        }

        using (var revealRequest = Authorized(HttpMethod.Post, $"/api/keys/{created.Id}/reveal", owner.Token))
        {
            var revealResponse = await _client.SendAsync(revealRequest);
            revealResponse.EnsureSuccessStatusCode();
            Assert.True(revealResponse.Headers.CacheControl?.NoStore == true);
            var revealed = await revealResponse.Content.ReadFromJsonAsync<ApiKeySecretResponse>();
            Assert.Equal(created.ApiKey, revealed?.ApiKey);
        }

        const string legacyRaw = "aibus_legacy_key_that_remains_valid_during_secure_backfill";
        Guid legacyId;
        Guid corruptId;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<SecretProtector>();
            var user = await db.Users.SingleAsync(x => x.Mobile == "09124440001");
            var legacy = new UserApiKey
            {
                UserId = user.Id,
                Name = "کلید قدیمی",
                KeyHash = Hashing.Sha256(legacyRaw),
                KeyPrefix = legacyRaw[..12],
                ProtectedApiKey = null,
                RequestLimit = 42,
                SpendLimitUsd = 12.34m,
                RequestCount = 7,
                SpentUsd = 0.123m,
                AccessMode = "allow",
                ModelRulesJson = "[\"gpt-5.6-luna\"]"
            };
            const string corruptRaw = "aibus_corrupt_key_value_for_integrity_test";
            var corrupt = new UserApiKey
            {
                UserId = user.Id,
                Name = "کلید ناسازگار",
                KeyHash = Hashing.Sha256(corruptRaw),
                KeyPrefix = corruptRaw[..12],
                ProtectedApiKey = secrets.Protect("aibus_a_different_secret")
            };
            db.UserApiKeys.AddRange(legacy, corrupt);
            await db.SaveChangesAsync();
            legacyId = legacy.Id;
            corruptId = corrupt.Id;
        }

        using (var legacyRevealRequest = Authorized(HttpMethod.Post, $"/api/keys/{legacyId}/reveal", owner.Token))
        {
            var legacyRevealResponse = await _client.SendAsync(legacyRevealRequest);
            Assert.Equal(HttpStatusCode.Conflict, legacyRevealResponse.StatusCode);
            Assert.True(legacyRevealResponse.Headers.CacheControl?.NoStore == true);
        }
        using (var otherRevealRequest = Authorized(HttpMethod.Post, $"/api/keys/{legacyId}/reveal", otherUser.Token))
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(otherRevealRequest)).StatusCode);
        using (var otherRotateRequest = Authorized(HttpMethod.Post, $"/api/keys/{legacyId}/rotate", otherUser.Token))
            Assert.Equal(HttpStatusCode.NotFound, (await _client.SendAsync(otherRotateRequest)).StatusCode);

        using (var corruptListRequest = Authorized(HttpMethod.Get, "/api/keys", owner.Token))
        {
            var corruptListResponse = await _client.SendAsync(corruptListRequest);
            using var list = JsonDocument.Parse(await corruptListResponse.Content.ReadAsStringAsync());
            var corrupt = Assert.Single(list.RootElement.EnumerateArray(), x => x.GetProperty("id").GetGuid() == corruptId);
            Assert.False(corrupt.GetProperty("canReveal").GetBoolean());
        }
        using (var corruptRevealRequest = Authorized(HttpMethod.Post, $"/api/keys/{corruptId}/reveal", owner.Token))
            Assert.Equal(HttpStatusCode.Conflict, (await _client.SendAsync(corruptRevealRequest)).StatusCode);

        using (var authenticateLegacy = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            authenticateLegacy.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", legacyRaw);
            (await _client.SendAsync(authenticateLegacy)).EnsureSuccessStatusCode();
        }
        using (var revealBackfilledRequest = Authorized(HttpMethod.Post, $"/api/keys/{legacyId}/reveal", owner.Token))
        {
            var revealBackfilledResponse = await _client.SendAsync(revealBackfilledRequest);
            revealBackfilledResponse.EnsureSuccessStatusCode();
            var backfilled = await revealBackfilledResponse.Content.ReadFromJsonAsync<ApiKeySecretResponse>();
            Assert.Equal(legacyRaw, backfilled?.ApiKey);
        }

        ApiKeySecretResponse rotated;
        using (var rotateRequest = Authorized(HttpMethod.Post, $"/api/keys/{legacyId}/rotate", owner.Token))
        {
            var rotateResponse = await _client.SendAsync(rotateRequest);
            rotateResponse.EnsureSuccessStatusCode();
            Assert.True(rotateResponse.Headers.CacheControl?.NoStore == true);
            rotated = (await rotateResponse.Content.ReadFromJsonAsync<ApiKeySecretResponse>())!;
            Assert.NotEqual(legacyRaw, rotated.ApiKey);
            Assert.Equal(legacyId, rotated.Id);
        }

        using (var oldKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            oldKeyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", legacyRaw);
            Assert.Equal(HttpStatusCode.Unauthorized, (await _client.SendAsync(oldKeyRequest)).StatusCode);
        }
        using (var newKeyRequest = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
        {
            newKeyRequest.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", rotated.ApiKey);
            (await _client.SendAsync(newKeyRequest)).EnsureSuccessStatusCode();
        }

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var secrets = scope.ServiceProvider.GetRequiredService<SecretProtector>();
            var key = await db.UserApiKeys.AsNoTracking().SingleAsync(x => x.Id == legacyId);
            Assert.Equal(42, key.RequestLimit);
            Assert.Equal(12.34m, key.SpendLimitUsd);
            Assert.Equal(7, key.RequestCount);
            Assert.Equal(0.123m, key.SpentUsd);
            Assert.Equal("allow", key.AccessMode);
            Assert.Equal("[\"gpt-5.6-luna\"]", key.ModelRulesJson);
            Assert.Equal(rotated.ApiKey, secrets.Unprotect(key.ProtectedApiKey!));
            Assert.Equal(Hashing.Sha256(rotated.ApiKey), key.KeyHash);

            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(x => x.Action == "user_api_key.rotate" && x.EntityId == legacyId.ToString());
            Assert.Contains(legacyRaw[..12], audit.DetailsJson);
            Assert.Contains(rotated.KeyPrefix, audit.DetailsJson);
            Assert.DoesNotContain(legacyRaw, audit.DetailsJson);
            Assert.DoesNotContain(rotated.ApiKey, audit.DetailsJson);
        }

        using (var finalListRequest = Authorized(HttpMethod.Get, "/api/keys", owner.Token))
        {
            var finalListResponse = await _client.SendAsync(finalListRequest);
            var json = await finalListResponse.Content.ReadAsStringAsync();
            Assert.DoesNotContain(legacyRaw, json);
            Assert.DoesNotContain(rotated.ApiKey, json);
        }
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
    private sealed record ApiKeySecretResponse(Guid Id, string ApiKey, string KeyPrefix);
    private static HttpRequestMessage Authorized(HttpMethod method, string path, string token, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = JsonContent.Create(body);
        return request;
    }
}
