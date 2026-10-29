using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using TMailSharp.Identity;
using TMailSharp.Services;

namespace TMailSharp.Controllers;

[ApiController, Route("account/security-check"), EnableRateLimiting("challenge")]
public sealed class SecurityCheckController(ProofOfWork proof, MailboxAccess access) : ControllerBase
{
    [HttpPost]
    public IActionResult Create(SecurityCheckRequest request) => Ok(proof.Issue(request.Purpose, access.GuestOwner(HttpContext)));
}

public sealed class SecurityCheckRequest
{
    [Required, RegularExpression("^(login|register)$")] public string Purpose { get; set; } = "";
}
