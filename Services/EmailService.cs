using Microsoft.Extensions.Options;
using Portfolio_server.Models;
using StackExchange.Redis;
using System.Text.Json;
using System.Net;
using SendGrid;
using SendGrid.Helpers.Mail;

namespace Portfolio_server.Services
{
    public class EmailService : IEmailService
    {
        private readonly ILogger<EmailService> _logger;
        private readonly SendGridOptions _sendGridOptions;
        private readonly IConnectionMultiplexer _redis;
        private const int DEFAULT_EMAIL_RATE_LIMIT = 2; // Max emails per IP

        public EmailService(
            ILogger<EmailService> logger,
            IOptions<SendGridOptions> sendGridOptions,
            IConnectionMultiplexer redis)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _sendGridOptions = sendGridOptions?.Value ?? throw new ArgumentNullException(nameof(sendGridOptions));
            _redis = redis;

            // Log SendGrid settings (without API key)
            _logger.LogInformation($"Email service initialized with SendGrid: FromEmail={_sendGridOptions.FromEmail}, " +
                $"ToEmail={_sendGridOptions.ToEmail}, RateLimit={_sendGridOptions.EmailRateLimit}, " +
                $"ApiKeyConfigured={(string.IsNullOrEmpty(_sendGridOptions.ApiKey) ? "No" : "Yes")}");
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

            // Get client IP from context (passed in the request metadata)
            string ipAddress = contactRequest.ClientIp ?? "unknown";

            // Check email rate limit for this IP
            if (!await CheckEmailRateLimitAsync(ipAddress))
            {
                _logger.LogWarning($"Email rate limit exceeded for IP: {ipAddress}");
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

            // Try sending the email with SendGrid
            bool emailSent = await SendEmailWithSendGridAsync(contactRequest);

            if (emailSent)
            {
                // Increment email count for this IP
                await IncrementEmailCountAsync(ipAddress);
            }

            // Return true if either the email was sent OR it was stored in Redis
            return emailSent || storedInRedis;
        }

        private async Task<bool> SendEmailWithSendGridAsync(ContactRequest contactRequest)
        {
            try
            {
                if (string.IsNullOrEmpty(_sendGridOptions.ApiKey))
                {
                    _logger.LogError("SendGrid API key is not configured. Cannot send email.");
                    return false;
                }

                _logger.LogInformation($"Attempting to send email with SendGrid from {contactRequest.Name}");

                var client = new SendGridClient(_sendGridOptions.ApiKey);
                var from = new EmailAddress(_sendGridOptions.FromEmail, _sendGridOptions.FromName);
                var to = new EmailAddress(_sendGridOptions.ToEmail, _sendGridOptions.ToName);
                var replyTo = new EmailAddress(contactRequest.Email, contactRequest.Name);

                // Set subject
                string subject = $"Portfolio Contact: {contactRequest.Name}";

                // Create HTML content
                string htmlContent = FormatEmailBody(contactRequest);
                string plainTextContent = $"Name: {contactRequest.Name}\nEmail: {contactRequest.Email}\nPhone: {contactRequest.Phone ?? "Not provided"}\n\nMessage:\n{contactRequest.Message}";

                // Create SendGrid message
                var msg = MailHelper.CreateSingleEmail(
                    from,
                    to,
                    subject,
                    plainTextContent,
                    htmlContent
                );

                // Set reply-to
                msg.SetReplyTo(replyTo);

                // Send the email
                var response = await client.SendEmailAsync(msg);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation($"Email sent successfully via SendGrid from {contactRequest.Name} ({contactRequest.Email})");
                    return true;
                }
                else
                {
                    string responseBody = await response.Body.ReadAsStringAsync();
                    _logger.LogError($"SendGrid error: Status {response.StatusCode}, Message: {responseBody}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error sending email with SendGrid from {contactRequest.Name}");
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
                    ["date"] = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss"),
                    ["ip"] = contactRequest.ClientIp ?? "unknown"
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

        private async Task<bool> CheckEmailRateLimitAsync(string ipAddress)
        {
            try
            {
                if (_redis == null) return true; // If Redis is not available, allow the request

                var db = _redis.GetDatabase();
                if (db == null) return true;

                var key = $"email:ratelimit:{ipAddress}";

                // Check if key exists
                if (!await db.KeyExistsAsync(key))
                {
                    return true; // No emails sent yet
                }

                // Get current count
                var value = await db.StringGetAsync(key);
                if (!value.HasValue)
                {
                    return true;
                }

                if (!int.TryParse(value, out int count))
                {
                    _logger.LogWarning($"Invalid email rate limit value in Redis for IP {ipAddress}: {value}");
                    return true; // Allow on error
                }

                // Get limit from settings or use default
                int limit = _sendGridOptions.EmailRateLimit > 0 ?
                    _sendGridOptions.EmailRateLimit : DEFAULT_EMAIL_RATE_LIMIT;

                bool result = count < limit;
                _logger.LogInformation($"Email rate limit check for IP {ipAddress}: {count}/{limit} emails used, allowed = {result}");

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking email rate limit for IP {ipAddress}");
                return true; // Allow the request on error
            }
        }

        private async Task<bool> IncrementEmailCountAsync(string ipAddress)
        {
            try
            {
                if (_redis == null) return false;

                var db = _redis.GetDatabase();
                if (db == null) return false;

                var key = $"email:ratelimit:{ipAddress}";

                if (await db.KeyExistsAsync(key))
                {
                    // Increment existing counter
                    var newValue = await db.StringIncrementAsync(key);
                    _logger.LogInformation($"Incremented email count for IP {ipAddress} to {newValue}");
                }
                else
                {
                    // Create new counter with 24-hour TTL
                    await db.StringSetAsync(key, 1, TimeSpan.FromHours(24));
                    _logger.LogInformation($"Created new email count for IP {ipAddress} with TTL of 24 hours");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error incrementing email count for IP {ipAddress}");
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
                            <p>IP: {contactRequest.ClientIp ?? "unknown"}</p>
                        </div>
                    </div>
                </body>
                </html>
            ";
        }
    }
}