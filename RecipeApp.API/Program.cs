using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.FileProviders;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using RecipeApp.API;
using RecipeApp.API.Endpoints;
using RecipeApp.Application.Auth.Abstractions;
using RecipeApp.Application.Common;
using RecipeApp.Application.MealPlanning.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Social.Abstractions;
using RecipeApp.Infrastructure.Auth;
using RecipeApp.Infrastructure.Chat;
using RecipeApp.Infrastructure.Images;
using RecipeApp.Infrastructure.MealPlanning;
using RecipeApp.Infrastructure.Persistence;
using RecipeApp.Infrastructure.Recipes;
using RecipeApp.Infrastructure.Social;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();

builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

// Secrets live outside the repo (audit fix 1): user-secrets in local dev, env vars elsewhere.
var connectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException(
        "ConnectionStrings:DefaultConnection not configured — set via user-secrets or ConnectionStrings__DefaultConnection env var.");

var npgsqlDataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
npgsqlDataSourceBuilder.EnableDynamicJson();
var npgsqlDataSource = npgsqlDataSourceBuilder.Build();

builder.Services.AddDbContext<ApplicationDbContext>(options => options.UseNpgsql(npgsqlDataSource));

builder.Services.AddValidatorsFromAssemblyContaining<IApplicationAssemblyMarker>();

builder.Services.Configure<JwtSettings>(builder.Configuration.GetSection(JwtSettings.SectionName));
builder.Services.AddScoped<IPasswordHasher, PasswordHasher>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IRecipeService, RecipeService>();
builder.Services.AddScoped<ISocialService, SocialService>();
builder.Services.AddScoped<IMealPlanService, MealPlanService>();

// social-feed cp04 (decision I1): uploaded images live on local disk behind the
// IImageStorage seam — S3/MinIO is later a one-class swap. The root is config-overridable
// (ImageStorage:RootPath) so the integration tests point it at a temp directory; the
// default sits under the content root (git-ignored uploads/). The same path backs the
// public-read static-file mount on /images below.
var imageRootPath = builder.Configuration["ImageStorage:RootPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "uploads", "images");
builder.Services.AddLocalDiskImageStorage(imageRootPath);

// chat-ai cp02: registers IChatAssistantService (Gemini-backed). Nothing consumes it until
// cp03 wires the /chat endpoints; the Gemini:ApiKey check is deferred to resolution time.
builder.Services.AddChatAssistant(builder.Configuration);

// Structured error responses: RFC-7807 ProblemDetails everywhere, with a global handler
// (GlobalExceptionHandler) that logs unhandled exceptions and returns a 500 ProblemDetails
// without leaking a stack trace. Wired into the pipeline via app.UseExceptionHandler() below.
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();

// Rate limiting (audit 4.5): brute-force protection for the anonymous /auth/* endpoints.
// The "auth" policy is IP-partitioned, PermitLimit requests per Window (default 10/min),
// no queue, and rejects overflow with 429. See RateLimitPolicies for the convention
// chat-ai reuses. The permit limit is configurable (RateLimiting:AuthPermitLimit) so
// integration tests — which all share the null "unknown" partition under the TestServer —
// can raise it out of the way; production runs on the default.
var authPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:AuthPermitLimit") ?? 10;
// chat-ai cp3: chat costs real money per call, so the /chat lane gets its own (tighter, tunable)
// budget rather than reusing auth's. Same IP-partitioned fixed-window shape (see RateLimitPolicies
// / Decisions/rate-limiter-policy-convention).
var chatPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:ChatPermitLimit") ?? 20;
// social-feed cp1: cheap DB-only actions across many endpoints (a feed scroll plus a few
// like taps burns requests fast), so the budget is looser — spam protection, not cost.
var socialPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:SocialPermitLimit") ?? 100;
// social-feed cp4: image uploads write multi-MB files to disk, so the lane gets its own,
// tighter budget than social's cheap DB taps (nobody legitimately uploads 20+ photos/min).
var imagesPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:ImagesPermitLimit") ?? 20;
// meal-planning plan cp02: shared by the whole meal-plan/shopping-list family (cp02–04).
// Cheap DB-only actions like social, so the default mirrors social's looser budget.
var mealPermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:MealPermitLimit") ?? 100;
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy(RateLimitPolicies.Auth, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = authPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy(RateLimitPolicies.Chat, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = chatPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy(RateLimitPolicies.Social, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = socialPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy(RateLimitPolicies.Images, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = imagesPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
    options.AddPolicy(RateLimitPolicies.Meal, httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = mealPermitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var jwtSettings = builder.Configuration.GetSection(JwtSettings.SectionName).Get<JwtSettings>()
    ?? throw new InvalidOperationException("Missing 'Jwt' configuration section.");

if (string.IsNullOrWhiteSpace(jwtSettings.Key))
{
    throw new InvalidOperationException(
        "Jwt:Key not configured — set via user-secrets or Jwt__Key env var.");
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSettings.Key)),
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();
});

var app = builder.Build();

// Global exception handler first, so it wraps auth and every endpoint. Logs the exception
// and returns a ProblemDetails 500 with no stack trace (safe in Production).
app.UseExceptionHandler();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// social-feed cp04: serve stored images from the upload root at /images. Deliberately
// PUBLIC-read (decision I1): the middleware sits outside endpoint routing, so the
// RequireAuthenticatedUser fallback policy doesn't apply here — the access control is the
// unguessable server-generated GUID filename. Content types resolve from the extension
// (.jpg/.png/.webp), which ImageUploadRules pinned to the magic-byte-verified type.
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new PhysicalFileProvider(imageRootPath),
    RequestPath = "/images",
});

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapRecipeEndpoints();
app.MapChatEndpoints();
app.MapSocialEndpoints();
app.MapImageEndpoints();
app.MapMealPlanEndpoints();

// Anonymous liveness probe (audit 4.5): must return 200 without a bearer, otherwise the
// fallback RequireAuthenticatedUser policy makes an uptime check read the API as down.
app.MapGet("/api/health", () => Results.Ok(new { status = "ok" }))
.AllowAnonymous()
.WithName("GetHealth");

app.Run();

public partial class Program { }
