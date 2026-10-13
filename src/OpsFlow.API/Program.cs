using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.API.Auth;
using OpsFlow.API.Middleware;
using OpsFlow.API.Realtime;
using OpsFlow.Core.Abstractions;
using OpsFlow.Infrastructure;
using OpsFlow.Infrastructure.Persistence;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// ---- Configuration that must never silently fall back to something weak ---------------------------
var jwt = config.GetSection("Jwt").Get<JwtOptions>() ?? new JwtOptions();
if (jwt.Key.Length < JwtOptions.MinKeyLength || jwt.Key.Distinct().Count() < 8)
{
    throw new InvalidOperationException(
        $"Jwt:Key must be at least {JwtOptions.MinKeyLength} characters and not trivially repetitive. Set it via the Jwt__Key environment variable.");
}

if (!builder.Environment.IsDevelopment() && jwt.Key.StartsWith(JwtOptions.DevKeyPrefix, StringComparison.Ordinal))
{
    // The development key is committed to the repository: anyone could forge tokens with it.
    throw new InvalidOperationException("The committed development signing key must not be used outside the Development environment.");
}

var platformKey = config["Platform:ApiKey"];
if (!string.IsNullOrEmpty(platformKey) && platformKey.Length < PlatformKeyFilter.MinKeyLength)
{
    throw new InvalidOperationException($"Platform:ApiKey must be at least {PlatformKeyFilter.MinKeyLength} characters (or empty to disable the endpoint).");
}

// Nothing here legitimately needs a large body; the default (30 MB) only helps an attacker.
var maxBodyBytes = config.GetValue("Limits:MaxRequestBodyBytes", 1_000_000L);
builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = maxBodyBytes);

builder.Services.Configure<JwtOptions>(config.GetSection("Jwt"));
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddScoped<PlatformKeyFilter>();
builder.Services.AddOpsFlowInfrastructure(config);
builder.Services.AddSingleton<ConnectionRegistry>();
builder.Services.Replace(ServiceDescriptor.Singleton<ITicketEventPublisher, SignalRTicketEventPublisher>());
builder.Services.Replace(ServiceDescriptor.Singleton<IConnectionRevoker, HubConnectionRevoker>());

// Enums travel as names only. Integers are refused, so {"priority": 99} is a 400, not an undefined enum value.
JsonStringEnumConverter StrictEnums() => new(namingPolicy: null, allowIntegerValues: false);
builder.Services
    .AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(StrictEnums()));
builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(StrictEnums()));

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidIssuer = jwt.Issuer,
            ValidAudience = jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.Key)),
            ValidateIssuerSigningKey = true,
            ClockSkew = TimeSpan.FromSeconds(30),
            RoleClaimType = OpsClaims.Role,
            NameClaimType = OpsClaims.Name
        };

        // Browsers cannot set an Authorization header on a WebSocket, so the hub accepts the token
        // from the query string - for the hub path only.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                if (ctx.Request.Path.StartsWithSegments("/hubs") && ctx.Request.Query.TryGetValue("access_token", out var token))
                {
                    ctx.Token = token;
                }

                return Task.CompletedTask;
            }
        };
    });

// One clock for everything: token issuing and token validation read the same TimeProvider.
builder.Services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
    .Configure<TimeProvider>((o, time) => o.TokenValidationParameters.LifetimeValidator = (notBefore, expires, _, p) =>
    {
        var now = time.GetUtcNow().UtcDateTime;
        return expires is not null
               && (notBefore is null || notBefore.Value <= now + p.ClockSkew)
               && expires.Value > now - p.ClockSkew;
    });

// Deny by default: an endpoint is public only if it says [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    RateLimitPartition<string> PerClient(HttpContext http, int permits) => RateLimitPartition.GetFixedWindowLimiter(
        http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = permits, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 });

    o.AddPolicy("login", http => PerClient(http, config.GetValue("RateLimit:LoginPerMinute", 10)));
    o.AddPolicy("platform", http => PerClient(http, config.GetValue("RateLimit:PlatformPerMinute", 10)));
});

builder.Services.AddHsts(o =>
{
    o.MaxAge = TimeSpan.FromDays(365);
    o.IncludeSubDomains = false; // this host only: the app may share a parent domain with unrelated sites
});

builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    if (config.GetValue("Proxy:TrustForwardedHeaders", false))
    {
        // Behind a known reverse proxy / PaaS load balancer (Render, Fly, nginx ...).
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();

        // How many proxies sit in front of the app (each appends the address it saw). The client address is the entry that
        // many places from the right; anything further left was written by the client and is never trusted. Render sits
        // behind Cloudflare, so there it is 2 - with the default of 1 the limiter would key on a changing Cloudflare edge address.
        o.ForwardLimit = Math.Max(1, config.GetValue("Proxy:ForwardLimit", 1));
    }
});

var app = builder.Build();

// ---- Pipeline --------------------------------------------------------------------------------------
app.UseForwardedHeaders();

