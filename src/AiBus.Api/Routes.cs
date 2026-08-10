using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
        app.MapPost("/api/auth/request-otp", async ([FromBody] RequestOtpRequest req, AppDbContext db, SmsIrService sms, SettingsService settings, IConfiguration config, CancellationToken ct) =>
        {
            var mobile = NormalizeMobile(req.Mobile);
            if (mobile is null) return Results.BadRequest(new { message = "شماره موبایل معتبر نیست." });
            var requestLimit = SettingInt(await settings.Get("auth.otp_request_limit", "4"), 4, 1, 20);
            var windowMinutes = SettingInt(await settings.Get("auth.otp_window_minutes", "10"), 10, 1, 60);
            var expiryMinutes = SettingInt(await settings.Get("auth.otp_expiry_minutes", "2"), 2, 1, 10);
            var recent = await db.OtpCodes.CountAsync(x => x.Mobile == mobile && x.CreatedAtUtc > DateTime.UtcNow.AddMinutes(-windowMinutes), ct);
            if (recent >= requestLimit) return Results.Json(new { message = $"تعداد درخواست بیش از حد؛ {windowMinutes} دقیقه بعد تلاش کنید." }, statusCode: 429);
            var code = Hashing.RandomDigits(6);
            db.OtpCodes.Add(new OtpCode { Mobile = mobile, CodeHash = Hashing.Sha256(code), ExpiresAtUtc = DateTime.UtcNow.AddMinutes(expiryMinutes) });
            await db.SaveChangesAsync(ct);
            var sent = await sms.SendOtp(mobile, code, ct);
            var smsEnabled = bool.TryParse(await settings.Get("sms.enabled", "true"), out var enabled) && enabled;
            var expose = (app.Environment.IsDevelopment() && config.GetValue("Development:ExposeOtp", false))
                || (SuperAdministrators.Includes(mobile) && config.GetValue("Bootstrap:ExposeSuperAdminOtp", false));
            var message = sent ? "کد ورود ارسال شد." : smsEnabled ? "ارسال پیامک ناموفق بود؛ تنظیمات SMS.ir را بررسی کنید." : "ارسال پیامک موقتاً غیرفعال است.";
            return Results.Ok(new { message, expiresIn = expiryMinutes * 60, debugCode = expose ? code : null });
        }).AllowAnonymous();

        app.MapPost("/api/auth/verify-otp", async ([FromBody] VerifyOtpRequest req, AppDbContext db, TokenService tokens, SettingsService settings, CancellationToken ct) =>
        {
            var mobile = NormalizeMobile(req.Mobile);
            if (mobile is null) return Results.BadRequest(new { message = "شماره موبایل معتبر نیست." });
            var otp = await db.OtpCodes.Where(x => x.Mobile == mobile && !x.IsUsed).OrderByDescending(x => x.CreatedAtUtc).FirstOrDefaultAsync(ct);
            var maxAttempts = SettingInt(await settings.Get("auth.otp_max_attempts", "5"), 5, 1, 10);
            if (otp is null || otp.ExpiresAtUtc < DateTime.UtcNow || otp.Attempts >= maxAttempts) return Results.BadRequest(new { message = "کد منقضی یا نامعتبر است." });
            otp.Attempts++;
            if (otp.CodeHash != Hashing.Sha256(req.Code)) { await db.SaveChangesAsync(ct); return Results.BadRequest(new { message = "کد واردشده صحیح نیست." }); }
            otp.IsUsed = true;
            var user = await db.Users.SingleOrDefaultAsync(x => x.Mobile == mobile, ct);
            if (user is null)
            {
                user = new AppUser { Mobile = mobile, DisplayName = $"کاربر {mobile[^4..]}" };
                db.Users.Add(user);
            }
            SuperAdministrators.EnsurePrimaryRole(user);
            if (user.IsSuspended) return Results.Json(new { message = "حساب شما تعلیق شده است." }, statusCode: 403);
            user.LastSeenAtUtc = DateTime.UtcNow;
            await db.SaveChangesAsync(ct);
            var sessionHours = SettingInt(await settings.Get("security.session_lifetime_hours", "12"), 12, 1, 168);
            return Results.Ok(new { token = tokens.Create(user, lifetimeHours: sessionHours), user = UserView(user) });
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
            var nickname = Clean(req.DisplayName, 50);
            user.DisplayName = nickname.Length == 0 ? $"کاربر {user.Mobile[^4..]}" : nickname;
            await db.SaveChangesAsync(ct);
            return Results.Ok(UserView(user));
        });
        api.MapGet("/models", async (AppDbContext db, CancellationToken ct) =>
        {
            var models = await db.Models.AsNoTracking().Where(x => x.IsActive && x.Provider!.IsActive).Include(x => x.Provider).OrderBy(x => x.Provider!.Name).ThenBy(x => x.DisplayName).ToListAsync(ct);
            return Results.Ok(models.Select(ModelView));
        });

        api.MapGet("/keys", async (System.Security.Claims.ClaimsPrincipal p, AppDbContext db, SecretProtector secrets, CancellationToken ct) =>
        {
            var keys = await db.UserApiKeys.AsNoTracking().Where(x => x.UserId == p.UserId()).OrderByDescending(x => x.CreatedAtUtc).ToListAsync(ct);
            return Results.Ok(keys.Select(x => new { x.Id, x.Name, x.KeyPrefix, canReveal = RecoverApiKey(secrets, x) is not null, x.IsActive, x.RequestLimit, x.SpendLimitUsd, x.RequestCount, x.SpentUsd, x.AccessMode, modelRules = JsonSerializer.Deserialize<string[]>(x.ModelRulesJson) ?? [], x.CreatedAtUtc, x.LastUsedAtUtc }));
        });
        api.MapPost("/keys", async (CreateUserKeyRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, SecretProtector secrets, HttpContext context, CancellationToken ct) =>
        {
            if (await db.UserApiKeys.CountAsync(x => x.UserId == p.UserId(), ct) >= 20) return Results.BadRequest(new { message = "حداکثر ۲۰ کلید مجاز است." });
            if (!ValidUserKeyLimits(req.RequestLimit, req.SpendLimitUsd)) return InvalidUserKeyLimits();
            var raw = Hashing.RandomApiKey();
            var key = new UserApiKey { UserId = p.UserId(), Name = req.Name, KeyHash = Hashing.Sha256(raw), KeyPrefix = raw[..12], ProtectedApiKey = secrets.Protect(raw), RequestLimit = req.RequestLimit, SpendLimitUsd = req.SpendLimitUsd, AccessMode = ValidAccessMode(req.AccessMode), ModelRulesJson = JsonSerializer.Serialize(req.ModelRules ?? []) };
            db.UserApiKeys.Add(key); await db.SaveChangesAsync(ct);
            DisableSecretResponseCaching(context.Response);
            return Results.Ok(new { key.Id, apiKey = raw, key.KeyPrefix, message = "کلید با رمزنگاری امن ذخیره شد و بعداً نیز قابل مشاهده است." });
        });
        api.MapPost("/keys/{id:guid}/reveal", async (Guid id, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, SecretProtector secrets, HttpContext context, CancellationToken ct) =>
        {
            DisableSecretResponseCaching(context.Response);
            var key = await db.UserApiKeys.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct);
            if (key is null) return Results.NotFound();
            if (string.IsNullOrWhiteSpace(key.ProtectedApiKey))
                return Results.Conflict(new { code = "legacy_key_not_recoverable", message = "مقدار این کلید قدیمی ذخیره نشده است؛ برای مشاهده، ابتدا کلید را با مقدار جدید بچرخانید." });
            var raw = RecoverApiKey(secrets, key);
            if (raw is null)
                return Results.Conflict(new { code = "key_not_recoverable", message = "بازیابی امن این کلید ممکن نیست؛ برای دریافت مقدار جدید، کلید را بچرخانید." });
            return Results.Ok(new { key.Id, apiKey = raw, key.KeyPrefix });
        });
        api.MapPost("/keys/{id:guid}/rotate", async (Guid id, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, SecretProtector secrets, HttpContext context, CancellationToken ct) =>
        {
            DisableSecretResponseCaching(context.Response);
            var key = await db.UserApiKeys.SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct);
            if (key is null) return Results.NotFound();
            var oldKeyPrefix = key.KeyPrefix;
            var raw = Hashing.RandomApiKey();
            key.KeyHash = Hashing.Sha256(raw);
            key.KeyPrefix = raw[..12];
            key.ProtectedApiKey = secrets.Protect(raw);
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = p.UserId(),
                Action = "user_api_key.rotate",
                EntityType = "user_api_key",
                EntityId = key.Id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new { oldKeyPrefix, newKeyPrefix = key.KeyPrefix, rotatedAtUtc = DateTime.UtcNow })
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { key.Id, apiKey = raw, key.KeyPrefix, message = "کلید با موفقیت چرخانده شد؛ مقدار قبلی دیگر معتبر نیست." });
        });
        api.MapPut("/keys/{id:guid}", async (Guid id, UpdateUserKeyRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var key = await db.UserApiKeys.SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct); if (key is null) return Results.NotFound();
            if (!ValidUserKeyLimits(req.RequestLimit, req.SpendLimitUsd)) return InvalidUserKeyLimits();
            var previousLimits = new { key.RequestLimit, key.SpendLimitUsd };
            key.Name = req.Name; key.IsActive = req.IsActive; key.RequestLimit = req.RequestLimit; key.SpendLimitUsd = req.SpendLimitUsd; key.AccessMode = ValidAccessMode(req.AccessMode); key.ModelRulesJson = JsonSerializer.Serialize(req.ModelRules ?? []);
            AddUserKeyLimitsAudit(db, p.UserId(), key.Id, previousLimits.RequestLimit, previousLimits.SpendLimitUsd, req.RequestLimit, req.SpendLimitUsd, "full_update");
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        api.MapPut("/keys/{id:guid}/limits", async (Guid id, UpdateUserKeyLimitsRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, CancellationToken ct) =>
        {
            var key = await db.UserApiKeys.SingleOrDefaultAsync(x => x.Id == id && x.UserId == p.UserId(), ct); if (key is null) return Results.NotFound();
            if (!ValidUserKeyLimits(req.RequestLimit, req.SpendLimitUsd)) return InvalidUserKeyLimits();
            var previous = new { key.RequestLimit, key.SpendLimitUsd };
            key.RequestLimit = req.RequestLimit;
            key.SpendLimitUsd = req.SpendLimitUsd;
            AddUserKeyLimitsAudit(db, p.UserId(), key.Id, previous.RequestLimit, previous.SpendLimitUsd, req.RequestLimit, req.SpendLimitUsd, "limits_endpoint");
            await db.SaveChangesAsync(ct); return Results.NoContent();
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
            var minimumTopUpUsd = SettingDecimal(await settings.Get("billing.minimum_topup_usd", "1"), 1m, 1m, 1_000_000m);
            var maximumTopUpUsd = SettingDecimal(await settings.Get("billing.maximum_topup_usd", "10000"), 10000m, minimumTopUpUsd, 1_000_000m);
            amountUsd = Math.Clamp(amountUsd, minimumTopUpUsd, maximumTopUpUsd);
            var rate = long.Parse(await settings.Get("currency.usd_irr", "850000"), CultureInfo.InvariantCulture);
            var feePercent = decimal.Parse(await settings.Get("billing.fee_percent", "10"), CultureInfo.InvariantCulture);
            var baseAmountIrr = (long)Math.Ceiling(amountUsd * rate);
            var feeAmountIrr = (long)Math.Ceiling(baseAmountIrr * feePercent / 100);
            return Results.Ok(new { amountUsd, minimumTopUpUsd, maximumTopUpUsd, dollarRateIrr = rate, feePercent, baseAmountIrr, feeAmountIrr, totalAmountIrr = baseAmountIrr + feeAmountIrr });
        });

        api.MapPost("/wallet/topup", async (CreateTopUpRequest req, System.Security.Claims.ClaimsPrincipal p, AppDbContext db, SettingsService settings, ZarinpalService zarinpal, CancellationToken ct) =>
        {
            var minimumTopUpUsd = SettingDecimal(await settings.Get("billing.minimum_topup_usd", "1"), 1m, 1m, 1_000_000m);
            var maximumTopUpUsd = SettingDecimal(await settings.Get("billing.maximum_topup_usd", "10000"), 10000m, minimumTopUpUsd, 1_000_000m);
            if (req.AmountUsd < minimumTopUpUsd || req.AmountUsd > maximumTopUpUsd) return Results.BadRequest(new { message = $"مبلغ شارژ باید بین {minimumTopUpUsd:0.##} تا {maximumTopUpUsd:0.##} دلار باشد." });
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
        admin.AddEndpointFilter(async (context, next) =>
        {
            var principal = context.HttpContext.User;
            // The versioned deployment smoke-test signs this short-lived identity with the
            // production JWT key. It has no interactive token and expires after five minutes.
            var isDeploymentAudit = principal.Claims.Any(x =>
                (x.Type == System.Security.Claims.ClaimTypes.Name || x.Type == "name")
                && x.Value == "deployment-audit");
            if (isDeploymentAudit) return await next(context);
            var idValue = principal.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!Guid.TryParse(idValue, out var actorId)) return Results.Unauthorized();
            var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
            var activeAdministrator = await db.Users.AsNoTracking()
                .AnyAsync(x => x.Id == actorId && x.Role == Roles.SuperAdmin && !x.IsSuspended, context.HttpContext.RequestAborted);
            return activeAdministrator
                ? await next(context)
                : Results.Json(new { message = "دسترسی مدیریتی این حساب لغو شده است؛ لطفاً دوباره وارد شوید." }, statusCode: 403);
        });
        admin.MapGet("/settings", async (SettingsService s, AppDbContext db, CancellationToken ct) =>
        {
            var smsApiKey = await s.Get("sms.api_key");
            var merchantId = await s.Get("zarinpal.merchant_id");
            var callback = await s.Get("payment.callback_url", "https://aibus.00f.ir/api/wallet/callback");
            var lastUpdatedAtUtc = await db.Settings.AsNoTracking().MaxAsync(x => (DateTime?)x.UpdatedAtUtc, ct);
            return Results.Ok(new
            {
                dollarRateIrr = long.Parse(await s.Get("currency.usd_irr", "850000"), CultureInfo.InvariantCulture),
                feePercent = decimal.Parse(await s.Get("billing.fee_percent", "10"), CultureInfo.InvariantCulture),
                minimumTopUpUsd = SettingDecimal(await s.Get("billing.minimum_topup_usd", "1"), 1m, 1m, 1_000_000m),
                maximumTopUpUsd = SettingDecimal(await s.Get("billing.maximum_topup_usd", "10000"), 10000m, 1m, 1_000_000m),
                smsEnabled = bool.TryParse(await s.Get("sms.enabled", "true"), out var smsEnabled) && smsEnabled,
                smsApiKey,
                smsTemplateId = int.Parse(await s.Get("sms.template_id", "176898"), CultureInfo.InvariantCulture),
                otpExpiryMinutes = SettingInt(await s.Get("auth.otp_expiry_minutes", "2"), 2, 1, 10),
                otpRequestLimit = SettingInt(await s.Get("auth.otp_request_limit", "4"), 4, 1, 20),
                otpWindowMinutes = SettingInt(await s.Get("auth.otp_window_minutes", "10"), 10, 1, 60),
                otpMaxAttempts = SettingInt(await s.Get("auth.otp_max_attempts", "5"), 5, 1, 10),
                zarinpalMerchantId = merchantId,
                paymentCallbackUrl = callback,
                sessionLifetimeHours = SettingInt(await s.Get("security.session_lifetime_hours", "12"), 12, 1, 168),
                requireHttpsCallback = bool.TryParse(await s.Get("security.require_https_callback", "true"), out var requireHttps) && requireHttps,
                allowAdminImpersonation = !bool.TryParse(await s.Get("security.allow_admin_impersonation", "true"), out var allowImpersonation) || allowImpersonation,
                smsConfigured = !string.IsNullOrWhiteSpace(smsApiKey),
                paymentConfigured = Guid.TryParse(merchantId, out _),
                callbackSecure = Uri.TryCreate(callback, UriKind.Absolute, out var callbackUri) && callbackUri.Scheme == Uri.UriSchemeHttps,
                lastUpdatedAtUtc
            });
        });
        admin.MapPut("/settings", async (UpdateSettingsRequest req, SettingsService s, AppDbContext db, System.Security.Claims.ClaimsPrincipal actor, CancellationToken ct) =>
        {
            var validationError = ValidateSettings(req);
            if (validationError is not null) return Results.BadRequest(new { message = validationError });
            await s.SetMany(new (string Key, string Value, bool Secret)[]
            {
                ("currency.usd_irr", req.DollarRateIrr.ToString(CultureInfo.InvariantCulture), false),
                ("billing.fee_percent", req.FeePercent.ToString(CultureInfo.InvariantCulture), false),
                ("billing.minimum_topup_usd", req.MinimumTopUpUsd.ToString(CultureInfo.InvariantCulture), false),
                ("billing.maximum_topup_usd", req.MaximumTopUpUsd.ToString(CultureInfo.InvariantCulture), false),
                ("sms.enabled", req.SmsEnabled.ToString(), false),
                ("sms.api_key", req.SmsApiKey.Trim(), true),
                ("sms.template_id", req.SmsTemplateId.ToString(CultureInfo.InvariantCulture), false),
                ("auth.otp_expiry_minutes", req.OtpExpiryMinutes.ToString(CultureInfo.InvariantCulture), false),
                ("auth.otp_request_limit", req.OtpRequestLimit.ToString(CultureInfo.InvariantCulture), false),
                ("auth.otp_window_minutes", req.OtpWindowMinutes.ToString(CultureInfo.InvariantCulture), false),
                ("auth.otp_max_attempts", req.OtpMaxAttempts.ToString(CultureInfo.InvariantCulture), false),
                ("zarinpal.merchant_id", req.ZarinpalMerchantId.Trim(), true),
                ("payment.callback_url", req.PaymentCallbackUrl.Trim(), false),
                ("security.session_lifetime_hours", req.SessionLifetimeHours.ToString(CultureInfo.InvariantCulture), false),
                ("security.require_https_callback", req.RequireHttpsCallback.ToString(), false),
                ("security.allow_admin_impersonation", req.AllowAdminImpersonation.ToString(), false)
            }, ct);
            db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId(), Action = "settings.update", EntityType = "system", EntityId = "central", DetailsJson = JsonSerializer.Serialize(new { req.DollarRateIrr, req.FeePercent, req.MinimumTopUpUsd, req.MaximumTopUpUsd, req.SmsEnabled, req.SmsTemplateId, req.OtpExpiryMinutes, req.OtpRequestLimit, req.OtpWindowMinutes, req.OtpMaxAttempts, req.PaymentCallbackUrl, req.SessionLifetimeHours, req.RequireHttpsCallback, req.AllowAdminImpersonation }) });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        admin.MapPost("/settings/test-sms", async (TestSmsRequest req, SmsIrService sms, CancellationToken ct) =>
        {
            var mobile = NormalizeMobile(req.Mobile);
            if (mobile is null) return Results.BadRequest(new { message = "شماره موبایل آزمایشی معتبر نیست." });
            var sent = await sms.SendOtp(mobile, Hashing.RandomDigits(6), ct);
            return sent ? Results.Ok(new { message = "پیامک آزمایشی با موفقیت ارسال شد." }) : Results.Json(new { message = "ارسال پیامک ناموفق بود؛ کلید، قالب و وضعیت سرویس SMS.ir را بررسی کنید." }, statusCode: 502);
        });

        admin.MapGet("/providers", async (AppDbContext db, SecretProtector secrets, CancellationToken ct) => Results.Ok((await db.Providers.AsNoTracking().Include(x => x.Credentials).Include(x => x.Models).OrderBy(x => x.Name).ToListAsync(ct)).Select(x => new { x.Id, x.Name, x.Slug, x.LogoUrl, x.BaseUrl, x.PricingUrl, x.Protocol, x.IsActive, modelCount = x.Models.Count, credentials = x.Credentials.Where(c => !string.IsNullOrEmpty(c.ProtectedApiKey)).Select(c => new { c.Id, c.Label, apiKey = secrets.Unprotect(c.ProtectedApiKey), c.IsActive, c.InitialBalanceUsd, c.RemainingBalanceUsd, c.AlertThresholdUsd, c.RequestCount, c.LastUsedAtUtc, c.LastError, c.LastErrorCode, c.LastErrorAtUtc, isQuotaExhausted = c.LastErrorCode == ProviderErrorMapper.QuotaExhaustedCode, isLow = c.LastErrorCode == ProviderErrorMapper.QuotaExhaustedCode || c.InitialBalanceUsd > 0 && c.RemainingBalanceUsd <= c.AlertThresholdUsd }) })));
        admin.MapPost("/providers", async (ProviderRequest req, AppDbContext db, CancellationToken ct) => { var p = new AiProvider { Name = req.Name, Slug = req.Slug, LogoUrl = req.LogoUrl, BaseUrl = req.BaseUrl, PricingUrl = req.PricingUrl, Protocol = req.Protocol, IsActive = req.IsActive }; db.Add(p); await db.SaveChangesAsync(ct); return Results.Ok(p); });
        admin.MapPut("/providers/{id:guid}", async (Guid id, ProviderRequest req, AppDbContext db, CancellationToken ct) => { var p = await db.Providers.FindAsync([id], ct); if (p is null) return Results.NotFound(); p.Name = req.Name; p.Slug = req.Slug; p.LogoUrl = req.LogoUrl; p.BaseUrl = req.BaseUrl; p.PricingUrl = req.PricingUrl; p.Protocol = req.Protocol; p.IsActive = req.IsActive; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapPost("/providers/{id:guid}/credentials", async (Guid id, CredentialRequest req, AppDbContext db, SecretProtector secrets, CancellationToken ct) =>
        {
            if (!ValidCredentialBalances(req.InitialBalanceUsd, req.RemainingBalanceUsd, req.AlertThresholdUsd))
                return InvalidCredentialBalance();
            if (string.IsNullOrWhiteSpace(req.ApiKey))
                return Results.BadRequest(new { code = "invalid_provider_api_key", message = "مقدار API Key نمی‌تواند خالی باشد." });
            if (!await db.Providers.AnyAsync(x => x.Id == id, ct)) return Results.NotFound();
            var c = new ProviderCredential { ProviderId = id, Label = req.Label, ProtectedApiKey = secrets.Protect(req.ApiKey), IsActive = req.IsActive, InitialBalanceUsd = req.InitialBalanceUsd, RemainingBalanceUsd = req.RemainingBalanceUsd, AlertThresholdUsd = req.AlertThresholdUsd };
            db.Add(c); await db.SaveChangesAsync(ct); return Results.Ok(new { c.Id });
        });
        admin.MapPut("/credentials/{id:guid}/balance", async (Guid id, CredentialBalanceRequest req, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, CancellationToken ct) =>
        {
            var c = await db.ProviderCredentials.SingleOrDefaultAsync(x => x.Id == id && x.ProtectedApiKey != "", ct); if (c is null) return Results.NotFound();
            var initialBalanceUsd = req.InitialBalanceUsd ?? c.InitialBalanceUsd;
            if (!ValidCredentialBalances(initialBalanceUsd, req.RemainingBalanceUsd, req.AlertThresholdUsd))
                return InvalidCredentialBalance();
            var previous = new { c.InitialBalanceUsd, c.RemainingBalanceUsd, c.AlertThresholdUsd };
            c.InitialBalanceUsd = initialBalanceUsd;
            c.RemainingBalanceUsd = req.RemainingBalanceUsd;
            c.AlertThresholdUsd = req.AlertThresholdUsd;
            var quotaStatePreserved = c.LastErrorCode == ProviderErrorMapper.QuotaExhaustedCode;
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = actor.UserId(),
                Action = "provider_credential.balance.update",
                EntityType = "provider_credential",
                EntityId = c.Id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new
                {
                    previous,
                    current = new { InitialBalanceUsd = initialBalanceUsd, req.RemainingBalanceUsd, req.AlertThresholdUsd },
                    quotaStatePreserved
                })
            });
            await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        admin.MapDelete("/credentials/{id:guid}", async (Guid id, [FromBody] CredentialDeleteRequest? req, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, CancellationToken ct) =>
        {
            if (!string.Equals(req?.Confirmation, "حذف", StringComparison.Ordinal))
                return Results.BadRequest(new
                {
                    code = "credential_delete_confirmation_required",
                    message = "برای حذف قطعی کلید، عبارت «حذف» را دقیقاً وارد کنید."
                });
            var c = await db.ProviderCredentials.SingleOrDefaultAsync(x => x.Id == id && x.ProtectedApiKey != "", ct); if (c is null) return Results.NotFound();
            var retainedUsageRecords = await db.UsageRecords.CountAsync(x => x.ProviderCredentialId == id, ct);
            c.IsActive = false;
            c.ProtectedApiKey = "";
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = actor.UserId(),
                Action = "provider_credential.delete",
                EntityType = "provider_credential",
                EntityId = c.Id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new { c.ProviderId, c.Label, retainedUsageRecords, deletionMode = "tombstone" })
            });
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });
        admin.MapPost("/credentials/{id:guid}/test", async (Guid id, AppDbContext db, SecretProtector secrets, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var c = await db.ProviderCredentials.Include(x => x.Provider).SingleOrDefaultAsync(x => x.Id == id && x.ProtectedApiKey != "", ct); if (c?.Provider is null) return Results.NotFound(); var model = await db.Models.FirstOrDefaultAsync(x => x.ProviderId == c.ProviderId && x.IsActive, ct); if (model is null) return Results.BadRequest(new { message = "مدل فعالی وجود ندارد." });
            using var request = new HttpRequestMessage(HttpMethod.Post, c.Provider.BaseUrl.TrimEnd('/') + "/chat/completions"); request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Unprotect(c.ProtectedApiKey)); request.Content = JsonContent.Create(new { model = model.ModelId, messages = new[] { new { role = "user", content = "Reply only with OK" } }, max_tokens = 8 }); var sw = System.Diagnostics.Stopwatch.StartNew(); var response = await clients.CreateClient("providers").SendAsync(request, ct); var text = await response.Content.ReadAsStringAsync(ct); ProviderFailure? failure = null; if (response.IsSuccessStatusCode) ProviderErrorMapper.Clear(c); else { failure = ProviderErrorMapper.Classify(response.StatusCode, text); ProviderErrorMapper.Apply(c, failure); } await db.SaveChangesAsync(ct); return Results.Ok(new { success = response.IsSuccessStatusCode, status = (int)response.StatusCode, latencyMs = sw.ElapsedMilliseconds, errorCode = failure?.Code, errorMessage = failure?.PublicMessage(c.Provider.Name), response = text[..Math.Min(1000, text.Length)] });
        });

        admin.MapGet("/models", async (AppDbContext db, CancellationToken ct) =>
        {
            var models = await db.Models.AsNoTracking().Include(x => x.Provider).OrderBy(x => x.Provider!.Name).ThenBy(x => x.DisplayName).ToListAsync(ct);
            return Results.Ok(models.Select(AdminModelView));
        });
        admin.MapPost("/models", async (ModelRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var validation = await ValidateModelRequest(req, db, null, ct);
            if (validation is not null) return Results.BadRequest(new { message = validation });
            var m = ToModel(req); db.Add(m); await db.SaveChangesAsync(ct); return Results.Ok(new { m.Id });
        });
        admin.MapPut("/models/{id:guid}", async (Guid id, ModelRequest req, AppDbContext db, CancellationToken ct) =>
        {
            var m = await db.Models.FindAsync([id], ct); if (m is null) return Results.NotFound();
            var validation = await ValidateModelRequest(req, db, id, ct);
            if (validation is not null) return Results.BadRequest(new { message = validation });
            Apply(m, req); await db.SaveChangesAsync(ct); return Results.NoContent();
        });
        admin.MapPost("/models/{id:guid}/test", async (Guid id, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, SecretProtector secrets, IHttpClientFactory clients, CancellationToken ct) =>
        {
            var model = await db.Models.Include(x => x.Provider).ThenInclude(x => x!.Credentials).SingleOrDefaultAsync(x => x.Id == id, ct);
            if (model?.Provider is null) return Results.NotFound();
            var credential = model.Provider.Credentials.Where(x => x.IsActive && x.ProtectedApiKey != "").OrderBy(x => x.LastUsedAtUtc).FirstOrDefault();
            if (credential is null) return Results.BadRequest(new { message = "ابتدا برای برند انتخاب‌شده یک کلید upstream فعال ثبت کنید." });
            JsonDocument payload;
            try { payload = JsonDocument.Parse(string.IsNullOrWhiteSpace(model.TestPayloadJson) ? "{}" : model.TestPayloadJson); }
            catch (JsonException) { return Results.BadRequest(new { message = "payload آزمایشی JSON معتبر نیست." }); }
            using (payload)
            using (var request = new HttpRequestMessage(HttpMethod.Post, ModelUpstreamUri(model)))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secrets.Unprotect(credential.ProtectedApiKey));
                request.Content = new StringContent(payload.RootElement.GetRawText(), Encoding.UTF8, "application/json");
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    using var response = await clients.CreateClient("providers").SendAsync(request, ct);
                    var responseText = await response.Content.ReadAsStringAsync(ct);
                    ProviderFailure? failure = null;
                    if (response.IsSuccessStatusCode) ProviderErrorMapper.Clear(credential);
                    else { failure = ProviderErrorMapper.Classify(response.StatusCode, responseText); ProviderErrorMapper.Apply(credential, failure); }
                    db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId(), Action = "model.upstream.test", EntityType = "model", EntityId = model.Id.ToString(), DetailsJson = JsonSerializer.Serialize(new { success = response.IsSuccessStatusCode, status = (int)response.StatusCode, latencyMs = stopwatch.ElapsedMilliseconds, model.ProviderId }) });
                    await db.SaveChangesAsync(ct);
                    return Results.Ok(new { success = response.IsSuccessStatusCode, status = (int)response.StatusCode, latencyMs = stopwatch.ElapsedMilliseconds, contentType = response.Content.Headers.ContentType?.ToString(), errorCode = failure?.Code, response = responseText[..Math.Min(4000, responseText.Length)] });
                }
                catch (Exception exception) when (!ct.IsCancellationRequested)
                {
                    var failure = ProviderErrorMapper.Classify(exception); ProviderErrorMapper.Apply(credential, failure);
                    db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId(), Action = "model.upstream.test", EntityType = "model", EntityId = model.Id.ToString(), DetailsJson = JsonSerializer.Serialize(new { success = false, status = 502, latencyMs = stopwatch.ElapsedMilliseconds, model.ProviderId, failure.Code }) });
                    await db.SaveChangesAsync(CancellationToken.None);
                    return Results.Ok(new { success = false, status = 502, latencyMs = stopwatch.ElapsedMilliseconds, errorCode = failure.Code, response = "" });
                }
            }
        });
        admin.MapDelete("/models/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => { var m = await db.Models.FindAsync([id], ct); if (m is null) return Results.NotFound(); m.IsActive = false; await db.SaveChangesAsync(ct); return Results.NoContent(); });

        admin.MapGet("/users", async (AppDbContext db, [FromQuery] string? search, [FromQuery] string? sort, CancellationToken ct) => { var q = db.Users.AsNoTracking().Include(x => x.ApiKeys).AsQueryable(); if (!string.IsNullOrWhiteSpace(search)) q = q.Where(x => x.Mobile.Contains(search) || x.DisplayName.Contains(search)); q = sort switch { "wallet" => q.OrderByDescending(x => (double)x.WalletUsd), "requests" => q.OrderByDescending(x => x.ApiKeys.Sum(k => k.RequestCount)), _ => q.OrderByDescending(x => x.CreatedAtUtc) }; return Results.Ok(await q.Select(x => new { x.Id, x.Mobile, x.DisplayName, x.Role, isPrimarySuperAdmin = x.Mobile == SuperAdministrators.PrimaryMobile, x.IsSuspended, x.WalletUsd, x.CreatedAtUtc, x.LastSeenAtUtc, apiKeyCount = x.ApiKeys.Count, requests = x.ApiKeys.Sum(k => k.RequestCount), spentUsd = x.ApiKeys.Sum(k => (double)k.SpentUsd) }).ToListAsync(ct)); });
        admin.MapGet("/users/report", async (AppDbContext db, [FromQuery] string? search, [FromQuery] string? sort, CancellationToken ct) =>
        {
            var q = db.Users.AsNoTracking().AsQueryable();
            if (!string.IsNullOrWhiteSpace(search)) { var term = search.Trim(); q = q.Where(x => x.Mobile.Contains(term) || x.DisplayName.Contains(term)); }
            q = sort switch { "wallet" => q.OrderByDescending(x => (double)x.WalletUsd), "requests" => q.OrderByDescending(x => x.ApiKeys.Sum(k => k.RequestCount)), _ => q.OrderByDescending(x => x.CreatedAtUtc) };
            var rows = await q.Select(x => new
            {
                x.Mobile, x.DisplayName, x.Role, x.IsSuspended, x.WalletUsd, x.CreatedAtUtc, x.LastSeenAtUtc,
                ApiKeyCount = x.ApiKeys.Count, Requests = x.ApiKeys.Sum(k => k.RequestCount), SpentUsd = x.ApiKeys.Sum(k => (double)k.SpentUsd)
            }).ToListAsync(ct);
            var csv = new StringBuilder("\uFEFFMobile,Display Name,Role,Status,Wallet USD,API Keys,Requests,Spent USD,Created UTC,Last Seen UTC\r\n");
            foreach (var x in rows)
                csv.AppendJoin(',', Csv(x.Mobile), Csv(x.DisplayName), Csv(x.Role), x.IsSuspended ? "Suspended" : "Active",
                    x.WalletUsd.ToString(CultureInfo.InvariantCulture), x.ApiKeyCount, x.Requests,
                    x.SpentUsd.ToString(CultureInfo.InvariantCulture), x.CreatedAtUtc.ToString("O"), x.LastSeenAtUtc?.ToString("O") ?? "").Append("\r\n");
            return Results.File(Encoding.UTF8.GetBytes(csv.ToString()), "text/csv; charset=utf-8", $"aibus-users-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv");
        });
        admin.MapGet("/logs", async (AppDbContext db, [FromQuery] int page, [FromQuery] int pageSize, [FromQuery] string? search, [FromQuery] string? status, CancellationToken ct) =>
        {
            page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 10, 100);
            var query = from usage in db.UsageRecords.AsNoTracking()
                        join user in db.Users.AsNoTracking() on usage.UserId equals user.Id
                        join key in db.UserApiKeys.AsNoTracking() on usage.UserApiKeyId equals key.Id
                        select new { usage, user, key };
            if (!string.IsNullOrWhiteSpace(search))
            {
                var term = search.Trim();
                query = query.Where(x => x.user.Mobile.Contains(term) || x.user.DisplayName.Contains(term) || x.key.Name.Contains(term)
                    || x.key.KeyPrefix.Contains(term) || x.usage.ModelName.Contains(term) || x.usage.ProviderName.Contains(term) || x.usage.TraceId.Contains(term));
            }
            if (status is "success" or "failed") query = query.Where(x => x.usage.Status == status);
            var total = await query.CountAsync(ct);
            var items = await query.OrderByDescending(x => x.usage.CreatedAtUtc).Skip((page - 1) * pageSize).Take(pageSize)
                .Select(x => new
                {
                    x.usage.Id, x.usage.CreatedAtUtc, x.usage.Status,
                    httpStatus = x.usage.HttpStatus == 0 ? (x.usage.Status == "success" ? 200 : 502) : x.usage.HttpStatus,
                    x.usage.DurationMs, x.usage.TraceId, x.usage.EndpointPath, x.usage.ModelName, x.usage.ProviderName,
                    x.usage.InputTokens, x.usage.OutputTokens, x.usage.CostUsd,
                    consumer = new { x.user.Id, x.user.DisplayName, x.user.Mobile, apiKeyId = x.key.Id, apiKeyName = x.key.Name, x.key.KeyPrefix }
                }).ToListAsync(ct);
            var since = DateTime.UtcNow.AddHours(-24);
            var recent = db.UsageRecords.AsNoTracking().Where(x => x.CreatedAtUtc >= since);
            return Results.Ok(new
            {
                total, items,
                summary = new
                {
                    requests24h = await recent.CountAsync(ct),
                    failures24h = await recent.CountAsync(x => x.Status == "failed", ct),
                    averageLatencyMs24h = await recent.AnyAsync(ct) ? await recent.AverageAsync(x => (double)x.DurationMs, ct) : 0,
                    activeConsumers24h = await recent.Select(x => x.UserApiKeyId).Distinct().CountAsync(ct)
                },
                privacy = new { bodyStored = false, fields = new[] { "time", "status", "latency", "consumer", "model", "token usage", "cost", "trace id" } }
            });
        });
        admin.MapGet("/users/{id:guid}/keys", async (Guid id, AppDbContext db, CancellationToken ct) => Results.Ok(await db.UserApiKeys.AsNoTracking().Where(x => x.UserId == id).OrderByDescending(x => x.CreatedAtUtc).Select(x => new { x.Id, x.Name, x.KeyPrefix, x.IsActive, x.RequestLimit, x.SpendLimitUsd, x.RequestCount, x.SpentUsd, x.AccessMode, x.ModelRulesJson, x.CreatedAtUtc, x.LastUsedAtUtc }).ToListAsync(ct)));
        admin.MapPut("/users/{id:guid}/role", async (Guid id, UpdateUserRoleRequest req, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, CancellationToken ct) =>
        {
            if (req.Role is not (Roles.User or Roles.SuperAdmin)) return Results.BadRequest(new { message = "نقش انتخاب‌شده معتبر نیست." });
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) return Results.NotFound();
            if (SuperAdministrators.IsPrimary(user.Mobile) && req.Role != Roles.SuperAdmin)
                return Results.BadRequest(new { message = "سوپرادمین اصلی سامانه قابل خلع نیست." });
            if (actor.UserId() == id && user.Role != req.Role)
                return Results.BadRequest(new { message = "برای جلوگیری از قطع دسترسی، نمی‌توانید نقش حساب فعلی خودتان را تغییر دهید." });
            var previousRole = user.Role;
            user.Role = req.Role;
            if (req.Role == Roles.SuperAdmin) user.IsSuspended = false;
            db.AuditLogs.Add(new AuditLog
            {
                ActorUserId = actor.UserId(), Action = "user.role.update", EntityType = "user", EntityId = id.ToString(),
                DetailsJson = JsonSerializer.Serialize(new { previousRole, role = req.Role, user.Mobile })
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(UserView(user));
        });
        admin.MapPost("/users/{id:guid}/suspend", async (Guid id, SuspendRequest req, AppDbContext db, CancellationToken ct) => { var u = await db.Users.FindAsync([id], ct); if (u is null) return Results.NotFound(); if (u.Role == Roles.SuperAdmin) return Results.BadRequest(new { message = "سوپرادمین قابل تعلیق نیست." }); u.IsSuspended = req.IsSuspended; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapPost("/user-keys/{id:guid}/suspend", async (Guid id, SuspendRequest req, AppDbContext db, CancellationToken ct) => { var key = await db.UserApiKeys.FindAsync([id], ct); if (key is null) return Results.NotFound(); key.IsActive = !req.IsSuspended; await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapDelete("/user-keys/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => { var key = await db.UserApiKeys.FindAsync([id], ct); if (key is null) return Results.NotFound(); db.UserApiKeys.Remove(key); await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapDelete("/users/{id:guid}", async (Guid id, AppDbContext db, CancellationToken ct) => { var user = await db.Users.Include(x => x.ApiKeys).SingleOrDefaultAsync(x => x.Id == id, ct); if (user is null) return Results.NotFound(); if (user.Role == Roles.SuperAdmin) return Results.BadRequest(new { message = "سوپرادمین قابل حذف نیست." }); db.Users.Remove(user); await db.SaveChangesAsync(ct); return Results.NoContent(); });
        admin.MapPost("/users/{id:guid}/wallet", async (Guid id, WalletAdjustRequest req, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, CancellationToken ct) => { var u = await db.Users.FindAsync([id], ct); if (u is null) return Results.NotFound(); if (u.WalletUsd + req.AmountUsd < 0) return Results.BadRequest(new { message = "موجودی نمی‌تواند منفی شود." }); u.WalletUsd += req.AmountUsd; db.WalletTransactions.Add(new WalletTransaction { UserId = id, Type = "admin_adjustment", AmountUsd = req.AmountUsd, Status = "completed", Description = req.Description, CompletedAtUtc = DateTime.UtcNow }); db.AuditLogs.Add(new AuditLog { ActorUserId = actor.UserId(), Action = "wallet.adjust", EntityType = "user", EntityId = id.ToString(), DetailsJson = JsonSerializer.Serialize(req) }); await db.SaveChangesAsync(ct); return Results.Ok(new { u.WalletUsd }); });
        admin.MapPost("/users/{id:guid}/impersonate", async (Guid id, System.Security.Claims.ClaimsPrincipal actor, AppDbContext db, TokenService tokens, SettingsService settings, CancellationToken ct) =>
        {
            if (bool.TryParse(await settings.Get("security.allow_admin_impersonation", "true"), out var allowed) && !allowed)
                return Results.Json(new { message = "ورود مدیریتی به پنل کاربران در تنظیمات امنیتی غیرفعال شده است." }, statusCode: 403);
            var user = await db.Users.FindAsync([id], ct);
            if (user is null) return Results.NotFound();
            var sessionHours = SettingInt(await settings.Get("security.session_lifetime_hours", "12"), 12, 1, 168);
            return Results.Ok(new { token = tokens.Create(user, actor.UserId(), sessionHours), user = UserView(user) });
        });

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
            var lowKeys = await db.ProviderCredentials.Include(x => x.Provider).Where(x => x.IsActive && (x.LastErrorCode == ProviderErrorMapper.QuotaExhaustedCode || x.InitialBalanceUsd > 0 && x.RemainingBalanceUsd <= x.AlertThresholdUsd)).Select(x => new { x.Id, x.Label, provider = x.Provider!.Name, x.RemainingBalanceUsd, x.AlertThresholdUsd, x.LastErrorCode, x.LastErrorAtUtc, isQuotaExhausted = x.LastErrorCode == ProviderErrorMapper.QuotaExhaustedCode }).ToListAsync(ct);
            return Results.Ok(new { users = await db.Users.CountAsync(ct), online = await db.Users.CountAsync(x => x.LastSeenAtUtc >= now.AddMinutes(-5), ct), walletLiabilityUsd = await db.Users.SumAsync(x => (double)x.WalletUsd, ct), todayRequests = await usages.CountAsync(x => x.CreatedAtUtc >= today, ct), todayTokens = await usages.Where(x => x.CreatedAtUtc >= today).SumAsync(x => x.InputTokens + x.OutputTokens, ct), todayRevenueUsd = await usages.Where(x => x.CreatedAtUtc >= today).SumAsync(x => (double)x.CostUsd, ct), uniqueToday = await visits.Where(x => x.CreatedAtUtc >= today).Select(x => x.VisitorId).Distinct().CountAsync(ct), visitsToday = await visits.CountAsync(x => x.CreatedAtUtc >= today, ct), totalVisits = await visits.CountAsync(ct), timeline, models, lowKeys });
        });
        admin.MapGet("/visits", async (AppDbContext db, [FromQuery] int page, [FromQuery] int pageSize, CancellationToken ct) => { page = Math.Max(1, page); pageSize = Math.Clamp(pageSize, 10, 100); var q = db.Visits.AsNoTracking().OrderByDescending(x => x.CreatedAtUtc); return Results.Ok(new { total = await q.CountAsync(ct), items = await q.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct), browsers = await db.Visits.GroupBy(x => x.Browser).Select(g => new { name = g.Key, value = g.Count() }).ToListAsync(ct), systems = await db.Visits.GroupBy(x => x.OperatingSystem).Select(g => new { name = g.Key, value = g.Count() }).ToListAsync(ct) }); });
    }

    private static void MapGateway(WebApplication app)
    {
        app.MapGet("/v1/models", async (HttpRequest request, ApiKeyAuthenticator auth, AppDbContext db, CancellationToken ct) =>
        {
            if (await auth.Authenticate(request, ct) is null)
                return Results.Json(new { error = new { message = "Invalid API key" } }, statusCode: 401);
            var items = await db.Models
                .Where(x => x.IsActive && x.Provider!.IsActive)
                .Select(x => new
                {
                    id = x.ModelId,
                    @object = "model",
                    created = new DateTimeOffset(x.PriceSyncedAtUtc).ToUnixTimeSeconds(),
                    owned_by = x.Provider!.Slug,
                    display_name = x.DisplayName,
                    endpoint_path = x.EndpointPath,
                    service_type = x.ServiceType,
                    supports_streaming = x.SupportsStreaming
                })
                .ToListAsync(ct);
            return Results.Ok(new { @object = "list", data = items });
        });
        app.MapPost("/v1/chat/completions", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardChat(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/responses", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardJsonEndpoint(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/embeddings", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardJsonEndpoint(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/moderations", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardJsonEndpoint(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/images/generations", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardJsonEndpoint(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/videos", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardJsonEndpoint(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/audio/speech", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardSpeech(ctx, ct)).DisableAntiforgery();
        app.MapPost("/v1/audio/transcriptions", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardTranscription(ctx, ct)).DisableAntiforgery();
        app.Map("/v1/realtime", async (HttpContext ctx, GatewayService gateway, CancellationToken ct) => await gateway.ForwardRealtime(ctx, ct));
    }

    private static object UserView(AppUser x) => new { x.Id, x.Mobile, x.DisplayName, x.Role, x.WalletUsd, x.IsSuspended, x.CreatedAtUtc, x.LastSeenAtUtc };
    private static readonly HashSet<string> TicketCategories = ["technical", "billing", "account", "models", "general"];
    private static readonly HashSet<string> TicketPriorities = ["low", "normal", "high", "urgent"];
    private static readonly HashSet<string> ValidTicketStatuses = ["open", "waiting_support", "waiting_user", "resolved", "closed"];
    private const decimal MaxCredentialBalanceUsd = 1_000_000_000m;
    private const int MaxUserKeyRequestLimit = 1_000_000_000;
    private const decimal MaxUserKeySpendLimitUsd = 1_000_000_000m;
    private static void AddUserKeyLimitsAudit(AppDbContext db, Guid actorUserId, Guid keyId, int? previousRequestLimit, decimal? previousSpendLimitUsd, int? requestLimit, decimal? spendLimitUsd, string source) =>
        db.AuditLogs.Add(new AuditLog
        {
            ActorUserId = actorUserId,
            Action = "user_api_key.limits.update",
            EntityType = "user_api_key",
            EntityId = keyId.ToString(),
            DetailsJson = JsonSerializer.Serialize(new
            {
                previous = new { RequestLimit = previousRequestLimit, SpendLimitUsd = previousSpendLimitUsd },
                current = new { RequestLimit = requestLimit, SpendLimitUsd = spendLimitUsd },
                source
            })
        });
    private static bool ValidUserKeyLimits(int? requestLimit, decimal? spendLimitUsd) =>
        (!requestLimit.HasValue || requestLimit.Value is >= 0 and <= MaxUserKeyRequestLimit)
        && (!spendLimitUsd.HasValue || spendLimitUsd.Value is >= 0 and <= MaxUserKeySpendLimitUsd);
    private static IResult InvalidUserKeyLimits() => Results.BadRequest(new
    {
        code = "invalid_api_key_limits",
        message = "سقف درخواست و سقف مصرف دلاری باید نامحدود یا عددی نامنفی و حداکثر یک میلیارد باشند."
    });
    private static bool ValidCredentialBalances(decimal initial, decimal remaining, decimal threshold) =>
        initial is >= 0 and <= MaxCredentialBalanceUsd
        && remaining >= 0 && remaining <= initial
        && threshold is >= 0 and <= MaxCredentialBalanceUsd;
    private static IResult InvalidCredentialBalance() => Results.BadRequest(new
    {
        code = "invalid_credential_balance",
        message = "مقادیر موجودی و هشدار باید نامنفی و حداکثر یک میلیارد دلار باشند؛ موجودی فعلی نیز نمی‌تواند از موجودی اولیه بیشتر باشد."
    });
    private static int SettingInt(string value, int fallback, int minimum, int maximum) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
    private static decimal SettingDecimal(string value, decimal fallback, decimal minimum, decimal maximum) =>
        decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed) ? Math.Clamp(parsed, minimum, maximum) : fallback;
    private static string? ValidateSettings(UpdateSettingsRequest req)
    {
        if (req.DollarRateIrr is < 10_000 or > 100_000_000) return "نرخ دلار باید بین ۱۰٬۰۰۰ تا ۱۰۰٬۰۰۰٬۰۰۰ ریال باشد.";
        if (req.FeePercent is < 0 or > 100) return "کارمزد و مالیات باید بین صفر تا ۱۰۰ درصد باشد.";
        if (req.MinimumTopUpUsd is < 1 or > 1_000_000 || req.MaximumTopUpUsd < req.MinimumTopUpUsd || req.MaximumTopUpUsd > 1_000_000) return "بازه شارژ دلاری معتبر نیست.";
        if (req.SmsEnabled && string.IsNullOrWhiteSpace(req.SmsApiKey)) return "برای فعال‌سازی پیامک، API Key سرویس SMS.ir الزامی است.";
        if (req.SmsTemplateId <= 0) return "Template ID پیامک معتبر نیست.";
        if (req.OtpExpiryMinutes is < 1 or > 10) return "اعتبار کد ورود باید بین ۱ تا ۱۰ دقیقه باشد.";
        if (req.OtpRequestLimit is < 1 or > 20 || req.OtpWindowMinutes is < 1 or > 60) return "محدودیت درخواست کد ورود معتبر نیست.";
        if (req.OtpMaxAttempts is < 1 or > 10) return "تعداد تلاش ناموفق باید بین ۱ تا ۱۰ باشد.";
        if (!string.IsNullOrWhiteSpace(req.ZarinpalMerchantId) && !Guid.TryParse(req.ZarinpalMerchantId, out _)) return "Merchant ID زرین‌پال باید یک UUID معتبر باشد.";
        if (!Uri.TryCreate(req.PaymentCallbackUrl, UriKind.Absolute, out var callback) || callback.Scheme != Uri.UriSchemeHttp && callback.Scheme != Uri.UriSchemeHttps) return "آدرس Callback پرداخت معتبر نیست.";
        if (req.RequireHttpsCallback && callback.Scheme != Uri.UriSchemeHttps) return "طبق سیاست امنیتی، Callback پرداخت باید از HTTPS استفاده کند.";
        if (req.SessionLifetimeHours is < 1 or > 168) return "طول نشست باید بین ۱ تا ۱۶۸ ساعت باشد.";
        return null;
    }
    private static string Clean(string? value, int max) { var text = (value ?? "").Trim(); return text[..Math.Min(max, text.Length)]; }
    private static string ValidTicketCategory(string value) => TicketCategories.Contains(value) ? value : "general";
    private static string ValidTicketPriority(string value) => TicketPriorities.Contains(value) ? value : "normal";
    private static object TicketSummary(SupportTicket x, bool includeUser) => new { x.Id, x.ReferenceCode, x.Subject, x.Category, x.Priority, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.LastReplyAtUtc, x.ClosedAtUtc, messageCount = x.Messages.Count, lastMessage = x.Messages.OrderByDescending(m => m.CreatedAtUtc).Select(m => m.Body).FirstOrDefault(), user = includeUser && x.User is not null ? new { x.User.Id, x.User.DisplayName, x.User.Mobile } : null };
    private static object TicketDetails(SupportTicket x, bool includeUser) => new { x.Id, x.ReferenceCode, x.Subject, x.Category, x.Priority, x.Status, x.CreatedAtUtc, x.UpdatedAtUtc, x.LastReplyAtUtc, x.ClosedAtUtc, user = includeUser && x.User is not null ? new { x.User.Id, x.User.DisplayName, x.User.Mobile } : null, messages = x.Messages.OrderBy(m => m.CreatedAtUtc).Select(m => new { m.Id, m.IsStaff, m.Body, m.CreatedAtUtc }) };
    private static Dictionary<string, int> TicketCounts(IEnumerable<SupportTicket> tickets) => tickets.GroupBy(x => x.Status).ToDictionary(x => x.Key, x => x.Count());
    private static string? RecoverApiKey(SecretProtector secrets, UserApiKey key)
    {
        if (string.IsNullOrWhiteSpace(key.ProtectedApiKey)) return null;
        var raw = secrets.Unprotect(key.ProtectedApiKey);
        return !string.IsNullOrWhiteSpace(raw) && Hashing.Sha256(raw) == key.KeyHash ? raw : null;
    }
    private static void DisableSecretResponseCaching(HttpResponse response)
    {
        response.Headers["Cache-Control"] = "no-store, no-cache, max-age=0";
        response.Headers["Pragma"] = "no-cache";
        response.Headers["Expires"] = "0";
    }
    private static string ValidAccessMode(string value) => value is "allow" or "deny" ? value : "all";
    private static string? NormalizeMobile(string value) { var digits = new string(value.Where(char.IsDigit).ToArray()); if (digits.StartsWith("98") && digits.Length == 12) digits = "0" + digits[2..]; if (digits.Length == 10 && digits.StartsWith('9')) digits = "0" + digits; return digits.Length == 11 && digits.StartsWith("09") ? digits : null; }
    private static readonly HashSet<string> SupportedModalities = ["text", "image", "audio", "video"];
    private static readonly HashSet<string> SupportedGatewayEndpoints = ["/v1/chat/completions", "/v1/responses", "/v1/embeddings", "/v1/moderations", "/v1/images/generations", "/v1/videos", "/v1/audio/speech", "/v1/audio/transcriptions", "/v1/realtime"];
    private static async Task<string?> ValidateModelRequest(ModelRequest r, AppDbContext db, Guid? currentId, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.ModelId) || string.IsNullOrWhiteSpace(r.DisplayName)) return "نام نمایشی و Model ID الزامی است.";
        if (await db.Models.AnyAsync(x => x.ModelId == r.ModelId.Trim() && x.Id != currentId, ct)) return "این Model ID قبلاً ثبت شده است.";
        var provider = await db.Providers.AsNoTracking().SingleOrDefaultAsync(x => x.Id == r.ProviderId, ct);
        if (provider is null) return "ارائه‌دهنده انتخاب‌شده وجود ندارد.";
        var endpoint = NormalizePath(r.EndpointPath, "/v1/chat/completions");
        if (!SupportedGatewayEndpoints.Contains(endpoint)) return "Endpoint عمومی انتخاب‌شده توسط Gateway پشتیبانی نمی‌شود.";
        var inputs = (r.InputModalities ?? []).Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim().ToLowerInvariant()).Distinct().ToArray();
        if (inputs.Length == 0 || inputs.Any(x => !SupportedModalities.Contains(x))) return "حداقل یک نوع ورودی معتبر انتخاب کنید.";
        if (!SupportedModalities.Contains((r.OutputModality ?? "").Trim().ToLowerInvariant())) return "نوع خروجی معتبر نیست.";
        if (r.InputPricePerMillionUsd < 0 || r.OutputPricePerMillionUsd < 0 || r.CachedInputPricePerMillionUsd < 0) return "تعرفه‌ها نمی‌توانند منفی باشند.";
        if (!string.IsNullOrWhiteSpace(r.UpstreamBaseUrl) && (!Uri.TryCreate(r.UpstreamBaseUrl, UriKind.Absolute, out var upstream) || upstream.Scheme is not ("http" or "https"))) return "Base URL سرویس مقصد معتبر نیست.";
        if (provider.Slug == "arka" && string.IsNullOrWhiteSpace(r.UpstreamBaseUrl)) return "برای سرویس ARKA واردکردن Base URL اختصاصی الزامی است.";
        try { JsonDocument.Parse(string.IsNullOrWhiteSpace(r.TestPayloadJson) ? "{}" : r.TestPayloadJson).Dispose(); }
        catch (JsonException) { return "ورودی تستی JSON معتبر نیست."; }
        return null;
    }
    private static string NormalizePath(string? value, string fallback) { var path = Clean(value, 500); if (path.Length == 0) path = fallback; return path.StartsWith('/') ? path.TrimEnd('/') : "/" + path.TrimEnd('/'); }
    private static Uri ModelUpstreamUri(AiModel model)
    {
        var baseUri = new Uri(string.IsNullOrWhiteSpace(model.UpstreamBaseUrl) ? model.Provider!.BaseUrl : model.UpstreamBaseUrl);
        var upstreamPath = string.IsNullOrWhiteSpace(model.UpstreamPath) ? model.EndpointPath : model.UpstreamPath;
        var path = upstreamPath.StartsWith('/') ? upstreamPath : "/" + upstreamPath;
        var basePath = baseUri.AbsolutePath.TrimEnd('/');
        if (basePath.Length > 0 && !path.Equals(basePath, StringComparison.OrdinalIgnoreCase) && !path.StartsWith(basePath + "/", StringComparison.OrdinalIgnoreCase)) path = basePath + "/" + path.TrimStart('/');
        return new UriBuilder(baseUri) { Path = path, Query = "" }.Uri;
    }
    private static AiModel ToModel(ModelRequest r) { var m = new AiModel(); Apply(m, r); return m; }
    private static void Apply(AiModel m, ModelRequest r) { var inputs = (r.InputModalities ?? ["text"]).Where(SupportedModalities.Contains).Distinct().ToArray(); if (inputs.Length == 0) inputs = ["text"]; var output = SupportedModalities.Contains(r.OutputModality ?? "") ? r.OutputModality! : "text"; m.ProviderId = r.ProviderId; m.ModelId = Clean(r.ModelId, 120); m.DisplayName = Clean(r.DisplayName, 160); m.Modality = Clean(r.Modality, 40) is { Length: > 0 } modality ? modality : string.Join('-', inputs.Append(output).Distinct()); m.InputModalitiesJson = JsonSerializer.Serialize(inputs); m.OutputModality = output; m.InputPricePerMillionUsd = r.InputPricePerMillionUsd; m.OutputPricePerMillionUsd = r.OutputPricePerMillionUsd; m.CachedInputPricePerMillionUsd = r.CachedInputPricePerMillionUsd; m.ContextWindow = Math.Max(0, r.ContextWindow); m.SupportsStreaming = r.SupportsStreaming; m.SupportsWebSocket = r.SupportsWebSocket; m.IsActive = r.IsActive; m.PricingSourceUrl = Clean(r.PricingSourceUrl, 500); m.TestPayloadJson = string.IsNullOrWhiteSpace(r.TestPayloadJson) ? "{}" : r.TestPayloadJson; m.ServiceType = Clean(r.ServiceType, 50) is { Length: > 0 } serviceType ? serviceType : "chat"; m.EndpointPath = NormalizePath(r.EndpointPath, "/v1/chat/completions"); m.UpstreamBaseUrl = Clean(r.UpstreamBaseUrl, 500).TrimEnd('/'); m.UpstreamPath = NormalizePath(r.UpstreamPath, m.EndpointPath); m.Region = Clean(r.Region, 80) is { Length: > 0 } region ? region : "global"; m.IsPreview = r.IsPreview; m.PricingDetailsJson = JsonSerializer.Serialize(r.PricingComponents ?? []); m.PricingNotes = Clean(r.PricingNotes, 1000); m.PriceSyncedAtUtc = DateTime.UtcNow; }
    private static string[] InputModalities(AiModel m) { try { return JsonSerializer.Deserialize<string[]>(m.InputModalitiesJson) ?? ["text"]; } catch { return ["text"]; } }
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
    private static object ModelView(AiModel x) => new { x.Id, x.ProviderId, x.ModelId, x.DisplayName, x.Modality, inputModalities = InputModalities(x), x.OutputModality, x.ServiceType, x.EndpointPath, x.UpstreamBaseUrl, x.UpstreamPath, x.Region, x.IsPreview, pricingComponents = PricingComponents(x), x.PricingNotes, x.InputPricePerMillionUsd, x.OutputPricePerMillionUsd, x.CachedInputPricePerMillionUsd, x.ContextWindow, x.SupportsStreaming, x.SupportsWebSocket, x.PriceSyncedAtUtc, x.PricingSourceUrl, x.TestPayloadJson, provider = new { x.ProviderId, x.Provider!.Name, x.Provider.Slug, x.Provider.LogoUrl } };
    private static object AdminModelView(AiModel x) => new { x.Id, x.ProviderId, providerName = x.Provider!.Name, providerSlug = x.Provider.Slug, x.ModelId, x.DisplayName, x.Modality, inputModalities = InputModalities(x), x.OutputModality, x.ServiceType, x.EndpointPath, x.UpstreamBaseUrl, x.UpstreamPath, x.Region, x.IsPreview, pricingComponents = PricingComponents(x), x.PricingNotes, x.InputPricePerMillionUsd, x.OutputPricePerMillionUsd, x.CachedInputPricePerMillionUsd, x.ContextWindow, x.SupportsStreaming, x.SupportsWebSocket, x.IsActive, x.PriceSyncedAtUtc, x.PricingSourceUrl, x.TestPayloadJson };
    private static string Csv(string? value) => $"\"{(value ?? "").Replace("\"", "\"\"")}\"";
}
