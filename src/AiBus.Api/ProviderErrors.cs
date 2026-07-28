using System.Net;
using System.Text.Json;

namespace AiBus.Api;

public enum ProviderFailureKind
{
    QuotaExhausted,
    RateLimited,
    Authentication,
    Transient,
    RequestRejected
}

public sealed record ProviderFailure(
    ProviderFailureKind Kind,
    string Code,
    int PublicStatus,
    bool ShouldFailover,
    string AdministrativeDetail)
{
    public string PublicMessage(string providerName) => Kind switch
    {
        ProviderFailureKind.QuotaExhausted =>
            $"اعتبار سرویس {providerName} در حال حاضر کافی نیست. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً کمی بعد دوباره تلاش کنید یا با پشتیبانی تماس بگیرید.",
        ProviderFailureKind.RateLimited =>
            $"ظرفیت سرویس {providerName} موقتاً تکمیل است. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً چند لحظه دیگر دوباره تلاش کنید.",
        ProviderFailureKind.Authentication =>
            $"ارتباط ایمن با سرویس {providerName} موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً با پشتیبانی تماس بگیرید.",
        ProviderFailureKind.RequestRejected =>
            $"درخواست توسط سرویس {providerName} پذیرفته نشد. ورودی و تنظیمات مدل را بررسی کنید؛ هزینه‌ای کسر نشد.",
        _ =>
            $"سرویس {providerName} موقتاً در دسترس نیست. هزینه‌ای از کیف پول شما کسر نشد؛ لطفاً کمی بعد دوباره تلاش کنید."
    };
}

public static class ProviderErrorMapper
{
    public const string QuotaExhaustedCode = "provider_quota_exhausted";
    public const string RateLimitedCode = "provider_rate_limited";
    public const string AuthenticationFailedCode = "provider_authentication_failed";
    public const string UnavailableCode = "provider_unavailable";
    public const string RequestRejectedCode = "upstream_request_rejected";

    private static readonly string[] QuotaSignals =
    [
        "insufficient_quota", "billing_hard_limit_reached",
        "billing details", "out of credits", "not enough credits", "credit balance is too low",
        "insufficient balance", "monthly spend"
    ];

    private static readonly string[] FinancialQuotaMessageSignals = ["exceeded your current quota"];

    private static readonly string[] AuthenticationSignals =
    [
        "invalid_api_key", "invalid api key", "authentication_error", "authentication failed",
        "incorrect api key", "unauthorized"
    ];

    private static readonly string[] RateLimitSignals =
    [
        "rate_limit_exceeded", "rate limit exceeded", "too many requests", "requests per minute",
        "tokens per minute", "temporarily rate-limited", "quota_exceeded", "quota exceeded",
        "resource_exhausted"
    ];

    public static ProviderFailure Classify(HttpStatusCode status, string? responseBody)
    {
        var body = responseBody ?? "";
        var searchable = ExtractSearchableError(body);
        var detail = Truncate($"{(int)status}: {body}");

        if (status == HttpStatusCode.PaymentRequired || ContainsAny(searchable, QuotaSignals))
            return new(ProviderFailureKind.QuotaExhausted, QuotaExhaustedCode, StatusCodes.Status503ServiceUnavailable, true, detail);
        if (ContainsAny(searchable, RateLimitSignals))
            return new(ProviderFailureKind.RateLimited, RateLimitedCode, StatusCodes.Status429TooManyRequests, true, detail);
        if (ContainsAny(searchable, FinancialQuotaMessageSignals))
            return new(ProviderFailureKind.QuotaExhausted, QuotaExhaustedCode, StatusCodes.Status503ServiceUnavailable, true, detail);
        if (status == HttpStatusCode.TooManyRequests)
            return new(ProviderFailureKind.RateLimited, RateLimitedCode, StatusCodes.Status429TooManyRequests, true, detail);
        if (status is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden || ContainsAny(searchable, AuthenticationSignals))
            return new(ProviderFailureKind.Authentication, AuthenticationFailedCode, StatusCodes.Status503ServiceUnavailable, true, detail);
        if ((int)status is 408 or 425 || (int)status >= 500)
            return new(ProviderFailureKind.Transient, UnavailableCode, StatusCodes.Status503ServiceUnavailable, true, detail);

        var publicStatus = (int)status is >= 400 and < 500 ? (int)status : StatusCodes.Status502BadGateway;
        return new(ProviderFailureKind.RequestRejected, RequestRejectedCode, publicStatus, false, detail);
    }

