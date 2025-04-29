using Microsoft.Extensions.Options;
using Portfolio_server.Models;
using System.Net.Mail;
using System.Net;

namespace Portfolio_server.Services
{
    public class EmailService : IEmailService
    {
        private readonly ILogger<EmailService> _logger;
        private readonly SmtpOptions _smtpOptions;

        public EmailService(ILogger<EmailService> logger, IOptions<SmtpOptions> smtpOptions)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _smtpOptions = smtpOptions?.Value ?? throw new ArgumentNullException(nameof(smtpOptions));
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

                // Create the email message
                var message = new MailMessage
                {
                    From = new MailAddress(_smtpOptions.Username),
                    Subject = $"Portfolio Contact: {contactRequest.Name}",
                    Body = FormatEmailBody(contactRequest),
                    IsBodyHtml = true
                };

                // Add recipient (your email address)
                message.To.Add(new MailAddress(_smtpOptions.Username));

                // Set up reply-to so you can reply directly to the sender
                message.ReplyToList.Add(new MailAddress(contactRequest.Email, contactRequest.Name));

                // Configure SMTP client
                using var client = new SmtpClient(_smtpOptions.Host, _smtpOptions.Port)
                {
                    EnableSsl = _smtpOptions.EnableSsl,
                    Credentials = new NetworkCredential(_smtpOptions.Username, _smtpOptions.Password),
                    DeliveryMethod = SmtpDeliveryMethod.Network
                };

                // Send the email
                await client.SendMailAsync(message);
                _logger.LogInformation($"Contact email sent successfully from {contactRequest.Name} ({contactRequest.Email})");
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error sending contact email");
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
