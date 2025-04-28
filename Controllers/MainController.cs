using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Portfolio_server.Services;

namespace Portfolio_server.Controllers
{
    [Route("api")]
    [ApiController]
    public class MainController : ControllerBase
    {
        private readonly ILogger<MainController> _logger;
        private readonly PortfolioController _portfolioController;
        private readonly IGeminiService _geminiService;
        private readonly IConversationService _conversationService;
        private readonly IRateLimiterService _rateLimiterService;

        // Constructor with dependency injection
        public MainController(
            ILogger<MainController> logger,
            IGeminiService geminiService,
            IConversationService conversationService,
            IRateLimiterService rateLimiterService)
        {
            _logger = logger;
            _geminiService = geminiService;
            _conversationService = conversationService;
            _rateLimiterService = rateLimiterService;

            // Create a new logger for the portfolio controller directly
            _portfolioController = new PortfolioController(
                LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<PortfolioController>()
            );
        }

        // Health check endpoint
        [HttpGet("health")]
        public async Task<IActionResult> Health()
        {
            bool redisAvailable = await TestRedisAvailability();
            return Ok(new { status = "healthy", redisAvailable });
        }

        // Simple ping endpoint for connectivity testing
        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new { status = "pong", timestamp = DateTime.UtcNow });
        }

        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request)
        {
            try
            {
                // Validate request
                if (request == null || string.IsNullOrEmpty(request.Message))
                {
                    return BadRequest(new { message = "Message cannot be empty" });
                }

                // Get client IP address for rate limiting
                string ipAddress = GetClientIpAddress();
                _logger.LogInformation($"Processing chat request from IP: {ipAddress}");

                // Check rate limit
                bool withinRateLimit = await _rateLimiterService.CheckRateLimitAsync(ipAddress);
                if (!withinRateLimit)
                {
                    return StatusCode(429, new { message = "Rate limit exceeded. Try again tomorrow." });
                }

                // Generate a session ID for the user
                string sessionId = await GetOrCreateSessionId(ipAddress);

                // Enrich the message with portfolio context
                string enrichedMessage = request.Message;
                try
                {
                    _logger.LogInformation("Enriching chat context with portfolio data");
                    var enrichResult = _portfolioController.EnrichChatContext(request.Message) as ObjectResult;

                    if (enrichResult?.Value is object value)
                    {
                        var contextProperty = value.GetType().GetProperty("context");
                        if (contextProperty != null)
                        {
                            string portfolioContext = contextProperty.GetValue(value)?.ToString() ?? "";

                            if (!string.IsNullOrEmpty(portfolioContext))
                            {
                                // Combine the context with the original message when sending to the AI
                                enrichedMessage = $"{portfolioContext}\n\nUser message: {request.Message}";
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Failed to enrich message with portfolio data: {ex.Message}. Using original message.");
                    // Continue with original message if enrichment fails
                }

                try
                {
                    // Process the message using the Gemini service
                    var response = await _geminiService.ProcessMessageAsync(enrichedMessage, sessionId, request.Style ?? "NORMAL");

                    // Increment rate limit counter
                    await _rateLimiterService.IncrementRateLimitAsync(ipAddress);

                    return Ok(new
                    {
                        response = response.Text,
                        format = response.Format,
                        formatData = response.FormatData
                    });
                }
                catch (Exception ex)
                {
                    _logger.LogError($"Error processing message with Gemini service: {ex.Message}");

                    // Create a fallback response
                    return Ok(new
                    {
                        response = $"I'm sorry, I couldn't process your request due to a technical issue. Your message was: '{request.Message}'. Please try again later.",
                        format = "text",
                        formatData = new { } // Empty object, not null
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing chat request");

                return StatusCode(500, new
                {
                    response = "An error occurred while processing your request. Please try again later.",
                    format = "text",
                    formatData = new { }
                });
            }
        }

        [HttpPost("clear-session")]
        public async Task<IActionResult> ClearSession()
        {
            try
            {
                string ipAddress = GetClientIpAddress();
                string sessionId = await GetOrCreateSessionId(ipAddress);

                bool cleared = await _conversationService.ClearConversationAsync(sessionId);

                if (cleared)
                {
                    _logger.LogInformation($"Cleared conversation for session {sessionId}");
                    return Ok(new { message = "Session cleared successfully" });
                }
                else
                {
                    return BadRequest(new { message = "Failed to clear session or session does not exist" });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing session");
                return StatusCode(500, new { message = "An error occurred while clearing your session" });
            }
        }

        [HttpGet("remaining")]
        public async Task<IActionResult> RemainingRequests()
        {
            try
            {
                string ipAddress = GetClientIpAddress();
                _logger.LogInformation($"Checking remaining requests for IP: {ipAddress}");

                int remaining = await _rateLimiterService.GetRemainingRequestsAsync(ipAddress);

                return Ok(new { remaining });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting remaining requests");
                // Return a default value rather than error
                return Ok(new { remaining = 15 });
            }
        }

        // Helper method to get client IP address
        private string GetClientIpAddress()
        {
            // Try to get IP from forwarded headers first (for proxy scenarios)
            string ip = Request.Headers["X-Forwarded-For"].ToString();

            if (string.IsNullOrEmpty(ip))
            {
                ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown";
            }

            return ip;
        }

        // Helper method to get or create session ID
        private async Task<string> GetOrCreateSessionId(string ipAddress)
        {
            // This is a simple implementation where session ID is just the IP address
            // In a production app, you would use a more sophisticated approach
            return $"session-{ipAddress}";
        }

        // Helper method to test Redis availability
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

    // Request model
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string? Style { get; set; }
    }
}