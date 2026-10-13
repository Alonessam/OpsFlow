using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using OpsFlow.Core.Abstractions;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;

namespace OpsFlow.Tests.Support;

internal sealed class RemotePeerFilter(System.Net.IPAddress address) : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = address;
            await nextMiddleware();
        });
        next(app);
    };
}

public sealed class FakeClock : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 15, 9, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan by) => _now += by;
}

public sealed class CapturingPublisher : ITicketEventPublisher
{
    private readonly List<TicketEvent> _events = [];
    public IReadOnlyList<TicketEvent> Events { get { lock (_events) { return _events.ToList(); } } }

    /// <summary>When set, the next publish throws once - simulates a broken push channel.</summary>
    public volatile bool FailNext;

    public Task PublishAsync(TicketEvent ticketEvent, CancellationToken cancellationToken = default)
    {
        if (FailNext)
        {
            FailNext = false;
            throw new InvalidOperationException("Simulated push failure.");
        }

        lock (_events) { _events.Add(ticketEvent); }
        return Task.CompletedTask;
    }
}

/// <summary>The real application, in memory, with a controllable clock and event capture.</summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public const string PlatformKey = "test-platform-key";
    public const string Password = "Sup3r-Secret-Pass!";
    public const string JwtKey = "unit-test-signing-key-0123456789-abcdefghijklmnop";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly string _dbName = $"opsflow-test-{Guid.NewGuid():N}";
    private readonly int _loginLimit;
    private readonly int _platformLimit;
    private readonly bool _platformEnabled;
    private readonly int _jwtMinutes;
    private readonly bool _realPublisher;
    private readonly string? _postgres;
    private readonly bool _seedDemo;
    private readonly bool _trustProxy;
    private readonly string? _remoteIp;

    // Tokens are issued by the fake clock (Jan 2026) but SignalR checks their expiry against the real clock
    // when it closes sockets on expiry, so the default lifetime is long enough to still be valid "now".
    public const int LongLivedMinutes = 2_000_000;

    public TestApp() : this(10_000, 10_000, true, LongLivedMinutes, false, null, false, false, null)
    {
    }

    public static TestApp With(int loginLimit = 10_000, bool platformEnabled = true, int jwtMinutes = LongLivedMinutes,
        bool realPublisher = false, string? postgres = null, int platformLimit = 10_000, bool seedDemo = false, bool trustProxy = false, string? remoteIp = null) =>
        new(loginLimit, platformLimit, platformEnabled, jwtMinutes, realPublisher, postgres, seedDemo, trustProxy, remoteIp);

    private TestApp(int loginLimit, int platformLimit, bool platformEnabled, int jwtMinutes, bool realPublisher, string? postgres, bool seedDemo, bool trustProxy, string? remoteIp)
    {
        _trustProxy = trustProxy;
        _remoteIp = remoteIp;
        _loginLimit = loginLimit;
        _platformLimit = platformLimit;
        _platformEnabled = platformEnabled;
        _jwtMinutes = jwtMinutes;
        _realPublisher = realPublisher;
        _postgres = postgres;
        _seedDemo = seedDemo;
    }

    public FakeClock Clock { get; } = new();
    public CapturingPublisher Publisher { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        if (_postgres is null)
        {
            builder.UseSetting("Database:Provider", "InMemory");
            builder.UseSetting("Database:InMemoryName", _dbName);
        }
        else
        {
            builder.UseSetting("Database:Provider", "Postgres");
            builder.UseSetting("ConnectionStrings:Default", _postgres);
            builder.UseSetting("Database:MigrateOnStartup", "true");
        }

        builder.UseSetting("Jwt:Key", JwtKey);
        builder.UseSetting("Jwt:ExpiryMinutes", _jwtMinutes.ToString());
        builder.UseSetting("Security:BcryptWorkFactor", "4");
        builder.UseSetting("Sla:WorkerEnabled", "false");
        builder.UseSetting("RateLimit:LoginPerMinute", _loginLimit.ToString());
        builder.UseSetting("Platform:ApiKey", _platformEnabled ? PlatformKey : "");
        builder.UseSetting("RateLimit:PlatformPerMinute", _platformLimit.ToString());
        builder.UseSetting("Seed:Demo", _seedDemo ? "true" : "false");
        builder.UseSetting("Proxy:TrustForwardedHeaders", _trustProxy ? "true" : "false");

        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<TimeProvider>();
            services.AddSingleton<TimeProvider>(Clock);
            if (_remoteIp is not null)
            {
                // The in-memory test server has no real peer address. Give the connection one, so that forwarded-header and
                // per-IP rate-limit behaviour can be tested as it happens behind a proxy (default: loopback = trusted).
                services.AddTransient<IStartupFilter>(_ => new RemotePeerFilter(System.Net.IPAddress.Parse(_remoteIp)));
            }

            if (!_realPublisher)
            {
                services.RemoveAll<ITicketEventPublisher>();
                services.AddSingleton<ITicketEventPublisher>(Publisher);
            }
        });
    }

    public HttpClient Anonymous() => CreateClient();

    public async Task<TenantHarness> CreateTenantAsync(string slug)
    {
        var anon = Anonymous();
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants")
        {
            Content = JsonContent.Create(new CreateTenantRequest
            {
                Name = $"{slug} inc",
                Slug = slug,
                AdminEmail = $"admin@{slug}.test",
                AdminFullName = $"{slug} admin",
                AdminPassword = Password
            })
        };
        request.Headers.Add("X-Platform-Key", PlatformKey);
        var response = await anon.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var tenant = (await response.Content.ReadFromJsonAsync<TenantDto>(Json))!;

        var admin = await LoginAsync(slug, $"admin@{slug}.test");
        return new TenantHarness(this, tenant, admin);
    }

    public async Task<Actor> LoginAsync(string slug, string email, string password = Password)
    {
        var client = CreateClient();
        var response = await client.PostAsJsonAsync("/api/auth/login",
            new LoginRequest { TenantSlug = slug, Email = email, Password = password });
        response.EnsureSuccessStatusCode();
        var login = (await response.Content.ReadFromJsonAsync<LoginResponse>(Json))!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", login.AccessToken);
        return new Actor(client, login.User, login.AccessToken);
    }
}

