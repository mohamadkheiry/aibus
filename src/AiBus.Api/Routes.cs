using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace AiBus.Api;

public static class Routes
{
    public static void MapAiBusRoutes(this WebApplication app)
    {
        MapAuth(app);
        MapUser(app);
        MapAdmin(app);
        MapGateway(app);
    }

    private static void MapAuth(WebApplication app)
    {
        app.MapPost("/api/auth/request-otp", async ([FromBody] RequestOtpRequest req, AppDbContext db, SmsIrService sms, IConfiguration config, CancellationToken ct) =>
        {
            var mobile = NormalizeMobile(req.Mobile);
            if (mobile is null) return Results.BadRequest(new { message = "شماره موبایل معتبر نیست." });
            var recent = await db.OtpCodes.CountAsync(x => x.Mobile == mobile && x.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-10), ct);
            if (recent >= 4) return Results.Json(new { message = "تعداد درخواست بیش از حد؛ ۱۰ دقیقه بعد تلاش کنید." }, statusCode: 429);
            var code = Hashing.RandomDigits(6);
            db.OtpCodes.Add(new OtpCode { Mobile = mobile, CodeHash = Hashing.Sha256(code), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(2) });
            await db.SaveChangesAsync(ct);
            var sent = await sms.SendOtp(mobile, code, ct);
            var expose = (app.Environment.IsDevelopment() && config.GetValue("Development:ExposeOtp", false))
                || (mobile == "09015909044" && config.GetValue("Bootstrap:ExposeSuperAdminOtp", false));
            return Results.Ok(new { message = sent ? "کد ورود ارسال شد." : "سرویس پیامک تنظیم نشده؛ حالت توسعه فعال است.", expiresIn = 120, debugCode = expose ? code : null });
        }).AllowAnonymous();

