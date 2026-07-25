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
    private sealed record LoginUser(string Role);
}
