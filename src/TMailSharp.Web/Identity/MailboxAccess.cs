using System.Security.Claims;
using Microsoft.AspNetCore.DataProtection;

namespace TMailSharp.Identity;

public sealed class MailboxAccess(IDataProtectionProvider protection, IWebHostEnvironment environment)
{
    private readonly IDataProtector _protector = protection.CreateProtector("GuestMailbox.v1.1.0");
    private string CookieName => environment.IsDevelopment() ? "tmailsharp.guest.dev" : "__Host-tmailsharp.guest";

    public string GuestOwner(HttpContext context)
    {
        if (context.Items["GuestOwner"] is string owner) return owner;
        string? token = null;
        if (context.Request.Cookies.TryGetValue(CookieName, out var cookie))
        {
            try { token = _protector.Unprotect(cookie); }
            catch (System.Security.Cryptography.CryptographicException) { }
        }
        if (token is null || token.Length != 64)
        {
            token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
            context.Response.Cookies.Append(CookieName, _protector.Protect(token), new CookieOptions
            {
                HttpOnly = true, Secure = !environment.IsDevelopment() || context.Request.IsHttps,
                SameSite = SameSiteMode.Strict, MaxAge = TimeSpan.FromDays(2), Path = "/", IsEssential = true
            });
        }
        owner = Services.AddressRules.Hash(token);
        context.Items["GuestOwner"] = owner;
        return owner;
    }

    public string Owner(HttpContext context) => context.User.Identity?.IsAuthenticated == true
        ? "user:" + context.User.FindFirstValue(ClaimTypes.NameIdentifier)
        : GuestOwner(context);
}
