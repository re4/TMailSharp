using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TMailSharp.Identity;
using TMailSharp.Models;
using TMailSharp.Services;

namespace TMailSharp.Controllers;

[Authorize(Policy = "WebsiteAccount"), Route("account/api-keys"), EnableRateLimiting("login")]
public sealed class ApiKeysController(ApiKeyService keys, UserManager<ApplicationUser> users,
    SignInManager<ApplicationUser> signIn) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Index(CancellationToken ct) => View(await Model(await Current(), ct));

    [HttpPost]
    public async Task<IActionResult> Create([Bind(Prefix = "Input")] CreateApiKeyInput input, CancellationToken ct)
    {
        var user = await Current();
        if (ModelState.IsValid && !(await signIn.CheckPasswordSignInAsync(user, input.Password, lockoutOnFailure: true)).Succeeded)
            ModelState.AddModelError("Input.Password", "Password verification failed.");
        string? token = null;
        if (ModelState.IsValid)
        {
            try { token = await keys.CreateAsync(user, input.Name, ct); }
            catch (AppException ex) { ModelState.AddModelError("", ex.Message); }
        }
        ModelState.SetModelValue("Input.Password", "", "");
        input.Password = "";
        return View("Index", await Model(user, ct, input, token));
    }

    [HttpPost("{id}/revoke")]
    public async Task<IActionResult> Revoke(string id, CancellationToken ct)
    {
        await keys.RevokeAsync((await Current()).Id, id, ct);
        return RedirectToAction(nameof(Index));
    }

    private async Task<ApplicationUser> Current() => await users.GetUserAsync(User) ?? throw new AppException(401, "Sign in again.");
    private async Task<ApiKeysViewModel> Model(ApplicationUser user, CancellationToken ct, CreateApiKeyInput? input = null, string? token = null) =>
        new() { Keys = await keys.ListAsync(user, ct), Input = input ?? new(), NewKey = token };
}
