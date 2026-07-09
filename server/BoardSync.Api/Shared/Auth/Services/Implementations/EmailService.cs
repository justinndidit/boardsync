using BoardSync.Api.Shared.Auth.Configuration;
using BoardSync.Api.Shared.Auth.DTOs;
using BoardSync.Api.Shared.Auth.Services;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace BoardSync.Api.Shared.Auth.Services.Implementations;

public class EmailService : IEmailService
{
    private readonly EmailSettings _emailSettings;
    private readonly ILogger<EmailService> _logger;

    public EmailService(IOptions<EmailSettings> emailSettings, ILogger<EmailService> logger)
    {
        _emailSettings = emailSettings.Value;
        _logger = logger;
    }

    public async Task<ApiResponse> SendEmailConfirmationAsync(string email, string token, string baseUrl)
    {
        var confirmationUrl = $"{baseUrl}/auth/confirm-email?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        
        var subject = "Confirm Your Email Address - BoardSync";
        var body = GenerateEmailConfirmationTemplate(confirmationUrl);
        
        return await SendEmailAsync(email, subject, body);
    }

    public async Task<ApiResponse> SendPasswordResetAsync(string email, string token, string baseUrl)
    {
        var resetUrl = $"{baseUrl}/auth/reset-password?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}";
        
        var subject = "Reset Your Password - BoardSync";
        var body = GeneratePasswordResetTemplate(resetUrl);
        
        return await SendEmailAsync(email, subject, body);
    }

    public async Task<ApiResponse> SendWelcomeEmailAsync(string email, string firstName, string baseUrl)
    {
        var subject = "Welcome to BoardSync!";
        var body = GenerateWelcomeTemplate(firstName, baseUrl);
        
        return await SendEmailAsync(email, subject, body);
    }

    public async Task<ApiResponse> SendEmailAsync(string to, string subject, string body, bool isHtml = true)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_emailSettings.SenderName, _emailSettings.SenderEmail));
            message.To.Add(new MailboxAddress("", to));
            message.Subject = subject;

            var builder = new BodyBuilder();
            if (isHtml)
            {
                builder.HtmlBody = body;
            }
            else
            {
                builder.TextBody = body;
            }
            message.Body = builder.ToMessageBody();

            using var client = new SmtpClient();
            await client.ConnectAsync(_emailSettings.SmtpServer, _emailSettings.SmtpPort, _emailSettings.EnableSsl);
            
            if (!string.IsNullOrEmpty(_emailSettings.Username))
            {
                await client.AuthenticateAsync(_emailSettings.Username, _emailSettings.Password);
            }
            
            await client.SendAsync(message);
            await client.DisconnectAsync(true);

            _logger.LogInformation("Email sent successfully to {Email}", to);
            return new ApiResponse(true, "Email sent successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {Email}: {Message}", to, ex.Message);
            return new ApiResponse(false, "Failed to send email");
        }
    }

    private static string GenerateEmailConfirmationTemplate(string confirmationUrl)
    {
        return $@"
            <html>
            <body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
                <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
                    <h2 style='color: #2c3e50;'>Confirm Your Email Address</h2>
                    <p>Thank you for registering with BoardSync! Please confirm your email address by clicking the button below:</p>
                    <div style='text-align: center; margin: 30px 0;'>
                        <a href='{confirmationUrl}' 
                           style='background-color: #3498db; color: white; padding: 12px 30px; text-decoration: none; border-radius: 5px; display: inline-block;'>
                           Confirm Email Address
                        </a>
                    </div>
                    <p style='color: #666;'>If the button doesn't work, you can copy and paste this link into your browser:</p>
                    <p style='word-break: break-all; color: #3498db;'>{confirmationUrl}</p>
                    <p style='color: #666; font-size: 12px; margin-top: 30px;'>
                        If you didn't create an account with BoardSync, you can safely ignore this email.
                    </p>
                </div>
            </body>
            </html>";
    }

    private static string GeneratePasswordResetTemplate(string resetUrl)
    {
        return $@"
            <html>
            <body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
                <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
                    <h2 style='color: #2c3e50;'>Reset Your Password</h2>
                    <p>We received a request to reset your password for your BoardSync account. Click the button below to reset it:</p>
                    <div style='text-align: center; margin: 30px 0;'>
                        <a href='{resetUrl}' 
                           style='background-color: #e74c3c; color: white; padding: 12px 30px; text-decoration: none; border-radius: 5px; display: inline-block;'>
                           Reset Password
                        </a>
                    </div>
                    <p style='color: #666;'>If the button doesn't work, you can copy and paste this link into your browser:</p>
                    <p style='word-break: break-all; color: #e74c3c;'>{resetUrl}</p>
                    <p style='color: #666; font-size: 12px; margin-top: 30px;'>
                        If you didn't request a password reset, you can safely ignore this email. This link will expire in 1 hour.
                    </p>
                </div>
            </body>
            </html>";
    }

    private static string GenerateWelcomeTemplate(string firstName, string baseUrl)
    {
        var loginUrl = $"{baseUrl}/auth/login";
        return $@"
            <html>
            <body style='font-family: Arial, sans-serif; line-height: 1.6; color: #333;'>
                <div style='max-width: 600px; margin: 0 auto; padding: 20px;'>
                    <h2 style='color: #2c3e50;'>Welcome to BoardSync, {firstName}!</h2>
                    <p>Your account has been successfully created and your email has been confirmed.</p>
                    <p>You can now start using BoardSync to manage your projects and collaborate with your team.</p>
                    <div style='text-align: center; margin: 30px 0;'>
                        <a href='{loginUrl}' 
                           style='background-color: #27ae60; color: white; padding: 12px 30px; text-decoration: none; border-radius: 5px; display: inline-block;'>
                           Get Started
                        </a>
                    </div>
                    <p style='color: #666;'>
                        If you have any questions or need help getting started, feel free to contact our support team.
                    </p>
                </div>
            </body>
            </html>";
    }
}