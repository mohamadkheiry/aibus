using System.ComponentModel.DataAnnotations;

namespace AiBus.Api;

public static class Roles
{
    public const string SuperAdmin = "SuperAdmin";
    public const string User = "User";
}

public sealed class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(15)] public string Mobile { get; set; } = "";
    [MaxLength(100)] public string DisplayName { get; set; } = "کاربر AiBus";
    [MaxLength(20)] public string Role { get; set; } = Roles.User;
    public bool IsSuspended { get; set; }
    public decimal WalletUsd { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastSeenAtUtc { get; set; }
    public List<UserApiKey> ApiKeys { get; set; } = [];
}

public sealed class OtpCode
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(15)] public string Mobile { get; set; } = "";
    [MaxLength(128)] public string CodeHash { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public int Attempts { get; set; }
    public bool IsUsed { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AiProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();
    [MaxLength(80)] public string Name { get; set; } = "";
    [MaxLength(80)] public string Slug { get; set; } = "";
    [MaxLength(500)] public string LogoUrl { get; set; } = "";
    [MaxLength(500)] public string BaseUrl { get; set; } = "";
    [MaxLength(500)] public string PricingUrl { get; set; } = "";
    [MaxLength(30)] public string Protocol { get; set; } = "openai";
    public bool IsActive { get; set; } = true;
    public List<ProviderCredential> Credentials { get; set; } = [];
    public List<AiModel> Models { get; set; } = [];
}

public sealed class ProviderCredential
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public AiProvider? Provider { get; set; }
    [MaxLength(100)] public string Label { get; set; } = "کلید اصلی";
    public string ProtectedApiKey { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public decimal InitialBalanceUsd { get; set; }
    public decimal RemainingBalanceUsd { get; set; }
    public decimal AlertThresholdUsd { get; set; } = 5;
    public long RequestCount { get; set; }
    public DateTime? LastUsedAtUtc { get; set; }
    [MaxLength(500)] public string? LastError { get; set; }
}

public sealed class AiModel
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ProviderId { get; set; }
    public AiProvider? Provider { get; set; }
    [MaxLength(120)] public string ModelId { get; set; } = "";
    [MaxLength(160)] public string DisplayName { get; set; } = "";
    [MaxLength(40)] public string Modality { get; set; } = "text";
    public decimal InputPricePerMillionUsd { get; set; }
    public decimal OutputPricePerMillionUsd { get; set; }
    public decimal? CachedInputPricePerMillionUsd { get; set; }
    public int ContextWindow { get; set; }
    public bool SupportsStreaming { get; set; } = true;
    public bool SupportsWebSocket { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTime PriceSyncedAtUtc { get; set; } = DateTime.UtcNow;
    [MaxLength(500)] public string PricingSourceUrl { get; set; } = "";
    public string TestPayloadJson { get; set; } = "{\"messages\":[{\"role\":\"user\",\"content\":\"سلام! فقط بگو اتصال موفق است.\"}]}";
}

public sealed class UserApiKey
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    [MaxLength(80)] public string Name { get; set; } = "کلید من";
    [MaxLength(128)] public string KeyHash { get; set; } = "";
    [MaxLength(16)] public string KeyPrefix { get; set; } = "";
    public bool IsActive { get; set; } = true;
    public int? RequestLimit { get; set; }
    public decimal? SpendLimitUsd { get; set; }
    public long RequestCount { get; set; }
    public decimal SpentUsd { get; set; }
    [MaxLength(20)] public string AccessMode { get; set; } = "all";
    public string ModelRulesJson { get; set; } = "[]";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAtUtc { get; set; }
}

public sealed class UsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public Guid UserApiKeyId { get; set; }
    public Guid ModelId { get; set; }
    public Guid? ProviderCredentialId { get; set; }
    [MaxLength(120)] public string ModelName { get; set; } = "";
    [MaxLength(80)] public string ProviderName { get; set; } = "";
    public long InputTokens { get; set; }
    public long OutputTokens { get; set; }
    public decimal CostUsd { get; set; }
    public int DurationMs { get; set; }
    [MaxLength(20)] public string Status { get; set; } = "success";
    [MaxLength(60)] public string TraceId { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class WalletTransaction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    [MaxLength(30)] public string Type { get; set; } = "topup";
    public decimal AmountUsd { get; set; }
    public long AmountIrr { get; set; }
    public decimal ExchangeRateIrr { get; set; }
    public decimal FeePercent { get; set; }
    [MaxLength(40)] public string Status { get; set; } = "pending";
    [MaxLength(100)] public string? Authority { get; set; }
    [MaxLength(100)] public string? ReferenceId { get; set; }
    [MaxLength(500)] public string? Description { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
}

public sealed class SystemSetting
{
    [Key, MaxLength(100)] public string Key { get; set; } = "";
    public string Value { get; set; } = "";
    public bool IsSecret { get; set; }
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class VisitEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? UserId { get; set; }
    [MaxLength(64)] public string VisitorId { get; set; } = "";
    [MaxLength(80)] public string IpAddress { get; set; } = "";
    [MaxLength(100)] public string Browser { get; set; } = "";
    [MaxLength(100)] public string OperatingSystem { get; set; } = "";
    [MaxLength(1000)] public string UserAgent { get; set; } = "";
    [MaxLength(1000)] public string Referrer { get; set; } = "";
    [MaxLength(500)] public string Path { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? ActorUserId { get; set; }
    [MaxLength(100)] public string Action { get; set; } = "";
    [MaxLength(80)] public string EntityType { get; set; } = "";
    [MaxLength(100)] public string EntityId { get; set; } = "";
    public string DetailsJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}

public sealed class SupportTicket
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid UserId { get; set; }
    public AppUser? User { get; set; }
    [MaxLength(32)] public string ReferenceCode { get; set; } = "";
    [MaxLength(160)] public string Subject { get; set; } = "";
    [MaxLength(40)] public string Category { get; set; } = "general";
    [MaxLength(20)] public string Priority { get; set; } = "normal";
    [MaxLength(30)] public string Status { get; set; } = "open";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastReplyAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? ClosedAtUtc { get; set; }
    public List<TicketMessage> Messages { get; set; } = [];
}

public sealed class TicketMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TicketId { get; set; }
    public SupportTicket? Ticket { get; set; }
    public Guid AuthorUserId { get; set; }
    public bool IsStaff { get; set; }
    [MaxLength(5000)] public string Body { get; set; } = "";
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
}
