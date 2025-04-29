namespace Portfolio_server.Services
{
    using global::Portfolio_server.Models;
    using Microsoft.Extensions.Logging;
    using StackExchange.Redis;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Text;
    using System.Text.Json;
    using System.Threading.Tasks;
 

    namespace Portfolio_server.Services
    {
        public class RedisPortfolioService : IPortfolioService
        {
            private readonly ILogger<RedisPortfolioService> _logger;
            private readonly IConnectionMultiplexer _redis;
            private const string PortfolioDataPrefix = "portfolio:data:";

            public RedisPortfolioService(
                ILogger<RedisPortfolioService> logger,
                IConnectionMultiplexer redis)
            {
                _logger = logger;
                _redis = redis;
            }

            public async Task<List<string>> GetCategoriesAsync()
            {
                try
                {
                    if (_redis == null)
                    {
                        // Fallback to predefined categories if no Redis connection is available
                        return new List<string> { "skills", "projects", "experience", "education", "about" };
                    }

                    var db = _redis.GetDatabase();
                    var server = _redis.GetServer(_redis.GetEndPoints().First());
                    var pattern = $"{PortfolioDataPrefix}category:*";
                    var categoryKeys = server.Keys(pattern: pattern).ToArray();

                    var categories = new List<string>();
                    foreach (var key in categoryKeys)
                    {
                        var categoryJson = await db.StringGetAsync(key);
                        if (!categoryJson.HasValue) continue;

                        var category = JsonSerializer.Deserialize<PortfolioCategory>(categoryJson);
                        if (category?.IsActive == true)
                        {
                            categories.Add(category.Name.ToLower());
                        }
                    }

                    return categories;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting portfolio categories");
                    // Return default categories as fallback
                    return new List<string> { "skills", "projects", "experience", "education", "about" };
                }
            }

            public async Task<List<PortfolioContent>> GetCategoryContentAsync(string category)
            {
                try
                {
                    _logger.LogInformation("Getting portfolio data. Category: {Category}", category);

                    if (_redis == null)
                    {
                        // Return empty list if no Redis connection
                        return new List<PortfolioContent>();
                    }

                    // Normalize category name for case-insensitive matching
                    var normalizedCategory = category.ToLower();
                    var db = _redis.GetDatabase();

                    // Find the matching category
                    var server = _redis.GetServer(_redis.GetEndPoints().First());
                    var categoryKeys = server.Keys(pattern: $"{PortfolioDataPrefix}category:*").ToArray();

                    int categoryId = -1;
                    foreach (var key in categoryKeys)
                    {
                        var categoryJson = await db.StringGetAsync(key);
                        if (!categoryJson.HasValue) continue;

                        var categoryData = JsonSerializer.Deserialize<PortfolioCategory>(categoryJson);
                        if (categoryData?.Name?.ToLower() == normalizedCategory)
                        {
                            // Extract the category ID from the key or determine from name
                            var keyParts = key.ToString().Split(':');
                            if (keyParts.Length > 3)
                            {
                                if (int.TryParse(keyParts[3], out int id))
                                {
                                    categoryId = id;
                                }
                            }
                            else
                            {
                                // Alternative: parse from name
                                switch (normalizedCategory)
                                {
                                    case "aboutme": categoryId = 1; break;
                                    case "skills": categoryId = 2; break;
                                    case "projects": categoryId = 3; break;
                                    case "experience": categoryId = 4; break;
                                    case "education": categoryId = 5; break;
                                    case "contact": categoryId = 6; break;
                                }
                            }
                            break;
                        }
                    }

                    if (categoryId == -1)
                    {
                        _logger.LogWarning($"Category '{category}' not found");
                        return new List<PortfolioContent>();
                    }

                    // Get content items for this category
                    var contentPattern = $"{PortfolioDataPrefix}content:{normalizedCategory}:*";
                    // Also try the simpler pattern for single content items
                    var singleContentPattern = $"{PortfolioDataPrefix}content:{normalizedCategory}";

                    var contentKeys = server.Keys(pattern: contentPattern).ToList();
                    contentKeys.AddRange(server.Keys(pattern: singleContentPattern));

                    var result = new List<PortfolioContent>();
                    foreach (var key in contentKeys)
                    {
                        var contentJson = await db.StringGetAsync(key);
                        if (!contentJson.HasValue) continue;

                        var content = JsonSerializer.Deserialize<PortfolioContent>(contentJson);
                        if (content != null)
                        {
                            result.Add(content);
                        }
                    }

                    // Order by display order
                    result = result.OrderBy(c => c.DisplayOrder).ToList();

                    return result;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error getting portfolio content for category {Category}", category);
                    return new List<PortfolioContent>();
                }
            }

            public async Task<List<PortfolioContent>> SearchPortfolioAsync(string query)
            {
                try
                {
                    _logger.LogInformation("Searching portfolio data. Query: {Query}", query);

                    if (string.IsNullOrWhiteSpace(query))
                    {
                        return new List<PortfolioContent>();
                    }

                    if (_redis == null)
                    {
                        // Return empty list if no Redis connection
                        return new List<PortfolioContent>();
                    }

                    var searchTerms = query.ToLower().Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries);
                    var db = _redis.GetDatabase();
                    var server = _redis.GetServer(_redis.GetEndPoints().First());

                    // Get all content keys
                    var contentKeys = server.Keys(pattern: $"{PortfolioDataPrefix}content:*").ToArray();

                    var results = new List<PortfolioContent>();
                    foreach (var key in contentKeys)
                    {
                        var contentJson = await db.StringGetAsync(key);
                        if (!contentJson.HasValue) continue;

                        var content = JsonSerializer.Deserialize<PortfolioContent>(contentJson);
                        if (content == null) continue;

                        // Check if content matches search terms
                        bool matches = searchTerms.Any(term =>
                            content.Title.ToLower().Contains(term) ||
                            content.Content.ToLower().Contains(term) ||
                            content.Tags.Any(tag => tag.ToLower().Contains(term)));

                        if (matches)
                        {
                            results.Add(content);
                        }
                    }

                    return results.OrderBy(c => c.DisplayOrder).Take(20).ToList();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error searching portfolio content with query {Query}", query);
                    return new List<PortfolioContent>();
                }
            }

            public async Task<string> EnrichChatContextAsync(string message)
            {
                try
                {
                    _logger.LogInformation("Enriching chat context with portfolio data");

                    if (string.IsNullOrWhiteSpace(message))
                    {
                        return "";
                    }

                    if (_redis == null)
                    {
                        _logger.LogWarning("Redis connection not available for chat context enrichment");
                        return "";
                    }

                    var db = _redis.GetDatabase();
                    var server = _redis.GetServer(_redis.GetEndPoints().First());
                    var relevantContent = new List<PortfolioContent>();
                    var messageLower = message.ToLower();

                    // Simple keyword matching to find relevant content
                    if (messageLower.Contains("skill") || messageLower.Contains("know") || messageLower.Contains("technology"))
                    {
                        var skillsKeys = server.Keys(pattern: $"{PortfolioDataPrefix}content:skills:*").ToList();
                        skillsKeys.AddRange(server.Keys(pattern: $"{PortfolioDataPrefix}content:softskills:*"));

                        foreach (var key in skillsKeys.Take(5))
                        {
                            var contentJson = await db.StringGetAsync(key);
                            if (!contentJson.HasValue) continue;

                            var content = JsonSerializer.Deserialize<PortfolioContent>(contentJson);
                            if (content != null)
                            {
                                relevantContent.Add(content);
                            }
                        }
                    }

                    if (messageLower.Contains("project") || messageLower.Contains("portfolio") || messageLower.Contains("work") || messageLower.Contains("build"))
                    {
                        var projectKeys = server.Keys(pattern: $"{PortfolioDataPrefix}content:projects:*").ToList();
                        projectKeys.AddRange(server.Keys(pattern: $"{PortfolioDataPrefix}content:projectlinks"));

                        foreach (var key in projectKeys.Take(5))
                        {
                            var contentJson = await db.StringGetAsync(key);
                            if (!contentJson.HasValue) continue;

                            var content = JsonSerializer.Deserialize<PortfolioContent>(contentJson);
                            if (content != null)
                            {
                                relevantContent.Add(content);
                            }
                        }
                    }

                    if (messageLower.Contains("experience") || messageLower.Contains("job") || messageLower.Contains("career") || messageLower.Contains("company"))
                    {
                        var experienceKey = $"{PortfolioDataPrefix}content:experience";
                        var contentJson = await db.StringGetAsync(experienceKey);

                        if (contentJson.HasValue)
                        {
                            var content = JsonSerializer.Deserialize<PortfolioContent>(contentJson);
                            if (content != null)
                            {
                                relevantContent.Add(content);
                            }
                        }
                    }

                    if (messageLower.Contains("about") || messageLower.Contains("tell me about") || messageLower.Contains("who are") || messageLower.Contains("introduction"))
                    {
                        var aboutKey = $"{PortfolioDataPrefix}content:aboutme";
                        var interestsKey = $"{PortfolioDataPrefix}content:interests";

                        var aboutJson = await db.StringGetAsync(aboutKey);
                        var interestsJson = await db.StringGetAsync(interestsKey);

                        if (aboutJson.HasValue)
                        {
                            var content = JsonSerializer.Deserialize<PortfolioContent>(aboutJson);
                            if (content != null)
                            {
                                relevantContent.Add(content);
                            }
                        }

                        if (interestsJson.HasValue)
                        {
                            var content = JsonSerializer.Deserialize<PortfolioContent>(interestsJson);
                            if (content != null)
                            {
                                relevantContent.Add(content);
                            }
                        }
                    }

                    // If no specific category is detected, include a general overview
                    if (relevantContent.Count == 0)
                    {
                        // Get one content item from each category
                        var patterns = new[]
                        {
                        $"{PortfolioDataPrefix}content:aboutme",
                        $"{PortfolioDataPrefix}content:skills:0",
                        $"{PortfolioDataPrefix}content:projects:0",
                        $"{PortfolioDataPrefix}content:experience"
                    };

                        foreach (var pattern in patterns)
                        {
                            var contentJson = await db.StringGetAsync(pattern);
                            if (!contentJson.HasValue) continue;

                            var content = JsonSerializer.Deserialize<PortfolioContent>(contentJson);
                            if (content != null)
                            {
                                relevantContent.Add(content);
                            }
                        }
                    }

                    // Format the content as a string to be included in the chat context
                    var contextBuilder = new StringBuilder();
                    contextBuilder.AppendLine("PORTFOLIO INFORMATION:");

                    foreach (var item in relevantContent.Take(5)) // Limit to 5 items to keep context manageable
                    {
                        contextBuilder.AppendLine($"- {item.Title}: {item.Content}");
                    }

                    return contextBuilder.ToString();
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error enriching chat context");
                    return ""; // Return empty context on error rather than failing
                }
            }
        }
    }
}