    public static ProviderFailure Classify(Exception exception)
    {
        var detail = Truncate($"{exception.GetType().Name}: {exception.Message}");
        if (ContainsAny(detail, QuotaSignals))
            return new(ProviderFailureKind.QuotaExhausted, QuotaExhaustedCode, StatusCodes.Status503ServiceUnavailable, true, detail);
        if (ContainsAny(detail, RateLimitSignals))
            return new(ProviderFailureKind.RateLimited, RateLimitedCode, StatusCodes.Status429TooManyRequests, true, detail);
        if (ContainsAny(detail, FinancialQuotaMessageSignals))
            return new(ProviderFailureKind.QuotaExhausted, QuotaExhaustedCode, StatusCodes.Status503ServiceUnavailable, true, detail);
        if (ContainsAny(detail, AuthenticationSignals))
            return new(ProviderFailureKind.Authentication, AuthenticationFailedCode, StatusCodes.Status503ServiceUnavailable, true, detail);
        return new(ProviderFailureKind.Transient, UnavailableCode, StatusCodes.Status503ServiceUnavailable, true, detail);
    }

    public static bool TryClassifyInBand(string payload, out ProviderFailure? failure)
    {
        failure = null;
        if (string.IsNullOrWhiteSpace(payload) || payload == "[DONE]") return false;
        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            var isError = root.TryGetProperty("error", out var error) && HasErrorValue(error)
                || root.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String
                    && type.GetString()!.Contains("error", StringComparison.OrdinalIgnoreCase);
            if (!isError) return false;
            failure = Classify(HttpStatusCode.OK, payload);
            if (failure.Kind == ProviderFailureKind.RequestRejected)
                failure = failure with { PublicStatus = StatusCodes.Status502BadGateway, ShouldFailover = true };
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static ProviderFailure MostRelevant(IReadOnlyCollection<ProviderFailure> failures) =>
        failures.OrderBy(x => x.Kind switch
        {
            ProviderFailureKind.QuotaExhausted => 0,
            ProviderFailureKind.Authentication => 1,
            ProviderFailureKind.RateLimited => 2,
            ProviderFailureKind.Transient => 3,
            _ => 4
        }).FirstOrDefault() ?? new ProviderFailure(
            ProviderFailureKind.Transient, UnavailableCode, StatusCodes.Status503ServiceUnavailable, true, "No active provider route succeeded.");

    public static void Apply(ProviderCredential credential, ProviderFailure failure)
    {
        credential.LastError = Truncate(failure.AdministrativeDetail);
        credential.LastErrorCode = failure.Code;
        credential.LastErrorAtUtc = DateTime.UtcNow;
        credential.LastUsedAtUtc = DateTime.UtcNow;
        if (failure.Kind == ProviderFailureKind.QuotaExhausted) credential.RemainingBalanceUsd = 0;
    }

    public static void Clear(ProviderCredential credential)
    {
        credential.LastError = null;
        credential.LastErrorCode = null;
        credential.LastErrorAtUtc = null;
    }

    public static object PublicError(ProviderFailure failure, string providerName) => new
    {
        error = new { message = failure.PublicMessage(providerName), type = failure.Code, code = failure.Code }
    };

    public static string PublicErrorJson(ProviderFailure failure, string providerName) =>
        JsonSerializer.Serialize(PublicError(failure, providerName));

    public static string PublicRealtimeErrorJson(ProviderFailure failure, string providerName) =>
        JsonSerializer.Serialize(new
        {
            type = "error",
            error = new
            {
                message = failure.PublicMessage(providerName),
                type = failure.Code,
                code = failure.Code
            }
        });

    private static bool ContainsAny(string input, IEnumerable<string> signals) =>
        signals.Any(signal => input.Contains(signal, StringComparison.OrdinalIgnoreCase));

    private static bool HasErrorValue(JsonElement error) => error.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined or JsonValueKind.False => false,
        JsonValueKind.String => !string.IsNullOrWhiteSpace(error.GetString()),
        JsonValueKind.Object => error.EnumerateObject().Any(),
        JsonValueKind.Array => error.GetArrayLength() > 0,
        _ => true
    };

    private static string ExtractSearchableError(string input)
    {
        try
        {
            using var document = JsonDocument.Parse(input);
            var values = new List<string>();
            CollectStrings(document.RootElement, values);
            return string.Join(' ', values);
        }
        catch (JsonException)
        {
            return input;
        }
    }

    private static void CollectStrings(JsonElement element, List<string> values)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    values.Add(property.Name);
                    CollectStrings(property.Value, values);
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray()) CollectStrings(item, values);
                break;
            case JsonValueKind.String:
                values.Add(element.GetString() ?? "");
                break;
        }
    }

    private static string Truncate(string value) => value[..Math.Min(500, value.Length)];
}
