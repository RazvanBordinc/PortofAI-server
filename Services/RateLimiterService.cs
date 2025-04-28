using StackExchange.Redis;

namespace Portfolio_server.Services
{
    public class RateLimiterService : IRateLimiterService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<RateLimiterService> _logger;
        private readonly bool _redisAvailable;
        private readonly int _defaultMaxRequests = 15;
        private readonly TimeSpan _rateLimitDuration = TimeSpan.FromHours(24);

        public RateLimiterService(
            IConnectionMultiplexer redis,
            ILogger<RateLimiterService> logger)
        {
            _redis = redis;
            _logger = logger;

            // Test if Redis is actually working
            try
            {
                _redis.GetDatabase().Ping();
                _redisAvailable = true;
                _logger.LogInformation("Redis connection established for rate limiter service");
            }
            catch (Exception ex)
            {
                _redisAvailable = false;
                _logger.LogWarning($"Redis is not available for rate limiter service: {ex.Message}");
            }
        }

        public async Task<bool> CheckRateLimitAsync(string ipAddress)
        {
            if (!_redisAvailable)
            {
                _logger.LogWarning("Redis not available for rate limiting, allowing request");
                return true; // Allow the request if Redis is unavailable
            }

            try
            {
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

                if (!int.TryParse(value, out int count))
                {
                    _logger.LogWarning($"Invalid rate limit value in Redis for IP {ipAddress}: {value}");
                    return true; // Assume no rate limit on error
                }

                bool result = count < _defaultMaxRequests;
                _logger.LogInformation($"Rate limit check for IP {ipAddress}: {count}/{_defaultMaxRequests} requests used, allowed = {result}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error checking rate limit for IP {ipAddress}");
                return true; // Allow the request on error
            }
        }

        public async Task<bool> IncrementRateLimitAsync(string ipAddress)
        {
            if (!_redisAvailable)
            {
                _logger.LogWarning("Redis not available for rate limiting, skipping increment");
                return false;
            }

            try
            {
                var db = _redis.GetDatabase();
                var key = $"ratelimit:{ipAddress}";

                if (await db.KeyExistsAsync(key))
                {
                    // Increment existing counter
                    var newValue = await db.StringIncrementAsync(key);
                    _logger.LogInformation($"Incremented rate limit for IP {ipAddress} to {newValue}");

                    // Refresh TTL (in case it's close to expiring)
                    await db.KeyExpireAsync(key, _rateLimitDuration);
                }
                else
                {
                    // Create new counter with TTL
                    await db.StringSetAsync(key, 1, _rateLimitDuration);
                    _logger.LogInformation($"Created new rate limit for IP {ipAddress} with TTL of {_rateLimitDuration}");
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error incrementing rate limit for IP {ipAddress}");
                return false;
            }
        }

        public async Task<int> GetRemainingRequestsAsync(string ipAddress, int maxRequests = 15)
        {
            if (!_redisAvailable)
            {
                _logger.LogWarning("Redis not available for rate limiting, returning default remaining count");
                return maxRequests; // Return full count if Redis is unavailable
            }

            try
            {
                var db = _redis.GetDatabase();
                var key = $"ratelimit:{ipAddress}";

                // Check if the key exists
                if (!await db.KeyExistsAsync(key))
                {
                    return maxRequests; // No rate limit set yet, full count available
                }

                var value = await db.StringGetAsync(key);
                if (!value.HasValue)
                {
                    return maxRequests; // No rate limit set yet
                }

                if (!int.TryParse(value, out int usedRequests))
                {
                    _logger.LogWarning($"Invalid rate limit value in Redis for IP {ipAddress}: {value}");
                    return maxRequests; // Return full count on error
                }

                int remaining = Math.Max(0, maxRequests - usedRequests);
                _logger.LogInformation($"Remaining requests for IP {ipAddress}: {remaining}/{maxRequests}");
                return remaining;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting remaining requests for IP {ipAddress}");
                return maxRequests; // Return full count on error
            }
        }
    }
}
