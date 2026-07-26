using Microsoft.EntityFrameworkCore;
using System.Data;
using System.Text.Json;

namespace AiBus.Api;

public static class SeedData
{
    private sealed record ProviderSeed(string Name, string Slug, string Logo, string BaseUrl, string PricingUrl, string Protocol);
    private sealed record ModelSeed(string Provider, string Id, string Name, decimal Input, decimal Output, decimal? Cached, int Context = 128000, bool Ws = false, string Modality = "text");
    private sealed record PriceSeed(string Label, string Unit, decimal? PriceUsd, string? Note = null);
    private sealed record ServiceModelSeed(string Provider, string Id, string Name, string ServiceType, string Modality, string EndpointPath, string SourceUrl, PriceSeed[] Prices, int Context = 0, bool Stream = true, bool Ws = false, bool Preview = false, string Region = "global", string Notes = "");

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
        await EnsureAudioCatalog(db);
        await db.SaveChangesAsync();
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
            ["PricingNotes"] = "TEXT NOT NULL DEFAULT ''"
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

    private static async Task EnsureAudioCatalog(AppDbContext db)
    {
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
        var xai = "https://docs.x.ai/developers/model-capabilities/audio/voice";
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
            new ServiceModelSeed("openai","gpt-5.6","GPT-5.6 (Sol alias)","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",30)],1050000),
            new ServiceModelSeed("openai","gpt-5.6-sol","GPT-5.6 Sol","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",30)],1050000),
            new ServiceModelSeed("openai","gpt-5.6-terra","GPT-5.6 Terra","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",2.5m),P("ورودی Cache","million_text_tokens",.25m),P("خروجی متن","million_text_tokens",15)],1050000),
            new ServiceModelSeed("openai","gpt-5.6-luna","GPT-5.6 Luna","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",1),P("ورودی Cache","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",6)],1050000),
            new ServiceModelSeed("openai","gpt-5.5","GPT-5.5","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",30)],1050000),
            new ServiceModelSeed("openai","gpt-5.5-pro","GPT-5.5 Pro","chat","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",30),P("خروجی متن","million_text_tokens",180)],1050000),
            new ServiceModelSeed("openai","gpt-5.4","GPT-5.4","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",2.5m),P("ورودی Cache","million_text_tokens",.25m),P("خروجی متن","million_text_tokens",15)],1050000),
            new ServiceModelSeed("openai","gpt-5.4-mini","GPT-5.4 Mini","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.75m),P("ورودی Cache","million_text_tokens",.075m),P("خروجی متن","million_text_tokens",4.5m)],400000),
            new ServiceModelSeed("openai","gpt-5.4-nano","GPT-5.4 Nano","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.2m),P("ورودی Cache","million_text_tokens",.02m),P("خروجی متن","million_text_tokens",1.25m)],400000),
            new ServiceModelSeed("openai","gpt-5.4-pro","GPT-5.4 Pro","chat","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",30),P("خروجی متن","million_text_tokens",180)],1050000),
            new ServiceModelSeed("openai","chat-latest","Chat Latest","chat","text-image","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",5),P("ورودی Cache","million_text_tokens",.5m),P("خروجی متن","million_text_tokens",30)],128000),
            new ServiceModelSeed("openai","gpt-5.3-codex","GPT-5.3 Codex","coding","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",1.75m),P("ورودی Cache","million_text_tokens",.175m),P("خروجی متن","million_text_tokens",14)],400000),
            new ServiceModelSeed("openai","gpt-5-search-api","GPT-5 Search API","search","text","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",1.25m),P("ورودی Cache","million_text_tokens",.125m),P("خروجی متن","million_text_tokens",10),P("فراخوانی جستجوی وب","thousand_calls",10)],400000,Notes:"هزینه ابزار جستجوی وب جدا از هزینه توکن محاسبه می‌شود."),
            new ServiceModelSeed("openai","gpt-4o-search-preview","GPT-4o Search Preview","search","text","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",2.5m),P("ورودی Cache","million_text_tokens",1.25m),P("خروجی متن","million_text_tokens",10),P("فراخوانی جستجوی وب","thousand_calls",10)],128000,Preview:true),
            new ServiceModelSeed("openai","gpt-4o-mini-search-preview","GPT-4o Mini Search Preview","search","text","/v1/chat/completions",openAi,[P("ورودی متن","million_text_tokens",.15m),P("ورودی Cache","million_text_tokens",.075m),P("خروجی متن","million_text_tokens",.6m),P("فراخوانی جستجوی وب","thousand_calls",10)],128000,Preview:true),
            new ServiceModelSeed("openai","o3-deep-research","o3 Deep Research","deep_research","text-image","/v1/responses",openAi,[P("ورودی متن","million_text_tokens",5),P("خروجی متن","million_text_tokens",20)],200000),
            new ServiceModelSeed("openai","text-embedding-3-large","Text Embedding 3 Large","embeddings","text-vector","/v1/embeddings",openAi,[P("ورودی متن","million_text_tokens",.13m)],8191,false),
            new ServiceModelSeed("openai","text-embedding-3-small","Text Embedding 3 Small","embeddings","text-vector","/v1/embeddings",openAi,[P("ورودی متن","million_text_tokens",.02m)],8191,false),
            new ServiceModelSeed("openai","text-embedding-ada-002","Text Embedding Ada 002","embeddings","text-vector","/v1/embeddings",openAi,[P("ورودی متن","million_text_tokens",.1m)],8191,false),
            new ServiceModelSeed("openai","omni-moderation-latest","Omni Moderation","moderation","text-image","/v1/moderations",openAi,[P("بررسی ایمنی","free",0)],128000,false),
            new ServiceModelSeed("openai","gpt-image-2","GPT Image 2","image_generation","text-image","/v1/images/generations","https://developers.openai.com/api/docs/models/gpt-image-2",[P("ورودی متن","million_text_tokens",5),P("ورودی تصویر","million_image_tokens",8),P("خروجی تصویر","million_image_tokens",30)],Stream:false),
            new ServiceModelSeed("openai","gpt-image-1.5","GPT Image 1.5","image_generation","text-image","/v1/images/generations","https://developers.openai.com/api/docs/models/gpt-image-1.5",[P("ورودی متن","million_text_tokens",5),P("ورودی Cache متن","million_text_tokens",1.25m),P("خروجی متن","million_text_tokens",10),P("ورودی تصویر","million_image_tokens",8),P("ورودی Cache تصویر","million_image_tokens",2),P("خروجی تصویر","million_image_tokens",32)],Stream:false),
            new ServiceModelSeed("openai","gpt-image-1-mini","GPT Image 1 Mini","image_generation","text-image","/v1/images/generations",openAi,[P("ورودی متن","million_text_tokens",2),P("ورودی تصویر","million_image_tokens",2.5m),P("خروجی تصویر","million_image_tokens",8)],Stream:false),
            new ServiceModelSeed("openai","sora-2","Sora 2","video_generation","text-image-video","/v1/videos","https://developers.openai.com/api/docs/models/sora-2",[P("ویدیوی 720p","second",.1m)],Stream:false),
            new ServiceModelSeed("openai","sora-2-pro","Sora 2 Pro","video_generation","text-image-video","/v1/videos","https://developers.openai.com/api/docs/models/sora-2-pro",[P("ویدیوی 720p","second",.3m),P("ویدیوی 1024p","second",.5m),P("ویدیوی 1080p","second",.7m)],Stream:false),
            new ServiceModelSeed("openai","gpt-realtime-2.1","GPT Realtime 2.1","speech_to_speech","audio","/v1/realtime",openAi,[P("ورودی متن","million_text_tokens",4),P("ورودی Cache","million_text_tokens",.4m),P("خروجی متن","million_text_tokens",24),P("ورودی صوت","million_audio_tokens",32),P("خروجی صوت","million_audio_tokens",64)],128000,false,true),
            new ServiceModelSeed("openai","gpt-realtime-2.1-mini","GPT Realtime 2.1 Mini","speech_to_speech","audio","/v1/realtime",openAi,[P("ورودی متن","million_text_tokens",.6m),P("ورودی Cache صوت","million_audio_tokens",.3m),P("خروجی متن","million_text_tokens",2.4m),P("ورودی صوت","million_audio_tokens",10),P("خروجی صوت","million_audio_tokens",20)],128000,false,true),
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

            new ServiceModelSeed("xai","grok-voice-latest","Grok Voice Agent","speech_to_speech","audio","/v1/realtime",xai,[P("صوت ارسالی یا دریافتی","minute",.05m),P("پیام متنی مستقل","message",.004m)],Ws:true),
            new ServiceModelSeed("xai","xai-tts","xAI Text to Speech","text_to_speech","text-audio","/v1/tts",xai,[P("تولید گفتار","million_characters",15)]),
            new ServiceModelSeed("xai","xai-stt-batch","xAI Speech to Text Batch","speech_to_text","audio-text","/v1/stt",xai,[P("رونویسی Batch","hour",.10m)]),
            new ServiceModelSeed("xai","xai-stt-streaming","xAI Speech to Text Streaming","speech_to_text","audio-text","/v1/stt",xai,[P("رونویسی Streaming","hour",.20m)],Ws:true),

            new ServiceModelSeed("mistral","voxtral-mini-latest","Voxtral Mini Transcribe 2","speech_to_text","audio-text","/v1/audio/transcriptions",mistral,[P("ورودی صوت","minute",.003m)]),
            new ServiceModelSeed("mistral","voxtral-mini-transcribe-realtime-2602","Voxtral Mini Transcribe Realtime","speech_to_text","audio-text","/v1/audio/transcriptions",mistral,[P("ورودی صوت زنده","minute",.006m)],Ws:true),
            new ServiceModelSeed("mistral","voxtral-mini-tts-latest","Voxtral TTS","text_to_speech","text-audio","/v1/audio/speech",mistral,[P("تولید یا شبیه‌سازی صدا","thousand_characters",.016m)]),
            new ServiceModelSeed("mistral","voxtral-small-latest","Voxtral Small Audio Understanding","audio_understanding","audio-text","/v1/chat/completions",mistral,[P("ورودی صوت","minute",.004m),P("ورودی متن","million_text_tokens",.1m),P("خروجی متن","million_text_tokens",.4m)],32000),

            new ServiceModelSeed("glm","glm-asr-2512","GLM ASR 2512","speech_to_text","audio-text","/api/paas/v4/audio/transcriptions",glm,[P("ورودی صوت","million_audio_tokens",.03m,"≈ $0.0024/min")],Stream:true),
            new ServiceModelSeed("cohere","cohere-transcribe-03-2026","Cohere Transcribe","speech_to_text","audio-text","/v2/audio/transcriptions",cohere,[P("قیمت عمومی","contact_sales",null,"تماس با فروش Cohere")],Notes:"Cohere قیمت عمومی این سرویس را منتشر نکرده است."),
            new ServiceModelSeed("cohere","cohere-transcribe-arabic-07-2026","Cohere Transcribe Arabic","speech_to_text","audio-text","/v2/audio/transcriptions",cohere,[P("قیمت عمومی","contact_sales",null,"تماس با فروش Cohere")],Notes:"مدل عربی و انگلیسی؛ قیمت Production عمومی منتشر نشده است."),

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
            new ServiceModelSeed("elevenlabs","eleven_turbo_v2_5","Eleven Turbo v2.5","text_to_speech","text-audio","/text-to-speech",eleven,[P("تولید گفتار","thousand_characters",.05m)]),
            new ServiceModelSeed("elevenlabs","scribe_v2","Scribe v2","speech_to_text","audio-text","/speech-to-text",eleven,[P("رونویسی","hour",.22m)]),
            new ServiceModelSeed("elevenlabs","scribe_v2_realtime","Scribe v2 Realtime","speech_to_text","audio-text","/speech-to-text/realtime",eleven,[P("رونویسی زنده","hour",.39m)],Ws:true),
            new ServiceModelSeed("elevenlabs","eleven_voice_changer","ElevenLabs Voice Changer","speech_to_speech","audio","/speech-to-speech",eleven,[P("تبدیل صدا","minute",.12m)]),
            new ServiceModelSeed("elevenlabs","eleven_dubbing","ElevenLabs Dubbing","translation","audio","/dubbing",eleven,[P("دوبله خودکار با Watermark","minute",.33m),P("دوبله بدون Watermark","minute",.5m)]),

            new ServiceModelSeed("deepgram","flux-general-en","Deepgram Flux English","speech_to_text","audio-text","/v2/listen",deepgram,[P("رونویسی مکالمه‌ای","minute",.0065m)],Ws:true),
            new ServiceModelSeed("deepgram","flux-general-multi","Deepgram Flux Multilingual","speech_to_text","audio-text","/v2/listen",deepgram,[P("رونویسی مکالمه‌ای چندزبانه","minute",.0078m)],Ws:true),
            new ServiceModelSeed("deepgram","nova-3","Deepgram Nova-3 Monolingual","speech_to_text","audio-text","/v1/listen",deepgram,[P("رونویسی","minute",.0048m)],Ws:true),
            new ServiceModelSeed("deepgram","nova-3-multilingual","Deepgram Nova-3 Multilingual","speech_to_text","audio-text","/v1/listen",deepgram,[P("رونویسی چندزبانه","minute",.0058m)],Ws:true),
            new ServiceModelSeed("deepgram","aura-2","Deepgram Aura-2","text_to_speech","text-audio","/v1/speak",deepgram,[P("تولید گفتار","thousand_characters",.015m)]),
            new ServiceModelSeed("deepgram","deepgram-voice-agent","Deepgram Voice Agent API","speech_to_speech","audio","/v1/agent/converse",deepgram,[P("عامل صوتی کامل","hour",4.5m)],Ws:true),

            new ServiceModelSeed("assemblyai","universal-3-pro","AssemblyAI Universal-3 Pro","speech_to_text","audio-text","/v2/transcript",assembly,[P("رونویسی ضبط‌شده","hour",.21m)]),
            new ServiceModelSeed("assemblyai","universal-2","AssemblyAI Universal-2","speech_to_text","audio-text","/v2/transcript",assembly,[P("رونویسی ضبط‌شده","hour",.15m)]),
            new ServiceModelSeed("assemblyai","universal-3-pro-streaming","AssemblyAI Universal-3 Pro Streaming","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.45m)],Ws:true),
            new ServiceModelSeed("assemblyai","universal-streaming","AssemblyAI Universal Streaming","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.15m)],Ws:true),
            new ServiceModelSeed("assemblyai","universal-streaming-multilingual","AssemblyAI Universal Streaming Multilingual","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.15m)],Ws:true),
            new ServiceModelSeed("assemblyai","whisper-streaming","AssemblyAI Whisper Streaming","speech_to_text","audio-text","/v3/ws",assembly,[P("مدت نشست Streaming","hour",.30m)],Ws:true),
            new ServiceModelSeed("assemblyai","assemblyai-voice-agent","AssemblyAI Voice Agent API","speech_to_speech","audio","/v1/voice-agent",assembly,[P("عامل صوتی کامل","hour",4.5m)],Ws:true)
            ,
            new ServiceModelSeed("google-cloud-speech","google-stt-v2-standard","Google Cloud Speech-to-Text V2 Standard","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:recognize",googleSpeech,[P("رونویسی تا ۵۰۰ هزار دقیقه در ماه","minute",.016m)],Region:"global"),
            new ServiceModelSeed("google-cloud-speech","google-stt-v2-dynamic-batch","Google Cloud STT V2 Dynamic Batch","speech_to_text","audio-text","/v2/projects/{project}/locations/global/recognizers/_:batchRecognize",googleSpeech,[P("رونویسی Dynamic Batch","minute",.003m)],Region:"global"),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-standard","Google Cloud TTS Standard","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار","million_characters",4)]),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-wavenet-neural2","Google Cloud WaveNet / Neural2 TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار","million_characters",16)]),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-chirp3-hd","Google Cloud Chirp 3 HD TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار HD","million_characters",30)]),
            new ServiceModelSeed("google-cloud-speech","google-cloud-tts-studio","Google Cloud Studio TTS","text_to_speech","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار Studio","million_characters",160)]),
            new ServiceModelSeed("google-cloud-speech","google-cloud-instant-custom-voice","Google Cloud Instant Custom Voice","voice_clone","text-audio","/v1/text:synthesize",googleTts,[P("تولید گفتار سفارشی","million_characters",60)]),

            new ServiceModelSeed("aws-speech","amazon-transcribe-batch","Amazon Transcribe Standard Batch","speech_to_text","audio-text","/transcribe",awsTranscribe,[P("رونویسی Batch - ناحیه US East","minute",.006m)],Region:"us-east-1",Notes:"حداقل صورتحساب هر درخواست ۱۵ ثانیه است."),
            new ServiceModelSeed("aws-speech","amazon-transcribe-streaming","Amazon Transcribe Standard Streaming","speech_to_text","audio-text","/stream-transcription-websocket",awsTranscribe,[P("رونویسی Streaming - ناحیه US East","minute",.01m)],Ws:true,Region:"us-east-1",Notes:"صورتحساب ثانیه‌ای و حداقل هر نشست ۱۵ ثانیه است."),
            new ServiceModelSeed("aws-speech","amazon-polly-standard","Amazon Polly Standard Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار","million_characters",4)],Region:"us-east-1"),
            new ServiceModelSeed("aws-speech","amazon-polly-neural","Amazon Polly Neural Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار","million_characters",16)],Region:"us-east-1"),
            new ServiceModelSeed("aws-speech","amazon-polly-generative","Amazon Polly Generative Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار","million_characters",30)],Region:"us-east-1"),
            new ServiceModelSeed("aws-speech","amazon-polly-long-form","Amazon Polly Long-Form Voices","text_to_speech","text-audio","/v1/speech",awsPolly,[P("تولید گفتار طولانی","million_characters",100)],Region:"us-east-1"),

            new ServiceModelSeed("speechmatics","speechmatics-melia-1-batch","Speechmatics Melia 1 Batch","speech_to_text","audio-text","/jobs",speechmatics,[P("رونویسی Batch","hour",.129m)]),
            new ServiceModelSeed("speechmatics","speechmatics-standard-batch","Speechmatics Standard Batch","speech_to_text","audio-text","/jobs",speechmatics,[P("رونویسی Batch","hour",.24m)]),
            new ServiceModelSeed("speechmatics","speechmatics-enhanced-batch","Speechmatics Enhanced Batch","speech_to_text","audio-text","/jobs",speechmatics,[P("رونویسی Batch","hour",.40m)]),
            new ServiceModelSeed("speechmatics","speechmatics-standard-realtime","Speechmatics Standard Realtime","speech_to_text","audio-text","/realtime",speechmatics,[P("رونویسی هم‌زمان","hour",.24m)],Ws:true),
            new ServiceModelSeed("speechmatics","speechmatics-enhanced-realtime","Speechmatics Enhanced Realtime","speech_to_text","audio-text","/realtime",speechmatics,[P("رونویسی هم‌زمان","hour",.43m)],Ws:true),
            new ServiceModelSeed("speechmatics","speechmatics-translation","Speechmatics Speech Translation","translation","audio-text","/jobs",speechmatics,[P("افزونه ترجمه صوت","hour",.65m)]),
            new ServiceModelSeed("speechmatics","speechmatics-tts","Speechmatics Text-to-Speech","text_to_speech","text-audio","/tts",speechmatics,[P("تولید گفتار","thousand_characters",.011m)]),

            new ServiceModelSeed("gladia","gladia-pre-recorded","Gladia Pre-recorded Transcription","speech_to_text","audio-text","/pre-recorded",gladia,[P("رونویسی غیرهم‌زمان Starter","hour",.61m)]),
            new ServiceModelSeed("gladia","gladia-live","Gladia Live Transcription","speech_to_text","audio-text","/live",gladia,[P("رونویسی هم‌زمان Starter","hour",.75m)],Ws:true),
            new ServiceModelSeed("gladia","gladia-live-translation","Gladia Live Translation","realtime_translation","audio-text","/live",gladia,[P("رونویسی هم‌زمان همراه ترجمه","hour",.75m)],Ws:true,Notes:"ترجمه و تشخیص زبان در قابلیت‌های اصلی سرویس قرار دارند و هزینه پایه بر مدت صوت محاسبه می‌شود.")
        };

        var existingModels = await db.Models.ToDictionaryAsync(x => x.ModelId);
        foreach (var seed in models)
        {
            if (!knownProviders.TryGetValue(seed.Provider, out var provider)) continue;
            if (!existingModels.TryGetValue(seed.Id, out var model))
            {
                model = new AiModel { ModelId = seed.Id };
                db.Models.Add(model); existingModels[seed.Id] = model;
            }
            model.ProviderId = provider.Id; model.DisplayName = seed.Name; model.ServiceType = seed.ServiceType; model.Modality = seed.Modality;
            model.EndpointPath = seed.EndpointPath; model.Region = seed.Region; model.IsPreview = seed.Preview; model.SupportsStreaming = seed.Stream;
            model.SupportsWebSocket = seed.Ws; model.ContextWindow = seed.Context; model.PricingSourceUrl = seed.SourceUrl; model.PricingNotes = seed.Notes;
            model.PricingDetailsJson = JsonSerializer.Serialize(seed.Prices); model.IsActive = true; model.PriceSyncedAtUtc = new DateTime(2026, 7, 26, 0, 0, 0, DateTimeKind.Utc);
            model.InputPricePerMillionUsd = seed.Prices.FirstOrDefault(x => x.Unit == "million_text_tokens" && x.Label.Contains("ورودی") && !x.Label.Contains("Cache"))?.PriceUsd ?? 0;
            model.OutputPricePerMillionUsd = seed.Prices.FirstOrDefault(x => x.Unit == "million_text_tokens" && x.Label.Contains("خروجی"))?.PriceUsd ?? 0;
            model.CachedInputPricePerMillionUsd = seed.Prices.FirstOrDefault(x => x.Label.Contains("Cache"))?.PriceUsd;
            model.TestPayloadJson = TestPayload(seed);
        }
        var retiredOpenAiModels = new[] { "o4-mini-deep-research", "computer-use-preview" };
        foreach (var retired in existingModels.Values.Where(x => retiredOpenAiModels.Contains(x.ModelId))) retired.IsActive = false;
    }

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
