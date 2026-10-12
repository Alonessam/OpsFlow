using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpsFlow.Core.Contracts;

namespace OpsFlow.API.Auth;

public sealed class JwtOptions
{
    public const int MinKeyLength = 32;

    /// <summary>Prefix of the signing key committed in appsettings.Development.json. Refused outside Development.</summary>
    public const string DevKeyPrefix = "dev-only";

    public string Issuer { get; set; } = "opsflow";
    public string Audience { get; set; } = "opsflow-clients";
    public string Key { get; set; } = "";
    public int ExpiryMinutes { get; set; } = 30;
}

/// <summary>The claim names the tenant middleware and the hub read. One place, no magic strings.</summary>
public static class OpsClaims
{
    public const string UserId = "sub";
    public const string TenantId = "tid";
    public const string TenantSlug = "slug";
    public const string Role = "role";
    public const string Name = "name";
}

public sealed class JwtTokenService(IOptions<JwtOptions> options, TimeProvider time)
{
    public (string Token, DateTimeOffset ExpiresAt) Issue(UserDto user, TenantDto tenant)
    {
        var o = options.Value;
        var now = time.GetUtcNow();
        var expires = now.AddMinutes(o.ExpiryMinutes);

        var claims = new[]
        {
            new Claim(OpsClaims.UserId, user.Id.ToString()),
            new Claim(OpsClaims.TenantId, tenant.Id.ToString()),
            new Claim(OpsClaims.TenantSlug, tenant.Slug),
            new Claim(OpsClaims.Role, user.Role.ToString()),
            new Claim(OpsClaims.Name, user.FullName),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.Key));
        var token = new JwtSecurityToken(
            o.Issuer, o.Audience, claims,
            notBefore: now.UtcDateTime, expires: expires.UtcDateTime,
            signingCredentials: new SigningCredentials(key, SecurityAlgorithms.HmacSha256));

        return (new JwtSecurityTokenHandler().WriteToken(token), expires);
    }
}

