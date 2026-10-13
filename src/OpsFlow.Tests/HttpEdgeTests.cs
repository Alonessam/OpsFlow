using System.Net;
using System.Text;
using OpsFlow.Tests.Support;

namespace OpsFlow.Tests;

/// <summary>Behaviour at the edges of the HTTP surface that only shows up on a real deployment (found by the live checks).</summary>
public class HttpEdgeTests : IClassFixture<TestApp>
{
    private readonly TestApp _app;

    public HttpEdgeTests(TestApp app) => _app = app;

    [Theory]
    [InlineData("/nope")]
    [InlineData("/api/nope")]
    [InlineData("/favicon.ico")]
    [InlineData("/robots.txt")]
    [InlineData("/.env")]
    [InlineData("/appsettings.json")]
    [InlineData("/swagger")]
    [InlineData("/hubs/nope")]
    public async Task Unknown_paths_are_a_plain_404_not_a_misleading_401(string path)
    {
        var response = await _app.Anonymous().GetAsync(path);

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Theory]
    [InlineData("/", "text/html")]
    [InlineData("/index.html", "text/html")]
    [InlineData("/js/app.js", "javascript")]
    [InlineData("/css/app.css", "text/css")]
    [InlineData("/lib/signalr.min.js", "javascript")]
    public async Task The_web_ui_is_served_anonymously_despite_the_catch_all_404(string path, string contentType)
    {
        // Regression: the catch-all endpoint matched "/" and every asset first, and static files skip a request that already
        // has an endpoint - the whole UI answered 404 on the live site while every API test stayed green.
        var response = await _app.Anonymous().GetAsync(path);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(contentType, response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task Unknown_paths_do_not_weaken_the_deny_by_default_rule_for_real_endpoints()
    {
        var anonymous = _app.Anonymous();

        foreach (var path in new[] { "/api/tickets", "/api/users", "/api/dashboard/summary", "/api/sla-policies", "/api/auth/me" })
        {
            Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(path)).StatusCode);
        }
    }

    [Fact]
    public async Task The_application_files_that_are_not_meant_to_be_served_are_not_served()
    {
        // The static root is wwwroot only. Source, configuration and build output must be unreachable by any path.
        foreach (var path in new[] { "/appsettings.json", "/appsettings.Production.json", "/OpsFlow.API.dll", "/OpsFlow.API.csproj", "/Program.cs", "/../appsettings.json", "/..%2fappsettings.json" })
        {
            var response = await _app.Anonymous().GetAsync(path);
            Assert.True(response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest, $"{path} -> {(int)response.StatusCode}");
        }
    }

    [Fact]
    public async Task An_oversized_body_is_refused_with_413_before_it_is_read_even_without_a_token()
    {
        var big = new StringContent("{\"x\":\"" + new string('a', 1_300_000) + "\"}", Encoding.UTF8, "application/json");

        var response = await _app.Anonymous().PostAsync("/api/auth/login", big);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Contains("application/json", response.Content.Headers.ContentType?.ToString());
    }

    [Fact]
    public async Task A_body_just_under_the_limit_is_still_processed_normally()
    {
        var body = new StringContent("{\"tenantSlug\":\"x\",\"email\":\"a@b.test\",\"password\":\"" + new string('p', 100) + "\"}", Encoding.UTF8, "application/json");

        var response = await _app.Anonymous().PostAsync("/api/auth/login", body);

        Assert.NotEqual(HttpStatusCode.RequestEntityTooLarge, response.StatusCode); // 400/401 from validation or credentials, not 413
    }

    private static async Task<HttpStatusCode> BadLogin(TestApp app, string? forwardedFor)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/auth/login")
        {
            Content = JsonContent.Create(new { tenantSlug = "x", email = "a@b.test", password = "wrong-password" })
        };
        if (forwardedFor is not null) request.Headers.Add("X-Forwarded-For", forwardedFor);
        return (await app.Anonymous().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task Behind_a_trusted_proxy_the_login_limit_is_kept_per_real_client_not_per_proxy()
    {
        using var app = TestApp.With(loginLimit: 3, trustProxy: true, remoteIp: "203.0.113.9");

        var firstClient = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) firstClient.Add(await BadLogin(app, "198.51.100.1"));
        var secondClient = await BadLogin(app, "198.51.100.2");

        Assert.Equal(HttpStatusCode.TooManyRequests, firstClient[3]);   // client 1 is limited...
        Assert.Equal(HttpStatusCode.Unauthorized, secondClient);        // ...client 2, behind the same proxy, is not
    }

    [Fact]
    public async Task A_forged_leftmost_forwarded_for_entry_cannot_dodge_the_limit_behind_a_trusted_proxy()
    {
        using var app = TestApp.With(loginLimit: 3, trustProxy: true, remoteIp: "203.0.113.9");

        // The proxy APPENDS the real client address; whatever the client prepends is attacker-controlled and must not matter.
        var results = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) results.Add(await BadLogin(app, $"6.6.6.{i}, 198.51.100.1"));

        Assert.Equal(HttpStatusCode.TooManyRequests, results[3]);
    }

    [Fact]
    public async Task Without_a_trusted_proxy_forwarded_for_is_ignored_entirely_so_it_cannot_be_used_to_dodge_the_limit()
    {
        using var app = TestApp.With(loginLimit: 3, trustProxy: false, remoteIp: "203.0.113.9");

        var results = new List<HttpStatusCode>();
        for (var i = 0; i < 4; i++) results.Add(await BadLogin(app, $"198.51.100.{i + 10}")); // a different "client" every time

        Assert.Equal(HttpStatusCode.TooManyRequests, results[3]);
    }

    [Fact]
    public async Task Hsts_is_sent_only_on_requests_that_arrived_over_https_behind_a_trusted_proxy()
    {
        using var behindProxy = TestApp.With(trustProxy: true, remoteIp: "203.0.113.9"); // a proxy that is NOT on loopback

        // (The HSTS middleware never fires for localhost by design, so the request names a real-looking host.)
        using var https = new HttpRequestMessage(HttpMethod.Get, "/health");
        https.Headers.Host = "opsflow.example.test";
        https.Headers.Add("X-Forwarded-Proto", "https");
        var secure = await behindProxy.Anonymous().SendAsync(https);
        using var http = new HttpRequestMessage(HttpMethod.Get, "/health");
        http.Headers.Host = "opsflow.example.test";
        var plain = await behindProxy.Anonymous().SendAsync(http);

        Assert.True(secure.Headers.TryGetValues("Strict-Transport-Security", out var values), "HSTS missing on an HTTPS request");
        Assert.Contains("max-age=31536000", values!.Single());
        Assert.DoesNotContain("includeSubDomains", values!.Single());
        Assert.False(plain.Headers.Contains("Strict-Transport-Security")); // never over plain HTTP
    }

    [Fact]
    public async Task Forwarded_headers_from_an_untrusted_peer_are_ignored_so_https_cannot_be_faked()
    {
        using var notBehindProxy = TestApp.With(trustProxy: false, remoteIp: "203.0.113.9"); // an arbitrary internet peer

        using var request = new HttpRequestMessage(HttpMethod.Get, "/health");
        request.Headers.Host = "opsflow.example.test";
        request.Headers.Add("X-Forwarded-Proto", "https");
        var response = await notBehindProxy.Anonymous().SendAsync(request);

        Assert.False(response.Headers.Contains("Strict-Transport-Security"));
    }
}
