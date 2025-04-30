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

                    // IMPROVED: Better keyword detection for skills-related queries
                    bool isSkillsQuery = messageLower.Contains("skill") ||
                                         messageLower.Contains("know") ||
                                         messageLower.Contains("technology") ||
                                         messageLower.Contains("proficiency") ||
                                         messageLower.Contains("familiar with") ||
                                         messageLower.Contains("expertise") ||
                                         messageLower.Contains("tech stack") ||
                                         messageLower.Contains("can you do") ||
                                         messageLower.Contains("programming") ||
                                         messageLower.Contains("language") ||
                                         messageLower.Contains("framework") ||
                                         messageLower.Contains("ability") ||
                                         messageLower.Contains("experience with") ||
                                         messageLower.Contains("competent");

                    // IMPROVED: More aggressive skills data inclusion for relevant queries
                    if (isSkillsQuery)
                    {
                        // Get ALL skills content, not just a few
                        var skillsKeys = server.Keys(pattern: $"{PortfolioDataPrefix}content:skills:*").ToList();
                        skillsKeys.AddRange(server.Keys(pattern: $"{PortfolioDataPrefix}content:softskills:*"));

                        // Also get individual skill entities
                        var individualSkillKeys = server.Keys(pattern: $"{PortfolioDataPrefix}skill:*");

                        // Load all skills content
                        foreach (var key in skillsKeys)
                        {
                            var contentJson = await db.StringGetAsync(key);
                            if (!contentJson.HasValue) continue;

                            var content = JsonSerializer.Deserialize<PortfolioContent>(contentJson);
                            if (content != null)
                            {
                                // Prioritize these by adding them first
                                relevantContent.Insert(0, content);
                            }
                        }

                        // Get details from individual skills for more comprehensive data
                        StringBuilder skillDetails = new StringBuilder();
                        skillDetails.AppendLine("DETAILED SKILLS:");

                        int skillCount = 0;
                        foreach (var key in individualSkillKeys.Take(20))  // Limit to 20 skills for context size
                        {
                            var skillJson = await db.StringGetAsync(key);
                            if (!skillJson.HasValue) continue;

                            var skill = JsonSerializer.Deserialize<SkillEntity>(skillJson);
                            if (skill != null)
                            {
                                skillCount++;
                                skillDetails.AppendLine($"- {skill.Name} ({skill.Category}): Proficiency {skill.ProficiencyLevel}/5, Experience: {skill.YearsOfExperience} years. {skill.Description}");
                            }
                        }

                        // If we found individual skills, add them as synthetic content
                        if (skillCount > 0)
                        {
                            relevantContent.Add(new PortfolioContent
                            {
                                Title = "Individual Skills Details",
                                Content = skillDetails.ToString(),
                                Tags = new List<string> { "skills", "detailed", "proficiency" }
                            });
                        }
                    }

                    // Rest of the existing enrichment code for project queries etc.
                    if (messageLower.Contains("project") || messageLower.Contains("portfolio") ||
                        messageLower.Contains("work") || messageLower.Contains("build"))
                    {
                        // ... existing project handling ...
                    }

                    // Format the content as a string to be included in the chat context
                    var contextBuilder = new StringBuilder();
                    contextBuilder.AppendLine("PORTFOLIO INFORMATION:");

                    // When returning content, prioritize skills in responses when explicitly asked
                    if (isSkillsQuery && relevantContent.Any())
                    {
                        contextBuilder.AppendLine("IMPORTANT - User is asking about SKILLS. Here are my skills (use ALL this information in your response):");
                    }

                    foreach (var item in relevantContent.Take(10)) // Increased to 10 items from 5
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
