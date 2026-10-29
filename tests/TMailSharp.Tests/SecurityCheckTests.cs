using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using TMailSharp.Configuration;
using TMailSharp.Models;
using TMailSharp.Services;

namespace TMailSharp.Tests;

public sealed partial class MailAppTests
{
    private static async Task<ProofInput> SolveCheck(HttpClient client, string purpose)
    {
        var challenge = await Json(await client.PostAsJsonAsync("/account/security-check", new { purpose }));
        return new ProofInput
        {
            ProofToken = challenge.GetProperty("token").GetString(),
            ProofNonce = WorkSolver.Solve(challenge.GetProperty("challenge").GetString()!, challenge.GetProperty("difficulty").GetInt32())
        };
    }

    private static Task<HttpResponseMessage> RawLogin(HttpClient client, string name, string password, ProofInput? proof = null) =>
        client.PostAsync("/account/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = name, ["Password"] = password,
            ["ProofToken"] = proof?.ProofToken ?? "", ["ProofNonce"] = proof?.ProofNonce ?? ""
        }));

    [Fact]
    public async Task LoginAndRegistrationRejectMissingProofEvenWithAntiforgery()
    {
        using var client = await Client();
        Assert.Equal(HttpStatusCode.BadRequest, (await RawLogin(client, "admin", AdminPassword)).StatusCode);
        var response = await client.PostAsync("/account/register", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["UserName"] = "unverified", ["Password"] = UserPassword, ["ConfirmPassword"] = UserPassword
        }));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("fresh security check", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/account/passkeys/login-options", new { })).StatusCode);
    }

    [Fact]
    public async Task AuthenticationPagesServeVersionedLocalSecurityCheckAssets()
    {
        using var client = await Client();
        foreach (var path in new[] { "/account/login", "/account/register" })
        {
            using var response = await client.GetAsync(path);
            Assert.Contains("worker-src 'self'", Assert.Single(response.Headers.GetValues("Content-Security-Policy")));
            var html = await response.Content.ReadAsStringAsync();
            var worker = Regex.Match(html, "data-worker=\"([^\"]+)\"");
            Assert.True(worker.Success);
            Assert.StartsWith("/proof-worker.js?v=", worker.Groups[1].Value);
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(WebUtility.HtmlDecode(worker.Groups[1].Value))).StatusCode);
            Assert.Contains("name=\"ProofToken\"", html);
            Assert.Contains("/account.js?v=", html);
        }
    }

    [Fact]
    public async Task FailedPasswordConsumesProofAndAnotherBrowserCannotReuseIt()
    {
        using var owner = await Client();
        using var other = await Client();
        var proof = await SolveCheck(owner, "login");
        Assert.Equal(HttpStatusCode.BadRequest, (await RawLogin(other, "admin", AdminPassword, proof)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await RawLogin(owner, "admin", "incorrect-password", proof)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await RawLogin(owner, "admin", AdminPassword, proof)).StatusCode);
        var registrationProof = await SolveCheck(owner, "register");
        Assert.Equal(HttpStatusCode.BadRequest, (await RawLogin(owner, "admin", AdminPassword, registrationProof)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await RawLogin(owner, "admin", AdminPassword, await SolveCheck(owner, "login"))).StatusCode);
    }

    [Fact]
    public async Task SecurityCheckIssuanceRequiresCsrfAndIsRateLimited()
    {
        using var client = await Client();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/account/security-check", new { purpose = "login" })).StatusCode);
        await RefreshToken(client);
        for (var i = 0; i < 9; i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/account/security-check", new { purpose = "login" })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.PostAsJsonAsync("/account/security-check", new { purpose = "login" })).StatusCode);
    }

    [Fact]
    public async Task ApiCreationQuotaIsHigherAndSharedAcrossKeysForOneAccount()
    {
        using var owner = await Client();
        await Register(owner, "larger-api-quota");
        using var first = ApiClient(await CreateKey(owner, "First"));
        using var second = ApiClient(await CreateKey(owner, "Second"));
        for (var i = 0; i < 60; i++)
        {
            var client = i < 30 ? first : second;
            Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/mailboxes", new { localPart = "../invalid" })).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await second.PostAsJsonAsync("/api/mailboxes", new { localPart = "../invalid" })).StatusCode);
        using var otherOwner = await Client();
        await Register(otherOwner, "separate-api-quota");
        using var otherApi = ApiClient(await CreateKey(otherOwner));
        Assert.Equal(HttpStatusCode.BadRequest, (await otherApi.PostAsJsonAsync("/api/mailboxes", new { localPart = "../invalid" })).StatusCode);
        using var invalidKey = ApiClient("invalid-key");
        for (var i = 0; i < 12; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await invalidKey.PostAsJsonAsync("/api/mailboxes", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await invalidKey.PostAsJsonAsync("/api/mailboxes", new { })).StatusCode);
    }
}

internal static class WorkSolver
{
    public static string Solve(string challenge, int difficulty)
    {
        Span<byte> bytes = stackalloc byte[36];
        WebEncoders.Base64UrlDecode(challenge).CopyTo(bytes);
        Span<byte> digest = stackalloc byte[32];
        for (uint nonce = 0; ; nonce++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(bytes[32..], nonce);
            SHA256.HashData(bytes, digest);
            if ((BinaryPrimitives.ReadUInt32BigEndian(digest) >> (32 - difficulty)) == 0)
                return nonce.ToString(CultureInfo.InvariantCulture);
        }
    }
}

public sealed class ProofOfWorkTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    [Fact]
    public void ProofsAreBoundToSessionPurposeLifetimeAndServerInstance()
    {
        var clock = new Clock();
        var protection = new EphemeralDataProtectionProvider();
        var options = Options.Create(new SecurityCheckOptions { Difficulty = 16 });
        var service = new ProofOfWork(protection, options, clock);
        var challenge = service.Issue("login", "session-one");
        var nonce = WorkSolver.Solve(challenge.Challenge, challenge.Difficulty);
        Assert.False(service.TryConsume(challenge.Token, nonce, "login", "session-two"));
        Assert.False(service.TryConsume(challenge.Token, nonce, "register", "session-one"));
        Assert.False(service.TryConsume(challenge.Token, "-1", "login", "session-one"));
        Assert.False(service.TryConsume("broken" + challenge.Token, nonce, "login", "session-one"));
        Assert.False(new ProofOfWork(protection, options, clock).TryConsume(challenge.Token, nonce, "login", "session-one"));
        Assert.True(service.TryConsume(challenge.Token, nonce, "login", "session-one"));
        Assert.False(service.TryConsume(challenge.Token, nonce, "login", "session-one"));
        var expired = service.Issue("register", "session-one");
        var expiredNonce = WorkSolver.Solve(expired.Challenge, expired.Difficulty);
        clock.Now = clock.Now.AddSeconds(181);
        Assert.False(service.TryConsume(expired.Token, expiredNonce, "register", "session-one"));
    }

    [Fact]
    public async Task ConcurrentReplayHasExactlyOneWinner()
    {
        var service = new ProofOfWork(new EphemeralDataProtectionProvider(), Options.Create(new SecurityCheckOptions { Difficulty = 16 }), TimeProvider.System);
        var challenge = service.Issue("login", "one-browser");
        var nonce = WorkSolver.Solve(challenge.Challenge, challenge.Difficulty);
        var results = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => service.TryConsume(challenge.Token, nonce, "login", "one-browser"))));
        Assert.Single(results, x => x);
    }
}