// Strict-Transport-Security is only sent on requests that arrived over HTTPS (behind a trusted proxy that means
// X-Forwarded-Proto: https), never on plain-HTTP localhost, so local development and docker compose are unaffected.
if (!app.Environment.IsDevelopment())
{
    app.UseHsts();
}

app.UseExceptionHandler(errors => errors.Run(async ctx =>
{
    var exception = ctx.Features.Get<IExceptionHandlerFeature>()?.Error;
    var logger = ctx.RequestServices.GetRequiredService<ILogger<Program>>();

    // The exception middleware wiped the response, including the headers set earlier in the pipeline.
    SecurityHeaders.Apply(ctx);

    if (exception is OperationCanceledException && ctx.RequestAborted.IsCancellationRequested)
    {
        ctx.Response.StatusCode = 499; // the client went away; not a server fault, nothing to log as an error
        return;
    }

    if (exception is BadHttpRequestException badRequest)
    {
        // A client-side protocol problem (body over the limit, malformed request, read timeout ...): report the status
        // Kestrel chose (e.g. 413) and keep it out of the error log, which is for OUR faults.
        logger.LogInformation("Rejected request: {Reason}", badRequest.Message);
        ctx.Response.StatusCode = badRequest.StatusCode;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = badRequest.StatusCode, Title = "The request was rejected." });
        return;
    }

    if (exception is TenantViolationException)
    {
        // Reaching this means code tried to cross a tenant boundary - a bug or an attack. Say little, log loudly.
        logger.LogError(exception, "Tenant isolation violation blocked.");
        ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 403, Title = "Forbidden." });
        return;
    }

    logger.LogError(exception, "Unhandled exception.");
    ctx.Response.StatusCode = StatusCodes.Status500InternalServerError;
    await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 500, Title = "An unexpected error occurred." });
}));

app.Use(async (ctx, next) =>
{
    SecurityHeaders.Apply(ctx);

    // Refuse an announced oversized body up front (Kestrel enforces the same limit for chunked bodies, which carry no length).
    if (ctx.Request.ContentLength > maxBodyBytes)
    {
        ctx.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await ctx.Response.WriteAsJsonAsync(new ProblemDetails { Status = 413, Title = "The request body is too large." });
        return;
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

// Routing is placed explicitly, after the static files. The implicit one runs first, and static files skip any request that
// already matched an endpoint - with the catch-all 404 below that would swallow the whole UI.
app.UseRouting();
app.UseRateLimiter();
app.UseAuthentication();
app.UseMiddleware<TenantResolutionMiddleware>(); // after authentication: needs the validated principal
app.UseAuthorization();

app.MapControllers();
// CloseOnAuthenticationExpiration: a live socket is closed when its token expires, so a long-lived
// connection cannot outlive the 30-minute JWT (the client reconnects and is re-validated).
app.MapHub<OpsHub>("/hubs/ops", o => o.CloseOnAuthenticationExpiration = true);

// Lets the login page offer one-click demo accounts - only when demo seeding is explicitly on.
app.MapGet("/api/meta", (IConfiguration cfg) => cfg.GetValue("Seed:Demo", false)
        ? Results.Ok(new { demo = true, demoPassword = DemoDataSeeder.DemoPassword })
        : Results.Ok(new { demo = false }))
    .AllowAnonymous();
// Unknown paths: a plain 404. Without this the deny-by-default policy answers 401 for anything that does not exist,
// which is misleading (and noisy for crawlers, favicon requests and API clients with a typo).
// ("{**path}" instead of the default pattern, which deliberately skips paths that look like files such as /favicon.ico.)
app.MapFallback("{**path}", () => Results.NotFound()).AllowAnonymous();

// TEMPORARY DIAGNOSTIC (remove): shows what the proxy chain looks like to the app.
app.MapGet("/diag/ip", (HttpContext c) => Results.Ok(new
{
    remote = c.Connection.RemoteIpAddress?.ToString(),
    originalFor = c.Request.Headers["X-Original-For"].ToString(),
    xff = c.Request.Headers["X-Forwarded-For"].ToString(),
    limit = config.GetValue("Proxy:ForwardLimit", 1),
    trust = config.GetValue("Proxy:TrustForwardedHeaders", false)
})).AllowAnonymous();

app.MapGet("/health", async (OpsFlowDbContext db, CancellationToken ct) =>
        await db.Database.CanConnectAsync(ct) ? Results.Ok(new { status = "healthy" }) : Results.StatusCode(503))
    .AllowAnonymous();

// ---- Database bootstrap ------------------------------------------------------------------------------
await using (var scope = app.Services.CreateAsyncScope())
{
    var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
    if (db.Database.IsRelational())
    {
        if (config.GetValue("Database:MigrateOnStartup", false))
        {
            await db.Database.MigrateAsync();
        }
    }
    else
    {
        await db.Database.EnsureCreatedAsync();
    }

    if (config.GetValue("Seed:Demo", false))
    {
        await scope.ServiceProvider.GetRequiredService<DemoDataSeeder>().SeedAsync();
    }
}

app.Run();

public partial class Program;
