using DoTogether.Application.Interfaces;
using DoTogether.Application.Services;
using DoTogether.Infrastructure.Data;
using DoTogether.Infrastructure.Services;
using DoTogether.Middleware;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// ── Port binding ──
// Railway sets the PORT env var. ASP.NET Core doesn't pick it up automatically,
// so we configure Kestrel to listen on it explicitly.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrEmpty(port))
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");

// ── EF Core + PostgreSQL ──
// Resolution order (first non-null wins):
//   1. DATABASE_URL              – full Postgres URL set in Railway Variables tab
//   2. PGHOST / PGPORT / ...     – auto-injected by Railway when Postgres is in same project
//   3. ConnectionStrings config  – local dev (appsettings.json / docker-compose)
var connectionString =
    ParseDatabaseUrl(builder.Configuration.GetValue<string>("DATABASE_URL"))
    ?? BuildFromPgVars()
    ?? builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("No database connection string configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(connectionString));

builder.Services.AddScoped<IAppDbContext>(sp => sp.GetRequiredService<AppDbContext>());

// ── Authentication ──
var jwtKey = builder.Configuration["Jwt:Key"] ?? "SuperSecretDevKey_Change_In_Production_32+chars!";
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
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtKey))
        };
    });

builder.Services.AddAuthorization();

// ── Application services ──
builder.Services.AddScoped<ITokenService, TokenService>();
builder.Services.AddScoped<IDateTimeProvider, DateTimeProvider>();
builder.Services.AddScoped<ICurrentUserService, CurrentUserService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<HouseholdService>();
builder.Services.AddScoped<ChoreService>();
builder.Services.AddScoped<OccurrenceService>();
builder.Services.AddScoped<CalendarService>();
builder.Services.AddScoped<AchievementService>();

builder.Services.AddHttpContextAccessor();

builder.Services.AddControllers();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "DoTogether API", Version = "v1" });

    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Enter your JWT Bearer token",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT"
    };

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

var app = builder.Build();

// ── Seed / migrate ──
await SeedData.InitializeAsync(app.Services, app.Environment.IsDevelopment());

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwagger();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/swagger/v1/swagger.json", "DoTogether API v1");
    });
}

app.UseMiddleware<ExceptionHandlingMiddleware>();
// HTTPS redirection is handled by Railway's reverse proxy; enabling it inside
// the container causes redirect loops.
if (app.Environment.IsDevelopment())
    app.UseHttpsRedirection();
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
    var host     = Environment.GetEnvironmentVariable("PGHOST");
    var database = Environment.GetEnvironmentVariable("PGDATABASE");
    var user     = Environment.GetEnvironmentVariable("PGUSER");

    if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(database) || string.IsNullOrEmpty(user))
        return null;

    var pgPort  = Environment.GetEnvironmentVariable("PGPORT") ?? "5432";
    var password = Environment.GetEnvironmentVariable("PGPASSWORD") ?? "";

    return $"Host={host};Port={pgPort};Database={database};Username={user};Password={password};" +
           "SSL Mode=Prefer;Trust Server Certificate=true";
}

