using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Portfolio_server.Models;
using Portfolio_server.Services;
using System.Net.Mail;
using Microsoft.AspNetCore.Builder.Extensions;
using Microsoft.Extensions.Options;

namespace Portfolio_server.Controllers
{
    [Route("api")]
    [ApiController]
    [Produces("application/json")]
    [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
    public class MainController : ControllerBase
    {
        private readonly ILogger<MainController> _logger;
        private readonly IGeminiService _geminiService;
        private readonly IConversationService _conversationService;
        private readonly IRateLimiterService _rateLimiterService;
        private readonly IPortfolioService _portfolioService;
        private readonly SmtpOptions _smtp;              // ← already declared
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);

        public MainController(
            ILogger<MainController> logger,
            IGeminiService geminiService,
            IConversationService conversationService,
            IRateLimiterService rateLimiterService,
            IPortfolioService portfolioService,
            IOptions<SmtpOptions> smtpOptions        // ← inject here
        )
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _geminiService = geminiService ?? throw new ArgumentNullException(nameof(geminiService));
            _conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
            _rateLimiterService = rateLimiterService ?? throw new ArgumentNullException(nameof(rateLimiterService));
            _portfolioService = portfolioService ?? throw new ArgumentNullException(nameof(portfolioService));
            _smtp = smtpOptions?.Value ?? throw new ArgumentNullException(nameof(smtpOptions));
        }

        /// <summary>
        /// Checks the health status of the API and its dependencies
        /// </summary>
        /// <returns>Health status including Redis availability</returns>
        [HttpGet("health")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Health()
        {
            try
            {
                bool redisAvailable = await TestRedisAvailability();

                var response = new
                {
                    status = redisAvailable ? "healthy" : "degraded",
                    timestamp = DateTimeOffset.UtcNow,
                    redisAvailable,
                    version = GetType().Assembly.GetName().Version?.ToString() ?? "unknown"
                };

                return redisAvailable ? Ok(response) : StatusCode(StatusCodes.Status200OK, response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking health status");
                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    status = "unhealthy",
                    message = "Error checking health status",
                    timestamp = DateTimeOffset.UtcNow
                });
            }
        }

