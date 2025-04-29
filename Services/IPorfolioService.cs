using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public interface IPortfolioService
    {
        // Portfolio data retrieval methods
        Task<List<string>> GetCategoriesAsync();
        Task<List<PortfolioContent>> GetCategoryContentAsync(string category);
        Task<List<PortfolioContent>> SearchPortfolioAsync(string query);
        
        // Chat context enrichment method
        Task<string> EnrichChatContextAsync(string message);
    }
}
