using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using FluentValidation;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
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
using RecipeApp.Application.Moderation.Abstractions;
using RecipeApp.Application.Notifications.Abstractions;
using RecipeApp.Application.Recipes.Abstractions;
using RecipeApp.Application.Social.Abstractions;
using RecipeApp.Domain.Enums;
using RecipeApp.Infrastructure.Auth;
using RecipeApp.Infrastructure.Chat;
using RecipeApp.Infrastructure.Images;
using RecipeApp.Infrastructure.MealPlanning;
using RecipeApp.Infrastructure.Moderation;
using RecipeApp.Infrastructure.Notifications;
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
builder.Services.AddScoped<IShoppingListService, ShoppingListService>();
// Governor (stream D): reports (user-facing), the separate admin service (decision D5),
// and the security-state read behind the revocation check. The memory cache is the
// singleton store that read caches into.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<IReportService, ReportService>();
builder.Services.AddScoped<IAdminService, AdminService>();
builder.Services.AddScoped<IUserSecurityStateService, UserSecurityStateService>();
// Stream C (AI week proposal): the assistant needs IChatMessageCaller, registered by
// AddChatAssistant below — resolution is lazy, so order here doesn't matter.
builder.Services.AddScoped<IMealPlanAssistantService, MealPlanAssistantService>();
builder.Services.AddScoped<IMealPlanProposalService, MealPlanProposalService>();
// open-loops slice 3: the READ side only — notification writes are staged inline in
// SocialService so they share a transaction with the interaction that caused them.
builder.Services.AddScoped<INotificationService, NotificationService>();

// social-feed cp04 (decision I1): uploaded images live behind the IImageStorage seam.
// Production (Railway is ephemeral-disk) sets ImageStorage:R2:* and stores in Cloudflare
// R2, returning absolute public URLs; dev and the integration tests leave R2 unset and
// keep local disk. The local root is config-overridable (ImageStorage:RootPath) so the
// integration tests point it at a temp directory; the default sits under the content
// root (git-ignored uploads/). The same path backs the public-read static-file mount on
// /images below — R2 mode has no local files, so the mount only exists in disk mode.
var useR2ImageStorage = !string.IsNullOrEmpty(builder.Configuration["ImageStorage:R2:Bucket"]);
var imageRootPath = builder.Configuration["ImageStorage:RootPath"]
    ?? Path.Combine(builder.Environment.ContentRootPath, "uploads", "images");
if (useR2ImageStorage)
{
    builder.Services.AddR2ImageStorage(builder.Configuration);
}
else
{
    builder.Services.AddLocalDiskImageStorage(imageRootPath);
}

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

        // Governor (stream D, decision D3): the revocation check. A signature-valid,
        // unexpired JWT is no longer sufficient — the user's live security state must
        // agree. Fails validation when the account is banned, currently suspended, or the
        // token's "tver" claim is behind the row's TokenVersion (bumped on ban/suspend),
        // so banning actually bans instead of waiting out the token's expiry. The read is
        // one cached three-column lookup (UserSecurityStateService, 60s TTL, invalidated
        // by admin actions), so the per-request cost is a cache hit.
        //
        // Tokens issued before stream D carry no "tver" claim; they read as version 0,
        // which matches the column's backfill default — existing sessions survive the
        // deploy and revocation still catches them the moment their user is actioned.
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var sub = context.Principal?.FindFirst(System.IdentityModel.Tokens.Jwt.JwtRegisteredClaimNames.Sub)?.Value
                    ?? context.Principal?.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
                if (!Guid.TryParse(sub, out var userId))
                {
                    context.Fail("Token carries no usable subject.");
                    return;
                }

                var securityState = context.HttpContext.RequestServices.GetRequiredService<IUserSecurityStateService>();
                var state = await securityState.GetAsync(userId, context.HttpContext.RequestAborted);
                if (state is null)
                {
                    context.Fail("Unknown user.");
                    return;
                }

                var claimedVersion = int.TryParse(
                    context.Principal?.FindFirst(JwtTokenService.TokenVersionClaim)?.Value, out var v) ? v : 0;

                if (state.IsBanned
                    || (state.SuspendedUntilUtc is DateTime suspendedUntil && suspendedUntil > DateTime.UtcNow)
                    || claimedVersion != state.TokenVersion)
                {
                    context.Fail("Token has been revoked.");
                }
            },
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .Build();

    // Governor (stream D): the named admin policy — additive on top of deny-by-default.
    // The role claim is written as ClaimTypes.Role at token issue, which is what
    // RequireRole reads after the JWT handler's inbound claim-type mapping.
    options.AddPolicy(AuthorizationPolicies.AdminOnly, policy =>
        policy.RequireRole(nameof(UserRole.Admin)));
});

var app = builder.Build();

