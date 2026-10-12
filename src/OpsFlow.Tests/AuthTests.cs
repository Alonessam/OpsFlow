using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.API.Auth;
using OpsFlow.Core.Contracts;
using OpsFlow.Core.Domain;
using OpsFlow.Infrastructure.Persistence;
using OpsFlow.Infrastructure.Services;
using OpsFlow.Infrastructure.Tenancy;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

public class AuthTests
{
    private static LoginRequest Login(string slug, string email, string password) =>
        new() { TenantSlug = slug, Email = email, Password = password };

    private static async Task<(HttpStatusCode Status, string Body)> TryLoginAsync(TestApp app, string slug, string email, string password)
    {
        var response = await app.Anonymous().PostAsJsonAsync("/api/auth/login", Login(slug, email, password));
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<HttpStatusCode> MeAsync(TestApp app, string token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/auth/me");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (await app.Anonymous().SendAsync(request)).StatusCode;
    }

    /// <summary>Marks a tenant inactive straight in the database (there is deliberately no API for it yet).</summary>
    private static async Task SetTenantActiveAsync(TestApp app, Guid tenantId, bool active)
    {
        using var scope = app.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<TenantContext>().UseSystem();
        var db = scope.ServiceProvider.GetRequiredService<OpsFlowDbContext>();
        (await db.Tenants.FindAsync(tenantId))!.IsActive = active;
        await db.SaveChangesAsync();
    }

    // =====================================================================================================
    // Login
    // =====================================================================================================

    [Fact]
    public async Task Login_returns_a_token_and_the_callers_identity()
    {
        using var app = new TestApp();
        var tenant = await app.CreateTenantAsync("login-ok");

        var response = await app.Anonymous().PostAsJsonAsync("/api/auth/login", Login("LOGIN-OK ", "ADMIN@login-ok.test", TestApp.Password));
        var body = await response.Content.ReadFromJsonAsync<LoginResponse>(TestApp.Json);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(UserRole.TenantAdmin, body!.User.Role);
        Assert.Equal(tenant.Tenant.Id, body.Tenant.Id);
        Assert.False(string.IsNullOrEmpty(body.AccessToken));
    }

    [Fact]
    public async Task Every_way_of_failing_to_log_in_looks_exactly_the_same()
    {
        using var app = new TestApp();
        var tenant = await app.CreateTenantAsync("enum-test");
        await tenant.AddUserAsync(UserRole.Agent, "agent");
        var retired = await tenant.AddUserAsync(UserRole.Agent, "retired");
        await tenant.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{retired.Id}/status", new { isActive = false });
        var inactiveTenant = await app.CreateTenantAsync("enum-closed");
        await SetTenantActiveAsync(app, inactiveTenant.Tenant.Id, false);

        for (var i = 0; i < AuthService.MaxFailedAttempts; i++) // lock one real account
        {
            await TryLoginAsync(app, "enum-test", "admin@enum-test.test", "wrong-wrong-wrong");
        }

        var results = new[]
        {
            await TryLoginAsync(app, "enum-test", "retired@enum-test.test", TestApp.Password),   // deactivated user, RIGHT password
            await TryLoginAsync(app, "enum-test", "ghost@enum-test.test", "nope-nope-nope"),     // unknown user
            await TryLoginAsync(app, "no-such-tenant", "admin@enum-test.test", "nope-nope-nope"),// unknown tenant
            await TryLoginAsync(app, "enum-closed", "admin@enum-closed.test", TestApp.Password), // inactive tenant, right password
            await TryLoginAsync(app, "enum-test", "admin@enum-test.test", TestApp.Password),     // locked account, RIGHT password
        };
        var wrongPassword = await TryLoginAsync(app, "enum-test", "agent@enum-test.test", "nope-nope-nope");

        Assert.Equal(HttpStatusCode.Unauthorized, wrongPassword.Status);
        Assert.All(results, r => Assert.Equal(wrongPassword, r)); // same status AND same body: no account-state oracle
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_against_the_right_password_until_it_expires()
    {
        using var app = new TestApp();
        await app.CreateTenantAsync("lockout");

        for (var i = 0; i < AuthService.MaxFailedAttempts; i++)
        {
            await TryLoginAsync(app, "lockout", "admin@lockout.test", "wrong-wrong-wrong");
        }

        var locked = await TryLoginAsync(app, "lockout", "admin@lockout.test", TestApp.Password);
        app.Clock.Advance(AuthService.LockoutDuration + TimeSpan.FromSeconds(1));
        var afterExpiry = await TryLoginAsync(app, "lockout", "admin@lockout.test", TestApp.Password);

        Assert.Equal(HttpStatusCode.Unauthorized, locked.Status);
        Assert.Equal(HttpStatusCode.OK, afterExpiry.Status);
    }

    [Fact]
    public async Task Parallel_wrong_guesses_cannot_dodge_the_lockout()
    {
        using var app = new TestApp();
        await app.CreateTenantAsync("lockrace");

        await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => TryLoginAsync(app, "lockrace", "admin@lockrace.test", "wrong-wrong-wrong")));

        var withRightPassword = await TryLoginAsync(app, "lockrace", "admin@lockrace.test", TestApp.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, withRightPassword.Status); // 12 attempts must have tripped the 5-strike lock
    }

