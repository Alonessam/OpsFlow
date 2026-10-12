using System.Text.Json;
using System.Text.RegularExpressions;

namespace OpsFlow.Tests;

/// <summary>
/// Source-level guard rails: the few dangerous APIs may only appear in the files that are allowed to use
/// them, so a reviewer cannot miss a new cross-tenant escape hatch. These run as part of <c>dotnet test</c>
/// (not of the compile step), and every list below is itself checked for staleness.
/// </summary>
public class ArchitectureGuardTests
{
    private static readonly string Root = FindRoot();
    private static readonly string SrcRoot = Path.Combine(Root, "src");

    private sealed record Source(string Project, string RelativePath, string Text)
    {
        public string Name => System.IO.Path.GetFileName(RelativePath);
    }

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "OpsFlow.slnx")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName ?? throw new InvalidOperationException("Could not locate OpsFlow.slnx.");
    }

    /// <summary>Production sources only. Paths are judged RELATIVE to src/, so a checkout under a folder that happens to be called "bin" still works.</summary>
    private static IReadOnlyList<Source> ProductionSources()
    {
        var sources = Directory.EnumerateFiles(SrcRoot, "*.cs", SearchOption.AllDirectories)
            .Select(f => System.IO.Path.GetRelativePath(SrcRoot, f).Replace('\\', '/'))
            .Where(rel =>
            {
                var parts = rel.Split('/');
                return parts[0] != "OpsFlow.Tests" && !parts.Contains("obj") && !parts.Contains("bin") && !parts.Contains("Migrations");
            })
            .Select(rel => new Source(rel.Split('/')[0], rel, File.ReadAllText(System.IO.Path.Combine(SrcRoot, rel))))
            .ToList();

        // A guard that scans nothing passes vacuously. Fail loudly instead.
        Assert.True(sources.Count >= 25, $"Only {sources.Count} production files were found under {SrcRoot}; the scan is broken.");
        foreach (var project in new[] { "OpsFlow.Core", "OpsFlow.Infrastructure", "OpsFlow.API" })
        {
            Assert.Contains(sources, s => s.Project == project);
        }

        return sources;
    }

    private static string StripComment(string line) => Regex.Replace(line, @"//.*$", "");
    private static string StripComments(string text) => Regex.Replace(text, @"//.*$", "", RegexOptions.Multiline);

    private static void AssertOnlyIn(string token, params string[] allowedFiles)
    {
        var sources = ProductionSources();

        foreach (var allowed in allowedFiles)
        {
            Assert.True(sources.Any(s => s.Name == allowed), $"Allow-list entry '{allowed}' no longer exists - remove it from the list.");
        }

        var offenders = sources
            .Where(s => s.Text.Contains(token, StringComparison.Ordinal) && !allowedFiles.Contains(s.Name))
            .Select(s => s.RelativePath)
            .ToList();

        Assert.True(offenders.Count == 0, $"'{token}' is only allowed in [{string.Join(", ", allowedFiles)}] but found in: {string.Join(", ", offenders)}");
    }

    [Fact]
    public void IgnoreQueryFilters_is_only_used_by_the_cross_tenant_sla_sweep() =>
        AssertOnlyIn("IgnoreQueryFilters", "SlaMonitor.cs");

    [Fact]
    public void The_mutable_tenant_context_type_is_only_visible_to_the_files_that_must_set_it()
    {
        // Matching the TYPE name (not a variable name) means renaming the variable cannot dodge the check.
        // These are the places that legitimately resolve a tenant: login, the middleware, system jobs, wiring.
        AssertOnlyIn("TenantContext",
            "TenantContext.cs", "TenantResolutionMiddleware.cs", "AccountServices.cs", "SlaMonitor.cs", "DemoDataSeeder.cs", "DependencyInjection.cs");
    }

    [Fact]
    public void System_scope_can_only_be_entered_by_jobs_that_are_meant_to_be_cross_tenant() =>
        AssertOnlyIn("UseSystem(", "TenantContext.cs", "SlaMonitor.cs", "AccountServices.cs", "DemoDataSeeder.cs");

    [Fact]
    public void Raw_sql_and_bulk_operations_that_skip_the_filter_and_the_interceptors_are_not_used()
    {
        // FromSql*, ExecuteSql*, SqlQuery*, ExecuteUpdate/Delete bypass both the global query filter and the
        // stamping/audit interceptors. There is no legitimate use in this codebase today.
        var forbidden = new Regex(@"\b(FromSql(Raw|Interpolated)?|ExecuteSql(Raw|Interpolated)?(Async)?|SqlQuery(Raw)?|ExecuteUpdate(Async)?|ExecuteDelete(Async)?)\s*[(<]");

        var offenders = ProductionSources().Where(s => forbidden.IsMatch(s.Text)).Select(s => s.RelativePath);

        Assert.Empty(offenders);
    }

    [Fact]
    public void Services_never_filter_by_tenant_by_hand()
    {
        var manual = new Regex(@"\.TenantId\s*==|==\s*\w+\.TenantId\b|EF\.Property<[^>]+>\([^)]*""TenantId""");

        // Line by line, ignoring comments. Looking up the caller's own tenant ROW (db.Tenants...) is not a data filter.
        var offenders = ProductionSources()
            .Where(s => s.Project == "OpsFlow.Infrastructure" && s.RelativePath.Contains("/Services/"))
            .Where(s => s.Text.Split('\n').Select(StripComment).Any(line => manual.IsMatch(line) && !line.Contains("db.Tenants")))
            .Select(s => s.RelativePath);

        Assert.Empty(offenders); // isolation is the DbContext's job; a service looking up its own tenant ROW by id is not a filter
    }

    [Fact]
    public void Layers_only_depend_in_one_direction()
    {
        var sources = ProductionSources();

        // Core knows nothing about EF Core, ASP.NET or the other layers.
        Assert.Empty(sources.Where(s => s.Project == "OpsFlow.Core" &&
            Regex.IsMatch(s.Text, @"using\s+(Microsoft\.EntityFrameworkCore|Microsoft\.AspNetCore|OpsFlow\.Infrastructure|OpsFlow\.API)")).Select(s => s.RelativePath));

        // Infrastructure does not reach up into the web layer.
        Assert.Empty(sources.Where(s => s.Project == "OpsFlow.Infrastructure" &&
            Regex.IsMatch(s.Text, @"using\s+(Microsoft\.AspNetCore|OpsFlow\.API)")).Select(s => s.RelativePath));
    }

    [Fact]
    public void Controllers_reach_data_only_through_services()
    {
        var offenders = ProductionSources()
            .Where(s => s.RelativePath.Contains("/Controllers/") &&
                        Regex.IsMatch(s.Text, @"\b(OpsFlowDbContext|DbContext|IServiceProvider|IServiceScopeFactory|Microsoft\.EntityFrameworkCore)\b"))
            .Select(s => s.RelativePath);

        Assert.Empty(offenders);
    }

    [Fact]
    public void Security_code_never_uses_the_non_cryptographic_Random()
    {
        var offenders = ProductionSources()
            .Where(s => Regex.IsMatch(s.Text, @"System\.Random\b|new\s+Random\s*\(|\bRandom\.Shared\b|:\s*Random\b|Random\s+\w+\s*=\s*new\s*\("))
            .Select(s => s.RelativePath);

        Assert.Empty(offenders);
    }

    [Fact]
    public void Every_endpoint_that_skips_authentication_is_listed_here()
    {
        // The fallback policy denies by default; AllowAnonymous is the only way out. Keep the list explicit.
        var anonymous = ProductionSources()
            .SelectMany(s => Regex.Matches(StripComments(s.Text), @"AllowAnonymous").Select(_ => s.Name))
            .GroupBy(n => n)
            .ToDictionary(g => g.Key, g => g.Count());

        Assert.Equal(new Dictionary<string, int> { ["Controllers.cs"] = 2, ["Program.cs"] = 2 }, anonymous);
    }

    // ---- secrets -----------------------------------------------------------------------------------------

    [Fact]
    public void Committed_production_settings_contain_no_secrets()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(SrcRoot, "OpsFlow.API", "appsettings.json")));
        var root = doc.RootElement;

        Assert.Equal("", root.GetProperty("Jwt").GetProperty("Key").GetString());
        Assert.Equal("", root.GetProperty("Platform").GetProperty("ApiKey").GetString());
        Assert.Equal("", root.GetProperty("ConnectionStrings").GetProperty("Default").GetString());
        Assert.False(root.GetProperty("Seed").GetProperty("Demo").GetBoolean());
    }

    [Fact]
    public void Compose_and_the_example_env_file_ship_no_real_secrets_and_no_demo_by_default()
    {
        var compose = File.ReadAllText(Path.Combine(Root, "docker-compose.yml"));
        var example = File.ReadAllText(Path.Combine(Root, ".env.example"));

        Assert.Contains("${JWT_KEY:?", compose);                       // compose refuses to start without a key
        Assert.Contains("SEED_DEMO:-false", compose);                   // demo accounts are opt-in
        Assert.Matches(@"(?m)^JWT_KEY=\s*$", example);                  // no key in the example file
        Assert.Matches(@"(?m)^PLATFORM_API_KEY=\s*$", example);
        Assert.Matches(@"(?m)^SEED_DEMO=false\s*$", example);
        Assert.DoesNotMatch(@"(?im)^\s*Jwt__Key:\s*[A-Za-z0-9]{8,}\s*$", compose);
    }

    [Fact]
    public void Secrets_files_and_local_state_are_git_ignored_and_not_copied_into_the_image()
    {
        var gitignore = File.ReadAllText(Path.Combine(Root, ".gitignore"));
        var dockerignore = File.ReadAllText(Path.Combine(Root, ".dockerignore"));

        foreach (var entry in new[] { ".env", "bin/", "obj/" }) Assert.Contains(entry, gitignore);
        foreach (var entry in new[] { "**/bin", "**/obj", ".env", ".git" }) Assert.Contains(entry, dockerignore);
    }
}