public sealed record Actor(HttpClient Http, UserDto User, string Token)
{
    public Guid Id => User.Id;

    public async Task<(HttpResponseMessage Response, T? Body)> SendAsync<T>(HttpMethod method, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: TestApp.Json);
        }

        var response = await Http.SendAsync(request);
        var content = response.IsSuccessStatusCode && response.Content.Headers.ContentLength != 0
            ? await response.Content.ReadFromJsonAsync<T>(TestApp.Json)
            : default;
        return (response, content);
    }

    public Task<(HttpResponseMessage Response, T? Body)> GetAsync<T>(string url) => SendAsync<T>(HttpMethod.Get, url);
    public Task<(HttpResponseMessage Response, T? Body)> PostAsync<T>(string url, object body) => SendAsync<T>(HttpMethod.Post, url, body);
    public Task<(HttpResponseMessage Response, T? Body)> PatchAsync<T>(string url, object body) => SendAsync<T>(HttpMethod.Patch, url, body);

    public async Task<TicketDto> CreateTicketAsync(string title = "Printer on fire", TicketPriority priority = TicketPriority.Medium)
    {
        var (response, ticket) = await PostAsync<TicketDto>("/api/tickets",
            new CreateTicketRequest { Title = title, Description = "It is smoking quite a lot.", Priority = priority });
        response.EnsureSuccessStatusCode();
        return ticket!;
    }

    public async Task<TicketDto> GetTicketAsync(Guid id)
    {
        var (response, ticket) = await GetAsync<TicketDto>($"/api/tickets/{id}");
        response.EnsureSuccessStatusCode();
        return ticket!;
    }

    public Task<(HttpResponseMessage Response, TicketDto? Body)> TransitionAsync(
        TicketDto ticket, TicketStatus to, Guid? assignee = null, string? summary = null, int? version = null) =>
        PostAsync<TicketDto>($"/api/tickets/{ticket.Id}/transitions", new TransitionRequest
        {
            To = to,
            AssigneeId = assignee,
            ResolutionSummary = summary,
            Version = version ?? ticket.Version
        });
}

public sealed class TenantHarness(TestApp app, TenantDto tenant, Actor admin)
{
    public TenantDto Tenant { get; } = tenant;
    public Actor Admin { get; } = admin;

    public async Task<Actor> AddUserAsync(UserRole role, string label)
    {
        var email = $"{label}@{Tenant.Slug}.test";
        var (response, _) = await Admin.PostAsync<UserDto>("/api/users", new CreateUserRequest
        {
            Email = email,
            FullName = $"{label} {Tenant.Slug}",
            Role = role,
            Password = TestApp.Password
        });
        response.EnsureSuccessStatusCode();
        return await app.LoginAsync(Tenant.Slug, email);
    }
}