    [Fact]
    public async Task A_successful_login_resets_the_failure_counter()
    {
        using var app = new TestApp();
        await app.CreateTenantAsync("reset");

        for (var round = 0; round < 3; round++)
        {
            for (var i = 0; i < AuthService.MaxFailedAttempts - 1; i++)
            {
                await TryLoginAsync(app, "reset", "admin@reset.test", "wrong-wrong-wrong");
            }

            Assert.Equal(HttpStatusCode.OK, (await TryLoginAsync(app, "reset", "admin@reset.test", TestApp.Password)).Status);
        }
    }

    [Fact]
    public async Task Passwords_longer_than_bcrypts_72_byte_limit_are_fully_significant()
    {
        using var app = new TestApp();
        var tenant = await app.CreateTenantAsync("longpass");
        var prefix = new string('x', 80);
        await tenant.Admin.PostAsync<UserDto>("/api/users", new CreateUserRequest { Email = "long@longpass.test", FullName = "Long Pass", Role = UserRole.Employee, Password = prefix + "AAA" });

        var right = await TryLoginAsync(app, "longpass", "long@longpass.test", prefix + "AAA");
        var sameFirst72 = await TryLoginAsync(app, "longpass", "long@longpass.test", prefix + "BBB");

        Assert.Equal(HttpStatusCode.OK, right.Status);
        Assert.Equal(HttpStatusCode.Unauthorized, sameFirst72.Status); // plain bcrypt would have accepted this one
    }

    // =====================================================================================================
    // Revocation
    // =====================================================================================================

    [Fact]
    public async Task Deactivating_a_user_revokes_a_token_that_was_already_in_use()
    {
        using var app = new TestApp();
        var tenant = await app.CreateTenantAsync("revoke");
        var agent = await tenant.AddUserAsync(UserRole.Agent, "agent");
        Assert.Equal(HttpStatusCode.OK, (await agent.Http.GetAsync("/api/auth/me")).StatusCode); // warms the 30 s access cache

        var (response, _) = await tenant.Admin.SendAsync<UserDto>(HttpMethod.Patch, $"/api/users/{agent.Id}/status", new { isActive = false });
        var withOldToken = await agent.Http.GetAsync("/api/auth/me");
        var relogin = await TryLoginAsync(app, "revoke", "agent@revoke.test", TestApp.Password);

        Assert.True(response.IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, withOldToken.StatusCode); // immediately, not after the cache expires
        Assert.Equal(HttpStatusCode.Unauthorized, relogin.Status);
    }

