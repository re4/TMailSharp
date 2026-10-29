using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using TMailSharp.Data;
using TMailSharp.Services;

namespace TMailSharp.Controllers;

[Authorize(Roles = "Administrator")]
public sealed class AdminController(MongoMailStore store) : Controller
{
    [HttpGet("/admin")]
    public async Task<IActionResult> Index(CancellationToken ct) => View(new AdminViewModel
    {
        Domains = await store.GetDomainsAsync(true, ct), Settings = await store.GetSettingsAsync(ct)
    });

    [HttpPost("/admin/domains")]
    public async Task<IActionResult> Domain([Required] string name, bool enabled, CancellationToken ct)
    {
        if (!ModelState.IsValid) return BadRequest();
        await store.SetDomainAsync(AddressRules.NormalizeDomain(name), enabled, ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/admin/domains/delete")]
    public async Task<IActionResult> DeleteDomain(string name, CancellationToken ct)
    {
        await store.DeleteDomainAsync(AddressRules.NormalizeDomain(name), ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/admin/retention")]
    public async Task<IActionResult> Retention(int minutes, string? domain, CancellationToken ct)
    {
        await store.SetRetentionAsync(minutes, string.IsNullOrWhiteSpace(domain) ? null : AddressRules.NormalizeDomain(domain), ct);
        return RedirectToAction(nameof(Index));
    }

    [HttpPost("/admin/retention/reset")]
    public async Task<IActionResult> ResetRetention(string domain, CancellationToken ct)
    {
        await store.ResetDomainRetentionAsync(AddressRules.NormalizeDomain(domain), ct);
        return RedirectToAction(nameof(Index));
    }
}

public sealed class AdminViewModel
{
    public List<DomainDocument> Domains { get; init; } = [];
    public MailSettingsDocument Settings { get; init; } = new();
}
