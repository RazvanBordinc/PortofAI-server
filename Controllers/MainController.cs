using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Portfolio_server.Models;
using Portfolio_server.Services;

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
        private readonly IEmailService _emailService;
        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30); // Increased timeout for streaming

        public MainController(
            ILogger<MainController> logger,
            IGeminiService geminiService,
            IConversationService conversationService,
            IRateLimiterService rateLimiterService,
            IPortfolioService portfolioService,
            IEmailService emailService
        )
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _geminiService = geminiService ?? throw new ArgumentNullException(nameof(geminiService));
            _conversationService = conversationService ?? throw new ArgumentNullException(nameof(conversationService));
            _rateLimiterService = rateLimiterService ?? throw new ArgumentNullException(nameof(rateLimiterService));
            _portfolioService = portfolioService ?? throw new ArgumentNullException(nameof(portfolioService));
            _emailService = emailService ?? throw new ArgumentNullException(nameof(emailService));
        }

        // New endpoint to get remaining requests
        [HttpGet("remaining")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetRemainingRequests()
        {
            try
            {
                string ipAddress = GetClientIpAddress();
                int maxRequests = 15; // Default max requests

                int remaining = await _rateLimiterService.GetRemainingRequestsAsync(ipAddress, maxRequests);

                return Ok(new { remaining });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting remaining requests");
                return StatusCode(500, new { error = "Error getting remaining requests" });
            }
        }

        // New streaming chat endpoint
        [HttpPost("chat/stream")]
        [ProducesResponseType(typeof(string), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(object), StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task StreamChat([FromBody] ChatRequest request, CancellationToken cancellationToken = default)
        {
            var requestId = Guid.NewGuid().ToString("N");
            var sw = Stopwatch.StartNew();

            _logger.LogInformation($"[{requestId}] Streaming chat request received");

            try
            {
                // Validate request
                if (request == null || string.IsNullOrEmpty(request.Message))
                {
                    _logger.LogWarning($"[{requestId}] Invalid request: message is empty or null");
                    await WriteErrorResponseAsync("Message cannot be empty", StatusCodes.Status400BadRequest);
                    return;
                }

                if (request.Message.Length > 4000)
                {
                    _logger.LogWarning($"[{requestId}] Message too long: {request.Message.Length} characters");
                    await WriteErrorResponseAsync("Message is too long. Please limit to 4000 characters.", StatusCodes.Status400BadRequest);
                    return;
                }

                // Get client IP address for rate limiting
                string ipAddress = GetClientIpAddress();
                _logger.LogInformation($"[{requestId}] Processing streaming chat request from IP: {ipAddress}");

                // Check rate limit
                bool withinRateLimit = await _rateLimiterService.CheckRateLimitAsync(ipAddress);
                if (!withinRateLimit)
                {
                    _logger.LogWarning($"[{requestId}] Rate limit exceeded for IP: {ipAddress}");
                    await WriteErrorResponseAsync("Rate limit exceeded. Try again tomorrow.", StatusCodes.Status429TooManyRequests);
                    return;
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
                    // IMPORTANT: Set up the response for streaming with proper SSE headers
                    Response.StatusCode = StatusCodes.Status200OK;

                    // Clear any existing headers to avoid conflicts
                    Response.Headers.Clear();

                    // Set SSE headers
                    Response.ContentType = "text/event-stream";
                    Response.Headers.Add("Cache-Control", "no-cache");
                    Response.Headers.Add("Connection", "keep-alive");
                    Response.Headers.Add("X-Accel-Buffering", "no"); // For Nginx
                    Response.Headers.Add("Access-Control-Allow-Origin", "*"); // Enable CORS for SSE

                    // Process the message using the Gemini service with streaming
                    _logger.LogInformation($"[{requestId}] Starting streaming response from Gemini service");

                    // Send a heartbeat immediately to establish the connection
                    await WriteHeartbeatAsync();

                    // Accumulate the full response
                    var responseBuilder = new StringBuilder();

                    // Call the streaming version of the service
                    string fullResponse = await _geminiService.StreamMessageAsync(
                        enrichedMessage,
                        sessionId,
                        request.Style ?? "NORMAL",
                        async (chunk) => {
                            // Add to full response
                            responseBuilder.Append(chunk);
                            
                            // Write chunk to client
                            await WriteChunkAsync(chunk);
                            
                            // Send heartbeat after each chunk to keep connection alive
                            await WriteHeartbeatAsync();
                        },
                        linkedCts.Token
                    );

                    // If the fullResponse is empty but we accumulated chunks, use those instead
                    if (string.IsNullOrEmpty(fullResponse) && responseBuilder.Length > 0)
                    {
                        fullResponse = responseBuilder.ToString();
                    }

                    // Send the completion SSE event - this is where the final text is sent
                    await WriteDoneAsync(fullResponse);

                    // Increment rate limit counter
                    await _rateLimiterService.IncrementRateLimitAsync(ipAddress);

                    // Save the conversation after the full response is generated
                    await _conversationService.SaveConversationAsync(sessionId, request.Message, fullResponse);

                    sw.Stop();
                    _logger.LogInformation($"[{requestId}] Streaming chat request completed in {sw.ElapsedMilliseconds}ms");
                }
                catch (OperationCanceledException) when (timeoutCts.Token.IsCancellationRequested)
                {
                    _logger.LogWarning($"[{requestId}] Streaming request timed out after {RequestTimeout.TotalSeconds}s");
                    await WriteErrorResponseAsync("Sorry, the request timed out. Please try a shorter message or try again later.", StatusCodes.Status408RequestTimeout);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    _logger.LogWarning($"[{requestId}] Streaming request was cancelled by client");
                    // Client disconnected, no need to send a response
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"[{requestId}] Error processing streaming message with Gemini service: {ex.Message}");
                    await WriteErrorResponseAsync("I'm sorry, I couldn't process your request due to a technical issue. Please try again later.", StatusCodes.Status500InternalServerError);
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                _logger.LogError(ex, $"[{requestId}] Unhandled error processing streaming chat request: {ex.Message}");
                await WriteErrorResponseAsync("An error occurred while processing your request. Please try again later.", StatusCodes.Status500InternalServerError);
            }
        }
 
        [HttpGet("conversation/history")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status404NotFound)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> GetConversationHistory()
        {
            try
            {
                // Get client IP address to use as session ID
                string ipAddress = GetClientIpAddress();
                string sessionId = await GetOrCreateSessionId(ipAddress);

                // Get conversation history
                var historyData = await _conversationService.GetFormattedConversationHistoryAsync(sessionId);

                if (historyData == null || !historyData.Any())
                {
                    return NotFound(new { message = "No conversation history found" });
                }

                return Ok(new { messages = historyData });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving conversation history");
                return StatusCode(500, new { error = "Error retrieving conversation history" });
            }
        }

 
        [HttpPost("conversation/clear")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> ClearConversationHistory()
        {
            try
            {
                // Get client IP address to use as session ID
                string ipAddress = GetClientIpAddress();
                string sessionId = await GetOrCreateSessionId(ipAddress);

                // Clear conversation history
                bool success = await _conversationService.ClearConversationAsync(sessionId);

                return Ok(new { success });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing conversation history");
                return StatusCode(500, new { error = "Error clearing conversation history" });
            }
        }
        [HttpPost("contact")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(object), StatusCodes.Status400BadRequest)]
        [ProducesResponseType(typeof(object), StatusCodes.Status429TooManyRequests)]
        [ProducesResponseType(typeof(object), StatusCodes.Status500InternalServerError)]
        public async Task<IActionResult> SubmitContactForm([FromBody] ContactRequest request)
        {
            var requestId = Guid.NewGuid().ToString("N");
            _logger.LogInformation($"[{requestId}] Contact form submission received from {request.Name} ({request.Email})");

            // Validate request
            if (request == null || string.IsNullOrEmpty(request.Name) ||
                string.IsNullOrEmpty(request.Email) || string.IsNullOrEmpty(request.Message))
            {
                _logger.LogWarning($"[{requestId}] Invalid contact request: Missing required fields");
                return BadRequest(new { message = "Name, email, and message are required" });
            }

            try
            {
                // Get client IP address and add to the request
                var ipAddress = GetClientIpAddress();
                request.ClientIp = ipAddress;

                _logger.LogInformation($"[{requestId}] Processing contact request from {request.Name} from IP: {ipAddress}");

                // Try to send the email (this now also checks IP-based email rate limiting)
                bool result = await _emailService.SendContactEmailAsync(request);

                if (result)
                {
                    _logger.LogInformation($"[{requestId}] Contact form processed successfully for {request.Name}");
                    return Ok(new
                    {
                        message = "Your message has been sent successfully. Thank you for contacting me!",
                        success = true
                    });
                }
                else
                {
                    // Could fail due to rate limiting or SendGrid API issues
                    _logger.LogWarning($"[{requestId}] Failed to send contact email from {request.Name} ({request.Email})");

                    return StatusCode(429, new
                    {
                        message = "You've reached the maximum number of contact requests. Please try again later or contact me directly via email.",
                        contactEmail = "bordincrazvan2004@gmail.com",
                        success = false
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"[{requestId}] Error processing contact form submission from {request.Name}");

                // Provide a useful fallback even on error
                return StatusCode(500, new
                {
                    message = "There was an issue processing your message. Please try again or contact me directly via email.",
                    contactEmail = "bordincrazvan2004@gmail.com",
                    success = false
                });
            }
        }

        private async Task WriteChunkAsync(string chunk)
        {
            if (string.IsNullOrEmpty(chunk)) return;

            // Format the chunk as a proper SSE data event
            string escapedData = chunk.Replace("\n", "\\n").Replace("\r", "\\r");
            string message = $"event: message\ndata: {escapedData}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            await Response.Body.WriteAsync(bytes);
            await Response.Body.FlushAsync();
        }

        private async Task WriteDoneAsync(string fullText)
        {
            // Send a special event to mark completion with the full processed text
            var completionData = new
            {
                done = true,
                fullText = fullText
            };

            string jsonData = JsonSerializer.Serialize(completionData);
            string message = $"event: done\ndata: {jsonData}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(message);

            await Response.Body.WriteAsync(bytes);
            await Response.Body.FlushAsync();

            // Add a final heartbeat/comment to ensure the done event is processed
            await WriteHeartbeatAsync();
        }
        private async Task WriteHeartbeatAsync()
        {
            // Send a comment (heartbeat) to keep the connection alive
            string heartbeat = $": heartbeat {DateTime.UtcNow.Ticks}\n\n";
            byte[] bytes = Encoding.UTF8.GetBytes(heartbeat);

            await Response.Body.WriteAsync(bytes);
            await Response.Body.FlushAsync();
        }

        private async Task WriteErrorResponseAsync(string errorMessage, int statusCode)
        {
            Response.StatusCode = statusCode;
            Response.ContentType = "application/json";

            var error = new { error = errorMessage };
            await Response.WriteAsJsonAsync(error);
        }
         
        // Health check endpoint
        [HttpGet("health")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public IActionResult HealthCheck()
        {
            return Ok(new { status = "healthy", timestamp = DateTime.UtcNow });
        }

        // Ping endpoint for testing
        [HttpGet("ping")]
        [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
        public IActionResult Ping()
        {
            return Ok(new { message = "pong", timestamp = DateTime.UtcNow });
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
    }
}