    [Fact]
    public async Task An_inactive_tenant_is_locked_out_even_with_valid_tokens()
    {
        using var app = new TestApp();
        var tenant = await app.CreateTenantAsync("suspended");
        var agent = await tenant.AddUserAsync(UserRole.Agent, "agent"); // never used since login: cache is cold

        await SetTenantActiveAsync(app, tenant.Tenant.Id, false);

        Assert.Equal(HttpStatusCode.Unauthorized, (await agent.Http.GetAsync("/api/tickets")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await TryLoginAsync(app, "suspended", "agent@suspended.test", TestApp.Password)).Status);
    }

    // =====================================================================================================
    // Tokens
    // =====================================================================================================

    /// <summary>Builds a token the way the server would, but with arbitrary content. Times come from the app's clock.</summary>
    private static string Token(TestApp app, string key = TestApp.JwtKey, string issuer = "opsflow", string audience = "opsflow-clients",
        Guid? tenantId = null, Guid? userId = null, string role = "TenantAdmin", string? slug = "x", bool dropTenantClaim = false,
        TimeSpan? lifetime = null)
    {
        var now = app.Clock.GetUtcNow().UtcDateTime;
        var life = lifetime ?? TimeSpan.FromHours(1);
        var notBefore = life > TimeSpan.Zero ? now.AddMinutes(-1) : now + life - TimeSpan.FromHours(1); // an expired token started before it ended
        var claims = new List<Claim> { new(OpsClaims.UserId, (userId ?? Guid.NewGuid()).ToString()), new(OpsClaims.Role, role) };
        if (!dropTenantClaim) claims.Add(new Claim(OpsClaims.TenantId, (tenantId ?? Guid.NewGuid()).ToString()));
        if (slug is not null) claims.Add(new Claim(OpsClaims.TenantSlug, slug));
        var credentials = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(issuer, audience, claims, notBefore, now + life, credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    [Fact]
    public async Task Forged_or_malformed_tokens_are_all_rejected()
    {
        using var app = new TestApp();
        var good = await app.CreateTenantAsync("forge");
        string Make(string key = TestApp.JwtKey, string issuer = "opsflow", string audience = "opsflow-clients", TimeSpan? lifetime = null) =>
            Token(app, key, issuer, audience, good.Tenant.Id, good.Admin.Id, "TenantAdmin", "forge", lifetime: lifetime);
        var unsigned = Convert.ToBase64String("{\"alg\":\"none\",\"typ\":\"JWT\"}"u8.ToArray()).TrimEnd('=') + "."
                       + Convert.ToBase64String(Encoding.UTF8.GetBytes($"{{\"sub\":\"{good.Admin.Id}\",\"tid\":\"{good.Tenant.Id}\",\"slug\":\"forge\",\"role\":\"TenantAdmin\",\"iss\":\"opsflow\",\"aud\":\"opsflow-clients\",\"exp\":9999999999}}")).TrimEnd('=') + ".";

        Assert.Equal(HttpStatusCode.OK, await MeAsync(app, Make())); // control: the helper produces a token the server accepts

        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, Make(key: "a-completely-different-signing-key-0123456789")));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, Make(issuer: "evil")));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, Make(audience: "evil")));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, Make(lifetime: TimeSpan.FromHours(-2)))); // expired two hours ago
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, unsigned));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, "not.a.jwt"));
        Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, ""));
    }

    [Fact]
    public async Task A_correctly_signed_token_that_lacks_a_valid_tenant_context_is_refused()
    {
        using var app = new TestApp();
        var good = await app.CreateTenantAsync("claims");

        var control = Token(app, tenantId: good.Tenant.Id, userId: good.Admin.Id, slug: "claims");
        var missingTenant = Token(app, userId: good.Admin.Id, dropTenantClaim: true, slug: "claims");
        var missingSlug = Token(app, tenantId: good.Tenant.Id, userId: good.Admin.Id, slug: null);
        var unknownRole = Token(app, tenantId: good.Tenant.Id, userId: good.Admin.Id, role: "SuperUser", slug: "claims");
        var numericRole = Token(app, tenantId: good.Tenant.Id, userId: good.Admin.Id, role: "99", slug: "claims");
        var unknownTenant = Token(app, tenantId: Guid.NewGuid(), userId: good.Admin.Id, slug: "claims"); // real user id, wrong tenant
        var unknownUser = Token(app, tenantId: good.Tenant.Id, userId: Guid.NewGuid(), slug: "claims");

        Assert.Equal(HttpStatusCode.OK, await MeAsync(app, control)); // control: only the defects below make the difference
        foreach (var token in new[] { missingTenant, missingSlug, unknownRole, numericRole, unknownTenant, unknownUser })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, await MeAsync(app, token));
        }
    }
    [Fact]
    public async Task An_expired_token_is_rejected()
    {
        using var app = TestApp.With(jwtMinutes: 5);
        var tenant = await app.CreateTenantAsync("expiry");

        Assert.Equal(HttpStatusCode.OK, (await tenant.Admin.Http.GetAsync("/api/auth/me")).StatusCode);
        app.Clock.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(HttpStatusCode.Unauthorized, (await tenant.Admin.Http.GetAsync("/api/auth/me")).StatusCode);
    }

    // =====================================================================================================
    // Rate limits and the platform endpoint
    // =====================================================================================================

    [Fact]
    public async Task Login_is_rate_limited()
    {
        using var app = TestApp.With(loginLimit: 3);
        var codes = new List<HttpStatusCode>();

        for (var i = 0; i < 5; i++)
        {
            codes.Add((await TryLoginAsync(app, "x", "a@b.test", "whatever-pass")).Status);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task The_platform_endpoint_is_rate_limited_too()
    {
        using var app = TestApp.With(platformLimit: 3);
        static HttpRequestMessage Request() => new(HttpMethod.Post, "/api/platform/tenants") { Content = JsonContent.Create(new { }) };

        var codes = new List<HttpStatusCode>();
        for (var i = 0; i < 5; i++)
        {
            using var request = Request();
            request.Headers.Add("X-Platform-Key", "guess-guess-guess-guess");
            codes.Add((await app.Anonymous().SendAsync(request)).StatusCode);
        }

        Assert.Equal(HttpStatusCode.TooManyRequests, codes[^1]);
        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Unauthorized));
    }

    [Fact]
    public async Task Provisioning_a_tenant_creates_a_working_admin_and_the_default_policies()
    {
        using var app = new TestApp();

        var tenant = await app.CreateTenantAsync("fresh");
        var (_, policies) = await tenant.Admin.GetAsync<List<SlaPolicyDto>>("/api/sla-policies");

        Assert.Equal(UserRole.TenantAdmin, tenant.Admin.User.Role);
        Assert.Equal(4, policies!.Count);
        Assert.Equal(new SlaPolicyDto(TicketPriority.Critical, 30, 120), policies[0]);
    }

    [Fact]
    public async Task Tenant_provisioning_checks_the_platform_key_before_it_even_looks_at_the_body()
    {
        using var app = new TestApp();
        var valid = new CreateTenantRequest { Name = "Nope", Slug = "no-key", AdminEmail = "a@no-key.test", AdminFullName = "No Key", AdminPassword = TestApp.Password };

        var none = await app.Anonymous().PostAsJsonAsync("/api/platform/tenants", valid);
        using var wrong = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants") { Content = JsonContent.Create(valid) };
        wrong.Headers.Add("X-Platform-Key", "guess");
        var wrongKey = await app.Anonymous().SendAsync(wrong);
        using var garbageBody = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants") { Content = new StringContent("{\"slug\":\"a\"}", Encoding.UTF8, "application/json") };
        garbageBody.Headers.Add("X-Platform-Key", "guess");
        var wrongKeyAndGarbage = await app.Anonymous().SendAsync(garbageBody);

        Assert.Equal(HttpStatusCode.Unauthorized, none.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKey.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, wrongKeyAndGarbage.StatusCode); // 401, not 400: validation errors are not leaked to strangers
    }

    [Fact]
    public async Task Tenant_provisioning_is_disabled_when_no_key_is_configured()
    {
        using var app = TestApp.With(platformEnabled: false);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants")
        {
            Content = JsonContent.Create(new CreateTenantRequest { Name = "Nope", Slug = "disabled", AdminEmail = "a@x.test", AdminFullName = "No Key", AdminPassword = TestApp.Password })
        };
        request.Headers.Add("X-Platform-Key", "");
        using var garbage = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants") { Content = new StringContent("{}", Encoding.UTF8, "application/json") };

        Assert.Equal(HttpStatusCode.NotFound, (await app.Anonymous().SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await app.Anonymous().SendAsync(garbage)).StatusCode); // even an invalid body: it simply does not exist
    }

    [Theory]
    [InlineData("taken", HttpStatusCode.Conflict)]
    [InlineData("TAKEN", HttpStatusCode.BadRequest)]       // slugs are lower-case only
    [InlineData("Has Spaces", HttpStatusCode.BadRequest)]
    [InlineData("ab", HttpStatusCode.BadRequest)]
    [InlineData("-leading", HttpStatusCode.BadRequest)]
    [InlineData("trailing-", HttpStatusCode.BadRequest)]
    public async Task Duplicate_or_malformed_slugs_are_rejected(string slug, HttpStatusCode expected)
    {
        using var app = new TestApp();
        await app.CreateTenantAsync("taken");

        var status = await Provision(app, slug, "a@x.test", TestApp.Password);

        Assert.Equal(expected, status);
    }

    [Theory]
    [InlineData("not-an-email", "long-enough-pass")]
    [InlineData("a@x.test", "short")]
    public async Task Provisioning_validates_the_admin_account(string email, string password)
    {
        using var app = new TestApp();

        Assert.Equal(HttpStatusCode.BadRequest, await Provision(app, "valid-slug", email, password));
    }

    private static async Task<HttpStatusCode> Provision(TestApp app, string slug, string email, string password)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/platform/tenants")
        {
            Content = JsonContent.Create(new CreateTenantRequest { Name = "X Y", Slug = slug, AdminEmail = email, AdminFullName = "Ad Min", AdminPassword = password })
        };
        request.Headers.Add("X-Platform-Key", TestApp.PlatformKey);
        return (await app.Anonymous().SendAsync(request)).StatusCode;
    }

    // =====================================================================================================
    // Start-up guards
    // =====================================================================================================

    private static Exception StartupFailure(string environment, params (string Key, string Value)[] settings)
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment(environment);
            b.UseSetting("Database:Provider", "InMemory");
            b.UseSetting("Jwt:Key", TestApp.JwtKey);
            foreach (var (key, value) in settings) b.UseSetting(key, value);
        });

        return Assert.ThrowsAny<Exception>(() => factory.CreateClient());
    }

    [Fact]
    public void The_app_refuses_to_start_with_a_weak_signing_key()
    {
        Assert.Contains("Jwt:Key", StartupFailure("Testing", ("Jwt:Key", "too-short")).ToString());
        Assert.Contains("Jwt:Key", StartupFailure("Testing", ("Jwt:Key", new string('a', 40))).ToString()); // long but trivially repetitive
    }

    [Fact]
    public void The_committed_development_key_is_refused_outside_development()
    {
        var devKey = "dev-only-key-change-me-0123456789abcdef-opsflow";

        Assert.Contains("development signing key", StartupFailure("Production", ("Jwt:Key", devKey)).ToString());
        Assert.Contains("development signing key", StartupFailure("Staging", ("Jwt:Key", devKey)).ToString());
    }

    [Fact]
    public void A_short_platform_key_is_refused_and_an_empty_one_just_disables_the_endpoint()
    {
        Assert.Contains("Platform:ApiKey", StartupFailure("Testing", ("Platform:ApiKey", "short")).ToString());
    }
}