        app.MapPost("/api/auth/verify-otp", async ([FromBody] VerifyOtpRequest req, AppDbContext db, TokenService tokens, CancellationToken ct) =>
        {
            var mobile = NormalizeMobile(req.Mobile);
            if (mobile is null) return Results.BadRequest(new { message = "شماره موبایل معتبر نیست." });
            var otp = await db.OtpCodes.Where(x => x.Mobile == mobile && !x.IsUsed).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            if (otp is null || otp.ExpiresAtUtc < DateTime.UtcNow || otp.Attempts >= 5) return Results.BadRequest(new { message = "کد منقضی یا نامعتبر است." });
            otp.Attempts++;
            if (otp.CodeHash != Hashing.Sha256(req.Code)) { await db.SaveChangesAsync(ct); return Results.BadRequest(new { message = "کد واردشده صحیح نیست." }); }
            otp.IsUsed = true;
            var user = await db.Users.SingleOrDefaultAsync(x => x.Mobile == mobile, ct);
            if (user is null)
            {
                user = new AppUser { Mobile = mobile, Role = mobile == "09015909044" ? Roles.SuperAdmin : Roles.User, DisplayName = mobile == "09015909044" ? "سوپر ادمین" : $"کاربر {mobile[^4..]}" };
                db.Users.Add(user);
            }
            if (user.IsSuspended) return Results.Json(new { message = "حساب شما تعلیق شده است." }, statusCode: 403);
            user.LastSeenAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { token = tokens.Create(user), user = UserView(user) });
        }).AllowAnonymous();
    }

    private static void MapUser(WebApplication app)
    {
        var api = app.MapGroup("/api").RequireAuthorization();
        api.MapGet("/me", async (System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([principal.UserId()], ct); return user is null ? Results.NotFound() : Results.Ok(UserView(user));
        });
        api.MapPut("/me", async (UpdateProfileRequest req, System.Security.Claims.ClaimsPrincipal principal, AppDbContext db, CancellationToken ct) =>
        {
            var user = await db.Users.FindAsync([principal.UserId()], ct); if (user is null) return Results.NotFound();
            user.DisplayName = req.DisplayName.Trim()[..Math.Min(100, req.DisplayName.Trim().Length)]; await db.SaveChangesAsync(ct); return Results.Ok(UserView(user));
        });
        api.MapGet("/models", async (AppDbContext db, CancellationToken ct) =>
        {
            var models = await db.Models.AsNoTracking().Where(x => x.IsActive && x.Provider!.IsActive).Include(x => x.Provider).OrderBy(x => x.Provider!.Name).ThenBy(x => x.DisplayName).ToListAsync(ct);
            return Results.Ok(models.Select(ModelView));
        });

        api.MapGet("/keys", async (System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var keys = await db.UserApiKeys.AsNoTracking().Where(x => x.UserId == p.UserId()).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
            return Results.Ok(keys.Select(x => new { x.Id, x.Name, x.KeyPrefix, x.IsActive, x.RequestLimit, x.SpendLimitUsd, x.RequestCount, x.SpentUsd, x.AccessMode, modelRules = JsonSerializer.Deserialize<string[]>(x.ModelRulesJson), x.CreatedAtUtc, x.LastUsedAtUtc }));
        });
        api.MapPost("/keys", async (CreateUserKeyRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            if (await db.UserApiKeys.CountAsync(x => x.UserId == p.UserId(), ct) >= 20) return Results.BadRequest(new { message = "حداکثر ۲۰ کلید مجاز است." });
            var raw = Hashing.RandomApiKey();
            var key = new UserApiKey { UserId = p.UserId(), Name = req.Name, KeyHash = Hashing.Sha256(raw), KeyPrefix = raw[..12], RequestLimit = req.RequestLimit, SpendLimitUsd = req.SpendLimitUsd, AccessMode = ValidAccessMode(req.AccessMode), ModelRulesJson = JsonSerializer.Serialize(req.ModelRules ?? []) };
            db.UserApiKeys.Add(key); await db.SaveChangesAsync(ct);
            return Results.Ok(new { key.Id, apiKey = raw, key.KeyPrefix, message = "این کلید فقط یک‌بار نمایش داده می‌شود؛ همین حالا ذخیره کنید." });
        });
        api.MapPut("/keys/{id:guid}", async (Guid id, UpdateUserKeyRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var key = await db.UserApiKeys.SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct); if (key is null) return Results.NotFound();
            key.Name = req.Name; key.IsActive = req.IsActive; key.RequestLimit = req.RequestLimit; key.SpendLimitUsd = req.SpendLimitUsd; key.AccessMode = ValidAccessMode(req.AccessMode); key.ModelRulesJson = JsonSerializer.Serialize(req.ModelRules ?? []); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapDelete("/keys/{id:guid}", async (Guid id, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var key = await db.UserApiKeys.SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct); if (key is null) return Results.NotFound(); db.Remove(key); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        api.MapGet("/dashboard", async (System.Security.Claims.ClaimsPrincipal p, AppDbContext db, [FromQuery] DateTime? from, [FromQuery] DateTime? to, CancellationToken ct) =>
        {
            var userId = p.UserId(); var start = from ?? DateTime.UtcNow.AddDays(-30); var end = to ?? DateTime.UtcNow;
            var user = await db.Users.FindAsync([userId], ct); if (user is null) return Results.NotFound();
            var usage = db.UsageRecords.AsNoTracking().Where(x => x.UserId == userId && x.CreatedAtUtc >= start && x.CreatedAtUtc <= end);
            // SQLite cannot aggregate decimal columns. Reporting uses REAL casts while the wallet ledger remains decimal.
            var totals = await usage.GroupBy(_ => 1).Select(g => new { requests = g.Count(), inputTokens = g.Sum(x => x.InputTokens), outputTokens = g.Sum(x => x.OutputTokens), costUsd = g.Sum(x => (double)x.CostUsd), avgLatencyMs = g.Average(x => x.DurationMs) }).FirstOrDefaultAsync(ct);
            var timelineRaw = await usage.GroupBy(x => x.CreatedAtUtc.Date).Select(g => new { date = g.Key, requests = g.Count(), tokens = g.Sum(x => x.InputTokens + x.OutputTokens), costUsd = g.Sum(x => (double)x.CostUsd) }).OrderBy(x => x.date).ToListAsync(ct);
            var byModel = await usage.GroupBy(x => x.ModelName).Select(g => new { model = g.Key, requests = g.Count(), tokens = g.Sum(x => x.InputTokens + x.OutputTokens), costUsd = g.Sum(x => (double)x.CostUsd) }).OrderByDescending(x => x.tokens).Take(12).ToListAsync(ct);
            return Results.Ok(new { walletUsd = user.WalletUsd, walletIrr = user.WalletUsd * decimal.Parse((await db.Settings.FindAsync(["currency.usd_irr"], ct))?.Value ?? "0", CultureInfo.InvariantCulture), totals = totals ?? new { requests = 0, inputTokens = 0L, outputTokens = 0L, costUsd = 0d, avgLatencyMs = 0d }, timeline = timelineRaw, byModel });
        });
        api.MapGet("/usage", async (System.Security.Claims.ClaimsPrincipal p, AppDbContext db, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken ct) =>
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 10, 100); var q = db.UsageRecords.AsNoTracking().Where(x => x.UserId == p.UserId()).OrderByDescending(x => x.CreatedAtUtc); return Results.Ok(new { total = await q.CountAsync(ct), items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct) });
        });

        api.MapGet("/tickets", async (System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var tickets = await db.SupportTickets.AsNoTracking().Where(x => x.UserId == p.UserId()).Include(x => x.Messages).OrderByDescending(x => x.UpdatedAtUtc).ToListAsync(ct);
            return Results.Ok(new { items = tickets.Select(x => TicketSummary(x, false)), counts = TicketCounts(tickets) });
        });
        api.MapPost("/tickets", async (CreateTicketRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var subject = Clean(req.Subject, 160); var message = Clean(req.Message, 5000);
            if (subject.Length < 3) return Results.BadRequest(new { message = "موضوع تیکت باید حداقل ۳ کاراکتر باشد." });
            if (message.Length < 2) return Results.BadRequest(new { message = "متن تیکت نمی‌تواند خالی باشد." });
            var now = DateTime.UtcNow;
            var ticket = new SupportTicket { UserId = p.UserId(), ReferenceCode = $"TKT-{now:yyMMdd}-{Guid.NewGuid():N}"[..17].ToUpperInvariant(), Subject = subject, Category = ValidTicketCategory(req.Category), Priority = ValidTicketPriority(req.Priority), Status = "open", CreatedAtUtc = now, UpdatedAtUtc = now, LastReplyAtUtc = now };
            ticket.Messages.Add(new TicketMessage { AuthorUserId = p.UserId(), Body = message, CreatedAtUtc = now });
            db.SupportTickets.Add(ticket); await db.SaveChangesAsync(ct);
            return Results.Ok(TicketDetails(ticket, false));
        });
        api.MapGet("/tickets/{id:guid}", async (Guid id, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var ticket = await db.SupportTickets.AsNoTracking().Include(x => x.Messages).SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct);
            return ticket is null ? Results.NotFound() : Results.Ok(TicketDetails(ticket, false));
        });
        api.MapPost("/tickets/{id:guid}/messages", async (Guid id, TicketMessageRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var ticket = await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct); if (ticket is null) return Results.NotFound();
            if (ticket.Status == "closed") return Results.BadRequest(new { message = "این تیکت بسته شده است؛ برای موضوع جدید تیکت دیگری بسازید." });
            var body = Clean(req.Message, 5000); if (body.Length < 2) return Results.BadRequest(new { message = "متن پیام نمی‌تواند خالی باشد." });
            var now = DateTime.UtcNow; db.TicketMessages.Add(new TicketMessage { TicketId = ticket.Id, AuthorUserId = p.UserId(), Body = body, CreatedAtUtc = now }); ticket.Status = "waiting_support"; ticket.UpdatedAtUtc = now; ticket.LastReplyAtUtc = now; ticket.ClosedAtUtc = null; await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapPost("/tickets/{id:guid}/close", async (Guid id, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var ticket = await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct); if (ticket is null) return Results.NotFound(); var now = DateTime.UtcNow; ticket.Status = "closed"; ticket.ClosedAtUtc = now; ticket.UpdatedAtUtc = now; await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        api.MapGet("/wallet/quote", async ([FromQuery] decimal amountUsd, SettingsService settings) =>
        {
            amountUsd = Math.Clamp(amountUsd, 0, 10000);
            var rate = long.Parse(await settings.Get("currency.usd_irr", "850000"), CultureInfo.InvariantCulture);
            var feePercent = decimal.Parse(await settings.Get("billing.fee_percent", "10"), CultureInfo.InvariantCulture);
            var baseAmountIrr = (long)Math.Ceiling(amountUsd * rate);
            var feeAmountIrr = (long)Math.Ceiling(baseAmountIrr * feePercent / 100);
            return Results.Ok(new { amountUsd, dollarRateIrr = rate, feePercent, baseAmountIrr, feeAmountIrr, totalAmountIrr = baseAmountIrr + feeAmountIrr });
        });

        api.MapPost("/wallet/topup", async (CreateTopUpRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, SettingsService settings, ZarinpalService zarinpal, CancellationToken ct) =>
        {
            if (req.AmountUsd < 1 || req.AmountUsd > 10000) return Results.BadRequest(new { message = "مبلغ باید بین ۱ تا ۱۰٬۰۰۰ دلار باشد." });
            var rate = long.Parse(await settings.Get("currency.usd_irr", "850000"), CultureInfo.InvariantCulture); var fee = decimal.Parse(await settings.Get("billing.fee_percent", "10"), CultureInfo.InvariantCulture); var irr = (long)Math.Ceiling(req.AmountUsd * rate * (1 + fee / 100));
            var tx = new WalletTransaction { UserId = p.UserId(), AmountUsd = req.AmountUsd, AmountIrr = irr, ExchangeRateIrr = rate, FeePercent = fee, Description = $"شارژ کیف پول AiBus - {req.AmountUsd:N2} USD" }; db.WalletTransactions.Add(tx); await db.SaveChangesAsync(ct);
            var callbackBase = await settings.Get("payment.callback_url", "http://localhost:5050/api/wallet/callback"); var callback = callbackBase + (callbackBase.Contains('?') ? "&" : "?") + "transactionId=" + tx.Id;
            var result = await zarinpal.Request(irr, tx.Description!, callback, ct); if (!result.ok) { tx.Status = "failed"; await db.SaveChangesAsync(ct); return Results.BadRequest(new { message = result.error }); }
            tx.Authority = result.authority; await db.SaveChangesAsync(ct); return Results.Ok(new { tx.Id, tx.AmountUsd, tx.AmountIrr, tx.ExchangeRateIrr, tx.FeePercent, authority = result.authority, paymentUrl = $"https://www.zarinpal.com/pg/StartPay/{result.authority}" });
        });
        app.MapGet("/api/wallet/callback", async ([FromQuery] Guid transactionId, [FromQuery] string? Authority, [FromQuery] string? Status, AppDbContext db, ZarinpalService zarinpal, SettingsService settings, IConfiguration config, CancellationToken ct) =>
        {
            var tx = await db.WalletTransactions.FindAsync([transactionId], ct); var front = await settings.Get("web.public_url", config["Web:PublicUrl"] ?? "http://localhost:5173");
            if (tx is null || tx.Status != "pending" || Status != "OK" || Authority != tx.Authority) return Results.Redirect($"{front}/payment/callback?status=failed");
            var verified = await zarinpal.Verify(tx.AmountIrr, Authority!, ct); if (!verified.ok) { tx.Status = "failed"; await db.SaveChangesAsync(ct); return Results.Redirect($"{front}/payment/callback?status=failed"); }
            await using var transaction = await db.Database.BeginTransactionAsync(ct); tx.Status = "completed"; tx.ReferenceId = verified.refId; tx.CompletedAtUtc = DateTime.UtcNow; var user = await db.Users.FindAsync([tx.UserId], ct); if (user is not null) user.WalletUsd += tx.AmountUsd; await db.SaveChangesAsync(ct); await transaction.CommitAsync(ct); return Results.Redirect($"{front}/payment/callback?status=success&refId={verified.refId}");
        }).AllowAnonymous();

        api.MapPost("/analytics/visit", async (HttpContext ctx, AppDbContext db, CancellationToken ct) =>
        {
            var ua = ctx.Request.Headers.UserAgent.ToString(); var (browser, os) = UserAgentParser.Parse(ua); var visitor = ctx.Request.Cookies["aibus_vid"] ?? Guid.NewGuid().ToString("N"); ctx.Response.Cookies.Append("aibus_vid", visitor, new CookieOptions { MaxAge = TimeSpan.FromDays(365), HttpOnly = true, SameSite = SameSiteMode.Lax, Secure = ctx.Request.IsHttps });
            string path = "/", referrer = ""; try { using var doc = await JsonDocument.ParseAsync(ctx.Request.Body, cancellationToken: ct); if (doc.RootElement.TryGetProperty("path", out var v)) path = v.GetString() ?? "/"; if (doc.RootElement.TryGetProperty("referrer", out v)) referrer = v.GetString() ?? ""; } catch { }
            db.Visits.Add(new VisitEvent { UserId = ctx.User.Identity?.IsAuthenticated == true ? ctx.User.UserId() : null, VisitorId = visitor, IpAddress = ctx.Connection.RemoteIpAddress?.ToString() ?? "", Browser = browser, OperatingSystem = os, UserAgent = ua, Referrer = referrer, Path = path }); await db.SaveChangesAsync(ct); return Results.NoContent();
        }).AllowAnonymous();
    }

    private static void MapAdmin(WebApplication app)
    {
        var admin = app.MapGroup("/api/admin").RequireAuthorization(p => p.RequireRole(Roles.SuperAdmin));
        admin.MapGet("/settings", async (SettingsService s) => Results.Ok(new { dollarRateIrr = long.Parse(await s.Get("currency.usd_irr", "850000")), feePercent = decimal.Parse(await s.Get("billing.fee_percent", "10"), CultureInfo.InvariantCulture), smsApiKey = await s.Get("sms.api_key"), smsTemplateId = int.Parse(await s.Get("sms.template_id", "176898")), zarinpalMerchantId = await s.Get("zarinpal.merchant_id"), paymentCallbackUrl = await s.Get("payment.callback_url", "http://localhost:5050/api/wallet/callback") }));
        admin.MapPut("/settings", async (UpdateSettingsRequest req, SettingsService s) => { await s.Set("currency.usd_irr", req.DollarRateIrr.ToString(CultureInfo.InvariantCulture)); await s.Set("billing.fee_percent", req.FeePercent.ToString(CultureInfo.InvariantCulture)); await s.Set("sms.api_key", req.SmsApiKey, true); await s.Set("sms.template_id", req.SmsTemplateId.ToString()); await s.Set("zarinpal.merchant_id", req.ZarinpalMerchantId, true); await s.Set("payment.callback_url", req.PaymentCallbackUrl); return Results.NoContent(); });

        admin.MapGet("/providers", async (AppDbContext db, SecretProtector secrets, CancellationToken ct) => Results.Ok((await db.Providers.AsNoTracking().Include(x => x.Credentials).Include(x => x.Models).OrderBy(x => x.Name).ToListAsync(ct)).Select(x => new { x.Id, x.Name, x.Slug, x.LogoUrl, x.BaseUrl, x.PricingUrl, x.Protocol, x.IsActive, modelCount = x.Models.Count, credentials = x.Credentials.Select(c => new { c.Id, c.Label, apiKey = secrets.Unprotect(c.ProtectedApiKey), c.IsActive, c.InitialBalanceUsd, c.RemainingBalanceUsd, c.AlertThresholdUsd, c.RequestCount, c.LastUsedAtUtc, c.LastError, isLow = c.InitialBalanceUsd > 0 && c.RemainingBalanceUsd <= c.AlertThresholdUsd }) })));
        admin.MapPost("/providers", async (ProviderRequest req, AppDbContext db, CancellationToken ct) => { var p = new AiProvider { Name = req.Name, Slug = req.Slug, LogoUrl = req.LogoUrl, BaseUrl = req.BaseUrl, PricingUrl = req.PricingUrl, Protocol = req.Protocol, IsActive = req.IsActive }; db.Add(p); await db.SaveChangesAsync(ct); return Results.Ok(p); });
        admin.MapPut("/providers/{id:guid}", async (Guid id, ProviderRequest req, AppDbContext db, CancellationToken ct) => { var p = await db.Providers.FindAsync([id], ct); if (p is null) return Results.NotFound(); p.Name = req.Name; p.Slug = req.Slug; p.LogoUrl = req.LogoUrl; p.BaseUrl = req.BaseUrl; p.PricingUrl = req.PricingUrl; p.Protocol = req.Protocol; p.IsActive = req.IsActive; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapPost("/providers/{id:guid}/credentials", async (Guid id, CredentialRequest req, AppDbContext db, SecretProtector secrets, CancellationToken ct) => { if (!await db.Providers.AnyAsync(x => x.Id == id, ct)) return Results.NotFound(); var c = new ProviderCredential { ProviderId = id, Label = req.Label, ProtectedApiKey = secrets.Protect(req.ApiKey), IsActive = req.IsActive, InitialBalanceUsd = req.InitialBalanceUsd, RemainingBalanceUsd = req.RemainingBalanceUsd, AlertThresholdUsd = req.AlertThresholdUsd }; db.Add(c); await db.SaveChangesAsync(ct); return Results.Ok(new { c.Id }); });
        admin.MapPut("/credentials/{id:guid}/balance", async (Guid id, CredentialBalanceRequest req, AppDbContext db, CancellationToken ct) => { var c = await db.ProviderCredentials.FindAsync([id], ct); if (c is null) return Results.NotFound(); c.RemainingBalanceUsd = req.RemainingBalanceUsd; c.AlertThresholdUsd = req.AlertThresholdUsd; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapDelete("/credentials/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => { var c = await db.ProviderCredentials.FindAsync([id], ct); if (c is null) return Results.NotFound(); db.Remove(c); await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapPost("/credentials/{id:guid}/test", async (Guid id, AppDbContext db, SecretProtector secrets, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var c = await db.ProviderCredentials.Include(x => x.Provider).SingleOrDefaultAsync(x => x.Id == id, ct); if (c?.Provider is null) return Results.NotFound(); var model = await db.Models.FirstOrDefaultAsync(x => x.ProviderId == c.ProviderId && x.IsActive, ct); if (model is null) return Results.BadRequest(new { message = "مدل فعالی وجود ندارد." });
            using var request = new HttpRequestMessage(HttpMethod.Post, c.Provider.BaseUrl.TrimEnd('/') + "/chat/completions"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Unprotect(c.ProtectedApiKey)); request.Content = JsonContent.Create(new { model = model.ModelId, messages = new[] { new { role = "user", content = "Reply only with OK" } }, max_tokens = 8 }); var sw = System.Diagnostics.Stopwatch.StartNew(); var response = await clients.CreateClient("providers").SendAsync(request, ct); var text = await response.Content.ReadAsStringAsync(ct); c.LastError = response.IsSuccessStatusCode ? null : text[..Math.Min(500, text.Length)]; await db.SaveChangesAsync(ct); return Results.Ok(new { success = response.IsSuccessStatusCode, status = (int)response.StatusCode, latencyMs = sw.ElapsedMilliseconds, response = text[..Math.Min(1000, text.Length)] });
        });

        admin.MapGet("/models", async (AppDbContext db, CancellationToken ct) =>
        {
            var models = await db.Models.AsNoTracking().Include(x => x.Provider).OrderBy(x => x.Provider!.Name).ThenBy(x => x.DisplayName).ToListAsync(ct);
            return Results.Ok(models.Select(AdminModelView));
        });
        admin.MapPost("/models", async (ModelRequest req, AppDbContext db, CancellationToken ct) => { var m = ToModel(req); db.Add(m); await db.SaveChangesAsync(ct); return Results.Ok(m); });
        admin.MapPut("/models/{id:guid}", async (Guid id, ModelRequest req, AppDbContext db, CancellationToken ct) => { var m = await db.Models.FindAsync([id], ct); if (m is null) return Results.NotFound(); Apply(m, req); await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapDelete("/models/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => { var m = await db.Models.FindAsync([id], ct); if (m is null) return Results.NotFound(); m.IsActive = false; await db.SaveChangesAsync(ct); return Results.NoContent(); });

        admin.MapGet("/users", async (AppDbContext db, [FromQuery] string? search, [FromQuery] string? sort, CancellationToken ct) => { var q = db.Users.AsNoTracking().Include(x => x.ApiKeys).AsQueryable(); if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => x.Mobile.Contains(search) || x.DisplayName.Contains(search)); q = sort switch { "wallet" => q.OrderByDescending(x => (double)x.WalletUsd), "requests" => q.OrderByDescending(x => x.ApiKeys.Sum(k => k.RequestCount)), _ => q.OrderByDescending(x => x.CreatedAtUtc) }; return Results.Ok(await q.Select(x => new { x.Id, x.Mobile, x.DisplayName, x.Role, x.IsSuspended, x.WalletUsd, x.CreatedAtUtc, x.LastSeenAtUtc, apiKeyCount = x.ApiKeys.Count, requests = x.ApiKeys.Sum(k => k.RequestCount), spentUsd = x.ApiKeys.Sum(k => (double)k.SpentUsd) }).ToListAsync(ct)); });
        admin.MapGet("/users/{id:guid}/keys", async (Guid id, AppDbContext db, CancellationToken ct) => Results.Ok(await db.UserApiKeys.AsNoTracking().Where(x => x.UserId == id).OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, x.Name, x.KeyPrefix, x.IsActive, x.RequestLimit, x.SpendLimitUsd, x.RequestCount, x.SpentUsd, x.AccessMode, x.ModelRulesJson, x.CreatedAtUtc, x.LastUsedAtUtc }).ToListAsync(ct)));
        admin.MapPost("/users/{id:guid}/suspend", async (Guid id, SuspendRequest req, AppDbContext db, CancellationToken ct) => { var u = await db.Users.FindAsync([id], ct); if (u is null) return Results.NotFound(); if (u.Mobile == "09015909044") return Results.BadRequest(new { message = "سوپرادمین قابل تعلیق نیست." }); u.IsSuspended = req.IsSuspended; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapPost("/user-keys/{id:guid}/suspend", async (Guid id, SuspendRequest req, AppDbContext db, CancellationToken ct) => { var key = await db.UserApiKeys.FindAsync([id], ct); if (key is null) return Results.NotFound(); key.IsActive = !req.IsSuspended; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapDelete("/user-keys/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => { var key = await db.UserApiKeys.FindAsync([id], ct); if (key is null) return Results.NotFound(); db.UserApiKeys.Remove(key); await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapDelete("/users/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => { var user = await db.Users.Include(x => x.ApiKeys).SingleOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound(); if (user.Mobile == "09015909044") return Results.BadRequest(new { message = "سوپرادمین قابل حذف نیست." }); db.Users.Remove(user); await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapPost("/users/{id:guid}/wallet", async (Guid id, WalletAdjustRequest req, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, CancellationToken ct) => { var u = await db.Users.FindAsync([id], ct); if (u is null) return Results.NotFound(); if (u.WalletUsd + req.AmountUsd < 0) return Results.BadRequest(new { message = "موجودی نمی‌تواند منفی شود." }); u.WalletUsd += req.AmountUsd; db.WalletTransactions.Add(new WalletTransaction { UserId = id, Type = "admin_adjustment", AmountUsd = req.AmountUsd, Status = "completed", Description = req.Description, CompletedAtUtc = DateTime.UtcNow }); db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId(), Action = "wallet.adjust", EntityType = "user", EntityId = id.ToString(), DetailsJson = JsonSerializer.Serialize(req) }); await db.SaveChangesAsync(ct); return Results.Ok(new { u.WalletUsd }); });
        admin.MapPost("/users/{id:guid}/impersonate", async (Guid id, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, TokenService tokens, CancellationToken ct) => { var u = await db.Users.FindAsync([id], ct); return u is null ? Results.NotFound() : Results.Ok(new { token = tokens.Create(u, actor.UserId()), user = UserView(u) }); });

        admin.MapGet("/tickets", async (AppDbContext db, [FromQuery] string? search, [FromQuery] string? status, [FromQuery] string? priority, CancellationToken ct) =>
        {
            var q = db.SupportTickets.AsNoTracking().Include(x => x.User).Include(x => x.Messages).AsQueryable();
            if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); q = q.Where(x => x.ReferenceCode.Contains(term) || x.Subject.Contains(term) || x.User!.Mobile.Contains(term) || x.User.DisplayName.Contains(term)); }
            if (!string.IsNullOrWhiteSpace(status) && ValidTicketStatuses.Contains(status)) q = q.Where(x => x.Status == status);
            if (!string.IsNullOrWhiteSpace(priority) && TicketPriorities.Contains(priority)) q = q.Where(x => x.Priority == priority);
            var tickets = await q.OrderBy(x => x.Status == "closed" || x.Status == "resolved").ThenByDescending(x => x.Priority == "urgent").ThenByDescending(x => x.UpdatedAtUtc).Take(300).ToListAsync(ct);
            var allCounts = await db.SupportTickets.AsNoTracking().GroupBy(x => x.Status).Select(g => new { g.Key, Count = g.Count() }).ToListAsync(ct);
            return Results.Ok(new { items = tickets.Select(x => TicketSummary(x, true)), counts = allCounts.ToDictionary(x => x.Key, x => x.Count) });
        });
        admin.MapGet("/tickets/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) =>
        {
            var ticket = await db.SupportTickets.AsNoTracking().Include(x => x.User).Include(x => x.Messages).SingleOrDefaultAsync(x => x.Id == id, ct);
            return ticket is null ? Results.NotFound() : Results.Ok(TicketDetails(ticket, true));
        });
        admin.MapPost("/tickets/{id:guid}/messages", async (Guid id, TicketMessageRequest req, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, CancellationToken ct) =>
        {
            var ticket = await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id, ct); if (ticket is null) return Results.NotFound();
            var body = Clean(req.Message, 5000); if (body.Length < 2) return Results.BadRequest(new { message = "متن پاسخ نمی‌تواند خالی باشد." });
            var now = DateTime.UtcNow; db.TicketMessages.Add(new TicketMessage { TicketId = ticket.Id, AuthorUserId = actor.UserId(), IsStaff = true, Body = body, CreatedAtUtc = now }); ticket.Status = "waiting_user"; ticket.UpdatedAtUtc = now; ticket.LastReplyAtUtc = now; ticket.ClosedAtUtc = null; db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId(), Action = "ticket.reply", EntityType = "support_ticket", EntityId = id.ToString() }); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        admin.MapPut("/tickets/{id:guid}", async (Guid id, UpdateTicketRequest req, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, CancellationToken ct) =>
        {
            var ticket = await db.SupportTickets.SingleOrDefaultAsync(x => x.Id == id, ct); if (ticket is null) return Results.NotFound();
            var status = ValidTicketStatuses.Contains(req.Status) ? req.Status : ticket.Status; var priority = TicketPriorities.Contains(req.Priority) ? req.Priority : ticket.Priority; var now = DateTime.UtcNow; ticket.Status = status; ticket.Priority = priority; ticket.UpdatedAtUtc = now; ticket.ClosedAtUtc = status is "closed" or "resolved" ? now : null; db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId(), Action = "ticket.update", EntityType = "support_ticket", EntityId = id.ToString(), DetailsJson = JsonSerializer.Serialize(new { status, priority }) }); await db.SaveChangesAsync(ct); return Results.NoContent();
        });

        admin.MapGet("/dashboard", async (AppDbContext db, CancellationToken ct) =>
        {
            var now = DateTime.UtcNow; var today = now.Date; var month = today.AddDays(-29); var usages = db.UsageRecords.AsNoTracking(); var visits = db.Visits.AsNoTracking();
            var timeline = await usages.Where(x => x.CreatedAtUtc >= month).GroupBy(x => x.CreatedAtUtc.Date).Select(g => new { date = g.Key, requests = g.Count(), tokens = g.Sum(x => x.InputTokens + x.OutputTokens), revenueUsd = g.Sum(x => (double)x.CostUsd) }).OrderBy(x => x.date).ToListAsync(ct);
            var models = await usages.Where(x => x.CreatedAtUtc >= month).GroupBy(x => x.ModelName).Select(g => new { model = g.Key, tokens = g.Sum(x => x.InputTokens + x.OutputTokens), requests = g.Count(), revenueUsd = g.Sum(x => (double)x.CostUsd) }).OrderByDescending(x => x.tokens).Take(10).ToListAsync(ct);
            var lowKeys = await db.ProviderCredentials.Include(x => x.Provider).Where(x => x.IsActive && x.InitialBalanceUsd > 0 && x.RemainingBalanceUsd <= x.AlertThresholdUsd).Select(x => new { x.Id, x.Label, provider = x.Provider!.Name, x.RemainingBalanceUsd, x.AlertThresholdUsd }).ToListAsync(ct);
            return Results.Ok(new { users = await db.Users.CountAsync(ct), online = await db.Users.CountAsync(x => x.LastSeenAtUtc >= now.AddMinutes(-5), ct), walletLiabilityUsd = await db.Users.SumAsync(x => (double)x.WalletUsd, ct), todayRequests = await usages.CountAsync(x => x.CreatedAtUtc >= today, ct), todayTokens = await usages.Where(x => x.CreatedAtUtc >= today).SumAsync(x => x.InputTokens + x.OutputTokens, ct), todayRevenueUsd = await usages.Where(x => x.CreatedAtUtc >= today).SumAsync(x => (double)x.CostUsd, ct), uniqueToday = await visits.Where(x => x.CreatedAtUtc >= today).Select(x => x.VisitorId).Distinct().CountAsync(ct), visitsToday = await visits.CountAsync(x => x.CreatedAtUtc >= today, ct), totalVisits = await visits.CountAsync(ct), timeline, models, lowKeys });
        });
        admin.MapGet("/visits", async (AppDbContext db, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken ct) => { page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 10, 100); var q = db.Visits.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc); return Results.Ok(new { total = await q.CountAsync(ct), items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct), browsers = await db.Visits.GroupBy(x => x.Browser).Select(g => new { name = g.Key, value = g.Count() }).ToListAsync(ct), systems = await db.Visits.GroupBy(x => x.OperatingSystem).Select(g => new { name = g.Key, value = g.Count() }).ToListAsync(ct) }); });
    }

    private static void MapGateway(WebApplication app)
    {
        app.MapGet("/v1/models", async (HttpRequest request, ApiKeyAuthenticator auth, AppDbContext db, CancellationToken ct) => { if (await auth.Authenticate(request, ct) is null) return Results.Json(new { error = new { message = "Invalid API key" } }, statusCode: 401); var items = await db.Models.Where(x => x.IsActive && x.Provider!.IsActive).Select(x => new { id = x.ModelId, @object = "model", created = new DateTimeOffset(x.PriceSyncedAtUtc).ToUnixTimeSeconds(), owned_by = x.Provider!.Slug }).ToListAsync(ct); return Results.Ok(new { @object = "list", data = items }); });
        app.MapPost("/v1/chat/completions", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardChat(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/audio/speech", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardSpeech(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/audio/transcriptions", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardTranscription(ctx, ct)).DisableAntiforgery();
        app.Map("/v1/realtime", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardRealtime(ctx, ct));
    }

    private static object UserView(AppUser x) => new { x.Id, x.Mobile, x.DisplayName, x.Role, x.WalletUsd, x.IsSuspended, x.CreatedAtUtc, x.LastSeenAtUtc };
    private static readonly HashSet<string> TicketCategories = ["technical", "billing", "account", "models", "general"];
    private static readonly HashSet<string> TicketPriorities = ["low", "normal", "high", "urgent"];
    private static readonly HashSet<string> ValidTicketStatuses = ["open", "waiting_support", "waiting_user", "resolved", "closed"];
    private static string Clean(string? value, int max) { var text = (value ?? "").Trim(); return text[..Math.Min(max, text.Length)]; }
    private static string ValidTicketCategory(string value) => TicketCategories.Contains(value) ? value : "general";
    private static string ValidTicketPriority(string value) => TicketPriorities.Contains(value) ? value : "normal";
    private static object TicketSummary(SupportTicket x, bool includeUser) => new { x.Id, x.ReferenceCode, x.Subject, x.Category, x.Priority, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.LastReplyAtUtc, x.ClosedAtUtc, messageCount = x.Messages.Count, lastMessage = x.Messages.OrderByDescending(m => m.CreatedAtUtc).Select(m => m.Body).FirstOrDefault(), user = includeUser && x.User is not null ? new { x.User.Id, x.User.DisplayName, x.User.Mobile } : null };
    private static object TicketDetails(SupportTicket x, bool includeUser) => new { x.Id, x.ReferenceCode, x.Subject, x.Category, x.Priority, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.LastReplyAtUtc, x.ClosedAtUtc, user = includeUser && x.User is not null ? new { x.User.Id, x.User.DisplayName, x.User.Mobile } : null, messages = x.Messages.OrderBy(m => m.CreatedAtUtc).Select(m => new { m.Id, m.IsStaff, m.Body, m.CreatedAtUtc }) };
    private static Dictionary<string, int> TicketCounts(IEnumerable<SupportTicket> tickets) => tickets.GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count());
    private static string ValidAccessMode(string value) => value is "allow" or "deny" ? value : "all";
    private static string? NormalizeMobile(string value) { var digits = new string(value.Where(char.IsDigit).ToArray()); if (digits.StartsWith("98") && digits.Length == 12) digits = "0" + digits[2..]; if (digits.Length == 10 && digits.StartsWith('9')) digits = "0" + digits; return digits.Length == 11 && digits.StartsWith("09") ? digits : null; }
    private static AiModel ToModel(ModelRequest r) { var m = new AiModel(); Apply(m, r); return m; }
    private static void Apply(AiModel m, ModelRequest r) { m.ProviderId = r.ProviderId; m.ModelId = r.ModelId; m.DisplayName = r.DisplayName; m.Modality = r.Modality; m.InputPricePerMillionUsd = r.InputPricePerMillionUsd; m.OutputPricePerMillionUsd = r.OutputPricePerMillionUsd; m.CachedInputPricePerMillionUsd = r.CachedInputPricePerMillionUsd; m.ContextWindow = r.ContextWindow; m.SupportsStreaming = r.SupportsStreaming; m.SupportsWebSocket = r.SupportsWebSocket; m.IsActive = r.IsActive; m.PricingSourceUrl = r.PricingSourceUrl; m.TestPayloadJson = r.TestPayloadJson; m.ServiceType = Clean(r.ServiceType, 50) is { Length: > 0 } serviceType ? serviceType : "chat"; m.EndpointPath = Clean(r.EndpointPath, 200) is { Length: > 0 } endpoint ? endpoint : "/v1/chat/completions"; m.Region = Clean(r.Region, 80) is { Length: > 0 } region ? region : "global"; m.IsPreview = r.IsPreview; m.PricingDetailsJson = JsonSerializer.Serialize(r.PricingComponents ?? []); m.PricingNotes = Clean(r.PricingNotes, 1000); m.PriceSyncedAtUtc = DateTime.UtcNow; }
    private static PricingComponentRequest[] PricingComponents(AiModel m)
    {
        try
        {
            var saved = JsonSerializer.Deserialize<PricingComponentRequest[]>(m.PricingDetailsJson, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (saved is { Length: > 0 }) return saved;
        }
        catch (JsonException) { }
        var prices = new List<PricingComponentRequest>();
        prices.Add(new("ورودی متن", "million_text_tokens", m.InputPricePerMillionUsd, null));
        prices.Add(new("خروجی متن", "million_text_tokens", m.OutputPricePerMillionUsd, null));
        if (m.CachedInputPricePerMillionUsd is not null) prices.Add(new("ورودی Cache", "million_text_tokens", m.CachedInputPricePerMillionUsd, null));
        return prices.ToArray();
    }
    private static object ModelView(AiModel x) => new { x.Id, x.ProviderId, x.ModelId, x.DisplayName, x.Modality, x.ServiceType, x.EndpointPath, x.Region, x.IsPreview, pricingComponents = PricingComponents(x), x.PricingNotes, x.InputPricePerMillionUsd, x.OutputPricePerMillionUsd, x.CachedInputPricePerMillionUsd, x.ContextWindow, x.SupportsStreaming, x.SupportsWebSocket, x.PriceSyncedAtUtc, x.PricingSourceUrl, x.TestPayloadJson, provider = new { x.ProviderId, x.Provider!.Name, x.Provider.Slug, x.Provider.LogoUrl } };
    private static object AdminModelView(AiModel x) => new { x.Id, x.ProviderId, providerName = x.Provider!.Name, x.ModelId, x.DisplayName, x.Modality, x.ServiceType, x.EndpointPath, x.Region, x.IsPreview, pricingComponents = PricingComponents(x), x.PricingNotes, x.InputPricePerMillionUsd, x.OutputPricePerMillionUsd, x.CachedInputPricePerMillionUsd, x.ContextWindow, x.SupportsStreaming, x.SupportsWebSocket, x.IsActive, x.PriceSyncedAtUtc, x.PricingSourceUrl, x.TestPayloadJson };
}
