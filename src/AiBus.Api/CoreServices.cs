using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace AiBus.Api;

public sealed class SecretProtector(IDataProtectionProvider provider)
{
    private readonly IDataProtector _protector = provider.CreateProtector("AiBus.Secrets.v1");
    public string Protect(string value) => string.IsNullOrWhiteSpace(value) ? "" : _protector.Protect(value);
    public string Unprotect(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        try { return _protector.Unprotect(value); } catch { return ""; }
    }
}

public static class Hashing
{
    public static string Sha256(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public static string RandomDigits(int length) => string.Concat(RandomNumberGenerator.GetBytes(length).Select(b => (char)('0' + b % 10)));
    public static string RandomApiKey() => "aibus_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant();
}

public sealed class TokenService(IConfiguration config)
{
    public string Create(AppUser user, Guid? impersonatedBy = null)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.MobilePhone, user.Mobile),
            new(ClaimTypes.Name, user.DisplayName),
            new(ClaimTypes.Role, user.Role)
        };
        if (impersonatedBy.HasValue) claims.Add(new("impersonated_by", impersonatedBy.Value.ToString()));
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(config["Jwt:Key"]!));
        var token = new JwtSecurityToken(
            config["Jwt:Issuer"], config["Jwt:Audience"], claims,
            expires: DateTime.UtcNow.AddHours(12), signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

public sealed class SettingsService(AppDbContext db, SecretProtector secrets)
{
    public async Task<string> Get(string key, string fallback = "")
    {
        var setting = await db.Settings.FindAsync(key);
        if (setting is null) return fallback;
        return setting.IsSecret ? secrets.Unprotect(setting.Value) : setting.Value;
    }

    public async Task Set(string key, string value, bool secret = false)
    {
        var setting = await db.Settings.FindAsync(key);
        if (setting is null)
        {
            setting = new SystemSetting { Key = key };
            db.Settings.Add(setting);
        }
        setting.Value = secret ? secrets.Protect(value) : value;
        setting.IsSecret = secret;
        setting.UpdatedAtUtc = DateTime.UtcNow;
        await db.SaveChangesAsync();
    }
}

public sealed class SmsIrService(HttpClient http, SettingsService settings, ILogger<SmsIrService> logger)
{
    public async Task<bool> SendOtp(string mobile, string code, CancellationToken ct)
    {
        var apiKey = await settings.Get("sms.api_key");
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            logger.LogWarning("SMS.ir key is not configured; OTP for {Mobile} is available only in development logs: {Code}", mobile, code);
            return false;
        }
        var templateId = int.TryParse(await settings.Get("sms.template_id", "176898"), out var id) ? id : 176898;
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.sms.ir/v1/send/verify");
        request.Headers.Add("Accept", "application/json");
        request.Headers.Add("X-API-KEY", apiKey);
        request.Content = JsonContent.Create(new { mobile, templateId, parameters = new[] { new { name = "CODE", value = code } } });
        var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            logger.LogError("SMS.ir failed: {Status} {Body}", response.StatusCode, await response.Content.ReadAsStringAsync(ct));
        return response.IsSuccessStatusCode;
    }
}

public sealed class ZarinpalService(HttpClient http, SettingsService settings)
{
    public async Task<(bool ok, string authority, string? error)> Request(long amountIrr, string description, string callback, CancellationToken ct)
    {
        var merchant = await settings.Get("zarinpal.merchant_id");
        if (string.IsNullOrWhiteSpace(merchant)) return (false, "", "Merchant ID زرین‌پال تنظیم نشده است.");
        // Zarinpal accepts toman in the current v4 API; our ledger remains IRR.
        var payload = new { merchant_id = merchant, amount = Math.Max(1000, amountIrr / 10), callback_url = callback, description };
        var response = await http.PostAsJsonAsync("https://payment.zarinpal.com/pg/v4/payment/request.json", payload, ct);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var authority = doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("authority", out var a) ? a.GetString() : null;
        return response.IsSuccessStatusCode && !string.IsNullOrWhiteSpace(authority)
            ? (true, authority!, null)
            : (false, "", doc.RootElement.ToString());
    }

    public async Task<(bool ok, string? refId, string? error)> Verify(long amountIrr, string authority, CancellationToken ct)
    {
        var merchant = await settings.Get("zarinpal.merchant_id");
        var response = await http.PostAsJsonAsync("https://payment.zarinpal.com/pg/v4/payment/verify.json", new { merchant_id = merchant, amount = Math.Max(1000, amountIrr / 10), authority }, ct);
        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("code", out var code) && (code.GetInt32() is 100 or 101))
            return (true, data.TryGetProperty("ref_id", out var refId) ? refId.ToString() : null, null);
        return (false, null, doc.RootElement.ToString());
    }
}

public sealed class ApiKeyAuthenticator(AppDbContext db)
{
    public async Task<(AppUser user, UserApiKey key)?> Authenticate(HttpRequest request, CancellationToken ct)
    {
        var raw = request.Headers.Authorization.ToString();
        raw = raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? raw[7..] : request.Headers["X-API-Key"].ToString();
        if (string.IsNullOrWhiteSpace(raw) && request.Headers.TryGetValue("Sec-WebSocket-Protocol", out var protocols))
        {
            const string keyPrefix = "aibus-key.";
            raw = protocols.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(x => x.StartsWith(keyPrefix, StringComparison.Ordinal))?[keyPrefix.Length..] ?? "";
        }
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var hash = Hashing.Sha256(raw.Trim());
        var key = await db.UserApiKeys.Include(x => x.User).SingleOrDefaultAsync(x => x.KeyHash == hash, ct);
        if (key?.User is null || !key.IsActive || key.User.IsSuspended) return null;
        return (key.User, key);
    }
}

public static class UserAgentParser
{
    public static (string browser, string os) Parse(string ua)
    {
        var browser = ua.Contains("Edg/") ? "Edge" : ua.Contains("Chrome/") ? "Chrome" : ua.Contains("Firefox/") ? "Firefox" : ua.Contains("Safari/") ? "Safari" : "سایر";
        var os = ua.Contains("Windows") ? "Windows" : ua.Contains("Android") ? "Android" : ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" : ua.Contains("Mac OS") ? "macOS" : ua.Contains("Linux") ? "Linux" : "سایر";
        return (browser, os);
    }
}
