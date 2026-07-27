using Microsoft.EntityFrameworkCore;
using Microsoft.Data.Sqlite;
using System.Data;
using System.Text.Json;

namespace AiBus.Api;

public static class SeedData
{
    private sealed record ProviderSeed(string Name, string Slug, string Logo, string BaseUrl, string PricingUrl, string Protocol);
    private sealed record ModelSeed(string Provider, string Id, string Name, decimal Input, decimal Output, decimal? Cached, int Context = 128000, bool Ws = false, string Modality = "text");
    private sealed record PriceSeed(string Label, string Unit, decimal? PriceUsd, string? Note = null);
    private sealed record ServiceModelSeed(string Provider, string Id, string Name, string ServiceType, string Modality, string EndpointPath, string SourceUrl, PriceSeed[] Prices, int Context = 0, bool Stream = true, bool Ws = false, bool Preview = false, string Region = "global", string Notes = "", string UpstreamBaseUrl = "");

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
        await EnsureUserApiKeyColumns(db);
        await EnsureModelCatalogColumns(db);
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
                new ProviderSeed("Z.ai / GLM", "glm", "https://cdn.simpleicons.org/zotero/00836B", "https://api.z.ai/api/paas/v4", "https://docs.z.ai/guides/overview/pricing", "openai"),
                new ProviderSeed("xAI", "xai", "https://cdn.simpleicons.org/x/111111", "https://api.x.ai/v1", "https://docs.x.ai/developers/pricing", "openai"),
                new ProviderSeed("Mistral AI", "mistral", "https://cdn.simpleicons.org/mistralai/FF7000", "https://api.mistral.ai/v1", "https://mistral.ai/pricing/api/", "openai"),
                new ProviderSeed("Alibaba Qwen", "qwen", "https://cdn.simpleicons.org/alibabacloud/FF6A00", "https://dashscope-intl.aliyuncs.com/compatible-mode/v1", "https://www.alibabacloud.com/help/en/model-studio/model-pricing", "openai"),
                new ProviderSeed("Cohere", "cohere", "/providers/cohere.svg", "https://api.cohere.ai/compatibility/v1", "https://cohere.com/pricing", "openai")
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
                new ModelSeed("openai","gpt-realtime-2.1","GPT Realtime 2.1",4m,24m,.4m,128000,true,"audio"),
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
        await EnsureModelCatalog(db);
        await db.SaveChangesAsync();
    }

    private static async Task EnsureUserApiKeyColumns(AppDbContext db)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        var hasProtectedApiKey = false;
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(\"UserApiKeys\")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                if (string.Equals(reader.GetString(1), nameof(UserApiKey.ProtectedApiKey), StringComparison.OrdinalIgnoreCase))
                {
                    hasProtectedApiKey = true;
                    break;
                }
            }
        }
        if (hasProtectedApiKey) return;
        await using var alter = connection.CreateCommand();
        alter.CommandText = "ALTER TABLE \"UserApiKeys\" ADD COLUMN \"ProtectedApiKey\" TEXT NULL";
        try
        {
            await alter.ExecuteNonQueryAsync();
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 1 && ex.Message.Contains("duplicate column name", StringComparison.OrdinalIgnoreCase))
        {
            // Another application instance completed the same idempotent startup migration.
        }
    }

    private static async Task EnsureModelCatalogColumns(AppDbContext db)
    {
        var additions = new Dictionary<string, string>
        {
            ["ServiceType"] = "TEXT NOT NULL DEFAULT 'chat'",
            ["EndpointPath"] = "TEXT NOT NULL DEFAULT '/v1/chat/completions'",
            ["Region"] = "TEXT NOT NULL DEFAULT 'global'",
            ["IsPreview"] = "INTEGER NOT NULL DEFAULT 0",
            ["PricingDetailsJson"] = "TEXT NOT NULL DEFAULT '[]'",
            ["PricingNotes"] = "TEXT NOT NULL DEFAULT ''",
            ["UpstreamBaseUrl"] = "TEXT NOT NULL DEFAULT ''",
            ["UpstreamPath"] = "TEXT NOT NULL DEFAULT ''"
        };
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open) await connection.OpenAsync();
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA table_info(\"Models\")";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync()) existing.Add(reader.GetString(1));
        }
        foreach (var column in additions.Where(x => !existing.Contains(x.Key)))
        {
            await using var command = connection.CreateCommand();
            // Column names and definitions come exclusively from the static allow-list above.
            command.CommandText = $"ALTER TABLE \"Models\" ADD COLUMN \"{column.Key}\" {column.Value}";
            await command.ExecuteNonQueryAsync();
        }
    }

    private static async Task EnsureModelCatalog(AppDbContext db)
    {
        const string catalogSnapshot = "2026-07-27";
        var catalogSnapshotSetting = await db.Settings.SingleOrDefaultAsync(x => x.Key == "catalog.model_snapshot");
        var refreshExistingCatalog = catalogSnapshotSetting?.Value != catalogSnapshot;
        var extraProviders = new[]
        {
            new ProviderSeed("ElevenLabs", "elevenlabs", "https://cdn.simpleicons.org/elevenlabs/111111", "https://api.elevenlabs.io/v1", "https://elevenlabs.io/pricing/api", "elevenlabs"),
            new ProviderSeed("Deepgram", "deepgram", "https://cdn.simpleicons.org/deepgram/13EF93", "https://api.deepgram.com/v1", "https://deepgram.com/pricing", "deepgram"),
            new ProviderSeed("AssemblyAI", "assemblyai", "https://cdn.simpleicons.org/assemblyai/6C47FF", "https://api.assemblyai.com/v2", "https://www.assemblyai.com/pricing", "assemblyai"),
            new ProviderSeed("Google Cloud Speech", "google-cloud-speech", "https://cdn.simpleicons.org/googlecloud/4285F4", "https://speech.googleapis.com", "https://cloud.google.com/speech-to-text/pricing", "google"),
            new ProviderSeed("Amazon Web Services", "aws-speech", "https://cdn.simpleicons.org/amazonwebservices/FF9900", "https://transcribe.us-east-1.amazonaws.com", "https://aws.amazon.com/transcribe/pricing/", "aws"),
            new ProviderSeed("Speechmatics", "speechmatics", "", "https://asr.api.speechmatics.com/v2", "https://www.speechmatics.com/pricing", "speechmatics"),
            new ProviderSeed("Gladia", "gladia", "", "https://api.gladia.io/v2", "https://support.gladia.io/article/understanding-our-transcription-pricing-pv1atikh8y9c8sw7sudm3rcy", "gladia")
        };
        var knownProviders = await db.Providers.ToDictionaryAsync(x => x.Slug);
        // Migrate only untouched historical defaults; administrator overrides remain authoritative.
        if (knownProviders.TryGetValue("glm", out var glmProvider) && glmProvider.BaseUrl == "https://open.bigmodel.cn/api/paas/v4")
        {
            glmProvider.BaseUrl = "https://api.z.ai/api/paas/v4";
            glmProvider.PricingUrl = "https://docs.z.ai/guides/overview/pricing";
        }
        if (knownProviders.TryGetValue("cohere", out var cohereProvider) && cohereProvider.BaseUrl == "https://api.cohere.com/compatibility/v1")
            cohereProvider.BaseUrl = "https://api.cohere.ai/compatibility/v1";
        foreach (var seed in extraProviders)
        {
            if (knownProviders.ContainsKey(seed.Slug)) continue;
            var provider = new AiProvider { Name = seed.Name, Slug = seed.Slug, LogoUrl = seed.Logo, BaseUrl = seed.BaseUrl, PricingUrl = seed.PricingUrl, Protocol = seed.Protocol };
            db.Providers.Add(provider); knownProviders[seed.Slug] = provider;
        }
        await db.SaveChangesAsync();

        static PriceSeed P(string label, string unit, decimal? price, string? note = null) => new(label, unit, price, note);
        var openAi = "https://developers.openai.com/api/docs/models";
        var gemini = "https://ai.google.dev/gemini-api/docs/pricing";
        var anthropic = "https://platform.claude.com/docs/en/about-claude/pricing";
        var deepseek = "https://api-docs.deepseek.com/quick_start/pricing";
        var kimi = "https://platform.kimi.ai/docs/models";
        var xai = "https://docs.x.ai/developers/model-capabilities/audio/voice";
        var xaiPricing = "https://docs.x.ai/developers/pricing";
        var mistral = "https://mistral.ai/pricing/api/";
        var qwen = "https://www.alibabacloud.com/help/en/model-studio/model-pricing";
        var glm = "https://docs.z.ai/guides/overview/pricing";
        var cohere = "https://docs.cohere.com/v2/docs/audio-transcription-quickstart";
        var eleven = "https://elevenlabs.io/pricing/api";
        var deepgram = "https://deepgram.com/pricing";
        var assembly = "https://www.assemblyai.com/pricing";
        var googleSpeech = "https://cloud.google.com/speech-to-text/pricing";
        var googleTts = "https://cloud.google.com/text-to-speech/pricing";
        var awsTranscribe = "https://aws.amazon.com/transcribe/pricing/";
        var awsPolly = "https://aws.amazon.com/polly/pricing/";
        var speechmatics = "https://www.speechmatics.com/pricing";
        var gladia = "https://support.gladia.io/article/understanding-our-transcription-pricing-pv1atikh8y9c8sw7sudm3rcy";
        var models = new[]
        {
            new ServiceModelSeed("openai","gpt-5.6","GPT-5.6 (Sol alias)","chat","text-image","/v1/chat/completions",openAi,[P("ورودی تا 272K","million_text_tokens",5),P("ورودی Cache تا 272K","million_text_tokens",.5m),P("خروجی تا 272K","million_text_tokens",30),P("ورودی بیش از 272K","million_text_tokens",10),P("ورودی Cache بیش از 272K","million_text_tokens",1),P("خروجی بیش از 272K","million_text_tokens",45)],1050000),
            new ServiceModelSeed("openai","gpt-5.6-sol","GPT-5.6 Sol","chat","text-image","/v1/chat/completions",openAi,[P("ورودی تا 272K","million_text_tokens",5),P("ورودی Cache تا 272K","million_text_tokens",.5m),P("خروجی تا 272K","million_text_tokens",30),P("ورودی بیش از 272K","million_text_tokens",10),P("ورودی Cache بیش از 272K","million_text_tokens",1),P("خروجی بیش از 272K","million_text_tokens",45)],1050000),
            new ServiceModelSeed("openai","gpt-5.6-terra","GPT-5.6 Terra","chat","text-image","/v1/chat/completions",openAi,[P("ورودی تا 272K","million_text_tokens",2.5m),P("ورودی Cache تا 272K","million_text_tokens",.25m),P("خروجی تا 272K","million_text_tokens",15),P("ورودی بیش از 272K","million_text_tokens",5),P("ورودی Cache بیش از 272K","million_text_tokens",.5m),P("خروجی بیش از 272K","million_text_tokens",22.5m)],1050000),
            new ServiceModelSeed("openai","gpt-5.6-luna","GPT-5.6 Luna","chat","text-image","/v1/chat/completions",openAi,[P("ورودی تا 272K","million_text_tokens",1),P("ورودی Cache تا 272K","million_text_tokens",.1m),P("خروجی تا 272K","million_text_tokens",6),P("ورودی بیش از 272K","million_text_tokens",2),P("ورودی Cache بیش از 272K","million_text_tokens",.2m),P("خروجی بیش از 272K","million_text_tokens",9)],1050000),
            new ServiceModelSeed("openai","gpt-5.5","GPT-5.5","chat","text-image","/v1/chat/completions",openAi,[P("ورودی تا 272K","million_text_tokens",5),P("ورودی Cache تا 272K","million_text_tokens",.5m),P("خروجی تا 272K","million_text_tokens",30),P("ورودی بیش از 272K","million_text_tokens",10),P("ورودی Cache بیش از 272K","million_text_tokens",1),P("خروجی بیش از 272K","million_text_tokens",45)],1050000),
            new ServiceModelSeed("openai","gpt-5.5-pro","GPT-5.5 Pro","chat","text-image","/v1/responses",openAi,[P("ورودی تا 272K","million_text_tokens",30),P("خروجی تا 272K","million_text_tokens",180),P("ورودی بیش از 272K","million_text_tokens",60),P("خروجی بیش از 272K","million_text_tokens",270)],1050000),
            new ServiceModelSeed("openai","gpt-5.4","GPT-5.4","chat","text-image","/v1/chat/completions",openAi,[P("ورودی تا 272K","million_text_tokens",2.5m),P("ورودی Cache تا 272K","million_text_tokens",.25m),P("خروجی تا 272K","million_text_tokens",15),P("ورودی بیش از 272K","million_text_tokens",5),P("ورودی Cache بیش از 272K","million_text_tokens",.5m),P("خروجی بیش از 272K","million_text_tokens",22.5m)],1050000),
            new ServiceModelSeed("openai","gpt-5.4-mini","GPT-5.4 Mini","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.75m),P("ورودی Cache","million_text_tokens",.075m),P("خروجی متن","million_text_tokens",4.5m)],400000),
            new ServiceModelSeed("openai","gpt-5.4-nano","GPT-5.4 Nano","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.2m),P("ورودی Cache","million_text_tokens",.02m),P("خروجی متن","million_text_tokens",1.25m)],400000),
            new ServiceModelSeed("openai","gpt-5.4-pro","GPT-5.4 Pro","chat","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",30),P("خروجی متن","million_text_tokens",180)],1050000),
            new ServiceModelSeed("openai","gpt-5.2","GPT-5.2","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",1.75m),P("ورودی Cache","million_text_tokens",.175m),P("خروجی متن","million_text_tokens",14)],400000),
            new ServiceModelSeed("openai","gpt-5.2-pro","GPT-5.2 Pro","chat","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",21),P("خروجی متن","million_text_tokens",168)],400000,Stream:false),
            new ServiceModelSeed("openai","gpt-5.1","GPT-5.1","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",1.25m),P("ورودی Cache","million_text_tokens",.125m),P("خروجی متن","million_text_tokens",10)],400000),
            new ServiceModelSeed("openai","gpt-5","GPT-5","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",1.25m),P("ورودی Cache","million_text_tokens",.125m),P("خروجی متن","million_text_tokens",10)],400000),
            new ServiceModelSeed("openai","gpt-5-pro","GPT-5 Pro","chat","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",15),P("خروجی متن","million_text_tokens",120)],400000,Stream:false),
            new ServiceModelSeed("openai","gpt-5-mini","GPT-5 Mini","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.25m),P("ورودی Cache","million_text_tokens",.025m),P("خروجی متن","million_text_tokens",2)],400000),
            new ServiceModelSeed("openai","gpt-5-nano","GPT-5 Nano","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.05m),P("ورودی Cache","million_text_tokens",.005m),P("خروجی متن","million_text_tokens",.4m)],400000),
            new ServiceModelSeed("openai","o3-pro","o3 Pro","reasoning","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",20),P("خروجی متن","million_text_tokens",80)],200000,Stream:false),
            new ServiceModelSeed("openai","o3","o3","reasoning","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",2),P("ورودی Cache","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",8)],200000),
            new ServiceModelSeed("openai","gpt-4.1","GPT-4.1","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",2),P("ورودی Cache","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",8)],1047576),
            new ServiceModelSeed("openai","gpt-4.1-mini","GPT-4.1 Mini","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.4m),P("ورودی Cache","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",1.6m)],1047576),
            new ServiceModelSeed("openai","gpt-4o-mini","GPT-4o Mini","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.15m),P("ورودی Cache","million_text_tokens",.075m),P("خروجی متن","million_text_tokens",.6m)],128000),
            new ServiceModelSeed("openai","chat-latest","Chat Latest","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",30)],400000),
            new ServiceModelSeed("openai","gpt-5.3-codex","GPT-5.3 Codex","coding","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",1.75m),P("ورودی Cache","million_text_tokens",.175m),P("خروجی متن","million_text_tokens",14)],400000),
            new ServiceModelSeed("openai","gpt-5-search-api","GPT-5 Search API","search","text","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",1.25m),P("ورودی Cache","million_text_tokens",.125m),P("خروجی متن","million_text_tokens",10),P("فراخوانی جستجوی وب","thousand_calls",10)],400000,Notes:"هزینه ابزار جستجوی وب جدا از هزینه توکن محاسبه می‌شود."),
            new ServiceModelSeed("openai","gpt-4o-search-preview","GPT-4o Search Preview","search","text","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",2.5m),P("ورودی Cache","million_text_tokens",1.25m),P("خروجی متن","million_text_tokens",10),P("فراخوانی جستجوی وب","thousand_calls",10)],128000,Preview:true),
            new ServiceModelSeed("openai","gpt-4o-mini-search-preview","GPT-4o Mini Search Preview","search","text","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.15m),P("ورودی Cache","million_text_tokens",.075m),P("خروجی متن","million_text_tokens",.6m),P("فراخوانی جستجوی وب","thousand_calls",10)],128000,Preview:true),
            new ServiceModelSeed("openai","o3-deep-research","o3 Deep Research","deep_research","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",5),P("خروجی متن","million_text_tokens",20)],200000),
            new ServiceModelSeed("openai","text-embedding-3-large","Text Embedding 3 Large","embeddings","text-vector","/v1/embeddings",openAi,[P("ورودی متن","million_text_tokens",.13m)],8191,false),
            new ServiceModelSeed("openai","text-embedding-3-small","Text Embedding 3 Small","embeddings","text-vector","/v1/embeddings",openAi,[P("ورودی متن","million_text_tokens",.02m)],8191,false),
            new ServiceModelSeed("openai","text-embedding-ada-002","Text Embedding Ada 002","embeddings","text-vector","/v1/embeddings",openAi,[P("ورودی متن","million_text_tokens",.1m)],8191,false),
            new ServiceModelSeed("openai","omni-moderation-latest","Omni Moderation","moderation","text-image","/v1/moderations",openAi,[P("بررسی ایمنی","free",0)],128000,false),
            new ServiceModelSeed("openai","gpt-image-2","GPT Image 2","image_generation","text-image","/v1/images/generations","https://developers.openai.com/api/docs/models/gpt-image-2",[P("ورودی متن","million_text_tokens",5),P("ورودی Cache متن","million_text_tokens",1.25m),P("ورودی تصویر","million_image_tokens",8),P("ورودی Cache تصویر","million_image_tokens",2),P("خروجی تصویر","million_image_tokens",30)],Stream:false),
            new ServiceModelSeed("openai","gpt-image-1.5","GPT Image 1.5","image_generation","text-image","/v1/images/generations","https://developers.openai.com/api/docs/models/gpt-image-1.5",[P("ورودی متن","million_text_tokens",5),P("ورودی Cache متن","million_text_tokens",1.25m),P("خروجی متن","million_text_tokens",10),P("ورودی تصویر","million_image_tokens",8),P("ورودی Cache تصویر","million_image_tokens",2),P("خروجی تصویر","million_image_tokens",32)],Stream:false),
            new ServiceModelSeed("openai","gpt-image-1-mini","GPT Image 1 Mini","image_generation","text-image","/v1/images/generations",openAi,[P("ورودی متن","million_text_tokens",2),P("ورودی Cache متن","million_text_tokens",.2m),P("ورودی تصویر","million_image_tokens",2.5m),P("ورودی Cache تصویر","million_image_tokens",.25m),P("خروجی تصویر","million_image_tokens",8)],Stream:false),
            new ServiceModelSeed("openai","sora-2","Sora 2","video_generation","text-image-video","/v1/videos","https://developers.openai.com/api/docs/models/sora-2",[P("ویدیوی 720p","second",.1m)],Stream:false),
            new ServiceModelSeed("openai","sora-2-pro","Sora 2 Pro","video_generation","text-image-video","/v1/videos","https://developers.openai.com/api/docs/models/sora-2-pro",[P("ویدیوی 720p","second",.3m),P("ویدیوی 1024p","second",.5m),P("ویدیوی 1080p","second",.7m)],Stream:false),
            new ServiceModelSeed("openai","gpt-realtime-2.1","GPT Realtime 2.1","speech_to_speech","audio-image","/v1/realtime",openAi,[P("ورودی متن","million_text_tokens",4),P("ورودی Cache متن","million_text_tokens",.4m),P("خروجی متن","million_text_tokens",24),P("ورودی تصویر","million_image_tokens",5),P("ورودی Cache تصویر","million_image_tokens",.5m),P("ورودی صوت","million_audio_tokens",32),P("ورودی Cache صوت","million_audio_tokens",.4m),P("خروجی صوت","million_audio_tokens",64)],128000,false,true),
            new ServiceModelSeed("openai","gpt-realtime-2.1-mini","GPT Realtime 2.1 Mini","speech_to_speech","audio-image","/v1/realtime",openAi,[P("ورودی متن","million_text_tokens",.6m),P("ورودی Cache متن","million_text_tokens",.06m),P("خروجی متن","million_text_tokens",2.4m),P("ورودی تصویر","million_image_tokens",.8m),P("ورودی Cache تصویر","million_image_tokens",.08m),P("ورودی صوت","million_audio_tokens",10),P("ورودی Cache صوت","million_audio_tokens",.3m),P("خروجی صوت","million_audio_tokens",20)],128000,false,true),
            new ServiceModelSeed("openai","gpt-realtime-2","GPT Realtime 2","speech_to_speech","audio","/v1/realtime",openAi,[P("ورودی متن","million_text_tokens",4),P("خروجی متن","million_text_tokens",24),P("ورودی صوت","million_audio_tokens",32),P("خروجی صوت","million_audio_tokens",64)],128000,false,true),
            new ServiceModelSeed("openai","gpt-realtime-1.5","GPT Realtime 1.5","speech_to_speech","audio","/v1/realtime",openAi,[P("ورودی متن","million_text_tokens",4),P("خروجی متن","million_text_tokens",16),P("ورودی صوت","million_audio_tokens",32),P("خروجی صوت","million_audio_tokens",64)],32000,false,true),
            new ServiceModelSeed("openai","gpt-audio-1.5","GPT Audio 1.5","speech_to_speech","audio","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",2.5m),P("خروجی متن","million_text_tokens",10),P("ورودی صوت","million_audio_tokens",32),P("خروجی صوت","million_audio_tokens",64)],128000),
            new ServiceModelSeed("openai","gpt-realtime-translate","GPT Realtime Translate","realtime_translation","audio","/v1/realtime/translations","https://developers.openai.com/api/docs/models/gpt-realtime-translate",[P("ترجمه صوت هم‌زمان","minute",.034m)],16000,true,true),
            new ServiceModelSeed("openai","gpt-realtime-whisper","GPT Realtime Whisper","speech_to_text","audio-text","/v1/realtime","https://developers.openai.com/api/docs/models/gpt-realtime-whisper",[P("رونویسی هم‌زمان","minute",.017m)],16000,true,true),
            new ServiceModelSeed("openai","gpt-4o-transcribe","GPT-4o Transcribe","speech_to_text","audio-text","/v1/audio/transcriptions","https://developers.openai.com/api/docs/models/gpt-4o-transcribe",[P("ورودی صوت","million_audio_tokens",2.5m),P("خروجی متن","million_text_tokens",10)],16000),
            new ServiceModelSeed("openai","gpt-4o-mini-transcribe","GPT-4o Mini Transcribe","speech_to_text","audio-text","/v1/audio/transcriptions",openAi,[P("ورودی صوت","million_audio_tokens",1.25m),P("خروجی متن","million_text_tokens",5)],16000),
            new ServiceModelSeed("openai","gpt-4o-transcribe-diarize","GPT-4o Transcribe Diarize","speech_to_text","audio-text","/v1/audio/transcriptions","https://developers.openai.com/api/docs/models/gpt-4o-transcribe-diarize",[P("ورودی صوت","million_audio_tokens",2.5m),P("خروجی متن","million_text_tokens",10)],16000),
            new ServiceModelSeed("openai","tts-1","TTS-1","text_to_speech","text-audio","/v1/audio/speech","https://developers.openai.com/api/docs/models/tts-1",[P("تولید گفتار","million_characters",15)]),
            new ServiceModelSeed("openai","tts-1-hd","TTS-1 HD","text_to_speech","text-audio","/v1/audio/speech","https://developers.openai.com/api/docs/models/tts-1-hd",[P("تولید گفتار","million_characters",30)]),
            new ServiceModelSeed("openai","whisper-1","Whisper","speech_to_text","audio-text","/v1/audio/transcriptions","https://developers.openai.com/api/docs/models/whisper-1",[P("رونویسی یا ترجمه به انگلیسی","minute",.006m)],Notes:"هم رونویسی چندزبانه و هم ترجمه صوت به انگلیسی را پشتیبانی می‌کند."),

            new ServiceModelSeed("gemini","gemini-3.5-live-translate-preview","Gemini 3.5 Live Translate","realtime_translation","audio","/v1beta/live",gemini,[P("ورودی صوت","million_audio_tokens",3.5m,"≈ $0.0053/min"),P("خروجی صوت","million_audio_tokens",21,"≈ $0.0315/min")],128000,true,true,true),
            new ServiceModelSeed("gemini","gemini-3.1-flash-live-preview","Gemini 3.1 Flash Live","speech_to_speech","audio","/v1beta/live",gemini,[P("ورودی متن","million_text_tokens",.75m),P("خروجی متن","million_text_tokens",4.5m),P("ورودی صوت","million_audio_tokens",3,"≈ $0.005/min"),P("خروجی صوت","million_audio_tokens",12,"≈ $0.018/min")],128000,true,true,true),
            new ServiceModelSeed("gemini","gemini-3.1-flash-tts-preview","Gemini 3.1 Flash TTS","text_to_speech","text-audio","/v1beta/models/gemini-3.1-flash-tts-preview:generateContent",gemini,[P("ورودی متن","million_text_tokens",1),P("خروجی صوت","million_audio_tokens",20)],Preview:true),
            new ServiceModelSeed("gemini","gemini-2.5-flash-native-audio-preview-12-2025","Gemini 2.5 Flash Native Audio","speech_to_speech","audio","/v1beta/live",gemini,[P("ورودی متن","million_text_tokens",.5m),P("ورودی صوت/ویدئو","million_audio_tokens",3),P("خروجی متن","million_text_tokens",2),P("خروجی صوت","million_audio_tokens",12)],128000,true,true,true),
            new ServiceModelSeed("gemini","gemini-2.5-flash-preview-tts","Gemini 2.5 Flash TTS","text_to_speech","text-audio","/v1beta/models/gemini-2.5-flash-preview-tts:generateContent",gemini,[P("ورودی متن","million_text_tokens",.5m),P("خروجی صوت","million_audio_tokens",10)],Preview:true),
            new ServiceModelSeed("gemini","gemini-2.5-pro-preview-tts","Gemini 2.5 Pro TTS","text_to_speech","text-audio","/v1beta/models/gemini-2.5-pro-preview-tts:generateContent",gemini,[P("ورودی متن","million_text_tokens",1),P("خروجی صوت","million_audio_tokens",20)],Preview:true),

            new ServiceModelSeed("gemini","gemini-3.6-flash","Gemini 3.6 Flash","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",1.5m),P("ورودی Cache","million_text_tokens",.15m),P("خروجی متن","million_text_tokens",7.5m)],1000000),
            new ServiceModelSeed("gemini","gemini-3.5-flash","Gemini 3.5 Flash","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",1.5m),P("ورودی Cache","million_text_tokens",.15m),P("خروجی متن","million_text_tokens",9)],1000000),
            new ServiceModelSeed("gemini","gemini-3.5-flash-lite","Gemini 3.5 Flash Lite","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",.3m),P("ورودی Cache","million_text_tokens",.03m),P("خروجی متن","million_text_tokens",2.5m)],1000000),
            new ServiceModelSeed("gemini","gemini-3.1-pro-preview","Gemini 3.1 Pro Preview","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی تا 200K","million_text_tokens",2),P("ورودی Cache تا 200K","million_text_tokens",.2m),P("خروجی تا 200K","million_text_tokens",12),P("ورودی بیش از 200K","million_text_tokens",4),P("ورودی Cache بیش از 200K","million_text_tokens",.4m),P("خروجی بیش از 200K","million_text_tokens",18)],1000000,Preview:true),
            new ServiceModelSeed("gemini","gemini-3.1-flash-lite","Gemini 3.1 Flash Lite","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",.25m),P("ورودی صوت","million_audio_tokens",.5m),P("ورودی Cache متن","million_text_tokens",.025m),P("ورودی Cache صوت","million_audio_tokens",.05m),P("خروجی متن","million_text_tokens",1.5m)],1000000),
            new ServiceModelSeed("gemini","gemini-3-flash-preview","Gemini 3 Flash Preview","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",.5m),P("ورودی صوت","million_audio_tokens",1),P("ورودی Cache متن","million_text_tokens",.05m),P("ورودی Cache صوت","million_audio_tokens",.1m),P("خروجی متن","million_text_tokens",3)],1000000,Preview:true),
            new ServiceModelSeed("gemini","gemini-2.5-pro","Gemini 2.5 Pro","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی تا 200K","million_text_tokens",1.25m),P("ورودی Cache تا 200K","million_text_tokens",.125m),P("خروجی تا 200K","million_text_tokens",10),P("ورودی بیش از 200K","million_text_tokens",2.5m),P("ورودی Cache بیش از 200K","million_text_tokens",.25m),P("خروجی بیش از 200K","million_text_tokens",15)],1000000),
            new ServiceModelSeed("gemini","gemini-2.5-flash","Gemini 2.5 Flash","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",.3m),P("ورودی صوت","million_audio_tokens",1),P("ورودی Cache متن","million_text_tokens",.03m),P("ورودی Cache صوت","million_audio_tokens",.1m),P("خروجی متن","million_text_tokens",2.5m)],1000000),
            new ServiceModelSeed("gemini","gemini-2.5-flash-lite","Gemini 2.5 Flash Lite","chat","text-image-audio-video","/v1/chat/completions",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",.1m),P("ورودی صوت","million_audio_tokens",.3m),P("ورودی Cache متن","million_text_tokens",.01m),P("ورودی Cache صوت","million_audio_tokens",.03m),P("خروجی متن","million_text_tokens",.4m)],1000000),
            new ServiceModelSeed("gemini","gemini-omni-flash-preview","Gemini Omni Flash Preview","speech_to_speech","text-image-audio-video","/v1beta/live",gemini,[P("ورودی همه رسانه‌ها","million_text_tokens",1.5m),P("خروجی متن","million_text_tokens",9),P("خروجی ویدئو","million_video_tokens",17.5m,"حدود $0.10 بر ثانیه ویدئوی 720p")],1000000,true,true,true),
            new ServiceModelSeed("gemini","gemini-3.1-flash-image","Gemini 3.1 Flash Image","image_generation","text-image","/v1beta/models/gemini-3.1-flash-image:generateContent",gemini,[P("ورودی متن/تصویر","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",3),P("خروجی تصویر","million_image_tokens",60)],Stream:false),
            new ServiceModelSeed("gemini","gemini-3.1-flash-lite-image","Gemini 3.1 Flash Lite Image","image_generation","text-image","/v1beta/models/gemini-3.1-flash-lite-image:generateContent",gemini,[P("ورودی متن/تصویر","million_text_tokens",.25m),P("خروجی متن","million_text_tokens",1.5m),P("خروجی تصویر","million_image_tokens",30)],Stream:false),
            new ServiceModelSeed("gemini","gemini-3-pro-image","Gemini 3 Pro Image","image_generation","text-image","/v1beta/models/gemini-3-pro-image:generateContent",gemini,[P("ورودی متن/تصویر","million_text_tokens",2),P("خروجی متن","million_text_tokens",12),P("خروجی تصویر","million_image_tokens",120)],Stream:false),
            new ServiceModelSeed("gemini","gemini-2.5-flash-image","Gemini 2.5 Flash Image","image_generation","text-image","/v1beta/models/gemini-2.5-flash-image:generateContent",gemini,[P("ورودی متن/تصویر","million_text_tokens",.3m),P("خروجی تصویر","image",.039m,"معادل $30 به‌ازای یک میلیون توکن تصویر")],Stream:false),
            new ServiceModelSeed("gemini","veo-3.1-generate-preview","Veo 3.1","video_generation","text-image-video","/v1beta/models/veo-3.1-generate-preview:predictLongRunning",gemini,[P("ویدئوی 720p/1080p","second",.4m),P("ویدئوی 4K","second",.6m)],Stream:false,Preview:true),
            new ServiceModelSeed("gemini","veo-3.1-fast-generate-preview","Veo 3.1 Fast","video_generation","text-image-video","/v1beta/models/veo-3.1-fast-generate-preview:predictLongRunning",gemini,[P("ویدئوی 720p","second",.1m),P("ویدئوی 1080p","second",.12m),P("ویدئوی 4K","second",.3m)],Stream:false,Preview:true),
            new ServiceModelSeed("gemini","veo-3.1-lite-generate-preview","Veo 3.1 Lite","video_generation","text-image-video","/v1beta/models/veo-3.1-lite-generate-preview:predictLongRunning",gemini,[P("ویدئوی 720p","second",.05m),P("ویدئوی 1080p","second",.08m)],Stream:false,Preview:true),
            new ServiceModelSeed("gemini","lyria-3-clip-preview","Lyria 3 Clip","music_generation","text-audio","/v1beta/models/lyria-3-clip-preview:predict",gemini,[P("تولید موسیقی","song",.04m)],Stream:false,Preview:true),
            new ServiceModelSeed("gemini","lyria-3-pro-preview","Lyria 3 Pro","music_generation","text-audio","/v1beta/models/lyria-3-pro-preview:predict",gemini,[P("تولید موسیقی","song",.08m)],Stream:false,Preview:true),
            new ServiceModelSeed("gemini","gemini-embedding-2","Gemini Embedding 2","embeddings","multimodal-vector","/v1beta/models/gemini-embedding-2:embedContent",gemini,[P("ورودی متن","million_text_tokens",.2m),P("ورودی تصویر","million_image_tokens",.45m),P("ورودی صوت","million_audio_tokens",6.5m),P("ورودی ویدئو","million_video_tokens",12)],Stream:false),
            new ServiceModelSeed("gemini","gemini-embedding-001","Gemini Embedding 001","embeddings","text-vector","/v1beta/models/gemini-embedding-001:embedContent",gemini,[P("ورودی متن","million_text_tokens",.15m)],Stream:false),
            new ServiceModelSeed("gemini","gemini-robotics-er-1.6-preview","Gemini Robotics ER 1.6","robotics","text-image-audio-video","/v1beta/models/gemini-robotics-er-1.6-preview:generateContent",gemini,[P("ورودی متن/تصویر/ویدئو","million_text_tokens",1),P("ورودی صوت","million_audio_tokens",2),P("خروجی متن","million_text_tokens",5)],1000000,Preview:true),
            new ServiceModelSeed("gemini","gemini-2.5-computer-use-preview-10-2025","Gemini 2.5 Computer Use","computer_use","text-image","/v1beta/models/gemini-2.5-computer-use-preview-10-2025:generateContent",gemini,[P("ورودی تا 200K","million_text_tokens",1.25m),P("خروجی تا 200K","million_text_tokens",10),P("ورودی بیش از 200K","million_text_tokens",2.5m),P("خروجی بیش از 200K","million_text_tokens",15)],1000000,Preview:true),

            new ServiceModelSeed("anthropic","claude-sonnet-5","Claude Sonnet 5","chat","text-image","/v1/messages",anthropic,[P("ورودی متن تا 2026-08-31","million_text_tokens",2),P("ورودی Cache hit تا 2026-08-31","million_text_tokens",.2m),P("خروجی متن تا 2026-08-31","million_text_tokens",10),P("ورودی متن از 2026-09-01","million_text_tokens",3),P("ورودی Cache hit از 2026-09-01","million_text_tokens",.3m),P("خروجی متن از 2026-09-01","million_text_tokens",15)],1000000,Notes:"نرخ معرفی $2/$10 تا 31 اوت 2026 معتبر است؛ سپس $3/$15."),
            new ServiceModelSeed("anthropic","claude-fable-5","Claude Fable 5","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",10),P("ورودی Cache hit","million_text_tokens",1),P("خروجی متن","million_text_tokens",50)],1000000),
            new ServiceModelSeed("anthropic","claude-opus-5","Claude Opus 5","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache hit","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",25)],1000000),
            new ServiceModelSeed("anthropic","claude-mythos-5","Claude Mythos 5","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",10),P("ورودی Cache hit","million_text_tokens",1),P("خروجی متن","million_text_tokens",50)],1000000,Notes:"دسترسی invitation-only است."),
            new ServiceModelSeed("anthropic","claude-opus-4-8","Claude Opus 4.8","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache hit","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",25)],1000000),
            new ServiceModelSeed("anthropic","claude-opus-4-7","Claude Opus 4.7","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache hit","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",25)],1000000),
            new ServiceModelSeed("anthropic","claude-opus-4-6","Claude Opus 4.6","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache hit","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",25)],1000000),
            new ServiceModelSeed("anthropic","claude-sonnet-4-6","Claude Sonnet 4.6","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",3),P("ورودی Cache hit","million_text_tokens",.3m),P("خروجی متن","million_text_tokens",15)],1000000),
            new ServiceModelSeed("anthropic","claude-haiku-4-5","Claude Haiku 4.5","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",1),P("ورودی Cache hit","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",5)],200000),
            new ServiceModelSeed("anthropic","claude-opus-4-5","Claude Opus 4.5 (alias)","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache hit","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",25)],200000),
            new ServiceModelSeed("anthropic","claude-opus-4-5-20251101","Claude Opus 4.5","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache hit","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",25)],200000),
            new ServiceModelSeed("anthropic","claude-sonnet-4-5","Claude Sonnet 4.5 (alias)","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",3),P("ورودی Cache hit","million_text_tokens",.3m),P("خروجی متن","million_text_tokens",15)],200000),
            new ServiceModelSeed("anthropic","claude-sonnet-4-5-20250929","Claude Sonnet 4.5","chat","text-image","/v1/messages",anthropic,[P("ورودی متن","million_text_tokens",3),P("ورودی Cache hit","million_text_tokens",.3m),P("خروجی متن","million_text_tokens",15)],200000),

            new ServiceModelSeed("deepseek","deepseek-v4-flash","DeepSeek V4 Flash","chat","text-image","/v1/chat/completions",deepseek,[P("ورودی متن","million_text_tokens",.14m),P("ورودی Cache hit","million_text_tokens",.0028m),P("خروجی متن","million_text_tokens",.28m)],1000000),
            new ServiceModelSeed("deepseek","deepseek-v4-pro","DeepSeek V4 Pro","chat","text-image","/v1/chat/completions",deepseek,[P("ورودی متن","million_text_tokens",.435m),P("ورودی Cache hit","million_text_tokens",.003625m),P("خروجی متن","million_text_tokens",.87m)],1000000),

            new ServiceModelSeed("kimi","kimi-k3","Kimi K3","chat","text-image-video","/v1/chat/completions",kimi,[P("ورودی متن","million_text_tokens",3),P("ورودی Cache hit","million_text_tokens",.3m),P("خروجی متن","million_text_tokens",15)],1048576),
            new ServiceModelSeed("kimi","kimi-k2.7-code","Kimi K2.7 Code","coding","text-image-video","/v1/chat/completions",kimi,[P("ورودی متن","million_text_tokens",.95m),P("ورودی Cache hit","million_text_tokens",.19m),P("خروجی متن","million_text_tokens",4)],262144),
            new ServiceModelSeed("kimi","kimi-k2.7-code-highspeed","Kimi K2.7 Code HighSpeed","coding","text-image-video","/v1/chat/completions",kimi,[P("ورودی متن","million_text_tokens",1.9m),P("ورودی Cache hit","million_text_tokens",.38m),P("خروجی متن","million_text_tokens",8)],262144),
            new ServiceModelSeed("kimi","kimi-k2.6","Kimi K2.6","chat","text-image-video","/v1/chat/completions",kimi,[P("ورودی متن","million_text_tokens",.95m),P("ورودی Cache hit","million_text_tokens",.16m),P("خروجی متن","million_text_tokens",4)],262144),

            new ServiceModelSeed("xai","grok-4.5","Grok 4.5","chat","text-image","/v1/chat/completions",xaiPricing,[P("ورودی کمتر از 200K","million_text_tokens",2),P("ورودی Cache کمتر از 200K","million_text_tokens",.3m),P("خروجی کمتر از 200K","million_text_tokens",6),P("ورودی از 200K","million_text_tokens",4),P("ورودی Cache از 200K","million_text_tokens",.6m),P("خروجی از 200K","million_text_tokens",12)],500000),
            new ServiceModelSeed("xai","grok-4.3","Grok 4.3","chat","text-image","/v1/chat/completions",xaiPricing,[P("ورودی کمتر از 200K","million_text_tokens",1.25m),P("ورودی Cache کمتر از 200K","million_text_tokens",.2m),P("خروجی کمتر از 200K","million_text_tokens",2.5m),P("ورودی از 200K","million_text_tokens",2.5m),P("ورودی Cache از 200K","million_text_tokens",.4m),P("خروجی از 200K","million_text_tokens",5)],1000000),
            new ServiceModelSeed("xai","grok-build-0.1","Grok Build 0.1","coding","text-image","/v1/chat/completions",xaiPricing,[P("ورودی کمتر از 200K","million_text_tokens",1),P("ورودی Cache کمتر از 200K","million_text_tokens",.2m),P("خروجی کمتر از 200K","million_text_tokens",2),P("ورودی از 200K","million_text_tokens",2),P("ورودی Cache از 200K","million_text_tokens",.4m),P("خروجی از 200K","million_text_tokens",4)],256000),
            new ServiceModelSeed("xai","grok-4.20-multi-agent-0309","Grok 4.20 Multi-Agent","chat","text-image","/v1/responses",xaiPricing,[P("ورودی کمتر از 200K","million_text_tokens",1.25m),P("ورودی Cache کمتر از 200K","million_text_tokens",.2m),P("خروجی کمتر از 200K","million_text_tokens",2.5m),P("ورودی از 200K","million_text_tokens",2.5m),P("ورودی Cache از 200K","million_text_tokens",.4m),P("خروجی از 200K","million_text_tokens",5)],1000000),
            new ServiceModelSeed("xai","grok-4.20-0309-reasoning","Grok 4.20 Reasoning","reasoning","text-image","/v1/responses",xaiPricing,[P("ورودی کمتر از 200K","million_text_tokens",1.25m),P("ورودی Cache کمتر از 200K","million_text_tokens",.2m),P("خروجی کمتر از 200K","million_text_tokens",2.5m),P("ورودی از 200K","million_text_tokens",2.5m),P("ورودی Cache از 200K","million_text_tokens",.4m),P("خروجی از 200K","million_text_tokens",5)],1000000),
            new ServiceModelSeed("xai","grok-4.20-0309-non-reasoning","Grok 4.20 Non-Reasoning","chat","text-image","/v1/responses",xaiPricing,[P("ورودی کمتر از 200K","million_text_tokens",1.25m),P("ورودی Cache کمتر از 200K","million_text_tokens",.2m),P("خروجی کمتر از 200K","million_text_tokens",2.5m),P("ورودی از 200K","million_text_tokens",2.5m),P("ورودی Cache از 200K","million_text_tokens",.4m),P("خروجی از 200K","million_text_tokens",5)],1000000),
            new ServiceModelSeed("xai","grok-imagine-image-quality","Grok Imagine Image Quality","image_generation","text-image","/v1/images/generations",xaiPricing,[P("ورودی تصویر","image",.01m),P("خروجی تصویر 1K","image",.05m),P("خروجی تصویر 2K","image",.07m)],Stream:false),
            new ServiceModelSeed("xai","grok-imagine-image","Grok Imagine Image","image_generation","text-image","/v1/images/generations",xaiPricing,[P("ورودی تصویر","image",.002m),P("خروجی تصویر","image",.02m)],Stream:false),
            new ServiceModelSeed("xai","grok-imagine-video-1.5","Grok Imagine Video 1.5","video_generation","text-image-video","/v1/videos/generations",xaiPricing,[P("ورودی تصویر","image",.01m),P("خروجی 480p","second",.08m),P("خروجی 720p","second",.14m),P("خروجی 1080p","second",.25m)],Stream:false),
            new ServiceModelSeed("xai","grok-imagine-video","Grok Imagine Video","video_generation","text-image-video","/v1/videos/generations",xaiPricing,[P("ورودی تصویر","image",.002m),P("ورودی ویدئو","second",.01m),P("خروجی 480p","second",.05m),P("خروجی 720p","second",.07m)],Stream:false),

            new ServiceModelSeed("xai","grok-voice-latest","Grok Voice Agent","speech_to_speech","audio","/v1/realtime",xai,[P("صوت ارسالی یا دریافتی","minute",.05m),P("پیام متنی مستقل","message",.004m)],Ws:true),
            new ServiceModelSeed("xai","xai-tts","xAI Text to Speech","text_to_speech","text-audio","/v1/tts",xai,[P("تولید گفتار","million_characters",15)]),
            new ServiceModelSeed("xai","xai-stt-batch","xAI Speech to Text Batch","speech_to_text","audio-text","/v1/stt",xai,[P("رونویسی Batch","hour",.10m)]),
            new ServiceModelSeed("xai","xai-stt-streaming","xAI Speech to Text Streaming","speech_to_text","audio-text","/v1/stt",xai,[P("رونویسی Streaming","hour",.20m)],Ws:true),

            new ServiceModelSeed("mistral","mistral-medium-latest","Mistral Medium 3.5","chat","text-image","/v1/chat/completions",mistral,[P("ورودی متن","million_text_tokens",1.5m),P("ورودی Cache","million_text_tokens",.15m),P("خروجی متن","million_text_tokens",7.5m)],256000),
            new ServiceModelSeed("mistral","mistral-large-latest","Mistral Large 3","chat","text-image","/v1/chat/completions",mistral,[P("ورودی متن","million_text_tokens",.5m),P("ورودی Cache","million_text_tokens",.05m),P("خروجی متن","million_text_tokens",1.5m)],256000),
            new ServiceModelSeed("mistral","mistral-small-latest","Mistral Small 4","chat","text-image","/v1/chat/completions",mistral,[P("ورودی متن","million_text_tokens",.15m),P("ورودی Cache","million_text_tokens",.015m),P("خروجی متن","million_text_tokens",.6m)],256000),
            new ServiceModelSeed("mistral","codestral-latest","Codestral","coding","text","/v1/chat/completions",mistral,[P("ورودی متن","million_text_tokens",.3m),P("خروجی متن","million_text_tokens",.9m)],128000),
            new ServiceModelSeed("mistral","ministral-3b-latest","Ministral 3B","chat","text","/v1/chat/completions",mistral,[P("ورودی متن","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",.1m)],256000),
            new ServiceModelSeed("mistral","ministral-8b-latest","Ministral 8B","chat","text","/v1/chat/completions",mistral,[P("ورودی متن","million_text_tokens",.15m),P("خروجی متن","million_text_tokens",.15m)],256000),
            new ServiceModelSeed("mistral","ministral-14b-latest","Ministral 14B","chat","text","/v1/chat/completions",mistral,[P("ورودی متن","million_text_tokens",.2m),P("خروجی متن","million_text_tokens",.2m)],256000),
            new ServiceModelSeed("mistral","labs-leanstral-1-5","Leanstral 1.5","coding","text","/v1/chat/completions",mistral,[P("هزینه Labs","free",0,"رایگان تا retirement برنامه‌ریزی‌شده در 2026-09-30")],256000,Preview:true),
            new ServiceModelSeed("mistral","codestral-embed","Codestral Embed","embeddings","text-vector","/v1/embeddings",mistral,[P("ورودی متن","million_text_tokens",.15m)],8000,Stream:false),
            new ServiceModelSeed("mistral","mistral-embed","Mistral Embed","embeddings","text-vector","/v1/embeddings",mistral,[P("ورودی متن","million_text_tokens",.1m)],Stream:false),
            new ServiceModelSeed("mistral","mistral-moderation-2603","Mistral Moderation 2603","moderation","text","/v1/moderations",mistral,[P("ورودی متن","million_text_tokens",.1m)],Stream:false,Notes:"نرخ billing رسمی استفاده شده است؛ model card نرخ آزمایشی متفاوتی نمایش می‌دهد."),
            new ServiceModelSeed("mistral","mistral-ocr-4-0","Mistral OCR 4","ocr","image-pdf-text","/v1/ocr",mistral,[P("OCR","thousand_pages",4),P("Document AI","thousand_pages",5)],Stream:false),
            new ServiceModelSeed("mistral","mistral-ocr-2512","Mistral OCR 3","ocr","image-pdf-text","/v1/ocr",mistral,[P("OCR","thousand_pages",2),P("Document AI","thousand_pages",3)],Stream:false),

            new ServiceModelSeed("mistral","voxtral-mini-latest","Voxtral Mini Transcribe 2","speech_to_text","audio-text","/v1/audio/transcriptions",mistral,[P("ورودی صوت","minute",.003m)]),
            new ServiceModelSeed("mistral","voxtral-mini-transcribe-realtime-2602","Voxtral Mini Transcribe Realtime","speech_to_text","audio-text","/v1/audio/transcriptions",mistral,[P("ورودی صوت زنده","minute",.006m)],Ws:true),
            new ServiceModelSeed("mistral","voxtral-mini-tts-latest","Voxtral TTS","text_to_speech","text-audio","/v1/audio/speech",mistral,[P("تولید یا شبیه‌سازی صدا","thousand_characters",.016m)]),
            new ServiceModelSeed("mistral","voxtral-small-latest","Voxtral Small Audio Understanding","audio_understanding","audio-text","/v1/chat/completions",mistral,[P("ورودی صوت","minute",.004m),P("ورودی متن","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",.4m)],32000),

            new ServiceModelSeed("glm","glm-5.2","GLM 5.2","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",1.4m),P("ورودی Cache","million_text_tokens",.26m),P("خروجی متن","million_text_tokens",4.4m)],200000),
            new ServiceModelSeed("glm","glm-5.1","GLM 5.1","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",1.4m),P("ورودی Cache","million_text_tokens",.26m),P("خروجی متن","million_text_tokens",4.4m)],200000),
            new ServiceModelSeed("glm","glm-5","GLM 5","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",1),P("ورودی Cache","million_text_tokens",.2m),P("خروجی متن","million_text_tokens",3.2m)],200000),
            new ServiceModelSeed("glm","glm-5-turbo","GLM 5 Turbo","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",1.2m),P("ورودی Cache","million_text_tokens",.24m),P("خروجی متن","million_text_tokens",4)],200000),
            new ServiceModelSeed("glm","glm-4.7","GLM 4.7","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",.6m),P("ورودی Cache","million_text_tokens",.11m),P("خروجی متن","million_text_tokens",2.2m)],200000),
            new ServiceModelSeed("glm","glm-4.7-flashx","GLM 4.7 FlashX","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",.07m),P("ورودی Cache","million_text_tokens",.01m),P("خروجی متن","million_text_tokens",.4m)],200000),
            new ServiceModelSeed("glm","glm-4.7-flash","GLM 4.7 Flash","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",0),P("ورودی Cache","million_text_tokens",0),P("خروجی متن","million_text_tokens",0)],200000,Notes:"رایگان طبق جدول رسمی فعلی Z.ai."),
            new ServiceModelSeed("glm","glm-4.6","GLM 4.6","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",.6m),P("ورودی Cache","million_text_tokens",.11m),P("خروجی متن","million_text_tokens",2.2m)],200000),
            new ServiceModelSeed("glm","glm-4.5","GLM 4.5","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",.6m),P("ورودی Cache","million_text_tokens",.11m),P("خروجی متن","million_text_tokens",2.2m)],200000),
            new ServiceModelSeed("glm","glm-4.5-x","GLM 4.5 X","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",2.2m),P("ورودی Cache","million_text_tokens",.45m),P("خروجی متن","million_text_tokens",8.9m)],200000),
            new ServiceModelSeed("glm","glm-4.5-air","GLM 4.5 Air","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",.2m),P("ورودی Cache","million_text_tokens",.03m),P("خروجی متن","million_text_tokens",1.1m)],200000),
            new ServiceModelSeed("glm","glm-4.5-airx","GLM 4.5 AirX","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",1.1m),P("ورودی Cache","million_text_tokens",.22m),P("خروجی متن","million_text_tokens",4.5m)],200000),
            new ServiceModelSeed("glm","glm-4-32b-0414-128k","GLM 4 32B","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",.1m)],128000),
            new ServiceModelSeed("glm","glm-4.5-flash","GLM 4.5 Flash","chat","text","/chat/completions",glm,[P("ورودی متن","million_text_tokens",0),P("خروجی متن","million_text_tokens",0)],200000,Notes:"رایگان طبق جدول رسمی فعلی Z.ai."),
            new ServiceModelSeed("glm","glm-5v-turbo","GLM 5V Turbo","vision","text-image-video-file","/chat/completions",glm,[P("ورودی متن/رسانه","million_text_tokens",1.2m),P("ورودی Cache","million_text_tokens",.24m),P("خروجی متن","million_text_tokens",4)],200000),
            new ServiceModelSeed("glm","glm-4.6v","GLM 4.6V","vision","text-image-video-file","/chat/completions",glm,[P("ورودی متن/رسانه","million_text_tokens",.3m),P("ورودی Cache","million_text_tokens",.05m),P("خروجی متن","million_text_tokens",.9m)],200000),
            new ServiceModelSeed("glm","glm-ocr","GLM OCR","ocr","image-pdf-text","/chat/completions",glm,[P("ورودی تصویر/سند","million_text_tokens",.03m),P("خروجی متن","million_text_tokens",.03m)],200000),
            new ServiceModelSeed("glm","glm-4.6v-flashx","GLM 4.6V FlashX","vision","text-image-video-file","/chat/completions",glm,[P("ورودی متن/رسانه","million_text_tokens",.04m),P("ورودی Cache","million_text_tokens",.004m),P("خروجی متن","million_text_tokens",.4m)],200000),
            new ServiceModelSeed("glm","glm-4.5v","GLM 4.5V","vision","text-image-video-file","/chat/completions",glm,[P("ورودی متن/رسانه","million_text_tokens",.6m),P("ورودی Cache","million_text_tokens",.11m),P("خروجی متن","million_text_tokens",1.8m)],200000),
            new ServiceModelSeed("glm","glm-4.6v-flash","GLM 4.6V Flash","vision","text-image-video-file","/chat/completions",glm,[P("ورودی متن/رسانه","million_text_tokens",0),P("خروجی متن","million_text_tokens",0)],200000,Notes:"رایگان طبق جدول رسمی فعلی Z.ai."),
            new ServiceModelSeed("glm","glm-image","GLM Image","image_generation","text-image","/images/generations",glm,[P("خروجی تصویر","image",.015m)],Stream:false),
            new ServiceModelSeed("glm","cogview-4","CogView 4","image_generation","text-image","/images/generations",glm,[P("خروجی تصویر","image",.01m)],Stream:false),
            new ServiceModelSeed("glm","cogvideox-3","CogVideoX 3","video_generation","text-image-video","/videos/generations",glm,[P("خروجی ویدئو","video",.2m)],Stream:false),
            new ServiceModelSeed("glm","viduq1-text","Vidu Q1 Text to Video","video_generation","text-video","/videos/generations",glm,[P("خروجی ویدئو","video",.4m)],Stream:false),
            new ServiceModelSeed("glm","viduq1-image","Vidu Q1 Image to Video","video_generation","image-video","/videos/generations",glm,[P("خروجی ویدئو","video",.4m)],Stream:false),
            new ServiceModelSeed("glm","viduq1-start-end","Vidu Q1 Start/End Frame","video_generation","image-video","/videos/generations",glm,[P("خروجی ویدئو","video",.4m)],Stream:false),
            new ServiceModelSeed("glm","vidu2-image","Vidu 2 Image to Video","video_generation","image-video","/videos/generations",glm,[P("خروجی ویدئو","video",.2m)],Stream:false),
            new ServiceModelSeed("glm","vidu2-start-end","Vidu 2 Start/End Frame","video_generation","image-video","/videos/generations",glm,[P("خروجی ویدئو","video",.2m)],Stream:false),
            new ServiceModelSeed("glm","vidu2-reference","Vidu 2 Reference Video","video_generation","image-video","/videos/generations",glm,[P("خروجی ویدئو","video",.4m)],Stream:false),
            new ServiceModelSeed("glm","ZAI/AutoGLM-Phone-9B","AutoGLM Phone 9B","computer_use","text-action","/chat/completions",glm,[P("قیمت محدود فعلی","free",0,"رایگان برای مدت محدود")],Stream:true,Notes:"این مدل روی ModelScope میزبانی می‌شود.",UpstreamBaseUrl:"https://api-inference.modelscope.cn/v1"),
            new ServiceModelSeed("glm","glm-asr-2512","GLM ASR 2512","speech_to_text","audio-text","/audio/transcriptions",glm,[P("ورودی صوت","million_audio_tokens",.03m,"≈ $0.0024/min")],Stream:true),
            new ServiceModelSeed("cohere","command-a-03-2025","Command A","chat","text","/chat/completions","https://docs.cohere.com/docs/command-a",[P("ورودی متن","million_text_tokens",2.5m),P("خروجی متن","million_text_tokens",10)],256000),
            new ServiceModelSeed("cohere","command-r7b-12-2024","Command R7B","chat","text","/chat/completions","https://docs.cohere.com/v1/docs/command-r7b",[P("ورودی متن","million_text_tokens",.0375m),P("خروجی متن","million_text_tokens",.15m)],128000),
            new ServiceModelSeed("cohere","command-r-08-2024","Command R","chat","text","/chat/completions","https://docs.cohere.com/docs/command-r",[P("ورودی متن","million_text_tokens",.15m),P("خروجی متن","million_text_tokens",.6m)],128000),
            new ServiceModelSeed("cohere","command-r-plus-08-2024","Command R Plus","chat","text","/chat/completions","https://docs.cohere.com/docs/command-r",[P("ورودی متن","million_text_tokens",2.5m),P("خروجی متن","million_text_tokens",10)],128000),
            new ServiceModelSeed("cohere","command-a-plus-05-2026","Command A Plus","chat","text-image","/chat/completions","https://docs.cohere.com/docs/command-a-plus",[P("API محدود","free_rate_limited",0,"رایگان در محدودیت API؛ Production اختصاصی تماس با فروش")],128000),
            new ServiceModelSeed("cohere","command-a-reasoning-08-2025","Command A Reasoning","reasoning","text","/chat/completions","https://docs.cohere.com/docs/command-a-reasoning",[P("API محدود","free_rate_limited",0,"رایگان در محدودیت API؛ Production اختصاصی تماس با فروش")],256000),
            new ServiceModelSeed("cohere","command-a-translate-08-2025","Command A Translate","translation","text","/chat/completions","https://docs.cohere.com/docs/command-a-translate",[P("API محدود","free_rate_limited",0,"رایگان در محدودیت API؛ Production اختصاصی تماس با فروش")],8000),
            new ServiceModelSeed("cohere","command-a-vision-07-2025","Command A Vision","vision","text-image","/chat/completions","https://docs.cohere.com/v1/docs/command-a-vision",[P("API محدود","free_rate_limited",0,"رایگان در محدودیت API؛ Production اختصاصی تماس با فروش")],128000),
            new ServiceModelSeed("cohere","north-mini-code-1-0","North Mini Code 1.0","coding","text","/chat/completions","https://docs.cohere.com/docs/north-mini-code-1.0",[P("API محدود","free_rate_limited",0,"رایگان در محدودیت API؛ Production اختصاصی تماس با فروش")],256000),
            new ServiceModelSeed("cohere","c4ai-aya-expanse-32b","Aya Expanse 32B","chat","text","/chat/completions","https://cohere.com/pricing",[P("ورودی متن","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",1.5m)],128000),
            new ServiceModelSeed("cohere","c4ai-aya-vision-32b","Aya Vision 32B","vision","text-image","/chat/completions","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی منتشر نشده است")],16000),
            new ServiceModelSeed("cohere","tiny-aya-global","Tiny Aya Global","chat","text","/chat/completions","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"API آزمایشی محدود؛ قیمت عمومی Production منتشر نشده است")],8000),
            new ServiceModelSeed("cohere","tiny-aya-earth","Tiny Aya Earth","chat","text","/chat/completions","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"API آزمایشی محدود؛ قیمت عمومی Production منتشر نشده است")],8000),
            new ServiceModelSeed("cohere","tiny-aya-fire","Tiny Aya Fire","chat","text","/chat/completions","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"API آزمایشی محدود؛ قیمت عمومی Production منتشر نشده است")],8000),
            new ServiceModelSeed("cohere","tiny-aya-water","Tiny Aya Water","chat","text","/chat/completions","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"API آزمایشی محدود؛ قیمت عمومی Production منتشر نشده است")],8000),
            new ServiceModelSeed("cohere","embed-v4.0","Embed 4","embeddings","text-image-vector","/embed","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","embed-english-v3.0","Embed English v3","embeddings","text-vector","/embed","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","embed-english-light-v3.0","Embed English Light v3","embeddings","text-vector","/embed","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","embed-multilingual-v3.0","Embed Multilingual v3","embeddings","text-vector","/embed","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","embed-multilingual-light-v3.0","Embed Multilingual Light v3","embeddings","text-vector","/embed","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","rerank-v4.0-pro","Rerank 4 Pro","rerank","text","/rerank","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","rerank-v4.0-fast","Rerank 4 Fast","rerank","text","/rerank","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","rerank-v3.5","Rerank 3.5","rerank","text","/rerank","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","rerank-english-v3.0","Rerank English v3","rerank","text","/rerank","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","rerank-multilingual-v3.0","Rerank Multilingual v3","rerank","text","/rerank","https://docs.cohere.com/v1/docs/models",[P("قیمت Production","contact_sales",null,"قیمت عمومی فعلی منتشر نشده است")],Stream:false,UpstreamBaseUrl:"https://api.cohere.com/v2"),

            new ServiceModelSeed("cohere","cohere-transcribe-03-2026","Cohere Transcribe","speech_to_text","audio-text","/audio/transcriptions",cohere,[P("قیمت عمومی","contact_sales",null,"تماس با فروش Cohere")],Notes:"Cohere قیمت عمومی این سرویس را منتشر نکرده است.",UpstreamBaseUrl:"https://api.cohere.com/v2"),
            new ServiceModelSeed("cohere","cohere-transcribe-arabic-07-2026","Cohere Transcribe Arabic","speech_to_text","audio-text","/audio/transcriptions",cohere,[P("قیمت عمومی","contact_sales",null,"تماس با فروش Cohere")],Notes:"مدل عربی و انگلیسی؛ قیمت Production عمومی منتشر نشده است.",UpstreamBaseUrl:"https://api.cohere.com/v2"),

            new ServiceModelSeed("qwen","qwen3.7-max","Qwen3.7 Max","chat","text-image","/chat/completions",qwen,[P("ورودی متن - قیمت مرجع","million_text_tokens",2.5m),P("خروجی متن - قیمت مرجع","million_text_tokens",7.5m)],1000000,Region:"international",Notes:"قیمت مرجع دائمی ثبت شده؛ تخفیف موقت 50 درصدی صفحه رسمی در نرخ پایه اعمال نشده است."),
            new ServiceModelSeed("qwen","qwen3.6-max-preview","Qwen3.6 Max Preview","chat","text-image","/chat/completions",qwen,[P("ورودی تا 128K","million_text_tokens",1.3m),P("خروجی تا 128K","million_text_tokens",7.8m),P("ورودی 128K تا 256K","million_text_tokens",2),P("خروجی 128K تا 256K","million_text_tokens",12)],262144,Preview:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.7-plus","Qwen3.7 Plus","chat","text-image","/chat/completions",qwen,[P("ورودی تا 256K","million_text_tokens",.276m),P("خروجی تا 256K","million_text_tokens",1.101m),P("ورودی 256K تا 1M","million_text_tokens",.826m),P("خروجی 256K تا 1M","million_text_tokens",3.301m)],1000000,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.6-plus","Qwen3.6 Plus","chat","text-image","/chat/completions",qwen,[P("ورودی تا 256K","million_text_tokens",.276m),P("خروجی تا 256K","million_text_tokens",1.651m),P("ورودی بیش از 256K","million_text_tokens",1.101m),P("خروجی بیش از 256K","million_text_tokens",6.602m)],1000000,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.6-flash","Qwen3.6 Flash","chat","text-image","/chat/completions",qwen,[P("ورودی تا 256K","million_text_tokens",.25m),P("خروجی تا 256K","million_text_tokens",1.5m),P("ورودی بیش از 256K","million_text_tokens",1),P("خروجی بیش از 256K","million_text_tokens",4)],1000000,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.5-flash","Qwen3.5 Flash","chat","text-image","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",.4m)],1000000,Region:"international"),
            new ServiceModelSeed("qwen","qwen-flash","Qwen Flash","chat","text-image","/chat/completions",qwen,[P("ورودی تا 256K","million_text_tokens",.05m),P("خروجی تا 256K","million_text_tokens",.4m),P("ورودی بیش از 256K","million_text_tokens",.25m),P("خروجی بیش از 256K","million_text_tokens",2)],1000000,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-max","Qwen3 Max","chat","text-image","/chat/completions",qwen,[P("ورودی تا 32K","million_text_tokens",1.2m),P("خروجی تا 32K","million_text_tokens",6),P("ورودی 32K تا 128K","million_text_tokens",2.4m),P("خروجی 32K تا 128K","million_text_tokens",12),P("ورودی 128K تا 256K","million_text_tokens",3),P("خروجی 128K تا 256K","million_text_tokens",15)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-coder-plus","Qwen3 Coder Plus","coding","text","/chat/completions",qwen,[P("ورودی تا 32K","million_text_tokens",1),P("خروجی تا 32K","million_text_tokens",5),P("ورودی 32K تا 128K","million_text_tokens",1.8m),P("خروجی 32K تا 128K","million_text_tokens",9),P("ورودی 128K تا 256K","million_text_tokens",3),P("خروجی 128K تا 256K","million_text_tokens",15),P("ورودی 256K تا 1M","million_text_tokens",6),P("خروجی 256K تا 1M","million_text_tokens",60)],1000000,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-coder-flash","Qwen3 Coder Flash","coding","text","/chat/completions",qwen,[P("ورودی تا 32K","million_text_tokens",.3m),P("خروجی تا 32K","million_text_tokens",1.5m),P("ورودی 32K تا 128K","million_text_tokens",.5m),P("خروجی 32K تا 128K","million_text_tokens",2.5m),P("ورودی 128K تا 256K","million_text_tokens",.8m),P("خروجی 128K تا 256K","million_text_tokens",4),P("ورودی 256K تا 1M","million_text_tokens",1.6m),P("خروجی 256K تا 1M","million_text_tokens",9.6m)],1000000,Region:"international"),
            new ServiceModelSeed("qwen","qwen-turbo","Qwen Turbo","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.05m),P("خروجی non-thinking","million_text_tokens",.2m),P("خروجی thinking","million_text_tokens",.5m)],1000000,Region:"international",Notes:"مدل فعال Legacy؛ Qwen Flash برای توسعه جدید پیشنهاد می‌شود."),
            new ServiceModelSeed("qwen","qwen-mt-plus","Qwen MT Plus","translation","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",2.46m),P("خروجی متن","million_text_tokens",7.37m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-mt-flash","Qwen MT Flash","translation","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.16m),P("خروجی متن","million_text_tokens",.49m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-mt-lite","Qwen MT Lite","translation","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.12m),P("خروجی متن","million_text_tokens",.36m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-mt-turbo","Qwen MT Turbo","translation","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.16m),P("خروجی متن","million_text_tokens",.49m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-vl-plus","Qwen3 VL Plus","vision","text-image-video","/chat/completions",qwen,[P("ورودی tier 1","million_text_tokens",.2m),P("خروجی tier 1","million_text_tokens",1.6m),P("ورودی tier 2","million_text_tokens",.3m),P("خروجی tier 2","million_text_tokens",2.4m),P("ورودی tier 3","million_text_tokens",.6m),P("خروجی tier 3","million_text_tokens",4.8m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-vl-flash","Qwen3 VL Flash","vision","text-image-video","/chat/completions",qwen,[P("ورودی tier 1","million_text_tokens",.05m),P("خروجی tier 1","million_text_tokens",.4m),P("ورودی tier 2","million_text_tokens",.075m),P("خروجی tier 2","million_text_tokens",.6m),P("ورودی tier 3","million_text_tokens",.12m),P("خروجی tier 3","million_text_tokens",.96m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen-vl-max","Qwen VL Max","vision","text-image-video","/chat/completions",qwen,[P("ورودی متن/رسانه","million_text_tokens",.8m),P("خروجی متن","million_text_tokens",3.2m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-vl-plus","Qwen VL Plus","vision","text-image-video","/chat/completions",qwen,[P("ورودی متن/رسانه","million_text_tokens",.21m),P("خروجی متن","million_text_tokens",.63m)],Region:"international"),
            new ServiceModelSeed("qwen","qvq-max","QVQ Max","vision","text-image-video","/chat/completions",qwen,[P("ورودی متن/رسانه","million_text_tokens",1.2m),P("خروجی متن","million_text_tokens",4.8m)],Region:"international"),
            new ServiceModelSeed("qwen","text-embedding-v4","Qwen Text Embedding V4","embeddings","text-vector","/embeddings",qwen,[P("ورودی متن","million_text_tokens",.07m)],Stream:false,Region:"international"),
            new ServiceModelSeed("qwen","text-embedding-v3","Qwen Text Embedding V3","embeddings","text-vector","/embeddings",qwen,[P("ورودی متن","million_text_tokens",.07m)],Stream:false,Region:"international"),
            new ServiceModelSeed("qwen","tongyi-embedding-vision-plus","Tongyi Vision Embedding Plus","embeddings","image-video-vector","/embeddings",qwen,[P("ورودی چندرسانه‌ای","million_text_tokens",.09m)],Stream:false,Region:"international"),
            new ServiceModelSeed("qwen","tongyi-embedding-vision-flash","Tongyi Vision Embedding Flash","embeddings","text-image-video-vector","/embeddings",qwen,[P("ورودی متن","million_text_tokens",.09m),P("ورودی تصویر/ویدئو","million_image_tokens",.03m)],Stream:false,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.6-35b-a3b","Qwen3.6 35B A3B","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.375m),P("خروجی متن","million_text_tokens",2.25m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.6-27b","Qwen3.6 27B","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.6m),P("خروجی متن","million_text_tokens",3.6m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.5-397b-a17b","Qwen3.5 397B A17B","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.6m),P("خروجی متن","million_text_tokens",3.6m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.5-122b-a10b","Qwen3.5 122B A10B","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.4m),P("خروجی متن","million_text_tokens",3.2m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.5-27b","Qwen3.5 27B","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.3m),P("خروجی متن","million_text_tokens",2.4m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.5-35b-a3b","Qwen3.5 35B A3B","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.25m),P("خروجی متن","million_text_tokens",2)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-next-80b-a3b-thinking","Qwen3 Next 80B A3B Thinking","reasoning","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.15m),P("خروجی thinking","million_text_tokens",1.2m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-next-80b-a3b-instruct","Qwen3 Next 80B A3B Instruct","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.15m),P("خروجی متن","million_text_tokens",1.2m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-235b-a22b-thinking-2507","Qwen3 235B A22B Thinking","reasoning","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.23m),P("خروجی thinking","million_text_tokens",2.3m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-235b-a22b-instruct-2507","Qwen3 235B A22B Instruct","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.23m),P("خروجی متن","million_text_tokens",.92m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-30b-a3b-thinking-2507","Qwen3 30B A3B Thinking","reasoning","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.2m),P("خروجی thinking","million_text_tokens",2.4m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-30b-a3b-instruct-2507","Qwen3 30B A3B Instruct","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.2m),P("خروجی متن","million_text_tokens",.8m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-235b-a22b","Qwen3 235B A22B","reasoning","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.7m),P("خروجی non-thinking","million_text_tokens",2.8m),P("خروجی thinking","million_text_tokens",8.4m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-32b","Qwen3 32B","chat","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.16m),P("خروجی متن","million_text_tokens",.64m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-30b-a3b","Qwen3 30B A3B","reasoning","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.2m),P("خروجی non-thinking","million_text_tokens",.8m),P("خروجی thinking","million_text_tokens",2.4m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-14b","Qwen3 14B","reasoning","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.35m),P("خروجی non-thinking","million_text_tokens",1.4m),P("خروجی thinking","million_text_tokens",4.2m)],262144,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-8b","Qwen3 8B","reasoning","text","/chat/completions",qwen,[P("ورودی متن","million_text_tokens",.18m),P("خروجی non-thinking","million_text_tokens",.7m),P("خروجی thinking","million_text_tokens",2.1m)],262144,Region:"international"),

            new ServiceModelSeed("qwen","qwen3.5-omni-plus-realtime","Qwen3.5 Omni Plus Realtime","speech_to_speech","audio","/api-ws/v1/realtime",qwen,[P("ورودی متن/تصویر","million_text_tokens",2.1m),P("ورودی صوت","million_audio_tokens",16.5m),P("خروجی متن","million_text_tokens",12.4m),P("خروجی صوت","million_audio_tokens",62)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.5-omni-flash-realtime","Qwen3.5 Omni Flash Realtime","speech_to_speech","audio","/api-ws/v1/realtime",qwen,[P("ورودی متن/تصویر","million_text_tokens",.55m),P("ورودی صوت","million_audio_tokens",4.5m),P("خروجی متن","million_text_tokens",3.3m),P("خروجی صوت","million_audio_tokens",17.7m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-omni-flash-realtime","Qwen3 Omni Flash Realtime","speech_to_speech","audio","/api-ws/v1/realtime",qwen,[P("ورودی متن","million_text_tokens",.52m),P("ورودی صوت","million_audio_tokens",4.57m),P("ورودی تصویر","million_image_tokens",.94m),P("خروجی متن چندرسانه‌ای","million_text_tokens",3.67m),P("خروجی صوت","million_audio_tokens",18.13m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen-omni-turbo-realtime","Qwen Omni Turbo Realtime","speech_to_speech","audio","/api-ws/v1/realtime",qwen,[P("ورودی متن","million_text_tokens",.27m),P("ورودی صوت","million_audio_tokens",4.44m),P("ورودی تصویر","million_image_tokens",.84m),P("خروجی متن چندرسانه‌ای","million_text_tokens",2.52m),P("خروجی صوت","million_audio_tokens",8.89m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3.5-livetranslate-flash-realtime","Qwen3.5 LiveTranslate Realtime","realtime_translation","audio","/api-ws/v1/realtime",qwen,[P("ورودی صوت","million_audio_tokens",7.5m),P("ورودی تصویر","million_image_tokens",.55m),P("خروجی متن","million_text_tokens",20),P("خروجی صوت","million_audio_tokens",30)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-livetranslate-flash","Qwen3 LiveTranslate","translation","audio","/compatible-mode/v1/chat/completions",qwen,[P("ورودی صوت","million_audio_tokens",1.577m),P("ورودی تصویر","million_image_tokens",.631m),P("خروجی متن","million_text_tokens",1.577m),P("خروجی صوت","million_audio_tokens",6.308m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-audio-3.0-tts-plus","Qwen Audio 3.0 TTS Plus","text_to_speech","text-audio","/api/v1/services/aigc/multimodal-generation/generation",qwen,[P("ورودی متن","ten_thousand_characters",.2m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-audio-3.0-tts-flash","Qwen Audio 3.0 TTS Flash","text_to_speech","text-audio","/api/v1/services/aigc/multimodal-generation/generation",qwen,[P("ورودی متن","ten_thousand_characters",.15m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-instruct-flash","Qwen3 TTS Instruct Flash","text_to_speech","text-audio","/api/v1/services/aigc/multimodal-generation/generation",qwen,[P("ورودی متن","ten_thousand_characters",.115m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-vd-2026-01-26","Qwen3 TTS Voice Design","text_to_speech","text-audio","/api/v1/services/aigc/multimodal-generation/generation",qwen,[P("ورودی متن","ten_thousand_characters",.115m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-vc-2026-01-22","Qwen3 TTS Voice Clone","text_to_speech","text-audio","/api/v1/services/aigc/multimodal-generation/generation",qwen,[P("ورودی متن","ten_thousand_characters",.115m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-flash","Qwen3 TTS Flash","text_to_speech","text-audio","/api/v1/services/aigc/multimodal-generation/generation",qwen,[P("ورودی متن","ten_thousand_characters",.1m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-instruct-flash-realtime","Qwen3 TTS Instruct Realtime","text_to_speech","text-audio","/api-ws/v1/realtime",qwen,[P("ورودی متن","ten_thousand_characters",.143m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-vd-realtime-2026-01-15","Qwen3 TTS Voice Design Realtime","text_to_speech","text-audio","/api-ws/v1/realtime",qwen,[P("ورودی متن","ten_thousand_characters",.143353m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-vc-realtime-2026-01-15","Qwen3 TTS Voice Clone Realtime","text_to_speech","text-audio","/api-ws/v1/realtime",qwen,[P("ورودی متن","ten_thousand_characters",.13m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-tts-flash-realtime","Qwen3 TTS Flash Realtime","text_to_speech","text-audio","/api-ws/v1/realtime",qwen,[P("ورودی متن","ten_thousand_characters",.13m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","qwen3-asr-flash-filetrans","Qwen3 ASR File Transcription","speech_to_text","audio-text","/api/v1/services/audio/asr/transcription",qwen,[P("ورودی صوت","second",.000035m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-asr-flash","Qwen3 ASR Flash","speech_to_text","audio-text","/api/v1/services/aigc/multimodal-generation/generation",qwen,[P("ورودی صوت","second",.000035m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen3-asr-flash-realtime","Qwen3 ASR Flash Realtime","speech_to_text","audio-text","/api-ws/v1/realtime",qwen,[P("ورودی صوت زنده","second",.00009m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","fun-asr","Fun ASR","speech_to_text","audio-text","/api/v1/services/audio/asr/transcription",qwen,[P("ورودی صوت","second",.000035m)],Region:"international"),
            new ServiceModelSeed("qwen","fun-asr-mtl","Fun ASR Multilingual","speech_to_text","audio-text","/api/v1/services/audio/asr/transcription",qwen,[P("ورودی صوت","second",.000035m)],Region:"international"),
            new ServiceModelSeed("qwen","fun-asr-flash-2026-06-15","Fun ASR Flash","speech_to_text","audio-text","/api/v1/services/audio/asr/transcription",qwen,[P("ورودی صوت","second",.000035m)],Region:"international"),
            new ServiceModelSeed("qwen","fun-asr-realtime","Fun ASR Realtime","speech_to_text","audio-text","/api-ws/v1/realtime",qwen,[P("ورودی صوت زنده","second",.00009m)],Ws:true,Region:"international"),
            new ServiceModelSeed("qwen","cosyvoice-v3-plus","CosyVoice V3 Plus","text_to_speech","text-audio","/api/v1/services/audio/tts/synthesis",qwen,[P("ورودی متن","ten_thousand_characters",.26m)],Region:"international"),
            new ServiceModelSeed("qwen","cosyvoice-v3-flash","CosyVoice V3 Flash","text_to_speech","text-audio","/api/v1/services/audio/tts/synthesis",qwen,[P("ورودی متن","ten_thousand_characters",.13m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-voice-enrollment","Qwen Voice Enrollment","voice_clone","audio","/api/v1/services/audio/tts/customization",qwen,[P("ساخت صدای جدید","voice",.01m)],Region:"international"),
            new ServiceModelSeed("qwen","qwen-voice-design","Qwen Voice Design","voice_design","audio","/api/v1/services/audio/tts/customization",qwen,[P("طراحی صدای جدید","voice",.2m)],Region:"international"),

            new ServiceModelSeed("elevenlabs","eleven_v3","Eleven v3","text_to_speech","text-audio","/text-to-speech",eleven,[P("تولید گفتار","thousand_characters",.1m)]),
            new ServiceModelSeed("elevenlabs","eleven_multilingual_v2","Eleven Multilingual v2","text_to_speech","text-audio","/text-to-speech",eleven,[P("تولید گفتار","thousand_characters",.1m)]),
            new ServiceModelSeed("elevenlabs","eleven_flash_v2_5","Eleven Flash v2.5","text_to_speech","text-audio","/text-to-speech",eleven,[P("تولید گفتار","thousand_characters",.05m)]),
            new ServiceModelSeed("elevenlabs","eleven_flash_v2","Eleven Flash v2","text_to_speech","text-audio","/text-to-speech",eleven,[P("تولید گفتار","thousand_characters",.05m)]),
            new ServiceModelSeed("elevenlabs","eleven_turbo_v2_5","Eleven Turbo v2.5","text_to_speech","text-audio","/text-to-speech",eleven,[P("تولید گفتار","thousand_characters",.05m)]),
            new ServiceModelSeed("elevenlabs","scribe_v2","Scribe v2","speech_to_text","audio-text","/speech-to-text",eleven,[P("رونویسی","hour",.22m)]),
            new ServiceModelSeed("elevenlabs","scribe_v2_realtime","Scribe v2 Realtime","speech_to_text","audio-text","/speech-to-text/realtime",eleven,[P("رونویسی زنده","hour",.39m)],Ws:true),
            new ServiceModelSeed("elevenlabs","eleven_voice_changer","ElevenLabs Voice Changer","speech_to_speech","audio","/speech-to-speech",eleven,[P("تبدیل صدا","minute",.12m)]),
            new ServiceModelSeed("elevenlabs","eleven_multilingual_sts_v2","Eleven Multilingual Speech-to-Speech v2","speech_to_speech","audio","/speech-to-speech",eleven,[P("تبدیل صدا","minute",.12m)]),
            new ServiceModelSeed("elevenlabs","eleven_english_sts_v2","Eleven English Speech-to-Speech v2","speech_to_speech","audio","/speech-to-speech",eleven,[P("تبدیل صدا","minute",.12m)]),
            new ServiceModelSeed("elevenlabs","eleven_text_to_sound_v2","Eleven Text to Sound v2","sound_generation","text-audio","/sound-generation",eleven,[P("تولید افکت صوتی","minute",.12m)]),
            new ServiceModelSeed("elevenlabs","music_v2","Eleven Music v2","music_generation","text-audio","/music",eleven,[P("تولید موسیقی","minute",.15m)]),
            new ServiceModelSeed("elevenlabs","eleven_voice_isolator","Eleven Voice Isolator","audio_processing","audio","/audio-isolation",eleven,[P("جداسازی صدا","minute",.12m)]),
            new ServiceModelSeed("elevenlabs","eleven_speech_engine","Eleven Speech Engine","speech_to_speech","audio","/speech-engine",eleven,[P("پردازش پایه","minute",.08m),P("پردازش Burst","minute",.16m)]),
            new ServiceModelSeed("elevenlabs","eleven_dubbing","ElevenLabs Dubbing","translation","audio","/dubbing",eleven,[P("دوبله خودکار با Watermark","minute",.33m),P("دوبله بدون Watermark","minute",.5m)],Notes:"شناسه داخلی سرویس Dubbing است، نه model_id upstream."),

            new ServiceModelSeed("deepgram","flux-general-en","Deepgram Flux English","speech_to_text","audio-text","/v2/listen",deepgram,[P("رونویسی مکالمه‌ای","minute",.0065m)],Ws:true),
            new ServiceModelSeed("deepgram","flux-general-multi","Deepgram Flux Multilingual","speech_to_text","audio-text","/v2/listen",deepgram,[P("رونویسی مکالمه‌ای چندزبانه","minute",.0078m)],Ws:true),
            new ServiceModelSeed("deepgram","nova-3","Deepgram Nova-3 Monolingual","speech_to_text","audio-text","/v1/listen",deepgram,[P("رونویسی","minute",.0048m)],Ws:true),
            new ServiceModelSeed("deepgram","nova-3-multilingual","Deepgram Nova-3 Multilingual","speech_to_text","audio-text","/v1/listen",deepgram,[P("رونویسی چندزبانه","minute",.0058m)],Ws:true),
            new ServiceModelSeed("deepgram","aura-2","Deepgram Aura-2","text_to_speech","text-audio","/v1/speak",deepgram,[P("تولید گفتار","thousand_characters",.03m)],Notes:"شناسه upstream نهایی شامل صدا است؛ نمونه: aura-2-thalia-en."),
            new ServiceModelSeed("deepgram","aura-1","Deepgram Aura-1","text_to_speech","text-audio","/v1/speak",deepgram,[P("تولید گفتار","thousand_characters",.015m)]),
            new ServiceModelSeed("deepgram","deepgram-voice-agent","Deepgram Voice Agent API","speech_to_speech","audio","/v1/agent/converse",deepgram,[P("Standard","minute",.075m),P("BYO TTS","minute",.065m),P("BYO LLM","minute",.059m),P("BYO LLM + TTS","minute",.05m),P("Advanced","minute",.163m),P("Advanced BYO TTS","minute",.122m)],Ws:true,UpstreamBaseUrl:"https://agent.deepgram.com"),

            new ServiceModelSeed("assemblyai","universal-3-5-pro","AssemblyAI Universal 3.5 Pro","speech_to_text","audio-text","/v2/transcript",assembly,[P("رونویسی ضبط‌شده","hour",.21m)],UpstreamBaseUrl:"https://api.assemblyai.com"),
            new ServiceModelSeed("assemblyai","universal-2","AssemblyAI Universal 2","speech_to_text","audio-text","/v2/transcript",assembly,[P("رونویسی ضبط‌شده","hour",.15m)],UpstreamBaseUrl:"https://api.assemblyai.com"),
            new ServiceModelSeed("assemblyai","universal-3-5-pro-streaming","AssemblyAI Universal 3.5 Pro Streaming","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.45m)],Ws:true,Notes:"model upstream برابر universal-3-5-pro است.",UpstreamBaseUrl:"https://streaming.assemblyai.com"),
            new ServiceModelSeed("assemblyai","universal-streaming-english","AssemblyAI Universal Streaming English","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.15m)],Ws:true,UpstreamBaseUrl:"https://streaming.assemblyai.com"),
            new ServiceModelSeed("assemblyai","universal-streaming-multilingual","AssemblyAI Universal Streaming Multilingual","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.15m)],Ws:true,UpstreamBaseUrl:"https://streaming.assemblyai.com"),
            new ServiceModelSeed("assemblyai","whisper-rt","AssemblyAI Whisper RT","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.30m)],Ws:true,UpstreamBaseUrl:"https://streaming.assemblyai.com"),
            new ServiceModelSeed("assemblyai","assemblyai-sync-stt","AssemblyAI Sync STT API","speech_to_text","audio-text","/v2/transcript",assembly,[P("رونویسی همگام","hour",.45m)],UpstreamBaseUrl:"https://api.assemblyai.com"),
            new ServiceModelSeed("assemblyai","assemblyai-translation-addon","AssemblyAI Translation Add-on","translation","audio-text","/v2/transcript",assembly,[P("افزونه ترجمه","hour",.06m)],UpstreamBaseUrl:"https://api.assemblyai.com"),
            new ServiceModelSeed("assemblyai","assemblyai-voice-agent","AssemblyAI Voice Agent API","speech_to_speech","audio","/v1/ws",assembly,[P("عامل صوتی کامل","hour",4.5m)],Ws:true,UpstreamBaseUrl:"https://agents.assemblyai.com")
            ,
            new ServiceModelSeed("google-cloud-speech","google-stt-v2-standard","Google Cloud STT V2 Standard SKU","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("۰ تا ۵۰۰ هزار دقیقه ماهانه","minute",.016m),P("۵۰۰ هزار تا ۱ میلیون","minute",.01m),P("۱ تا ۲ میلیون","minute",.008m),P("بیش از ۲ میلیون","minute",.004m)],Region:"global",Notes:"این ردیف SKU قیمت‌گذاری است؛ مدل upstream در request انتخاب می‌شود.",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-stt-v2-dynamic-batch","Google Cloud STT V2 Dynamic Batch SKU","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:batchRecognize",googleSpeech,[P("رونویسی Dynamic Batch","minute",.003m)],Region:"global",Notes:"این ردیف SKU قیمت‌گذاری است.",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","chirp_3","Google Cloud STT Chirp 3","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("رونویسی استاندارد","minute",.016m)],Region:"global",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","chirp_2","Google Cloud STT Chirp 2","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("رونویسی استاندارد","minute",.016m)],Region:"global",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","telephony","Google Cloud STT Telephony","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("رونویسی استاندارد","minute",.016m)],Region:"global",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","latest_long","Google Cloud STT Latest Long","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("رونویسی استاندارد","minute",.016m)],Region:"global",Notes:"شناسه Legacy فعال برای سازگاری deploymentهای موجود.",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","latest_short","Google Cloud STT Latest Short","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("رونویسی استاندارد","minute",.016m)],Region:"global",Notes:"شناسه Legacy فعال برای سازگاری deploymentهای موجود.",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","phone_call","Google Cloud STT Phone Call","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("رونویسی استاندارد","minute",.016m)],Region:"global",Notes:"شناسه Legacy فعال برای سازگاری deploymentهای موجود.",UpstreamBaseUrl:"https://speech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-standard","Google Cloud TTS Standard","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار","million_characters",4)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-wavenet","Google Cloud WaveNet TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار","million_characters",4)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-neural2","Google Cloud Neural2 TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار","million_characters",16)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-polyglot-preview","Google Cloud Polyglot TTS Preview","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار","million_characters",16)],Preview:true,UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-chirp3-hd","Google Cloud Chirp 3 HD TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار HD","million_characters",30)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-studio","Google Cloud Studio TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار Studio","million_characters",160)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-instant-custom-voice","Google Cloud Instant Custom Voice","voice_clone","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار سفارشی","million_characters",60)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","gemini-2.5-flash-tts","Gemini 2.5 Flash TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("ورودی متن","million_text_tokens",.5m),P("خروجی صوت","million_audio_tokens",10)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","gemini-2.5-flash-lite-preview-tts","Gemini 2.5 Flash Lite TTS Preview","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("ورودی متن","million_text_tokens",.5m),P("خروجی صوت","million_audio_tokens",10)],Preview:true,UpstreamBaseUrl:"https://texttospeech.googleapis.com"),
            new ServiceModelSeed("google-cloud-speech","gemini-2.5-pro-tts","Gemini 2.5 Pro TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("ورودی متن","million_text_tokens",1),P("خروجی صوت","million_audio_tokens",20)],UpstreamBaseUrl:"https://texttospeech.googleapis.com"),

            new ServiceModelSeed("aws-speech","amazon-transcribe-batch","Amazon Transcribe Standard Batch","speech_to_text","audio-text","/",awsTranscribe,[P("رونویسی Batch - ناحیه US East","minute",.006m)],Region:"us-east-1",Notes:"AWS JSON operation: StartTranscriptionJob؛ حداقل صورتحساب ۱۵ ثانیه.",UpstreamBaseUrl:"https://transcribe.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-transcribe-streaming","Amazon Transcribe Standard Streaming","speech_to_text","audio-text","/stream-transcription-websocket",awsTranscribe,[P("رونویسی Streaming - ناحیه US East","minute",.01m)],Ws:true,Region:"us-east-1",Notes:"AWS operation: StartStreamTranscription؛ صورتحساب ثانیه‌ای و حداقل نشست ۱۵ ثانیه.",UpstreamBaseUrl:"https://transcribestreaming.us-east-1.amazonaws.com:8443"),
            new ServiceModelSeed("aws-speech","amazon-transcribe-medical","Amazon Transcribe Medical","speech_to_text","audio-text","/",awsTranscribe,[P("رونویسی پزشکی - نرخ مثال رسمی","minute",.075m)],Region:"us-east-1",UpstreamBaseUrl:"https://transcribe.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-transcribe-call-analytics","Amazon Transcribe Call Analytics","speech_to_text","audio-text","/",awsTranscribe,[P("Call Analytics tier 1","minute",.03m)],Region:"us-east-1",UpstreamBaseUrl:"https://transcribe.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-transcribe-call-summary","Amazon Generative Call Summary Add-on","audio_understanding","audio-text","/",awsTranscribe,[P("افزونه خلاصه تماس tier 1","minute",.0024m)],Region:"us-east-1",UpstreamBaseUrl:"https://transcribe.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-transcribe-content-redaction","Amazon Transcribe Content Redaction Add-on","audio_processing","audio-text","/",awsTranscribe,[P("افزونه حذف محتوای حساس tier 1","minute",.0024m)],Region:"us-east-1",UpstreamBaseUrl:"https://transcribe.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-polly-standard","Amazon Polly Standard Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار","million_characters",4)],Region:"us-east-1",UpstreamBaseUrl:"https://polly.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-polly-neural","Amazon Polly Neural Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار","million_characters",16)],Region:"us-east-1",UpstreamBaseUrl:"https://polly.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-polly-generative","Amazon Polly Generative Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار","million_characters",30)],Region:"us-east-1",UpstreamBaseUrl:"https://polly.us-east-1.amazonaws.com"),
            new ServiceModelSeed("aws-speech","amazon-polly-long-form","Amazon Polly Long-Form Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار طولانی","million_characters",100)],Region:"us-east-1",UpstreamBaseUrl:"https://polly.us-east-1.amazonaws.com"),

            new ServiceModelSeed("speechmatics","speechmatics-melia-1-batch","Speechmatics Melia 1 Batch","speech_to_text","audio-text","/v2/jobs",speechmatics,[P("رونویسی Batch","hour",.129m)],Preview:true,Notes:"Melia 1 در Production Preview و فقط Batch است.",UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-standard-batch","Speechmatics Standard Batch","speech_to_text","audio-text","/v2/jobs",speechmatics,[P("رونویسی Batch","hour",.24m)],UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-enhanced-batch","Speechmatics Enhanced Batch","speech_to_text","audio-text","/v2/jobs",speechmatics,[P("رونویسی Batch","hour",.40m)],UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-standard-realtime","Speechmatics Standard Realtime","speech_to_text","audio-text","/v2",speechmatics,[P("رونویسی هم‌زمان","hour",.24m)],Ws:true,Region:"eu2",UpstreamBaseUrl:"https://eu2.rt.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-enhanced-realtime","Speechmatics Enhanced Realtime","speech_to_text","audio-text","/v2",speechmatics,[P("رونویسی هم‌زمان","hour",.43m)],Ws:true,Region:"eu2",UpstreamBaseUrl:"https://eu2.rt.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-translation","Speechmatics Speech Translation","translation","audio-text","/v2/jobs",speechmatics,[P("افزونه ترجمه صوت","hour",.65m)],Notes:"این مبلغ افزونه است و به هزینه STT پایه افزوده می‌شود.",UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-summary-addon","Speechmatics Summary Add-on","audio_understanding","audio-text","/v2/jobs",speechmatics,[P("افزونه خلاصه‌سازی","hour",.12m)],UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-chapters-addon","Speechmatics Chapters Add-on","audio_understanding","audio-text","/v2/jobs",speechmatics,[P("افزونه فصل‌بندی","hour",.4m)],UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-sentiment-addon","Speechmatics Sentiment Add-on","audio_understanding","audio-text","/v2/jobs",speechmatics,[P("افزونه تحلیل احساسات","hour",.12m)],UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-topics-addon","Speechmatics Topics Add-on","audio_understanding","audio-text","/v2/jobs",speechmatics,[P("افزونه موضوعات","hour",.2m)],UpstreamBaseUrl:"https://asr.api.speechmatics.com"),
            new ServiceModelSeed("speechmatics","speechmatics-tts","Speechmatics Text-to-Speech","text_to_speech","text-audio","/tts",speechmatics,[P("تولید گفتار","thousand_characters",.011m)]),

            new ServiceModelSeed("gladia","solaria-1-prerecorded","Gladia Solaria 1 Pre-recorded","speech_to_text","audio-text","/v2/pre-recorded",gladia,[P("رونویسی غیرهم‌زمان Starter","hour",.61m)],Notes:"model upstream: solaria-1",UpstreamBaseUrl:"https://api.gladia.io"),
            new ServiceModelSeed("gladia","solaria-3-prerecorded","Gladia Solaria 3 Pre-recorded","speech_to_text","audio-text","/v2/pre-recorded",gladia,[P("رونویسی غیرهم‌زمان Starter","hour",.61m)],Notes:"model upstream: solaria-3؛ در حال حاضر Realtime ندارد.",UpstreamBaseUrl:"https://api.gladia.io"),
            new ServiceModelSeed("gladia","solaria-1-live","Gladia Solaria 1 Live","speech_to_text","audio-text","/v2/live",gladia,[P("رونویسی هم‌زمان Starter","hour",.75m)],Ws:true,Notes:"ابتدا نشست Live ساخته و سپس WebSocket tokenدار دریافت می‌شود؛ model upstream: solaria-1.",UpstreamBaseUrl:"https://api.gladia.io"),
            new ServiceModelSeed("gladia","gladia-prerecorded-translation","Gladia Pre-recorded Translation","translation","audio-text","/v2/pre-recorded",gladia,[P("رونویسی همراه ترجمه","hour",.61m)],Notes:"Translation surcharge جدا ندارد و در نرخ پایه transcription محاسبه شده است.",UpstreamBaseUrl:"https://api.gladia.io"),
            new ServiceModelSeed("gladia","gladia-live-translation","Gladia Live Translation","realtime_translation","audio-text","/v2/live",gladia,[P("رونویسی هم‌زمان همراه ترجمه","hour",.75m)],Ws:true,Notes:"Translation surcharge جدا ندارد؛ نشست Live ابتدا با HTTP ساخته می‌شود.",UpstreamBaseUrl:"https://api.gladia.io")
        };

        var existingModels = await db.Models.ToDictionaryAsync(x => x.ModelId);
        foreach (var seed in models)
        {
            if (!knownProviders.TryGetValue(seed.Provider, out var provider)) continue;
            var isNew = false;
            if (!existingModels.TryGetValue(seed.Id, out var model))
            {
                model = new AiModel { ModelId = seed.Id };
                db.Models.Add(model); existingModels[seed.Id] = model;
                isNew = true;
            }
            if (!isNew && !refreshExistingCatalog) continue;
            model.ProviderId = provider.Id; model.DisplayName = seed.Name; model.ServiceType = seed.ServiceType; model.Modality = seed.Modality;
            model.EndpointPath = PublicEndpoint(seed); model.UpstreamPath = seed.EndpointPath; model.Region = seed.Region; model.IsPreview = seed.Preview; model.SupportsStreaming = seed.Stream;
            model.SupportsWebSocket = seed.Ws; model.ContextWindow = seed.Context; model.PricingSourceUrl = seed.SourceUrl; model.PricingNotes = seed.Notes;
            model.UpstreamBaseUrl = seed.UpstreamBaseUrl.Length > 0
                ? seed.UpstreamBaseUrl
                : seed.Provider == "gemini" && seed.EndpointPath.StartsWith("/v1beta/", StringComparison.OrdinalIgnoreCase)
                    ? "https://generativelanguage.googleapis.com"
                    : seed.Provider == "deepgram"
                        ? "https://api.deepgram.com"
                        : seed.Provider == "qwen" && seed.EndpointPath.StartsWith("/api", StringComparison.OrdinalIgnoreCase)
                            ? "https://dashscope-intl.aliyuncs.com"
                    : "";
            model.PricingDetailsJson = JsonSerializer.Serialize(seed.Prices); model.IsActive = true; model.PriceSyncedAtUtc = new DateTime(2026, 7, 27, 0, 0, 0, DateTimeKind.Utc);
            model.InputPricePerMillionUsd = seed.Prices.FirstOrDefault(x => x.Unit == "million_text_tokens" && x.Label.Contains("ورودی") && !x.Label.Contains("Cache"))?.PriceUsd ?? 0;
            model.OutputPricePerMillionUsd = seed.Prices.FirstOrDefault(x => x.Unit == "million_text_tokens" && x.Label.Contains("خروجی"))?.PriceUsd ?? 0;
            model.CachedInputPricePerMillionUsd = seed.Prices.FirstOrDefault(x => x.Label.Contains("Cache"))?.PriceUsd;
            model.TestPayloadJson = TestPayload(seed);
        }
        var retiredModelIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            // OpenAI official deprecated catalog and unverified legacy aliases.
            "o4-mini-deep-research", "computer-use-preview", "gpt-image-1.5", "gpt-image-1-mini", "sora-2", "sora-2-pro",
            "o3-deep-research", "gpt-4o-search-preview", "gpt-4o-mini-search-preview", "gpt-5-search-api", "gpt-realtime",
            "gpt-realtime-mini", "gpt-audio", "gpt-audio-mini", "gpt-4o-audio-preview", "gpt-4o-realtime-preview", "gpt-4o-mini-tts",
            // Vendor lifecycle lists verified on 2026-07-27.
            "gemini-2.0-flash", "gemini-2.0-flash-lite", "gemini-3.1-flash-lite-preview", "gemini-3-pro-preview",
            "claude-opus-4-1-20250805", "deepseek-chat", "deepseek-reasoner", "kimi-k2.5", "moonshot-v1",
            "devstral-medium-latest", "devstral-small-latest", "labs-leanstral-2603", "eleven_turbo_v2_5",
            "command-r-03-2024", "command-r-plus-04-2024", "command-r", "command-r-plus", "c4ai-aya-expanse-8b", "c4ai-aya-vision-8b",
            "universal-3-pro", "universal-3-pro-streaming", "universal-streaming", "whisper-streaming",
            "google-cloud-tts-wavenet-neural2", "gladia-pre-recorded", "gladia-live"
        };
        foreach (var retired in existingModels.Values.Where(x => retiredModelIds.Contains(x.ModelId))) retired.IsActive = false;
        if (catalogSnapshotSetting is null)
            db.Settings.Add(new SystemSetting { Key = "catalog.model_snapshot", Value = catalogSnapshot });
        else
            catalogSnapshotSetting.Value = catalogSnapshot;
    }

    private static string PublicEndpoint(ServiceModelSeed seed) => seed.ServiceType switch
    {
        "text_to_speech" or "voice_clone" or "voice_design" => "/v1/audio/speech",
        "speech_to_text" or "audio_understanding" => "/v1/audio/transcriptions",
        "speech_to_speech" or "realtime_translation" when seed.Ws => "/v1/realtime",
        "translation" when seed.Modality.Contains("audio", StringComparison.OrdinalIgnoreCase) => "/v1/audio/transcriptions",
        "embeddings" => "/v1/embeddings",
        "moderation" => "/v1/moderations",
        "image_generation" => "/v1/images/generations",
        "video_generation" => "/v1/videos",
        _ when seed.EndpointPath == "/v1/responses" => "/v1/responses",
        _ => "/v1/chat/completions"
    };

    private static string TestPayload(ServiceModelSeed seed) => seed.ServiceType switch
    {
        "text_to_speech" => JsonSerializer.Serialize(new { model = seed.Id, input = "سلام! این یک آزمایش تولید گفتار است.", voice = "default" }),
        "speech_to_text" => JsonSerializer.Serialize(new { model = seed.Id, file = "@audio.wav", stream = seed.Ws }),
        "realtime_translation" or "translation" => JsonSerializer.Serialize(new { model = seed.Id, source_language = "fa", target_language = "en", audio = "@audio.wav" }),
        "speech_to_speech" => JsonSerializer.Serialize(new { model = seed.Id, type = "session.update", modalities = new[] { "audio", "text" } }),
        "image_generation" => JsonSerializer.Serialize(new { model = seed.Id, prompt = "یک ربات مینیمال و حرفه‌ای روی پس‌زمینه روشن", size = "1024x1024" }),
        "video_generation" => JsonSerializer.Serialize(new { model = seed.Id, prompt = "حرکت آرام نور روی یک لوگوی مینیمال", seconds = 4, size = "1280x720" }),
        "embeddings" => JsonSerializer.Serialize(new { model = seed.Id, input = "هوش مصنوعی به زبان فارسی" }),
        "moderation" => JsonSerializer.Serialize(new { model = seed.Id, input = "این یک متن سالم آزمایشی است." }),
        "search" when seed.EndpointPath == "/v1/chat/completions" => JsonSerializer.Serialize(new { model = seed.Id, messages = new[] { new { role = "user", content = "Reply only with OK" } }, stream = false }),
        "deep_research" or "coding" or "computer_use" or "search" => JsonSerializer.Serialize(new { model = seed.Id, input = "Reply only with OK", stream = false }),
        _ when seed.EndpointPath == "/v1/responses" => JsonSerializer.Serialize(new { model = seed.Id, input = "Reply only with OK", stream = false }),
        _ => JsonSerializer.Serialize(new { model = seed.Id, messages = new[] { new { role = "user", content = "Reply only with OK" } }, stream = false })
    };
}