        /// <summary>
        /// Simple ping endpoint for connectivity testing
        /// </summary>
        /// <returns>Pong response with timestamp</returns>
        [HttpGet("ping")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public IActionResult Ping()
        {
            return Ok(new { status = "pong", timestamp = DateTime.UtcNow });
        }

        /// <summary>
        /// Processes a chat request through the AI service
        /// </summary>
        /// <param name="request">Chat request with message and optional style</param>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>AI response with optional formatting</returns>
        [HttpPost("chat")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(object), StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request, CancellationToken cancellationToken = default)
        {
            var sw = Stopwatch.StartNew();
            var requestId = Guid.NewGuid().ToString("N");
            _logger.LogInformation($"[{requestId}] Chat request received");

            try
            {
                // Validate request
                if (request == null || string.IsNullOrEmpty(request.Message))
                {
                    _logger.LogWarning($"[{requestId}] Invalid request: message is empty or null");
                    return BadRequest(new { message = "Message cannot be empty" });
                }

                if (request.Message.Length > 4000)
                {
                    _logger.LogWarning($"[{requestId}] Message too long: {request.Message.Length} characters");
                    return BadRequest(new { message = "Message is too long. Please limit to 4000 characters." });
                }

                // Get client IP address for rate limiting
                string ipAddress = GetClientIpAddress();
                _logger.LogInformation($"[{requestId}] Processing chat request from IP: {ipAddress}");

                // Check rate limit
                bool withinRateLimit = await _rateLimiterService.CheckRateLimitAsync(ipAddress);
                if (!withinRateLimit)
                {
                    _logger.LogWarning($"[{requestId}] Rate limit exceeded for IP: {ipAddress}");
                    return StatusCode(StatusCodes.Status429TooManyRequests, new
                    {
                        message = "Rate limit exceeded. Try again tomorrow.",
                        retryAfter = "86400" // 24 hours in seconds 
                    });
                }

                // Apply timeout to the operation
                using var timeoutCts = new CancellationTokenSource(RequestTimeout);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);

                // Generate a session ID for the user
                string sessionId = await GetOrCreateSessionId(ipAddress);

                // Enrich the message with portfolio context
                string enrichedMessage = request.Message;
                try
                {
                    _logger.LogInformation($"[{requestId}] Enriching chat context with portfolio data");
                    string portfolioContext = await _portfolioService.EnrichChatContextAsync(request.Message);

                    if (!string.IsNullOrEmpty(portfolioContext))
                    {
                        // Combine the context with the original message when sending to the AI
                        enrichedMessage = $"{portfolioContext}\n\nUser message: {request.Message}";
                        _logger.LogDebug($"[{requestId}] Added portfolio context. Original length: {request.Message.Length}, Enriched length: {enrichedMessage.Length}");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, $"[{requestId}] Failed to enrich message with portfolio data. Using original message.");
                    // Continue with original message if enrichment fails
                }

                try
                {
                    // Process the message using the Gemini service
                    _logger.LogInformation($"[{requestId}] Sending request to Gemini service");
                    // Update the call to ProcessMessageAsync to match the provided signature
                    var response = await _geminiService.ProcessMessageAsync(
                        enrichedMessage,
                        sessionId,
                        request.Style ?? "NORMAL"
                    );

                    // Increment rate limit counter
                    await _rateLimiterService.IncrementRateLimitAsync(ipAddress);

                    // Save the conversation
                    await _conversationService.SaveConversationAsync(sessionId, request.Message, response.Text);

                    sw.Stop();
                    _logger.LogInformation($"[{requestId}] Chat request processed successfully in {sw.ElapsedMilliseconds}ms");

                    return Ok(new
                    {
                        response = response.Text,
                        format = response.Format,
                        formatData = response.FormatData,
                        processingTime = sw.ElapsedMilliseconds
                    });
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogWarning($"[{requestId}] Request timed out after {RequestTimeout.TotalSeconds}s");
                    return StatusCode(StatusCodes.Status408RequestTimeout, new
                    {
                        response = "Sorry, the request timed out. Please try a shorter message or try again later.",
                        format = "text",
                        formatData = new { }
                    });
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"[{requestId}] Request was cancelled by client");
                    return StatusCode(499, new // 499 is "Client Closed Request" in Nginx
                    {
                        response = "Request cancelled",
                        format = "text",
                        formatData = new { }
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[{requestId}] Error processing message with Gemini service: {ex.Message}");

                    // Create a fallback response
                    return Ok(new
                    {
                        response = $"I'm sorry, I couldn't process your request due to a technical issue. Please try again later.",
                        format = "text",
                        formatData = new { } // Empty object, not null
                    });
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, $"[{requestId}] Unhandled error processing chat request: {ex.Message}");

                return StatusCode(StatusCodes.Status500InternalServerError, new
                {
                    response = "An error occurred while processing your request. Please try again later.",
                    format = "text",
                    formatData = new { }
                });
            }
        }

        /// <summary>
        /// Clears the conversation history for the current session
        /// </summary>
        /// <returns>Success or failure message</returns>
        [HttpPost("clear-session")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ClearSession()
        {
            string requestId = Guid.NewGuid().ToString("N");
            try
            {
                string ipAddress = GetClientIpAddress();
                string sessionId = await GetOrCreateSessionId(ipAddress);

                _logger.LogInformation($"[{requestId}] Clearing session for IP: {ipAddress}, Session: {sessionId}");

                bool cleared = await _conversationService.ClearConversationAsync(sessionId);

                if (cleared)
                {
                    _logger.LogInformation($"[{requestId}] Cleared conversation for session {sessionId}");
                    return Ok(new { message = "Session cleared successfully" });
                }
                else
                {
                    _logger.LogWarning($"[{requestId}] Failed to clear session {sessionId}");
                    return BadRequest(new { message = "Failed to clear session or session does not exist" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{requestId}] Error clearing session: {ex.Message}");
                return StatusCode(StatusCodes.Status500InternalServerError, new { message = "An error occurred while clearing your session" });
            }
        }
        [HttpPost("contact")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SendContactEmail([FromBody] ContactRequest model)
        {
            if (model == null ||
                string.IsNullOrWhiteSpace(model.Name) ||
                string.IsNullOrWhiteSpace(model.Email) ||
                string.IsNullOrWhiteSpace(model.Message))
            {
                return BadRequest(new { message = "Name, email and message are required." });
            }

            try
            {
                // Build the mail
                var mail = new MailMessage
                {
                    From = new MailAddress("no-reply@yourdomain.com", "Portfolio Site"),
                    Subject = $"New contact from {model.Name}",
                    Body = $@"
                You have a new contact form submission:

                Name:    {model.Name}
                Email:   {model.Email}
                Phone:   {model.Phone}

                Message:
                {model.Message}
            ",
                    IsBodyHtml = false
                };
                mail.To.Add("razvan.bordinc@yahoo.com");

                // Configure SMTP (adjust host/port/creds to your SMTP provider)
                using var smtp = new SmtpClient("smtp.yourprovider.com", 587)
                {
                    Credentials = new NetworkCredential("smtp-username", "ylhyyjtxbuzbnncd"),
                    EnableSsl = true
                };

                // Send
                await smtp.SendMailAsync(mail);

                return Ok(new { message = "Email sent successfully." });
            }
            catch (SmtpException smtpEx)
            {
                _logger.LogError(smtpEx, "SMTP error sending contact email");
                return StatusCode(500, new { message = "Failed to send email." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in SendContactEmail");
                return StatusCode(500, new { message = "An unexpected error occurred." });
            }
        }
        /// <summary>
        /// Gets the number of remaining API requests for the current user
        /// </summary>
        /// <returns>Number of remaining requests</returns>
        [HttpGet("remaining")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public async Task<IActionResult> RemainingRequests()
        {
            string requestId = Guid.NewGuid().ToString("N");
            try
            {
                string ipAddress = GetClientIpAddress();
                _logger.LogInformation($"[{requestId}] Checking remaining requests for IP: {ipAddress}");

                int remaining = await _rateLimiterService.GetRemainingRequestsAsync(ipAddress);
                int maxRequests = 15; // Consider making this configurable

                return Ok(new
                {
                    remaining,
                    total = maxRequests,
                    resetInHours = 24 - DateTime.UtcNow.Hour
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{requestId}] Error getting remaining requests: {ex.Message}");
                // Return a default value rather than error
                return Ok(new { remaining = 15, total = 15 });
            }
        }

        /// <summary>
        /// Gets the client IP address from request headers or connection info
        /// </summary>
        /// <returns>Client IP address string</returns>
        private string GetClientIpAddress()
        {
            // Try to get IP from forwarded headers first (for proxy scenarios)
            string ip = Request.Headers["X-Forwarded-For"].ToString();

            if (string.IsNullOrEmpty(ip))
            {
                ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            }
            else
            {
                // X-Forwarded-For may contain multiple IPs - take the first one (client)
                int commaIndex = ip.IndexOf(',');
                if (commaIndex > 0)
                {
                    ip = ip.Substring(0, commaIndex).Trim();
                }
            }

            return ip;
        }

        /// <summary>
        /// Gets or creates a session ID for the user
        /// </summary>
        /// <param name="ipAddress">Client IP address</param>
        /// <returns>Session ID</returns>
        private Task<string> GetOrCreateSessionId(string ipAddress)
        {
 
            return Task.FromResult($"session-{ipAddress}");
        }

        /// <summary>
        /// Tests if Redis is available
        /// </summary>
        /// <returns>True if Redis is available, false otherwise</returns>
        private async Task<bool> TestRedisAvailability()
        {
            try
            {
                // Use the conversation service to test Redis
                await _conversationService.GetConversationHistoryAsync("test-session");
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}