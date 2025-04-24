using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;
using System.Text.RegularExpressions;

namespace Portfolio_server.Controllers
{
    [Route("api")]
    [ApiController]
    public class MainController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<MainController> _logger;
        private readonly bool _redisAvailable = true;

        // Constructor with dependency injection
        public MainController(
            IHttpClientFactory httpClientFactory,
            IConnectionMultiplexer redis,
            ILogger<MainController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _redis = redis;
            _logger = logger;

            // Test if Redis is actually working
            try
            {
                _redis.GetDatabase().Ping();
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Redis is not available: {ex.Message}. Will use fallback mechanisms.");
                _redisAvailable = false;
            }
        }

        // Health check endpoint
        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "healthy", redisAvailable = _redisAvailable });
        }

        // Simple ping endpoint for connectivity testing
        [HttpGet("ping")]
        public IActionResult Ping()
        {
            return Ok(new { status = "pong", timestamp = DateTime.UtcNow });
        }

        // Chat endpoint
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

                // Check rate limit (with fallback if Redis is unavailable)
                bool withinRateLimit = true;
                if (_redisAvailable)
                {
                    withinRateLimit = await CheckRateLimit(ipAddress);
                }

                if (!withinRateLimit)
                {
                    return StatusCode(429, new { message = "Rate limit exceeded. Try again tomorrow." });
                }

                // Generate a session ID for the user if not exists (with fallback)
                string sessionId;
                try
                {
                    sessionId = _redisAvailable
                        ? await GetOrCreateSessionId(ipAddress)
                        : $"fallback-{ipAddress}";
                }
                catch (Exception ex)
                {
                    _logger.LogWarning($"Error getting session ID: {ex.Message}. Fallback to IP-based session.");
                    sessionId = $"fallback-{ipAddress}";
                }

                // Call FastAPI service with the session ID
                var response = await CallFastApi(request.Message, sessionId);

                // Process the response to extract format information
                var processedResponse = ProcessAiResponse(response.Response);

                // Increment rate limit counter (if Redis is available)
                if (_redisAvailable)
                {
                    try
                    {
                        await IncrementRateLimit(ipAddress);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning($"Failed to increment rate limit: {ex.Message}");
                        // Continue anyway - we don't want to block the response
                    }
                }

                return Ok(new
                {
                    response = processedResponse.Text,
                    format = processedResponse.Format,
                    formatData = processedResponse.FormatData
                });
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError($"HTTP request error calling FastAPI: {ex.Message}");
                return StatusCode(502, new { message = "Unable to reach AI service. Please try again later." });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing chat request");
                return StatusCode(500, new { message = "An error occurred while processing your request" });
            }
        }

        // Helper method to call FastAPI
        private async Task<ChatResponse> CallFastApi(string message, string sessionId)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("FastAPI");

                var content = new StringContent(
                    JsonSerializer.Serialize(new { message, session_id = sessionId }),
                    Encoding.UTF8,
                    "application/json");

                _logger.LogInformation($"Sending request to FastAPI for session {sessionId}");

                // Add a timeout to the request
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(10));
                var response = await client.PostAsync("/chat", content, cts.Token);

                if (!response.IsSuccessStatusCode)
                {
                    _logger.LogWarning($"FastAPI returned status code: {response.StatusCode}");

                    // For this demo, we'll return a mock response if FastAPI is unavailable
                    if (response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable ||
                        response.StatusCode == System.Net.HttpStatusCode.NotFound ||
                        response.StatusCode == System.Net.HttpStatusCode.BadGateway)
                    {
                        return new ChatResponse
                        {
                            Response = $"I'm sorry, the AI service is currently unavailable. " +
                                     $"Your message was: '{message}'. Please try again later. [format:text]"
                        };
                    }

                    throw new HttpRequestException($"FastAPI returned {response.StatusCode}");
                }

                var responseContent = await response.Content.ReadAsStringAsync();
                _logger.LogInformation($"Received response from FastAPI: {responseContent.Substring(0, Math.Min(100, responseContent.Length))}...");

                return JsonSerializer.Deserialize<ChatResponse>(responseContent, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                }) ?? new ChatResponse { Response = "No response from AI service [format:text]" };
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error calling FastAPI: {ex.Message}", ex);

                // Return a fallback response for demo purposes
                return new ChatResponse
                {
                    Response = $"I'm sorry, I couldn't process your request due to a technical issue. " +
                             $"Your message was: '{message}'. Please try again later. [format:text]"
                };
            }
        }

        // Process AI response to extract format instructions
        private ProcessedResponse ProcessAiResponse(string aiResponse)
        {
            var processedResponse = new ProcessedResponse
            {
                Text = aiResponse,
                Format = "text", // Default format
                FormatData = null
            };

            if (string.IsNullOrEmpty(aiResponse))
            {
                _logger.LogWarning("Received empty response from AI service");
                processedResponse.Text = "I'm sorry, I couldn't generate a response at this time. Please try again later.";
                return processedResponse;
            }

            try
            {
                // Look for format tag: [format:type]
                var formatRegex = new Regex(@"\[format:(text|table|contact|pdf)\]", RegexOptions.IgnoreCase);
                var match = formatRegex.Match(aiResponse);

                if (match.Success)
                {
                    // Extract the format type
                    processedResponse.Format = match.Groups[1].Value.ToLower();

                    // Remove the format tag from the response
                    processedResponse.Text = formatRegex.Replace(aiResponse, "").Trim();

                    // Look for JSON data after the format
                    // This would handle cases where the AI wants to include structured data
                    var jsonDataRegex = new Regex(@"\[data:(.*?)\]", RegexOptions.Singleline);
                    var dataMatch = jsonDataRegex.Match(aiResponse);

                    if (dataMatch.Success)
                    {
                        try
                        {
                            // Try to parse the data as JSON
                            var jsonData = dataMatch.Groups[1].Value.Trim();
                            processedResponse.FormatData = JsonSerializer.Deserialize<object>(jsonData);

                            // Remove the data tag from the response
                            processedResponse.Text = jsonDataRegex.Replace(processedResponse.Text, "").Trim();
                        }
                        catch (JsonException ex)
                        {
                            _logger.LogWarning(ex, "Failed to parse JSON data from AI response");
                        }
                    }
                }

                return processedResponse;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing AI response format");
                return processedResponse; // Return default text format on error
            }
        }

        // Get or create a persistent session ID for the user
        private async Task<string> GetOrCreateSessionId(string ipAddress)
        {
            try
            {
                if (!_redisAvailable)
                {
                    return $"fallback-{ipAddress}";
                }

                var db = _redis.GetDatabase();
                var key = $"session:{ipAddress}";

                var sessionId = await db.StringGetAsync(key);

                if (!sessionId.HasValue)
                {
                    // Create a new session ID if none exists
                    sessionId = Guid.NewGuid().ToString();
                    await db.StringSetAsync(key, sessionId, TimeSpan.FromDays(30)); // 30 day session lifetime
                    _logger.LogInformation($"Created new session {sessionId} for IP {ipAddress}");
                }

                return sessionId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Redis error in GetOrCreateSessionId: {ex.Message}");
                return $"fallback-{ipAddress}";
            }
        }

        // Rate limiting methods using Redis
        private async Task<bool> CheckRateLimit(string ipAddress)
        {
            try
            {
                if (!_redisAvailable)
                {
                    return true; // No rate limiting if Redis is unavailable
                }

                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync($"ratelimit:{ipAddress}");

                if (!value.HasValue)
                {
                    return true; // No rate limit set yet
                }

                int count;
                if (!int.TryParse(value, out count))
                {
                    _logger.LogWarning($"Invalid rate limit value in Redis for IP {ipAddress}: {value}");
                    return true; // Assume no rate limit on error
                }

                return count < 15; // Allow 15 requests per day
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Redis error in CheckRateLimit: {ex.Message}");
                return true; // Allow the request on error
            }
        }

        private async Task IncrementRateLimit(string ipAddress)
        {
            try
            {
                if (!_redisAvailable)
                {
                    return; // Skip rate limiting if Redis is unavailable
                }

                var db = _redis.GetDatabase();
                var key = $"ratelimit:{ipAddress}";

                if (await db.KeyExistsAsync(key))
                {
                    await db.StringIncrementAsync(key);
                }
                else
                {
                    await db.StringSetAsync(key, 1);
                    // Set TTL for 24 hours (86400 seconds)
                    await db.KeyExpireAsync(key, TimeSpan.FromSeconds(86400));
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Redis error in IncrementRateLimit: {ex.Message}");
                // Continue execution - we don't want to stop the response for rate limiting
            }
        }

        // Get remaining requests for an IP
        [HttpGet("remaining")]
        public async Task<IActionResult> RemainingRequests()
        {
            try
            {
                string ipAddress = GetClientIpAddress();
                _logger.LogInformation($"Checking remaining requests for IP: {ipAddress}");

                // Default values if Redis is unavailable
                int used = 0;
                int remaining = 15;

                if (_redisAvailable)
                {
                    var db = _redis.GetDatabase();
                    var value = await db.StringGetAsync($"ratelimit:{ipAddress}");

                    if (value.HasValue)
                    {
                        if (!int.TryParse(value, out used))
                        {
                            _logger.LogWarning($"Invalid rate limit value in Redis: {value}");
                            used = 0;
                        }
                    }

                    remaining = 15 - used;
                }

                _logger.LogInformation($"Remaining requests for IP {ipAddress}: {remaining}");
                return Ok(new { remaining });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting remaining requests");
                // Return a default value rather than error
                return Ok(new { remaining = 15 });
            }
        }

        // Get client IP address
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
    }

    // Request model
    public class ChatRequest
    {
        public string Message { get; set; }
    }

    // Response model from AI service
    public class ChatResponse
    {
        public string Response { get; set; }
    }

    // Processed response with format information
    public class ProcessedResponse
    {
        public string Text { get; set; }
        public string Format { get; set; }
        public object FormatData { get; set; }
    }
}