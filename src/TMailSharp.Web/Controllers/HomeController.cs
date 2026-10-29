using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using TMailSharp.Configuration;
using TMailSharp.Data;
using TMailSharp.Services;

namespace TMailSharp.Controllers;

public sealed class HomeController : Controller
{
    [HttpGet("/")]
    public IActionResult Index() => View();

    [HttpGet("/index.html")]
    public IActionResult LegacyHome() => Redirect("/");

    [HttpGet("/admin.html")]
    public IActionResult LegacyAdmin() => Redirect("/admin");

    [HttpGet("/health")]
    public async Task<IActionResult> Health([FromServices] MongoMailStore store, [FromServices] SmtpReceiver smtp, [FromServices] IOptions<SmtpOptions> settings, CancellationToken ct)
    {
        await store.PingAsync(ct);
        return !settings.Value.Enabled || smtp.IsListening ? Ok(new { status = "healthy" }) : StatusCode(503);
    }
}
