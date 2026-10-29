using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace TMailSharp.Models;

public sealed class RegisterInput
{
    [Required, RegularExpression("^[A-Za-z0-9._-]{3,40}$")] public string UserName { get; set; } = "";
    [Required, MinLength(12), MaxLength(256), DataType(DataType.Password)] public string Password { get; set; } = "";
    [Compare(nameof(Password)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public sealed class LoginInput
{
    [Required] public string UserName { get; set; } = "";
    [Required, MaxLength(256), DataType(DataType.Password)] public string Password { get; set; } = "";
    public string? ReturnUrl { get; set; }
}
public sealed class TwoFactorInput
{
    [Required, MaxLength(100)] public string Code { get; set; } = "";
    public bool RecoveryCode { get; set; }
    public string? ReturnUrl { get; set; }
}
public sealed class SecurityInput
{
    [Required, MaxLength(256), DataType(DataType.Password)] public string Password { get; set; } = "";
    public string? Code { get; set; }
    public string? CredentialId { get; set; }
}
public sealed class PasskeyInput
{
    [Required, MaxLength(32768)] public string CredentialJson { get; set; } = "";
    [MaxLength(100)] public string? Name { get; set; }
}
public sealed class ChangePasswordInput
{
    [Required, DataType(DataType.Password)] public string CurrentPassword { get; set; } = "";
    [Required, MinLength(12), MaxLength(256), DataType(DataType.Password)] public string NewPassword { get; set; } = "";
    [Compare(nameof(NewPassword)), DataType(DataType.Password)] public string ConfirmPassword { get; set; } = "";
}
public sealed class SecurityViewModel
{
    public bool TwoFactorEnabled { get; init; }
    public int RecoveryCodesRemaining { get; init; }
    public IReadOnlyList<UserPasskeyInfo> Passkeys { get; init; } = [];
}

public sealed class CreateApiKeyInput
{
    [Required, StringLength(80)] public string Name { get; set; } = "";
    [Required, MaxLength(256), DataType(DataType.Password)] public string Password { get; set; } = "";
}

public sealed record ApiKeySummary(string Id, string Name, string Prefix, DateTime CreatedAt, string Status);

public sealed class ApiKeysViewModel
{
    public IReadOnlyList<ApiKeySummary> Keys { get; init; } = [];
    public string? NewKey { get; init; }
    public CreateApiKeyInput Input { get; init; } = new();
}
