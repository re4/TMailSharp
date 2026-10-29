using Microsoft.AspNetCore.Mvc;

namespace TMailSharp.Controllers;

public sealed class DocsController(IWebHostEnvironment environment) : Controller
{
    [HttpGet("/docs")]
    public IActionResult Index() => PhysicalFile(Path.Combine(environment.WebRootPath, "docs", "index.html"), "text/html; charset=utf-8");
}
