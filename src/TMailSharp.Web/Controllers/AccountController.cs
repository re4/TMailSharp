using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using TMailSharp.Configuration;
using TMailSharp.Data;
using TMailSharp.Identity;
using TMailSharp.Models;
using TMailSharp.Services;

namespace TMailSharp.Controllers;

[Route("account"), EnableRateLimiting("login")]
public sealed class AccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn,
    MailboxAccess access, MongoMailStore mail, IOptions<AdminOptions> admin) : Controller
{
    [HttpGet("register")]
    public IActionResult Register() => View(new RegisterInput());

    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterInput input, CancellationToken ct)
    {
        input.UserName = input.UserName.Trim();
        if (string.Equals(input.UserName, admin.Value.UserName, StringComparison.OrdinalIgnoreCase))
            ModelState.AddModelError(nameof(input.UserName), "That name is reserved.");
        if (!ModelState.IsValid) return View(input);
        var user = new ApplicationUser { UserName = input.UserName };
        var result = await users.CreateAsync(user, input.Password);
        if (!result.Succeeded) { Errors(result); return View(input); }
        await signIn.SignInAsync(user, isPersistent: false);
        await ClaimMailboxes(user, ct);
        return Redirect("/");
    }

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl) => View(new LoginInput { ReturnUrl = returnUrl });

    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return View(input);
        var result = await signIn.PasswordSignInAsync(input.UserName.Trim(), input.Password, false, lockoutOnFailure: true);
        if (result.RequiresTwoFactor) return RedirectToAction(nameof(TwoFactor), new { input.ReturnUrl });
        if (result.Succeeded)
        {
            await ClaimMailboxes((await users.FindByNameAsync(input.UserName.Trim()))!, ct);
            return LocalDestination(input.ReturnUrl);
        }
        ModelState.AddModelError("", result.IsLockedOut ? "Too many attempts. Try again in 15 minutes." : "Sign-in failed. Check your username and password.");
        return View(input);
    }

    [HttpGet("two-factor")]
    public async Task<IActionResult> TwoFactor(string? returnUrl)
    {
        if (await signIn.GetTwoFactorAuthenticationUserAsync() is null) return RedirectToAction(nameof(Login));
        return View(new TwoFactorInput { ReturnUrl = returnUrl });
    }

    [HttpPost("two-factor")]
    public async Task<IActionResult> TwoFactor(TwoFactorInput input, CancellationToken ct)
    {
        var user = await signIn.GetTwoFactorAuthenticationUserAsync();
        if (user is null) return RedirectToAction(nameof(Login));
        if (!ModelState.IsValid) return View(input);
        var code = input.Code.Replace(" ", "");
        var result = input.RecoveryCode ? await signIn.TwoFactorRecoveryCodeSignInAsync(code)
            : await signIn.TwoFactorAuthenticatorSignInAsync(code.Replace("-", ""), false, false);
        if (result.Succeeded) { await ClaimMailboxes(user, ct); return LocalDestination(input.ReturnUrl); }
        ModelState.AddModelError("", "The code was not accepted, or the account is temporarily locked.");
        return View(input);
    }

    [Authorize, HttpPost("logout")]
    public async Task<IActionResult> Logout() { await signIn.SignOutAsync(); return Redirect("/"); }

    [Authorize, HttpGet("security")]
    public async Task<IActionResult> Security()
    {
        var user = await Current();
        return View(new SecurityViewModel { TwoFactorEnabled = await users.GetTwoFactorEnabledAsync(user),
            Passkeys = (await users.GetPasskeysAsync(user)).ToArray(), RecoveryCodesRemaining = await users.CountRecoveryCodesAsync(user) });
    }

    [Authorize, HttpPost("authenticator/setup")]
    public async Task<IActionResult> SetupAuthenticator(SecurityInput input)
    {
        var user = await Current();
        if (!ModelState.IsValid || !await VerifyPassword(user, input.Password)) return BadRequest("Password verification failed.");
        if (await users.GetTwoFactorEnabledAsync(user)) return BadRequest("Disable the existing authenticator before replacing it.");
        var key = await users.GetAuthenticatorKeyAsync(user);
        if (string.IsNullOrEmpty(key)) { Require(await users.ResetAuthenticatorKeyAsync(user)); key = await users.GetAuthenticatorKeyAsync(user); }
        return View("SetupAuthenticator", key);
    }

    [Authorize, HttpPost("authenticator/enable")]
    public async Task<IActionResult> EnableAuthenticator(TwoFactorInput input)
    {
        var user = await Current();
        if (!ModelState.IsValid || !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, input.Code.Replace(" ", "").Replace("-", "")))
            return BadRequest("The authenticator code was not accepted.");
        Require(await users.SetTwoFactorEnabledAsync(user, true));
        var codes = (await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray();
        await RefreshSecurity(user);
        return View("RecoveryCodes", codes);
    }

    [Authorize, HttpPost("authenticator/disable")]
    public async Task<IActionResult> DisableAuthenticator(SecurityInput input)
    {
        var user = await Current();
        if (!ModelState.IsValid || !await VerifyPassword(user, input.Password) || string.IsNullOrWhiteSpace(input.Code) ||
            !await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, input.Code.Replace(" ", "").Replace("-", "")))
            return BadRequest("Your password and a current authenticator code are required.");
        Require(await users.SetTwoFactorEnabledAsync(user, false));
        Require(await users.ResetAuthenticatorKeyAsync(user));
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        await RefreshSecurity(user);
        return RedirectToAction(nameof(Security));
    }

    [Authorize, HttpPost("recovery-codes")]
    public async Task<IActionResult> RecoveryCodes(SecurityInput input)
    {
        var user = await Current();
        if (!ModelState.IsValid || !await VerifyPassword(user, input.Password) || !await users.GetTwoFactorEnabledAsync(user)) return BadRequest();
        return View((await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10))!.ToArray());
    }

    [Authorize, HttpPost("password")]
    public async Task<IActionResult> ChangePassword(ChangePasswordInput input)
    {
        if (!ModelState.IsValid) return BadRequest("Check your new password and confirmation.");
        var user = await Current();
        var result = await users.ChangePasswordAsync(user, input.CurrentPassword, input.NewPassword);
        if (!result.Succeeded) return BadRequest("Password change failed. Check your current password and use at least 12 characters.");
        await RefreshSecurity(user);
        return RedirectToAction(nameof(Security));
    }

    [Authorize, HttpPost("passkeys/options")]
    public async Task<IActionResult> PasskeyOptions([FromBody] SecurityInput input)
    {
        var user = await Current();
        if (!ModelState.IsValid || !await VerifyPassword(user, input.Password)) return BadRequest(new { error = "Password verification failed." });
        if ((await users.GetPasskeysAsync(user)).Count >= 10) return BadRequest(new { error = "Remove an existing key before adding another." });
        var options = await signIn.MakePasskeyCreationOptionsAsync(new PasskeyUserEntity { Id = user.Id, Name = user.UserName!, DisplayName = user.UserName! });
        return Content(options, "application/json");
    }

    [Authorize, HttpPost("passkeys/add")]
    public async Task<IActionResult> AddPasskey([FromBody] PasskeyInput input)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid key response." });
        var user = await Current();
        try
        {
            var result = await signIn.PerformPasskeyAttestationAsync(input.CredentialJson);
            if (!result.Succeeded || result.UserEntity.Id != user.Id) return BadRequest(new { error = "The key could not be verified." });
            result.Passkey.Name = string.IsNullOrWhiteSpace(input.Name) ? "Security key" : input.Name.Trim();
            Require(await users.AddOrUpdatePasskeyAsync(user, result.Passkey));
            await RefreshSecurity(user);
            return Ok(new { added = true });
        }
        catch (Exception ex) when (ex is InvalidOperationException or PasskeyException or System.Text.Json.JsonException)
        { return BadRequest(new { error = "The key request expired or could not be verified. Start again." }); }
    }

    [Authorize, HttpPost("passkeys/remove")]
    public async Task<IActionResult> RemovePasskey(SecurityInput input)
    {
        var user = await Current();
        if (!ModelState.IsValid || !await VerifyPassword(user, input.Password) || string.IsNullOrEmpty(input.CredentialId)) return BadRequest();
        byte[] id;
        try { id = WebEncoders.Base64UrlDecode(input.CredentialId); } catch (FormatException) { return BadRequest(); }
        Require(await users.RemovePasskeyAsync(user, id));
        await RefreshSecurity(user);
        return RedirectToAction(nameof(Security));
    }

    [HttpPost("passkeys/login-options")]
    public async Task<IActionResult> PasskeyLoginOptions() => Content(await signIn.MakePasskeyRequestOptionsAsync(null), "application/json");

    [HttpPost("passkeys/login")]
    public async Task<IActionResult> PasskeyLogin([FromBody] PasskeyInput input, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest(new { error = "Invalid key response." });
        try
        {
            var result = await signIn.PasskeySignInAsync(input.CredentialJson);
            if (!result.Succeeded) return Unauthorized(new { error = "The key was not accepted, or the account is locked." });
            var user = await users.GetUserAsync(signIn.Context.User);
            if (user is not null) await ClaimMailboxes(user, ct);
            return Ok(new { redirect = "/" });
        }
        catch (Exception ex) when (ex is InvalidOperationException or PasskeyException or System.Text.Json.JsonException)
        { return BadRequest(new { error = "The key request expired or could not be verified. Start again." }); }
    }

    [HttpGet("denied")]
    public IActionResult Denied() { Response.StatusCode = 403; return View(); }

    private async Task<ApplicationUser> Current() => await users.GetUserAsync(User) ?? throw new AppException(401, "Sign in again.");
    private async Task<bool> VerifyPassword(ApplicationUser user, string password) => (await signIn.CheckPasswordSignInAsync(user, password, lockoutOnFailure: true)).Succeeded;
    private async Task RefreshSecurity(ApplicationUser user)
    {
        Require(await users.UpdateSecurityStampAsync(user));
        await signIn.RefreshSignInAsync(user);
    }
    private Task ClaimMailboxes(ApplicationUser user, CancellationToken ct) => mail.ClaimGuestMailboxesAsync(access.GuestOwner(HttpContext), "user:" + user.Id, ct);
    private IActionResult LocalDestination(string? url) => Url.IsLocalUrl(url) ? LocalRedirect(url!) : Redirect("/");
    private void Errors(IdentityResult result) { foreach (var error in result.Errors) ModelState.AddModelError("", error.Description); }
    private static void Require(IdentityResult result)
    {
        if (!result.Succeeded) throw new AppException(400, string.Join(" ", result.Errors.Select(e => e.Description)));
    }
}
