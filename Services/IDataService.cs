using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public interface IDataService
    {
        // Content Categories
        Task<List<ContentCategory>> GetCategoriesAsync();
        Task<ContentCategory> GetCategoryByIdAsync(int id);
        Task<ContentCategory> GetCategoryByNameAsync(string name);

        // Content Items
        Task<List<ContentItem>> GetContentItemsByCategoryAsync(string categoryName);
        Task<ContentItem> GetContentItemByIdAsync(int id);
        Task<List<ContentItem>> SearchContentItemsAsync(string query);

        // Skills
        Task<List<Skill>> GetSkillsAsync();
        Task<List<Skill>> GetHighlightedSkillsAsync();
        Task<List<Skill>> GetSkillsByCategoryAsync(string category);
        Task<Skill> GetSkillByIdAsync(int id);

        // Projects
        Task<List<Project>> GetProjectsAsync();
        Task<List<Project>> GetHighlightedProjectsAsync();
        Task<Project> GetProjectByIdAsync(int id);
        Task<Project> GetProjectWithDetailsAsync(int id);

        // GitHub Repos
        Task<List<GitHubRepo>> GetGitHubReposAsync();
        Task<GitHubRepo> GetGitHubRepoByIdAsync(int id);

        // Contact
        Task<List<Contact>> GetContactsAsync();
        Task<List<Contact>> GetPublicContactsAsync();

        // Composite queries
        Task<object> GetPortfolioSummaryAsync();
        Task<object> GetRelevantContentForQueryAsync(string query);
    }
}
