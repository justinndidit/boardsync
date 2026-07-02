namespace BoardSync.Api.Shared.Auth.Configuration;

public class JwtSettings
{
    public string Secret { get; set; } = string.Empty;
    public string Issuer { get; set; } = string.Empty;
    public string Audience { get; set; } = string.Empty;
    public int AccessTokenExpirationMinutes { get; set; } = 15;
    public int RefreshTokenExpirationDays { get; set; } = 7;
    public bool ValidateIssuerSigningKey { get; set; } = true;
    public bool ValidateIssuer { get; set; } = true;
    public bool ValidateAudience { get; set; } = true;
    public bool ValidateLifetime { get; set; } = true;
}

public class EmailSettings
{
    public string SmtpServer { get; set; } = string.Empty;
    public int SmtpPort { get; set; } = 587;
    public string SenderEmail { get; set; } = string.Empty;
    public string SenderName { get; set; } = string.Empty;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool EnableSsl { get; set; } = true;
    public string BaseUrl { get; set; } = string.Empty;
}

public class SecuritySettings
{
    public int MaxFailedAccessAttempts { get; set; } = 5;
    public int AccountLockoutMinutes { get; set; } = 15;
    public int PasswordResetTokenExpirationHours { get; set; } = 1;
    public int EmailConfirmationTokenExpirationHours { get; set; } = 24;
    public bool RequireEmailConfirmation { get; set; } = true;
    public int MinPasswordLength { get; set; } = 6;
    public bool RequireUppercase { get; set; } = true;
    public bool RequireLowercase { get; set; } = true;
    public bool RequireDigit { get; set; } = true;
    public bool RequireSpecialChar { get; set; } = false;
}