using StackExchange.Redis;
using System.Text.Json;

namespace Portfolio_server.Services
{
    public class ConversationService : IConversationService
    {
        private readonly IConnectionMultiplexer _redis;
        private readonly ILogger<ConversationService> _logger;
        private readonly bool _redisAvailable;

        public ConversationService(
            IConnectionMultiplexer redis,
            ILogger<ConversationService> logger)
        {
            _redis = redis;
            _logger = logger;

            // Test if Redis is actually working
            try
            {
                _redis.GetDatabase().Ping();
                _redisAvailable = true;
                _logger.LogInformation("Redis connection established for conversation service");
            }
            catch (Exception ex)
            {
                _redisAvailable = false;
                _logger.LogWarning($"Redis is not available for conversation service: {ex.Message}");
            }
        }

        public async Task<string> GetConversationHistoryAsync(string sessionId)
        {
            if (!_redisAvailable)
            {
                _logger.LogWarning("Redis not available, returning empty conversation history");
                return string.Empty;
            }

            try
            {
                var db = _redis.GetDatabase();
                var key = $"conversation:{sessionId}";

                var savedData = await db.StringGetAsync(key);
                if (!savedData.HasValue)
                {
                    _logger.LogInformation($"No existing conversation found for session {sessionId}. Starting new.");
                    return string.Empty;
                }

                var data = JsonSerializer.Deserialize<ConversationData>(savedData);
                if (data == null || data.Messages == null || data.Messages.Count == 0)
                {
                    return string.Empty;
                }

                // Format the conversation history as a string
                return string.Join("\n", data.Messages);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error loading conversation history from Redis for session {sessionId}");
                return string.Empty;
            }
        }

        public async Task SaveConversationAsync(string sessionId, string userMessage, string aiResponse)
        {
            if (!_redisAvailable)
            {
                _logger.LogWarning("Redis not available, skipping conversation save");
                return;
            }

            try
            {
                var db = _redis.GetDatabase();
                var key = $"conversation:{sessionId}";

                // Get existing conversation or create new one
                List<string> messages = new List<string>();
                var savedData = await db.StringGetAsync(key);

                if (savedData.HasValue)
                {
                    var data = JsonSerializer.Deserialize<ConversationData>(savedData);
                    if (data?.Messages != null)
                    {
                        messages = data.Messages;
                    }
                }

                // Add new messages
                messages.Add($"Human: {userMessage}");
                messages.Add($"AI: {aiResponse}");

                // Keep only the last 10 exchanges (20 messages)
                if (messages.Count > 20)
                {
                    messages = messages.GetRange(messages.Count - 20, 20);
                }

                // Save back to Redis with 24-hour expiration
                var conversationData = new ConversationData { Messages = messages };
                await db.StringSetAsync(
                    key,
                    JsonSerializer.Serialize(conversationData),
                    TimeSpan.FromHours(24)
                );

                _logger.LogInformation($"Saved conversation for session {sessionId} with {messages.Count} messages");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error saving conversation to Redis for session {sessionId}");
            }
        }

        public async Task<bool> ClearConversationAsync(string sessionId)
        {
            if (!_redisAvailable)
            {
                _logger.LogWarning("Redis not available, cannot clear conversation");
                return false;
            }

            try
            {
                var db = _redis.GetDatabase();
                var key = $"conversation:{sessionId}";

                bool result = await db.KeyDeleteAsync(key);
                _logger.LogInformation($"Cleared conversation for session {sessionId}: {result}");
                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error clearing conversation for session {sessionId}");
                return false;
            }
        }
 
        private class ConversationData
        {
            public List<string> Messages { get; set; } = new List<string>();
        }
    }
}
