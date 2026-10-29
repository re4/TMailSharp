using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace TMailSharp.Identity;

public sealed class ApiKeyAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger, UrlEncoder encoder, ApiKeyService keys, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "MailboxApiKey";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var headers = Request.Headers.Authorization;
        if (headers.Count == 0) return AuthenticateResult.NoResult();
        if (headers.Count != 1 || !AuthenticationHeaderValue.TryParse(headers[0], out var header) ||
            !string.Equals(header.Scheme, "Bearer", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(header.Parameter))
            return AuthenticateResult.Fail("Invalid API key.");
        var key = await keys.FindAsync(header.Parameter, Context.RequestAborted);
        if (key is null) return AuthenticateResult.Fail("Invalid API key.");
        var user = await users.FindByIdAsync(key.UserId);
        if (user is null || user.SecurityStamp != key.SecurityStamp || !await signIn.CanSignInAsync(user) || await users.IsLockedOutAsync(user))
            return AuthenticateResult.Fail("Invalid API key.");
        // API credentials carry ownership only, even when their owner is an administrator.
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.Id), new Claim(ClaimTypes.Name, user.UserName!),
            new Claim("api_key_id", key.Id)
        ], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = 401;
        Response.Headers.WWWAuthenticate = "Bearer realm=\"TMailSharp\"";
        return Response.WriteAsJsonAsync(new { error = "A valid API key is required. Sign in to create one at /account/api-keys." }, Context.RequestAborted);
    }
}
