using System.Text.Json;

namespace AiBus.Api;

public sealed record RequestOtpRequest(string Mobile);
public sealed record VerifyOtpRequest(string Mobile, string Code);
public sealed record UpdateProfileRequest(string DisplayName);
public sealed record CreateUserKeyRequest(string Name, int? RequestLimit, decimal? SpendLimitUsd, string AccessMode, string[] ModelRules);
public sealed record UpdateUserKeyRequest(string Name, bool IsActive, int? RequestLimit, decimal? SpendLimitUsd, string AccessMode, string[] ModelRules);
public sealed record CreateTopUpRequest(decimal AmountUsd);
public sealed record UpdateSettingsRequest(long DollarRateIrr, decimal FeePercent, string SmsApiKey, int SmsTemplateId, string ZarinpalMerchantId, string PaymentCallbackUrl);
public sealed record ProviderRequest(string Name, string Slug, string LogoUrl, string BaseUrl, string PricingUrl, string Protocol, bool IsActive);
public sealed record CredentialRequest(string Label, string ApiKey, bool IsActive, decimal InitialBalanceUsd, decimal RemainingBalanceUsd, decimal AlertThresholdUsd);
public sealed record PricingComponentRequest(string Label, string Unit, decimal? PriceUsd, string? Note);
public sealed record ModelRequest(Guid ProviderId, string ModelId, string DisplayName, string Modality, decimal InputPricePerMillionUsd, decimal OutputPricePerMillionUsd, decimal? CachedInputPricePerMillionUsd, int ContextWindow, bool SupportsStreaming, bool SupportsWebSocket, bool IsActive, string PricingSourceUrl, string TestPayloadJson, string ServiceType, string EndpointPath, string Region, bool IsPreview, PricingComponentRequest[] PricingComponents, string PricingNotes);
public sealed record WalletAdjustRequest(decimal AmountUsd, string Description);
public sealed record SuspendRequest(bool IsSuspended);
public sealed record CredentialBalanceRequest(decimal? InitialBalanceUsd, decimal RemainingBalanceUsd, decimal AlertThresholdUsd);
public sealed record CredentialDeleteRequest(string Confirmation);
public sealed record CreateTicketRequest(string Subject, string Category, string Priority, string Message);
public sealed record TicketMessageRequest(string Message);
public sealed record UpdateTicketRequest(string Status, string Priority);

public sealed class OpenAiChatRequest
{
    public string Model { get; set; } = "";
    public JsonElement Messages { get; set; }
    public bool Stream { get; set; }
    public JsonElement? Temperature { get; set; }
    public JsonElement? MaxTokens { get; set; }
    public Dictionary<string, JsonElement>? ExtensionData { get; set; }
}
public static class ClaimHelpers
{
    public static Guid UserId(this System.Security.Claims.ClaimsPrincipal user) =>
        Guid.Parse(user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value ?? throw new UnauthorizedAccessException());
}
