using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Services;
using Microsoft.Extensions.Options;
using System.Text.RegularExpressions;

namespace BoardSync.Api.Shared.Auth.Services.Implementations;

public class PasswordService : IPasswordService
{
    private readonly SecuritySettings _securitySettings;

    public PasswordService(IOptions<SecuritySettings> securitySettings)
    {
        _securitySettings = securitySettings.Value;
    }

    public string HashPassword(string password)
    {
        return BCrypt.Net.BCrypt.HashPassword(password, BCrypt.Net.BCrypt.GenerateSalt(12));
    }

    public bool VerifyPassword(string password, string hash)
    {
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch
        {
            return false;
        }
    }

    public PasswordValidationResult ValidatePassword(string password)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(password))
        {
            errors.Add("Password is required.");
            return new PasswordValidationResult(false, errors.ToArray());
        }

        if (password.Length < _securitySettings.MinPasswordLength)
        {
            errors.Add($"Password must be at least {_securitySettings.MinPasswordLength} characters long.");
        }

        if (_securitySettings.RequireUppercase && !Regex.IsMatch(password, @"[A-Z]"))
        {
            errors.Add("Password must contain at least one uppercase letter.");
        }

        if (_securitySettings.RequireLowercase && !Regex.IsMatch(password, @"[a-z]"))
        {
            errors.Add("Password must contain at least one lowercase letter.");
        }

        if (_securitySettings.RequireDigit && !Regex.IsMatch(password, @"[0-9]"))
        {
            errors.Add("Password must contain at least one digit.");
        }

        if (_securitySettings.RequireSpecialChar && !Regex.IsMatch(password, @"[^a-zA-Z0-9]"))
        {
            errors.Add("Password must contain at least one special character.");
        }

        return new PasswordValidationResult(errors.Count == 0, errors.ToArray());
    }
}