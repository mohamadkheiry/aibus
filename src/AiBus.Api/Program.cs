using System.Text;
using AiBus.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);
if (!builder.Environment.IsDevelopment() && builder.Configuration["Jwt:Key"]?.StartsWith("CHANGE-ME", StringComparison.Ordinal) == true)
    throw new InvalidOperationException("Jwt__Key must be replaced in production.");
builder.Services.AddDbContext<AppDbContext>(o => o.UseSqlite(builder.Configuration.GetConnectionString("Default")));
var dataProtection = builder.Services.AddDataProtection();
var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
    dataProtection.PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
builder.Services.AddScoped<SecretProtector>();
builder.Services.AddScoped<TokenService>();
builder.Services.AddScoped<SettingsService>();
builder.Services.AddScoped<SmsIrService>();
builder.Services.AddScoped<ZarinpalService>();
builder.Services.AddScoped<ApiKeyAuthenticator>();
builder.Services.AddScoped<GatewayService>();
builder.Services.AddHttpClient<SmsIrService>();
builder.Services.AddHttpClient<ZarinpalService>();
builder.Services.AddHttpClient("providers", c => c.Timeout = TimeSpan.FromMinutes(10));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(o =>
{
    o.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuer = true, ValidateAudience = true, ValidateLifetime = true, ValidateIssuerSigningKey = true,
        ValidIssuer = builder.Configuration["Jwt:Issuer"], ValidAudience = builder.Configuration["Jwt:Audience"],
        IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(builder.Configuration["Jwt:Key"]!)),
        ClockSkew = TimeSpan.FromMinutes(1)
    };
});
builder.Services.AddAuthorization();
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p.WithOrigins(builder.Configuration.GetSection("CorsOrigins").Get<string[]>() ?? ["http://localhost:5173"]).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o =>
{
    o.SwaggerDoc("v1", new OpenApiInfo { Title = "AiBus Gateway API", Version = "v1", Description = "درگاه تجمیعی سرویس‌های هوش مصنوعی" });
    o.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Name = "Authorization", In = ParameterLocation.Header, Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
    o.AddSecurityRequirement(new OpenApiSecurityRequirement { [new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }] = [] });
});
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseCors();
app.UseSwagger();
app.UseSwaggerUI();
app.UseWebSockets();
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapAiBusRoutes();

using (var scope = app.Services.CreateScope())
    await SeedData.Initialize(scope.ServiceProvider.GetRequiredService<AppDbContext>());

app.Run();

public partial class Program;
