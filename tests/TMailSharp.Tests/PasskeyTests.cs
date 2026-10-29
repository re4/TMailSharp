using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace TMailSharp.Tests;

public sealed partial class MailAppTests
{
    [Fact]
    public async Task SecurityKeyRegistrationAndLoginVerifyChallengeOriginAndSignature()
    {
        using var client = await Client();
        client.DefaultRequestHeaders.Add("Origin", "https://localhost");
        await Register(client, "security-key-user");
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var credentialId = RandomNumberGenerator.GetBytes(32);
        var options = await Json(await client.PostAsJsonAsync("/account/passkeys/options", new { password = UserPassword }));
        Assert.Equal("required", options.GetProperty("authenticatorSelection").GetProperty("userVerification").GetString());
        var wrongOrigin = CreateAttestation(options, key, credentialId, "https://attacker.example");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/account/passkeys/add", new { credentialJson = wrongOrigin, name = "Rejected key" })).StatusCode);

        options = await Json(await client.PostAsJsonAsync("/account/passkeys/options", new { password = UserPassword }));
        var attestation = CreateAttestation(options, key, credentialId, "https://localhost");
        await Json(await client.PostAsJsonAsync("/account/passkeys/add", new { credentialJson = attestation, name = "Test YubiKey" }));
        await client.PostAsync("/account/logout", null);
        await RefreshToken(client);

        var assertionOptions = await Json(await client.PostAsJsonAsync("/account/passkeys/login-options", new { }));
        var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "webauthn.get", challenge = assertionOptions.GetProperty("challenge").GetString(), origin = "https://localhost", crossOrigin = false }));
        var authData = SHA256.HashData(Encoding.UTF8.GetBytes("localhost")).Concat(new byte[] { 5, 0, 0, 0, 1 }).ToArray();
        var signature = key.SignData(authData.Concat(SHA256.HashData(clientData)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
        var assertion = JsonSerializer.Serialize(new
        {
            id = WebEncoders.Base64UrlEncode(credentialId), rawId = WebEncoders.Base64UrlEncode(credentialId), type = "public-key",
            response = new { clientDataJSON = WebEncoders.Base64UrlEncode(clientData), authenticatorData = WebEncoders.Base64UrlEncode(authData),
                signature = WebEncoders.Base64UrlEncode(signature), userHandle = options.GetProperty("user").GetProperty("id").GetString() },
            clientExtensionResults = new { }, authenticatorAttachment = "cross-platform"
        });
        await Json(await client.PostAsJsonAsync("/account/passkeys/login", new { credentialJson = assertion }));
        var bootstrap = await Json(await client.GetAsync("/inbox/bootstrap"));
        Assert.Equal("security-key-user", bootstrap.GetProperty("user").GetProperty("name").GetString());
        // A successful ceremony consumes its challenge. Replaying a signature must fail.
        await RefreshToken(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/account/passkeys/login", new { credentialJson = assertion })).StatusCode);
    }

    private static string CreateAttestation(JsonElement options, ECDsa key, byte[] id, string origin)
    {
        var point = key.ExportParameters(false).Q;
        var cose = Cbor(new Dictionary<object, object> { [1] = 2, [3] = -7, [-1] = 1, [-2] = point.X!, [-3] = point.Y! });
        var rpId = options.GetProperty("rp").GetProperty("id").GetString()!;
        var authData = SHA256.HashData(Encoding.UTF8.GetBytes(rpId))
            .Concat(new byte[] { 0x45, 0, 0, 0, 0 }).Concat(new byte[16]).Concat(new byte[] { 0, (byte)id.Length }).Concat(id).Concat(cose).ToArray();
        var attestation = Cbor(new Dictionary<object, object> { ["fmt"] = "none", ["attStmt"] = new Dictionary<object, object>(), ["authData"] = authData });
        var clientData = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { type = "webauthn.create", challenge = options.GetProperty("challenge").GetString(), origin, crossOrigin = false }));
        return JsonSerializer.Serialize(new
        {
            id = WebEncoders.Base64UrlEncode(id), rawId = WebEncoders.Base64UrlEncode(id), type = "public-key", authenticatorAttachment = "cross-platform",
            response = new { clientDataJSON = WebEncoders.Base64UrlEncode(clientData), attestationObject = WebEncoders.Base64UrlEncode(attestation), transports = new[] { "usb" } },
            clientExtensionResults = new { }
        });
    }

    private static byte[] Cbor(object item)
    {
        static byte[] Header(int major, int length) => length < 24 ? [(byte)((major << 5) | length)]
            : length < 256 ? [(byte)((major << 5) | 24), (byte)length]
            : [(byte)((major << 5) | 25), (byte)(length >> 8), (byte)length];
        return item switch
        {
            int value => Header(value >= 0 ? 0 : 1, value >= 0 ? value : -1 - value),
            string value => Header(3, Encoding.UTF8.GetByteCount(value)).Concat(Encoding.UTF8.GetBytes(value)).ToArray(),
            byte[] value => Header(2, value.Length).Concat(value).ToArray(),
            Dictionary<object, object> value => Header(5, value.Count).Concat(value.SelectMany(pair => Cbor(pair.Key).Concat(Cbor(pair.Value)))).ToArray(),
            _ => throw new ArgumentException("Unsupported test CBOR value")
        };
    }
}
