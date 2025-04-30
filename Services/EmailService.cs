using Microsoft.Extensions.Options;
using Portfolio_server.Models;
using StackExchange.Redis;
using System.Text.Json;
using System.Net;
using MimeKit;
// Using MailKit explicitly with alias to avoid ambiguity
using MailKitSmtp = MailKit.Net.Smtp;
using MailKit.Security;

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

            // Log SMTP settings (without password)
            _logger.LogInformation($"Email service initialized with: Host={_smtpOptions.Host}, Port={_smtpOptions.Port}, SSL={_smtpOptions.EnableSsl}, Username={_smtpOptions.Username}");
        }

        public async Task<bool> SendContactEmailAsync(ContactRequest contactRequest)
        {
            if (contactRequest == null)
            {
                _logger.LogWarning("Invalid contact request: null request");
                return false;
            }

            if (string.IsNullOrEmpty(contactRequest.Name) ||
                string.IsNullOrEmpty(contactRequest.Email) ||
                string.IsNullOrEmpty(contactRequest.Message))
            {
                _logger.LogWarning("Invalid contact request: Missing required fields");
                return false;
            }

            // Always store in Redis as backup first
            bool storedInRedis = await StoreContactInRedisAsync(contactRequest);

            if (!storedInRedis)
            {
                _logger.LogWarning($"Failed to store contact request from {contactRequest.Name} in Redis");
            }
            else
            {
                _logger.LogInformation($"Successfully stored contact request from {contactRequest.Name} in Redis");
            }

            // Try sending the email with MailKit
            bool emailSent = await SendEmailWithMailKitAsync(contactRequest);

            // Return true if either the email was sent OR it was stored in Redis
            return emailSent || storedInRedis;
        }

        private async Task<bool> SendEmailWithMailKitAsync(ContactRequest contactRequest)
        {
            try
            {
                _logger.LogInformation($"Attempting to send email with MailKit from {contactRequest.Name} via {_smtpOptions.Host}:{_smtpOptions.Port}");

                // Create the email message
                var message = new MimeMessage();
                message.From.Add(new MailboxAddress("Portfolio Contact Form", _smtpOptions.Username));
                message.To.Add(new MailboxAddress("Razvan Bordinc", _smtpOptions.Username));
                message.ReplyTo.Add(new MailboxAddress(contactRequest.Name, contactRequest.Email));
                message.Subject = $"Portfolio Contact: {contactRequest.Name}";

                // Create HTML body
                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = FormatEmailBody(contactRequest)
                };

                message.Body = bodyBuilder.ToMessageBody();

                // Send using MailKit's SmtpClient - note we're using the MailKit version explicitly
                using (var client = new MailKitSmtp.SmtpClient())
                {
                    // Configure security based on port
                    var securityOptions = SecureSocketOptions.Auto;
                    if (_smtpOptions.Port == 465)
                    {
                        securityOptions = SecureSocketOptions.SslOnConnect;
                    }
                    else if (_smtpOptions.Port == 587)
                    {
                        securityOptions = SecureSocketOptions.StartTls;
                    }

                    client.Timeout = 15000; // 15 second timeout

                    // Connect to SMTP server
                    await client.ConnectAsync(_smtpOptions.Host, _smtpOptions.Port, securityOptions);

                    // Authenticate
                    await client.AuthenticateAsync(_smtpOptions.Username, _smtpOptions.Password);

                    // Send the message
                    await client.SendAsync(message);

                    // Disconnect properly
                    await client.DisconnectAsync(true);

                    _logger.LogInformation($"Email sent successfully via MailKit from {contactRequest.Name} ({contactRequest.Email})");
                    return true;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending email with MailKit from {contactRequest.Name}");
                return false;
            }
        }

        private async Task<bool> StoreContactInRedisAsync(ContactRequest contactRequest)
        {
            try
            {
                if (_redis == null) return false;

                var db = _redis.GetDatabase();
                if (db == null) return false;

                // Use timestamp for unique key
                var timestamp = DateTime.UtcNow.Ticks;
                var uniqueKey = $"contact:request:{timestamp}";

                // Create data dictionary
                var contactData = new Dictionary<string, string>
                {
                    ["name"] = contactRequest.Name,
                    ["email"] = contactRequest.Email,
                    ["phone"] = contactRequest.Phone ?? "Not provided",
                    ["message"] = contactRequest.Message,
                    ["timestamp"] = timestamp.ToString(),
                    ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")
                };

                // Serialize and store as string
                string jsonData = JsonSerializer.Serialize(contactData);
                await db.StringSetAsync(uniqueKey, jsonData, TimeSpan.FromDays(30));

                // Maintain a simple index of contact requests
                string indexKey = "contact:index";
                await db.SetAddAsync(indexKey, uniqueKey);

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error storing contact request in Redis");
                return false;
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
                            <p>Date: {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")} UTC</p>
                        </div>
                    </div>
                </body>
                </html>
            ";
        }
    }
}