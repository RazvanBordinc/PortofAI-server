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
        private readonly PortfolioController _portfolioController;
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
            // Create a new logger for the portfolio controller directly
            _portfolioController = new PortfolioController(
                LoggerFactory.Create(builder => builder.AddConsole())
                    .CreateLogger<PortfolioController>()
            );

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

                // Call FastAPI service with the session ID and enriched message
                var response = await CallFastApi(enrichedMessage, sessionId, request.Style ?? "NORMAL");

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
        private async Task<ChatResponse> CallFastApi(string message, string sessionId, string style)
        {
            try
            {
                var client = _httpClientFactory.CreateClient("FastAPI");

                var content = new StringContent(
                    JsonSerializer.Serialize(new { message, session_id = sessionId, style }),
                    Encoding.UTF8,
                    "application/json");

                _logger.LogInformation($"Sending request to FastAPI for session {sessionId} with style {style}");

                // Add a timeout to the request
                using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(15)); // Increased timeout
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

        // Enhanced JSON validation and debugging in ProcessAiResponse method
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
                }

                // Remove [/format] tag if present
                processedResponse.Text = processedResponse.Text.Replace("[/format]", "").Trim();

                // Look for JSON data after the format
                var jsonDataRegex = new Regex(@"\[data:([\s\S]*?)\]", RegexOptions.Singleline);
                var dataMatch = jsonDataRegex.Match(processedResponse.Text);

                if (dataMatch.Success)
                {
                    try
                    {
                        string jsonData = dataMatch.Groups[1].Value.Trim();
                        _logger.LogDebug($"Found JSON data: {jsonData.Substring(0, Math.Min(100, jsonData.Length))}...");

                        // Try to pre-process the JSON to fix common issues
                        jsonData = PreProcessJsonData(jsonData);

                        // Parse JSON data
                        processedResponse.FormatData = JsonSerializer.Deserialize<object>(jsonData, new JsonSerializerOptions
                        {
                            PropertyNameCaseInsensitive = true,
                            AllowTrailingCommas = true,
                            ReadCommentHandling = JsonCommentHandling.Skip
                        });

                        // Remove the data tag from the response text
                        processedResponse.Text = jsonDataRegex.Replace(processedResponse.Text, "").Trim();

                        _logger.LogInformation($"Parsed JSON data successfully for format: {processedResponse.Format}");
                    }
                    catch (JsonException ex)
                    {
                        _logger.LogWarning(ex, "Failed to parse JSON data from AI response");

                        // Keep the data in the text if we couldn't parse it
                        _logger.LogWarning($"JSON parse error: {ex.Message}, Data: {dataMatch.Groups[1].Value.Substring(0, Math.Min(100, dataMatch.Groups[1].Value.Length))}");
                    }
                }
                else
                {
                    _logger.LogInformation($"No JSON data found in response with format: {processedResponse.Format}");
                }

                return processedResponse;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error parsing AI response format");
                return processedResponse; // Return default text format on error
            }
        }

        // Helper method to pre-process JSON data to fix common issues
        private string PreProcessJsonData(string jsonData)
        {
            if (string.IsNullOrEmpty(jsonData))
                return jsonData;

            try
            {
                // Replace single quotes with double quotes (after escaping existing double quotes)
                string processed = jsonData
                    .Replace("\\\"", "\\TEMP_QUOTE") // Temporarily replace escaped double quotes
                    .Replace("\"", "\\\"") // Escape all double quotes
                    .Replace("'", "\"") // Replace single quotes with double quotes
                    .Replace("\\TEMP_QUOTE", "\\\""); // Restore originally escaped double quotes

                // Fix common malformed JSON issues
                processed = Regex.Replace(processed, @",(\s*[\]}])", "$1"); // Remove trailing commas before closing brackets

                // Add quotation marks to property names that are missing them
                processed = Regex.Replace(processed, @"([\{,])\s*([a-zA-Z0-9_]+)\s*:", "$1\"$2\":");

                _logger.LogDebug($"Pre-processed JSON: {processed.Substring(0, Math.Min(100, processed.Length))}...");

                return processed;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error pre-processing JSON data");
                return jsonData; // Return original on error
            }
        }

        // Get or create a persistent session ID for the user
        private async Task<string> GetOrCreateSessionId(string ipAddress)
        {
            try
            {
                if (!_redisAvailable)
                {
                    _logger.LogWarning("Redis not available, using fallback session ID based on IP");
                    return $"fallback-{ipAddress}";
                }

                var db = _redis.GetDatabase();
                var key = $"session:{ipAddress}";

                var sessionId = await db.StringGetAsync(key);

                if (!sessionId.HasValue)
                {
                    // Create a new session ID if none exists
                    sessionId = Guid.NewGuid().ToString();

                    // Store with 30-day expiration
                    var setResult = await db.StringSetAsync(key, sessionId, TimeSpan.FromDays(30));

                    if (setResult)
                    {
                        _logger.LogInformation($"Created new session {sessionId} for IP {ipAddress} with 30-day expiration");
                    }
                    else
                    {
                        _logger.LogWarning($"Failed to set new session in Redis for IP {ipAddress}");
                        return $"fallback-{ipAddress}";
                    }
                }
                else
                {
                    // Refresh expiration time on existing session
                    await db.KeyExpireAsync(key, TimeSpan.FromDays(30));
                    _logger.LogInformation($"Using existing session {sessionId} for IP {ipAddress}, refreshed expiration");
                }

                return sessionId;
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Redis error in GetOrCreateSessionId: {ex.Message}");
                return $"fallback-{ipAddress}";
            }
        }
        [HttpPost("clear-session")]
        public async Task<IActionResult> ClearSession()
        {
            try
            {
                if (!_redisAvailable)
                {
                    return BadRequest(new { message = "Redis is not available, cannot clear session" });
                }

                string ipAddress = GetClientIpAddress();
                var db = _redis.GetDatabase();

                // Clear session ID
                var sessionKey = $"session:{ipAddress}";
                bool sessionDeleted = await db.KeyDeleteAsync(sessionKey);

                // Clear conversation history
                string sessionId = await GetOrCreateSessionId(ipAddress);
                var conversationKey = $"conversation:{sessionId}";
                bool conversationDeleted = await db.KeyDeleteAsync(conversationKey);

                _logger.LogInformation($"Cleared session for IP {ipAddress}: Session key deleted: {sessionDeleted}, Conversation deleted: {conversationDeleted}");

                return Ok(new { message = "Session cleared successfully" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error clearing session");
                return StatusCode(500, new { message = "An error occurred while clearing your session" });
            }
        }

 
        [HttpGet("session-info")]
        public async Task<IActionResult> GetSessionInfo()
        {
            try
            {
                if (!_redisAvailable)
                {
                    return BadRequest(new { message = "Redis is not available, cannot retrieve session info" });
                }

                string ipAddress = GetClientIpAddress();
                string sessionId = await GetOrCreateSessionId(ipAddress);

                var db = _redis.GetDatabase();
                var sessionKey = $"session:{ipAddress}";
                var conversationKey = $"conversation:{sessionId}";

                // Get TTL for session
                var sessionTtl = await db.KeyTimeToLiveAsync(sessionKey);
                var sessionTtlDays = sessionTtl.HasValue ? Math.Round(sessionTtl.Value.TotalDays, 1) : 0;

                // Get TTL for conversation
                var conversationTtl = await db.KeyTimeToLiveAsync(conversationKey);
                var conversationTtlHours = conversationTtl.HasValue ? Math.Round(conversationTtl.Value.TotalHours, 1) : 0;

                // Check if conversation exists
                bool conversationExists = await db.KeyExistsAsync(conversationKey);

                return Ok(new
                {
                    ipAddress,
                    sessionId,
                    sessionTtlDays,
                    conversationExists,
                    conversationTtlHours
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting session info");
                return StatusCode(500, new { message = "An error occurred while retrieving session information" });
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
                var key = $"ratelimit:{ipAddress}";

                // Check if the key exists
                if (!await db.KeyExistsAsync(key))
                {
                    _logger.LogInformation($"No rate limit found for IP {ipAddress}, initializing new counter");
                    return true; // No rate limit set yet
                }

                var value = await db.StringGetAsync(key);

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

                bool result = count < 15; // Allow 15 requests per day
                _logger.LogInformation($"Rate limit check for IP {ipAddress}: {count}/15 requests used, allowed = {result}");
                return result;
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
                    var newValue = await db.StringIncrementAsync(key);
                    _logger.LogInformation($"Incremented rate limit for IP {ipAddress} to {newValue}");
                }
                else
                {
                    await db.StringSetAsync(key, 1);
                    // Set TTL for 24 hours (86400 seconds)
                    await db.KeyExpireAsync(key, TimeSpan.FromSeconds(86400));
                    _logger.LogInformation($"Created new rate limit for IP {ipAddress}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"Redis error in IncrementRateLimit: {ex.Message}");
                // Continue execution - we don't want to stop the response for rate limiting
            }
        }

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
                    var key = $"ratelimit:{ipAddress}";

                    if (await db.KeyExistsAsync(key))
                    {
                        var value = await db.StringGetAsync(key);

                        if (value.HasValue)
                        {
                            if (!int.TryParse(value, out used))
                            {
                                _logger.LogWarning($"Invalid rate limit value in Redis: {value}");
                                used = 0;
                            }
                        }

                        // Also check TTL to ensure the key hasn't expired
                        var ttl = await db.KeyTimeToLiveAsync(key);
                        if (!ttl.HasValue)
                        {
                            _logger.LogWarning($"Rate limit key for IP {ipAddress} has no TTL, resetting");
                            used = 0;
                            await db.KeyDeleteAsync(key);
                        }
                    }
                    else
                    {
                        _logger.LogInformation($"No rate limit key found for IP {ipAddress}");
                        used = 0;
                    }

                    remaining = Math.Max(0, 15 - used);
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
        public string Message { get; set; } = string.Empty;
        public string? Style { get; set; }
    }
    // Response model from AI service
    public class ChatResponse
    {
        public string Response { get; set; } = string.Empty;
    }

    // Processed response with format information
    public class ProcessedResponse
    {
        public string Text { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public object? FormatData { get; set; } = null;
    }
}