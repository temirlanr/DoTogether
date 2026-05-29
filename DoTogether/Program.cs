using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Infrastructure.Data;
using DoTogether.Infrastructure.Services;
using DoTogether.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

// ── Port binding ──
// Railway sets the PORT env var. ASP.NET Core doesn't pick it up automatically,
// so we configure Kestrel to listen on it explicitly.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ── EF Core + PostgreSQL ──
var connectionString =
    ParseDatabaseUrl(builder.Configuration.GetValue<string>("DATABASE_URL"))
    ?? BuildFromPgVars()
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("No database connection string configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

// ── Authentication ──
// Production must set JWT__KEY explicitly; dev gets a stable fallback.
var jwtKey = builder.Configuration["Jwt:Key"];
if (string.IsNullOrWhiteSpace(jwtKey))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "Jwt:Key configuration is required in non-development environments. " +
            "Set the JWT__KEY environment variable to a 32+ character random secret.");
    jwtKey = "SuperSecretDevKey_Change_In_Production_32+chars!";
}
if (jwtKey.Length < 32)
    throw new InvalidOperationException("Jwt:Key must be at least 32 characters long.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = builder.Configuration["Jwt:Issuer"] ?? "DoTogether",
            ValidateAudience = true,
            ValidAudience = builder.Configuration["Jwt:Audience"] ?? "DoTogether",
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey)),
            ClockSkew = TimeSpan.FromSeconds(30)
        };
    });

builder.Services.AddAuthorization();

// ── CORS ──
// Production must explicitly list trusted origins via the Cors:AllowedOrigins
// config array. AllowCredentials is required for the HttpOnly refresh cookie.
var allowedOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? [];
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        if (builder.Environment.IsDevelopment() && allowedOrigins.Length == 0)
        {
            // Dev: allow Vite dev server + LAN access for mobile testing.
            policy.SetIsOriginAllowed(origin =>
                {
                    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri)) return false;
                    return uri.Host == "localhost"
                        || uri.Host == "127.0.0.1"
                        || uri.Host.StartsWith("192.168.", StringComparison.Ordinal)
                        || uri.Host.StartsWith("10.", StringComparison.Ordinal);
                })
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .WithExposedHeaders("X-CSRF-Token");
        }
        else
        {
            policy.WithOrigins(allowedOrigins)
                .AllowAnyHeader()
                .AllowAnyMethod()
                .AllowCredentials()
                .WithExposedHeaders("X-CSRF-Token");
        }
    });
});

// ── Rate limiting ──
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Brute-force protection on auth endpoints — partition by IP.
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    // Recipe import: expensive outbound HTTP — limit per user.
    options.AddPolicy("recipe-import", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.User.FindFirst("sub")?.Value
                          ?? httpContext.Connection.RemoteIpAddress?.ToString()
                          ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddAntiforgery();

// ── Application services ──
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IDateTimeProvider, DateTimeProvider>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<HouseholdService>();
builder.Services.AddScoped<HouseholdAccessService>();
builder.Services.AddScoped<ChoreService>();
builder.Services.AddScoped<OccurrenceService>();
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<AchievementService>();
builder.Services.AddScoped<RecipeService>();
builder.Services.AddScoped<MealPlanService>();

if (string.IsNullOrWhiteSpace(builder.Configuration["Gemini:ApiKey"]))
{
    builder.Services.AddSingleton<IRecipeLlmExtractor, NullRecipeLlmExtractor>();
}
else
{
    builder.Services.AddHttpClient<IRecipeLlmExtractor, GeminiRecipeLlmExtractor>(client =>
    {
        client.Timeout = TimeSpan.FromSeconds(20);
    });
}

// Recipe import: hardened HttpClient — no auto-redirect (we follow manually with
// per-hop SSRF validation), tight connect timeout, and a 4 MB body cap.
builder.Services.AddHttpClient<RecipeImportService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
    client.MaxResponseContentBufferSize = 4 * 1024 * 1024; // 4 MB
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    AllowAutoRedirect = false,
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(5),
    PooledConnectionLifetime = TimeSpan.FromMinutes(2)
});

builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "DoTogether API", Version = "v1" });

    options.AddSecurityDefinition("bearer", new OpenApiSecurityScheme
    {
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Description = "JWT Authorization header using the Bearer scheme."
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        [new OpenApiSecuritySchemeReference("bearer", document)] = []
    });
});
builder.Services.AddProblemDetails();

var dataProtectionKeysPath = builder.Configuration["DataProtection:KeysPath"];
if (!string.IsNullOrWhiteSpace(dataProtectionKeysPath))
{
    Directory.CreateDirectory(dataProtectionKeysPath);
    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));
}

var app = builder.Build();

// ── Migrate ──
await SeedData.InitializeAsync(app.Services);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Middleware order is significant:
//  1. ExceptionHandling — wraps everything below in the unified error envelope.
//  2. SecurityHeaders   — must apply to every response, including errors.
//  3. CORS              — before Auth so preflight requests succeed.
//  4. RateLimiter       — before Auth so unauthenticated bursts are rejected cheaply.
//  5. CsrfDoubleSubmit  — only enforces when the refresh cookie is present.
//  6. Authentication / Authorization
app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();

// HTTPS redirection is handled by Railway's reverse proxy; enabling it inside
// the container causes redirect loops.
//if (app.Environment.IsDevelopment())
//    app.UseHttpsRedirection();

app.UseCors();
app.UseRateLimiter();
app.UseMiddleware<CsrfDoubleSubmitMiddleware>();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

// ── Helpers ──

/// <summary>
/// Converts a PostgreSQL URL (postgresql://user:pass@host:port/db) to an
/// Npgsql connection string. Railway always provides DATABASE_URL in this format.
/// SSL is required for Railway's managed Postgres.
/// </summary>
static string? ParseDatabaseUrl(string? url)
{
    if (string.IsNullOrWhiteSpace(url)) return null;

    var uri = new Uri(url);
    var userInfo = uri.UserInfo.Split(':', 2);
    var host = uri.Host;
    var dbPort = uri.Port > 0 ? uri.Port : 5432;
    var database = uri.AbsolutePath.TrimStart('/');
    var username = Uri.UnescapeDataString(userInfo[0]);
    var password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "";

    return $"Host={host};Port={dbPort};Database={database};Username={username};Password={password};" +
           "SSL Mode=Require;Trust Server Certificate=true";
}

static string? BuildFromPgVars()
{
    var host = Environment.GetEnvironmentVariable("PGHOST");
    var database = Environment.GetEnvironmentVariable("PGDATABASE");
    var user = Environment.GetEnvironmentVariable("PGUSER");

    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(database) || string.IsNullOrEmpty(user))
        return null;

    var pgPort = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var password = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "";

    return $"Host={host};Port={pgPort};Database={database};Username={user};Password={password};" +
           "SSL Mode=Prefer;Trust Server Certificate=true";
}
