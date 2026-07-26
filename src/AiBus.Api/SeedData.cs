using Microsoft.EntityFrameworkCore;

namespace AiBus.Api;

public static class SeedData
{
    private sealed record ProviderSeed(string Name, string Slug, string Logo, string BaseUrl, string PricingUrl, string Protocol);
    private sealed record ModelSeed(string Provider, string Id, string Name, decimal Input, decimal Output, decimal? Cached, int Context = 128000, bool Ws = false, string Modality = "text");

    public static async Task Initialize(AppDbContext db)
    {
        await db.Database.EnsureCreatedAsync();
        // EnsureCreated does not add new tables to an existing SQLite database.
        // These idempotent statements preserve all current production data while enabling support tickets.
        await db.Database.ExecuteSqlRawAsync("""
            CREATE TABLE IF NOT EXISTS "SupportTickets" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_SupportTickets" PRIMARY KEY,
                "UserId" TEXT NOT NULL,
                "ReferenceCode" TEXT NOT NULL,
                "Subject" TEXT NOT NULL,
                "Category" TEXT NOT NULL,
                "Priority" TEXT NOT NULL,
                "Status" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                "UpdatedAtUtc" TEXT NOT NULL,
                "LastReplyAtUtc" TEXT NOT NULL,
                "ClosedAtUtc" TEXT NULL,
                CONSTRAINT "FK_SupportTickets_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
            );
            CREATE UNIQUE INDEX IF NOT EXISTS "IX_SupportTickets_ReferenceCode" ON "SupportTickets" ("ReferenceCode");
            CREATE INDEX IF NOT EXISTS "IX_SupportTickets_UserId_UpdatedAtUtc" ON "SupportTickets" ("UserId", "UpdatedAtUtc");
            CREATE INDEX IF NOT EXISTS "IX_SupportTickets_Status_Priority_UpdatedAtUtc" ON "SupportTickets" ("Status", "Priority", "UpdatedAtUtc");
            CREATE TABLE IF NOT EXISTS "TicketMessages" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_TicketMessages" PRIMARY KEY,
                "TicketId" TEXT NOT NULL,
                "AuthorUserId" TEXT NOT NULL,
                "IsStaff" INTEGER NOT NULL,
                "Body" TEXT NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL,
                CONSTRAINT "FK_TicketMessages_SupportTickets_TicketId" FOREIGN KEY ("TicketId") REFERENCES "SupportTickets" ("Id") ON DELETE CASCADE
            );
            CREATE INDEX IF NOT EXISTS "IX_TicketMessages_TicketId_CreatedAtUtc" ON "TicketMessages" ("TicketId", "CreatedAtUtc");
            """);
        if (!await db.Users.AnyAsync(x => x.Mobile == "09015909044"))
            db.Users.Add(new AppUser { Mobile = "09015909044", DisplayName = "سوپر ادمین", Role = Roles.SuperAdmin });

        var defaults = new Dictionary<string, string>
        {
            ["currency.usd_irr"] = "850000",
            ["billing.fee_percent"] = "10",
            ["sms.template_id"] = "176898",
            ["payment.callback_url"] = "http://localhost:5173/payment/callback"
        };
        foreach (var item in defaults)
            if (!await db.Settings.AnyAsync(x => x.Key == item.Key)) db.Settings.Add(new SystemSetting { Key = item.Key, Value = item.Value });

        if (!await db.Providers.AnyAsync())
        {
            var providers = new[]
            {
                new ProviderSeed("OpenAI", "openai", "/providers/openai.svg", "https://api.openai.com/v1", "https://developers.openai.com/api/docs/pricing", "openai"),
                new ProviderSeed("Google Gemini", "gemini", "https://cdn.simpleicons.org/googlegemini/8E75B2", "https://generativelanguage.googleapis.com/v1beta/openai", "https://ai.google.dev/gemini-api/docs/pricing", "openai"),
                new ProviderSeed("Anthropic", "anthropic", "https://cdn.simpleicons.org/anthropic/191919", "https://api.anthropic.com/v1", "https://platform.claude.com/docs/en/about-claude/pricing", "anthropic"),
                new ProviderSeed("DeepSeek", "deepseek", "https://cdn.simpleicons.org/deepseek/4D6BFE", "https://api.deepseek.com", "https://api-docs.deepseek.com/quick_start/pricing", "openai"),
                new ProviderSeed("Moonshot Kimi", "kimi", "https://cdn.simpleicons.org/moonrepo/6F5CFF", "https://api.moonshot.ai/v1", "https://platform.kimi.ai/docs/pricing/chat", "openai"),
                new ProviderSeed("Z.ai / GLM", "glm", "https://cdn.simpleicons.org/zotero/00836B", "https://open.bigmodel.cn/api/paas/v4", "https://open.bigmodel.cn/pricing", "openai"),
                new ProviderSeed("xAI", "xai", "https://cdn.simpleicons.org/x/111111", "https://api.x.ai/v1", "https://docs.x.ai/developers/pricing", "openai"),
                new ProviderSeed("Mistral AI", "mistral", "https://cdn.simpleicons.org/mistralai/FF7000", "https://api.mistral.ai/v1", "https://mistral.ai/pricing/api/", "openai"),
                new ProviderSeed("Alibaba Qwen", "qwen", "https://cdn.simpleicons.org/alibabacloud/FF6A00", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "https://www.alibabacloud.com/help/en/model-studio/model-pricing", "openai"),
                new ProviderSeed("Cohere", "cohere", "/providers/cohere.svg", "https://api.cohere.com/compatibility/v1", "https://cohere.com/pricing", "openai")
            };
            db.Providers.AddRange(providers.Select(x => new AiProvider { Name = x.Name, Slug = x.Slug, LogoUrl = x.Logo, BaseUrl = x.BaseUrl, PricingUrl = x.PricingUrl, Protocol = x.Protocol }));
            await db.SaveChangesAsync();

            // Snapshot verified against each vendor's official pricing page on 2026-07-25.
            var models = new[]
            {
                new ModelSeed("openai","gpt-5.6-sol","GPT-5.6 Sol",5m,30m,.5m,1050000),
                new ModelSeed("openai","gpt-5.6-terra","GPT-5.6 Terra",2.5m,15m,.25m,1050000),
                new ModelSeed("openai","gpt-5.6-luna","GPT-5.6 Luna",1m,6m,.1m,1050000),
                new ModelSeed("openai","gpt-5.4","GPT-5.4",2.5m,15m,.25m,1050000),
                new ModelSeed("openai","gpt-5-mini","GPT-5 mini",.25m,2m,.025m,400000),
                new ModelSeed("openai","gpt-5-nano","GPT-5 nano",.05m,.4m,.005m,400000),
                new ModelSeed("openai","gpt-realtime-2.1","GPT Realtime 2.1",4m,16m,.4m,128000,true,"audio"),
                new ModelSeed("gemini","gemini-3.5-flash","Gemini 3.5 Flash",1.5m,9m,.15m,1000000),
                new ModelSeed("gemini","gemini-3.1-pro-preview","Gemini 3.1 Pro Preview",2m,12m,.2m,1000000),
                new ModelSeed("gemini","gemini-3-flash-preview","Gemini 3 Flash Preview",.5m,3m,.05m,1000000),
                new ModelSeed("gemini","gemini-2.5-pro","Gemini 2.5 Pro",1.25m,10m,.125m,1000000),
                new ModelSeed("gemini","gemini-2.5-flash","Gemini 2.5 Flash",.30m,2.5m,.03m,1000000),
                new ModelSeed("anthropic","claude-sonnet-5","Claude Sonnet 5",2m,10m,.2m,1000000),
                new ModelSeed("anthropic","claude-opus-4-8","Claude Opus 4.8",5m,25m,.5m,1000000),
                new ModelSeed("anthropic","claude-opus-4-7","Claude Opus 4.7",5m,25m,.5m,1000000),
                new ModelSeed("anthropic","claude-opus-4-6","Claude Opus 4.6",5m,25m,.5m,1000000),
                new ModelSeed("anthropic","claude-sonnet-4-6","Claude Sonnet 4.6",3m,15m,.3m,1000000),
                new ModelSeed("anthropic","claude-haiku-4-5","Claude Haiku 4.5",1m,5m,.1m,200000),
                new ModelSeed("deepseek","deepseek-v4-flash","DeepSeek V4 Flash",.14m,.28m,.0028m,1000000),
                new ModelSeed("deepseek","deepseek-v4-pro","DeepSeek V4 Pro",.435m,.87m,.003625m,1000000),
                new ModelSeed("kimi","kimi-k3","Kimi K3",3m,15m,.30m,1048576),
                new ModelSeed("kimi","kimi-k2.7-code","Kimi K2.7 Code",.95m,4m,.19m,262144,false,"vision"),
                new ModelSeed("kimi","kimi-k2.7-code-highspeed","Kimi K2.7 Code HighSpeed",1.90m,8m,.38m,262144,false,"vision"),
                new ModelSeed("kimi","kimi-k2.6","Kimi K2.6",.95m,4m,.16m,262144,false,"vision"),
                new ModelSeed("glm","glm-5.2","GLM 5.2",1m,3.2m,.2m,1000000),
                new ModelSeed("glm","glm-5v-turbo","GLM 5V Turbo",.8m,2m,.16m,200000,false,"vision"),
                new ModelSeed("glm","glm-4.7-flash","GLM 4.7 Flash",0m,0m,0m,128000),
                new ModelSeed("xai","grok-4.5","Grok 4.5",2m,6m,.30m,500000),
                new ModelSeed("xai","grok-4.3","Grok 4.3",1.25m,2.5m,.20m,1000000),
                new ModelSeed("xai","grok-build-0.1","Grok Build 0.1",1m,2m,.20m,256000),
                new ModelSeed("mistral","mistral-medium-latest","Mistral Medium 3.5",1.5m,7.5m,.15m,128000),
                new ModelSeed("mistral","mistral-large-latest","Mistral Large 3",.5m,1.5m,.05m,128000),
                new ModelSeed("mistral","mistral-small-latest","Mistral Small 4",.15m,.60m,.015m,128000),
                new ModelSeed("mistral","devstral-medium-latest","Devstral 2",.4m,2m,.04m,256000),
                new ModelSeed("mistral","devstral-small-latest","Devstral Small 2",.1m,.3m,.01m,256000),
                new ModelSeed("qwen","qwen3-max","Qwen3 Max",1.2m,6m,.24m,262144),
                new ModelSeed("qwen","qwen3-coder-plus","Qwen3 Coder Plus",1m,5m,.2m,1000000),
                new ModelSeed("cohere","command-a-03-2025","Command A",2.5m,10m,null,256000),
                new ModelSeed("cohere","command-r7b-12-2024","Command R7B",.0375m,.15m,null,128000)
            };
            var providerMap = await db.Providers.ToDictionaryAsync(x => x.Slug);
            db.Models.AddRange(models.Select(x => new AiModel
            {
                ProviderId = providerMap[x.Provider].Id, ModelId = x.Id, DisplayName = x.Name, Modality = x.Modality,
                InputPricePerMillionUsd = x.Input, OutputPricePerMillionUsd = x.Output, CachedInputPricePerMillionUsd = x.Cached,
                ContextWindow = x.Context, SupportsWebSocket = x.Ws, PricingSourceUrl = providerMap[x.Provider].PricingUrl,
                PriceSyncedAtUtc = new DateTime(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc)
            }));
        }
        await db.SaveChangesAsync();
    }
}
