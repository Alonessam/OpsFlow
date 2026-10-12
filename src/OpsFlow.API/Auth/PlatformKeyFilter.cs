using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace OpsFlow.API.Auth;

/// <summary>
/// Guards the platform-operator endpoint with a static key. It is a resource filter on purpose: it runs
/// BEFORE model binding/validation, so an unauthenticated caller learns nothing from validation errors,
/// and a disabled endpoint is a plain 404 whatever the body looks like.
/// </summary>
public sealed class PlatformKeyFilter(IConfiguration config) : IAsyncResourceFilter
{
    public const string HeaderName = "X-Platform-Key";
    public const int MinKeyLength = 16;

    public async Task OnResourceExecutionAsync(ResourceExecutingContext context, ResourceExecutionDelegate next)
    {
        var expected = config["Platform:ApiKey"];
        if (string.IsNullOrEmpty(expected))
        {
            context.Result = new NotFoundResult(); // feature disabled unless a key is configured
            return;
        }

        var supplied = context.HttpContext.Request.Headers[HeaderName].ToString();

        // Hash both sides so the comparison is constant-time regardless of the supplied length.
        var given = SHA256.HashData(Encoding.UTF8.GetBytes(supplied));
        var wanted = SHA256.HashData(Encoding.UTF8.GetBytes(expected));
        if (!CryptographicOperations.FixedTimeEquals(given, wanted))
        {
            context.Result = new UnauthorizedResult();
            return;
        }

        await next();
    }
}