// Railway (publish cp1) terminates TLS at its edge and proxies over http, so trust the
// edge's X-Forwarded-For/-Proto: without this every client shares the proxy's IP (one
// global rate-limit bucket) and UseHttpsRedirection loops. Railway's edge IPs are dynamic,
// so the loopback-only KnownIPNetworks/KnownProxies defaults are cleared and ForwardLimit=1
// keeps only the nearest (edge-appended, unspoofable) entry.
var forwardedHeadersOptions = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = 1,
};
forwardedHeadersOptions.KnownIPNetworks.Clear();
forwardedHeadersOptions.KnownProxies.Clear();
app.UseForwardedHeaders(forwardedHeadersOptions);

// Global exception handler first, so it wraps auth and every endpoint. Logs the exception
// and returns a ProblemDetails 500 with no stack trace (safe in Production).
app.UseExceptionHandler();

// Single-origin serving (publish cp1): the SPA calls /api/* (frozen client.ts), the API
// routes live at the root. In dev the Vite proxy strips the /api prefix; deployed, this
// rewrite reproduces exactly that. Unconditional so dev, tests, and prod all share one
// routing behavior ("/api/health" keeps answering via "/health", for example).
//
// Publish fix (2026-07-23): the Vite proxy doesn't just strip the prefix — it GATEKEEPS.
// In dev only /api/* ever reaches the API, so a browser navigating to /feed gets the SPA
// from Vite. Deployed, SPA routes that share a path with a root API route (/feed,
// /users/:id, /recipes/:id) matched the API endpoint on a cold GET and 401ed before the
// SPA could load. So the else-branch: a GET that did NOT arrive /api-prefixed and
// negotiates text/html is a browser navigation (the frozen client.ts always talks
// /api/* JSON) and is rewritten to /index.html for the static-file middleware below.
// Exempt: extension-bearing paths (SPA assets, /images/*.jpg — their pipelines sit
// below) and /health (stays browser-inspectable). With no wwwroot (dev/tests) the
// rewritten path finds no file and 404s, matching the SPA fallback's behavior there.
app.Use((context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api", out var rest) && rest.HasValue)
    {
        context.Request.Path = rest;
    }
    else if (HttpMethods.IsGet(context.Request.Method)
        && !context.Request.Path.StartsWithSegments("/health")
        && !Path.HasExtension(context.Request.Path.Value)
        && context.Request.Headers.Accept.ToString().Contains("text/html", StringComparison.OrdinalIgnoreCase))
    {
        context.Request.Path = "/index.html";
    }

    return next(context);
});

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// publish cp1: the built SPA ships in wwwroot (copied in by the Dockerfile). Static-file
// middleware sits outside endpoint routing, so the auth fallback policy doesn't apply —
// correct for public assets. In dev/tests wwwroot doesn't exist and this is a no-op.
app.UseStaticFiles();

// social-feed cp04: serve stored images from the upload root at /images. Deliberately
// PUBLIC-read (decision I1): the middleware sits outside endpoint routing, so the
// RequireAuthenticatedUser fallback policy doesn't apply here — the access control is the
// unguessable server-generated GUID filename. Content types resolve from the extension
// (.jpg/.png/.webp), which ImageUploadRules pinned to the magic-byte-verified type.
// Disk mode only: in R2 mode nothing creates the local root (PhysicalFileProvider would
// throw) and images serve from the absolute R2 public URL instead.
if (!useR2ImageStorage)
{
    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(imageRootPath),
        RequestPath = "/images",
    });
}

// Explicit UseRouting so route matching happens HERE — after the /api rewrite above.
// Without it, minimal hosting auto-inserts routing at the very start of the pipeline,
// which selects an endpoint from the ORIGINAL path: every /api/* request would bypass
// the rewrite and land on the SPA fallback instead of its API route (caught by
// HealthEndpointTests in CI).
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapAuthEndpoints();
app.MapRecipeEndpoints();
app.MapChatEndpoints();
app.MapSocialEndpoints();
app.MapImageEndpoints();
app.MapMealPlanEndpoints();
app.MapReportEndpoints();
app.MapAdminEndpoints();
app.MapNotificationEndpoints();

// Anonymous liveness probe (audit 4.5): must return 200 without a bearer, otherwise the
// fallback RequireAuthenticatedUser policy makes an uptime check read the API as down.
// Root path like every other endpoint (publish cp1) — Railway's healthcheck hits /health
// directly, and the old /api/health still answers via the /api rewrite above.
app.MapGet("/health", () => Results.Ok(new { status = "ok" }))
.AllowAnonymous()
.WithName("GetHealth");

// publish cp1: SPA fallback — client-routed deep links (/login, /recipes/:id) must serve
// index.html on a cold GET. Lowest routing priority, so it never shadows an API route.
// AllowAnonymous is mandatory: the RequireAuthenticatedUser fallback policy would other-
// wise 401 the login page itself. With no wwwroot (dev/tests) a fallback hit 404s as
// before.
app.MapFallbackToFile("index.html")
.AllowAnonymous();

app.Run();

public partial class Program { }
