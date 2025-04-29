using Microsoft.Extensions.Options;
using Portfolio_server.Models;
using System.Net.Mail;
using System.Net;
using StackExchange.Redis;
using System.Text.Json;

namespace Portfolio_server.Services
{
    public class EmailService : IEmailService
    {
        private readonly ILogger<EmailService> _logger;
        private readonly SmtpOptions _smtpOptions;
        private readonly IConnectionMultiplexer _redis;

        public EmailService(
            ILogger<EmailService> logger,
            IOptions<SmtpOptions> smtpOptions,
            IConnectionMultiplexer redis)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _smtpOptions = smtpOptions?.Value ?? throw new ArgumentNullException(nameof(smtpOptions));
            _redis = redis;

            // Log SMTP settings (without password) for debugging
            _logger.LogInformation($"Email service initialized with: Host={_smtpOptions.Host}, Port={_smtpOptions.Port}, SSL={_smtpOptions.EnableSsl}, Username={_smtpOptions.Username}");
        }

        public async Task<bool> SendContactEmailAsync(ContactRequest contactRequest)
        {
            try
            {
                if (contactRequest == null)
                {
                    throw new ArgumentNullException(nameof(contactRequest));
                }

                if (string.IsNullOrEmpty(contactRequest.Name) ||
                    string.IsNullOrEmpty(contactRequest.Email) ||
                    string.IsNullOrEmpty(contactRequest.Message))
                {
                    _logger.LogWarning("Invalid contact request: Missing required fields");
                    return false;
                }

                // Store the contact request in Redis first (as backup)
                await StoreContactRequestInRedisAsync(contactRequest);
                _logger.LogInformation($"Stored contact request from {contactRequest.Name} in Redis for backup");

                // Check if we have SMTP password set
                if (string.IsNullOrEmpty(_smtpOptions.Password) || _smtpOptions.Password == "YOUR_YAHOO_APP_PASSWORD")
                {
                    _logger.LogError("SMTP password not configured. Please set a valid password in configuration.");
                    return false;
                }

                // Always use the configured username for sending
                string senderEmail = _smtpOptions.Username;
                _logger.LogInformation($"Using sender email: {senderEmail}");

                // Create the email message
                var message = new MailMessage
                {
                    From = new MailAddress(senderEmail),
                    Subject = $"Portfolio Contact: {contactRequest.Name}",
                    Body = FormatEmailBody(contactRequest),
                    IsBodyHtml = true
                };

                // Add recipient (your email address)
                message.To.Add(new MailAddress(senderEmail));

                // Set up reply-to so you can reply directly to the sender
                message.ReplyToList.Add(new MailAddress(contactRequest.Email, contactRequest.Name));

                // Configure SMTP client with more robust error handling
                using var client = new SmtpClient
                {
                    Host = _smtpOptions.Host,
                    Port = _smtpOptions.Port,
                    EnableSsl = _smtpOptions.EnableSsl,
                    DeliveryMethod = SmtpDeliveryMethod.Network,
                    UseDefaultCredentials = false,
                    Credentials = new NetworkCredential(senderEmail, _smtpOptions.Password),
                    Timeout = 30000 // 30 seconds timeout
                };

                _logger.LogInformation($"Attempting to send email via {_smtpOptions.Host}:{_smtpOptions.Port}");

                try
                {
                    // Send the email
                    await client.SendMailAsync(message);
                    _logger.LogInformation($"Contact email sent successfully from {contactRequest.Name} ({contactRequest.Email})");
                    return true;
                }
                catch (SmtpException smtpEx)
                {
                    _logger.LogError(smtpEx, "SMTP Error sending contact email");

                    // Log specific error code
                    _logger.LogError($"SMTP Status Code: {smtpEx.StatusCode}, Response: {smtpEx.Message}");

                    // For certain SMTP errors, provide more detailed troubleshooting
                    if (smtpEx.StatusCode == SmtpStatusCode.MailboxUnavailable)
                    {
                        _logger.LogError("Mailbox unavailable error. Check that Yahoo App Password is correct and enabled.");
                    }
                    else if (smtpEx.Message.Contains("authentication"))
                    {
                        _logger.LogError("Authentication error. Verify username and password in configuration.");
                    }

                    // Already stored contact in Redis at the beginning
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending contact email");
                return false;
            }
        }

        private async Task StoreContactRequestInRedisAsync(ContactRequest contactRequest)
        {
            try
            {
                if (_redis == null) return;

                var db = _redis.GetDatabase();
                var key = $"contact:request:{DateTime.UtcNow.Ticks}";

                var content = JsonSerializer.Serialize(contactRequest);
                await db.StringSetAsync(key, content, TimeSpan.FromDays(30));
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing contact request in Redis");
            }
        }

        private string FormatEmailBody(ContactRequest contactRequest)
        {
            return $@"
                <html>
                <body style='font-family: Arial, sans-serif; color: #333; max-width: 600px; margin: 0 auto;'>
                    <div style='padding: 20px; background-color: #f5f5f5; border-radius: 5px;'>
                        <h2 style='color: #4f46e5;'>New Contact Form Submission</h2>
                        <hr style='border: 1px solid #ddd;' />
                        
                        <div style='margin-top: 20px;'>
                            <p><strong>Name:</strong> {WebUtility.HtmlEncode(contactRequest.Name)}</p>
                            <p><strong>Email:</strong> {WebUtility.HtmlEncode(contactRequest.Email)}</p>
                            <p><strong>Phone:</strong> {WebUtility.HtmlEncode(contactRequest.Phone ?? "Not provided")}</p>
                        </div>
                        
                        <div style='margin-top: 20px; background-color: white; padding: 15px; border-radius: 5px; border-left: 4px solid #4f46e5;'>
                            <h3 style='margin-top: 0; color: #4f46e5;'>Message:</h3>
                            <p style='white-space: pre-line;'>{WebUtility.HtmlEncode(contactRequest.Message)}</p>
                        </div>
                        
                        <div style='margin-top: 20px; font-size: 0.9em; color: #666;'>
                            <p>This message was sent from your portfolio website contact form.</p>
                            <p>IP: {GetIpAddress()}</p>
                            <p>Date: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")} UTC</p>
                        </div>
                    </div>
                </body>
                </html>
            ";
        }

        private string GetIpAddress()
        {
            try
            {
                return Dns.GetHostEntry(Dns.GetHostName())
                    .AddressList
                    .FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                    ?.ToString() ?? "Unknown";
            }
            catch
            {
                return "Unknown";
            }
        }
    }
}