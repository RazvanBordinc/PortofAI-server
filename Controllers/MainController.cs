using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;

namespace Portfolio_server.Controllers
{
    [Route("api")]
    [ApiController]
    public class MainController : ControllerBase
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<MainController> _logger;

        // Constructor with dependency injection
        public MainController(
            IHttpClientFactory httpClientFactory,
            IConnectionMultiplexer redis,
            ILogger<MainController> logger)
        {
            _httpClientFactory = httpClientFactory;
            _redis = redis;
            _logger = logger;
        }

        // Health check endpoint
        [HttpGet("health")]
        public IActionResult Health()
        {
            return Ok(new { status = "healthy" });
        }

        // Chat endpoint
        [HttpPost("chat")]
        public async Task<IActionResult> Chat([FromBody] ChatRequest request)
        {
            try
            {
                // Get client IP address for rate limiting
                string ipAddress = GetClientIpAddress();

                // Check rate limit
                if (!await CheckRateLimit(ipAddress))
                {
                    return StatusCode(429, new { message = "Rate limit exceeded. Try again tomorrow." });
                }

                // Validate request
                if (string.IsNullOrEmpty(request.Message))
                {
                    return BadRequest(new { message = "Message cannot be empty" });
                }

                // Generate a session ID for the user if not exists
                string sessionId = await GetOrCreateSessionId(ipAddress);

                // Call FastAPI service with the session ID
                var response = await CallFastApi(request.Message, sessionId);

                // Increment rate limit counter
                await IncrementRateLimit(ipAddress);

                return Ok(response);
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
            var client = _httpClientFactory.CreateClient("FastAPI");

            var content = new StringContent(
                JsonSerializer.Serialize(new { message, session_id = sessionId }),
                Encoding.UTF8,
                "application/json");

            var response = await client.PostAsync("/chat", content);

            if (!response.IsSuccessStatusCode)
            {
                throw new Exception($"FastAPI returned {response.StatusCode}");
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            return JsonSerializer.Deserialize<ChatResponse>(responseContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
        }

        // Get or create a persistent session ID for the user
        private async Task<string> GetOrCreateSessionId(string ipAddress)
        {
            var db = _redis.GetDatabase();
            var key = $"session:{ipAddress}";

            var sessionId = await db.StringGetAsync(key);

            if (!sessionId.HasValue)
            {
                // Create a new session ID if none exists
                sessionId = Guid.NewGuid().ToString();
                await db.StringSetAsync(key, sessionId, TimeSpan.FromDays(30)); // 30 day session lifetime
            }

            return sessionId;
        }

        // Rate limiting methods using Redis
        private async Task<bool> CheckRateLimit(string ipAddress)
        {
            var db = _redis.GetDatabase();
            var value = await db.StringGetAsync($"ratelimit:{ipAddress}");

            if (!value.HasValue)
            {
                return true; // No rate limit set yet
            }

            int count = int.Parse(value);
            return count < 15; // Allow 15 requests per day
        }

        private async Task IncrementRateLimit(string ipAddress)
        {
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

        // Get remaining requests for an IP
        [HttpGet("remaining")]
        public async Task<IActionResult> RemainingRequests()
        {
            try
            {
                string ipAddress = GetClientIpAddress();
                var db = _redis.GetDatabase();
                var value = await db.StringGetAsync($"ratelimit:{ipAddress}");

                int used = value.HasValue ? int.Parse(value) : 0;
                int remaining = 15 - used;

                return Ok(new { remaining });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting remaining requests");
                return StatusCode(500, new { message = "An error occurred" });
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

    // Response model
    public class ChatResponse
    {
        public string Response { get; set; }
    }
}