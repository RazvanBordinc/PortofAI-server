using Microsoft.Extensions.Logging;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public class RedisPortfolioService : IPortfolioService
    {
        private readonly ILogger<RedisPortfolioService> _logger;
        private readonly IConnectionMultiplexer _redis;
        private const string PortfolioDataKey = "portfolio:me:txt";

        public RedisPortfolioService(
            ILogger<RedisPortfolioService> logger,
            IConnectionMultiplexer redis)
        {
            _logger = logger;
            _redis = redis;
        }

        public async Task<List<string>> GetCategoriesAsync()
        {
            // For backwards compatibility - return basic categories
            return new List<string> { "skills", "projects", "experience", "about" };
        }

        public async Task<List<PortfolioContent>> GetCategoryContentAsync(string category)
        {
            // For backwards compatibility - return basic content for requested category
            var content = await GetPortfolioTextAsync();
            if (string.IsNullOrEmpty(content))
            {
                return new List<PortfolioContent>();
            }

            return new List<PortfolioContent> {
                new PortfolioContent {
                    Id = 1,
                    Title = $"{category} Information",
                    Content = content,
                    Tags = new List<string> { category, "portfolio" }
                }
            };
        }

        public async Task<List<PortfolioContent>> SearchPortfolioAsync(string query)
        {
            // Simply return the full text as the search result
            var content = await GetPortfolioTextAsync();
            if (string.IsNullOrEmpty(content))
            {
                return new List<PortfolioContent>();
            }

            return new List<PortfolioContent> {
                new PortfolioContent {
                    Id = 1,
                    Title = "Portfolio Information",
                    Content = content,
                    Tags = new List<string> { "search", "portfolio" }
                }
            };
        }

        public async Task<string> EnrichChatContextAsync(string message)
        {
            try
            {
                _logger.LogInformation("Enriching chat context with me.txt content");

                var portfolioContent = await GetPortfolioTextAsync();
                if (string.IsNullOrEmpty(portfolioContent))
                {
                    _logger.LogWarning("No portfolio content available for enrichment");
                    return "";
                }

                // Return the entire portfolio text as context
                return $"PORTFOLIO INFORMATION:\n{portfolioContent}";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching chat context");
                return ""; // Return empty context on error
            }
        }

        private async Task<string> GetPortfolioTextAsync()
        {
            try
            {
                if (_redis == null)
                {
                    _logger.LogWarning("Redis connection not available");
                    return "";
                }

                var db = _redis.GetDatabase();
                var content = await db.StringGetAsync(PortfolioDataKey);

                return content.HasValue ? content.ToString() : "";
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error retrieving portfolio text from Redis");
                return "";
            }
        }
    }
}