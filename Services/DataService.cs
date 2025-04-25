using Microsoft.EntityFrameworkCore;
using Portfolio_server.Data;
using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public class DataService : IDataService
    {
        private readonly AppDbContext _dbContext;
        private readonly ILogger<DataService> _logger;

        public DataService(AppDbContext dbContext, ILogger<DataService> logger)
        {
            _dbContext = dbContext;
            _logger = logger;
        }

        #region Content Categories

        public async Task<List<ContentCategory>> GetCategoriesAsync()
        {
            return await _dbContext.ContentCategories
                .Where(c => c.IsActive)
                .OrderBy(c => c.DisplayOrder)
                .ToListAsync();
        }

        public async Task<ContentCategory> GetCategoryByIdAsync(int id)
        {
            return await _dbContext.ContentCategories
                .FirstOrDefaultAsync(c => c.Id == id);
        }

        public async Task<ContentCategory> GetCategoryByNameAsync(string name)
        {
            return await _dbContext.ContentCategories
                .FirstOrDefaultAsync(c => c.Name.ToLower() == name.ToLower());
        }

        #endregion

        #region Content Items

        public async Task<List<ContentItem>> GetContentItemsByCategoryAsync(string categoryName)
        {
            return await _dbContext.ContentItems
                .Include(ci => ci.Category)
                .Where(ci => ci.IsActive && ci.Category.Name.ToLower() == categoryName.ToLower())
                .OrderBy(ci => ci.DisplayOrder)
                .ToListAsync();
        }

        public async Task<ContentItem> GetContentItemByIdAsync(int id)
        {
            return await _dbContext.ContentItems
                .Include(ci => ci.Category)
                .FirstOrDefaultAsync(ci => ci.Id == id);
        }

        public async Task<List<ContentItem>> SearchContentItemsAsync(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
            {
                return new List<ContentItem>();
            }

            var lowerQuery = query.ToLower();

            return await _dbContext.ContentItems
                .Include(ci => ci.Category)
                .Where(ci => ci.IsActive && (
                    ci.Title.ToLower().Contains(lowerQuery) ||
                    ci.Content.ToLower().Contains(lowerQuery) ||
                    ci.Tags.ToLower().Contains(lowerQuery)
                ))
                .OrderBy(ci => ci.Category.DisplayOrder)
                .ThenBy(ci => ci.DisplayOrder)
                .ToListAsync();
        }

        #endregion

        #region Skills

        public async Task<List<Skill>> GetSkillsAsync()
        {
            return await _dbContext.Skills
                .OrderBy(s => s.DisplayOrder)
                .ToListAsync();
        }

        public async Task<List<Skill>> GetHighlightedSkillsAsync()
        {
            return await _dbContext.Skills
                .Where(s => s.IsHighlighted)
                .OrderBy(s => s.DisplayOrder)
                .ToListAsync();
        }

        public async Task<List<Skill>> GetSkillsByCategoryAsync(string category)
        {
            return await _dbContext.Skills
                .Where(s => s.Category.ToLower() == category.ToLower())
                .OrderBy(s => s.DisplayOrder)
                .ToListAsync();
        }

        public async Task<Skill> GetSkillByIdAsync(int id)
        {
            return await _dbContext.Skills
                .FirstOrDefaultAsync(s => s.Id == id);
        }

        #endregion

        #region Projects

        public async Task<List<Project>> GetProjectsAsync()
        {
            return await _dbContext.Projects
                .OrderBy(p => p.DisplayOrder)
                .ToListAsync();
        }

        public async Task<List<Project>> GetHighlightedProjectsAsync()
        {
            return await _dbContext.Projects
                .Where(p => p.IsHighlighted)
                .OrderBy(p => p.DisplayOrder)
                .ToListAsync();
        }

        public async Task<Project> GetProjectByIdAsync(int id)
        {
            return await _dbContext.Projects
                .FirstOrDefaultAsync(p => p.Id == id);
        }

        public async Task<Project> GetProjectWithDetailsAsync(int id)
        {
            return await _dbContext.Projects
                .Include(p => p.GitHubRepo)
                .Include(p => p.ProjectSkills)
                    .ThenInclude(ps => ps.Skill)
                .FirstOrDefaultAsync(p => p.Id == id);
        }

        #endregion

        #region GitHub Repos

        public async Task<List<GitHubRepo>> GetGitHubReposAsync()
        {
            return await _dbContext.GitHubRepos
                .OrderByDescending(r => r.Stars)
                .ToListAsync();
        }

        public async Task<GitHubRepo> GetGitHubRepoByIdAsync(int id)
        {
            return await _dbContext.GitHubRepos
                .FirstOrDefaultAsync(r => r.Id == id);
        }

        #endregion

        #region Contact

        public async Task<List<Contact>> GetContactsAsync()
        {
            return await _dbContext.Contacts
                .OrderBy(c => c.DisplayOrder)
                .ToListAsync();
        }

        public async Task<List<Contact>> GetPublicContactsAsync()
        {
            return await _dbContext.Contacts
                .Where(c => c.IsPublic)
                .OrderBy(c => c.DisplayOrder)
                .ToListAsync();
        }

        #endregion

        #region Composite Queries

        public async Task<object> GetPortfolioSummaryAsync()
        {
            try
            {
                // Get basic about me content
                var aboutMeCategory = await GetCategoryByNameAsync("AboutMe");
                var aboutMeItems = aboutMeCategory != null
                    ? await GetContentItemsByCategoryAsync("AboutMe")
                    : new List<ContentItem>();

                // Get highlighted skills
                var highlightedSkills = await GetHighlightedSkillsAsync();

                // Get highlighted projects
                var highlightedProjects = await GetHighlightedProjectsAsync();

                // Get GitHub stats
                var githubStats = await _dbContext.GitHubStats.FirstOrDefaultAsync();

                // Get public contacts
                var publicContacts = await GetPublicContactsAsync();

                return new
                {
                    AboutMe = aboutMeItems,
                    Skills = highlightedSkills,
                    Projects = highlightedProjects,
                    GitHubStats = githubStats,
                    Contacts = publicContacts
                };
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting portfolio summary");
                throw;
            }
        }

        public async Task<object> GetRelevantContentForQueryAsync(string query)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(query))
                {
                    return await GetPortfolioSummaryAsync();
                }

                var lowerQuery = query.ToLower();
                var result = new Dictionary<string, object>();

                // Check for specific content types in the query
                bool isAboutMe = lowerQuery.Contains("about") || lowerQuery.Contains("who") || lowerQuery.Contains("introduction") || lowerQuery.Contains("bio");
                bool isSkills = lowerQuery.Contains("skill") || lowerQuery.Contains("technology") || lowerQuery.Contains("experience") || lowerQuery.Contains("know");
                bool isProjects = lowerQuery.Contains("project") || lowerQuery.Contains("portfolio") || lowerQuery.Contains("work") || lowerQuery.Contains("built");
                bool isContact = lowerQuery.Contains("contact") || lowerQuery.Contains("email") || lowerQuery.Contains("reach") || lowerQuery.Contains("social");
                bool isGitHub = lowerQuery.Contains("github") || lowerQuery.Contains("repo") || lowerQuery.Contains("opensource") || lowerQuery.Contains("source") || lowerQuery.Contains("code");

                // If no specific content type is detected, do a general search
                if (!isAboutMe && !isSkills && !isProjects && !isContact && !isGitHub)
                {
                    // Search content items
                    var contentItems = await SearchContentItemsAsync(query);
                    if (contentItems.Any())
                    {
                        result["Content"] = contentItems;
                    }

                    // Search skills by name
                    var skills = await _dbContext.Skills
                        .Where(s => s.Name.ToLower().Contains(lowerQuery) ||
                                   s.Description.ToLower().Contains(lowerQuery) ||
                                   s.Category.ToLower().Contains(lowerQuery))
                        .ToListAsync();
                    if (skills.Any())
                    {
                        result["Skills"] = skills;
                    }

                    // Search projects by name and description
                    var projects = await _dbContext.Projects
                        .Where(p => p.Name.ToLower().Contains(lowerQuery) ||
                                   p.Description.ToLower().Contains(lowerQuery) ||
                                   p.Role.ToLower().Contains(lowerQuery) ||
                                   p.Highlights.ToLower().Contains(lowerQuery))
                        .ToListAsync();
                    if (projects.Any())
                    {
                        result["Projects"] = projects;
                    }

                    // If we found nothing specific, return general portfolio summary
                    if (!result.Any())
                    {
                        return await GetPortfolioSummaryAsync();
                    }
                }
                else
                {
                    // Return specific content based on detected topic
                    if (isAboutMe)
                    {
                        var aboutMeItems = await GetContentItemsByCategoryAsync("AboutMe");
                        result["AboutMe"] = aboutMeItems;
                    }

                    if (isSkills)
                    {
                        var skills = await GetSkillsAsync();
                        result["Skills"] = skills;
                    }

                    if (isProjects)
                    {
                        var projects = await GetProjectsAsync();
                        result["Projects"] = projects;
                    }

                    if (isContact)
                    {
                        var contacts = await GetPublicContactsAsync();
                        result["Contacts"] = contacts;
                    }

                    if (isGitHub)
                    {
                        var repos = await GetGitHubReposAsync();
                        var stats = await _dbContext.GitHubStats.FirstOrDefaultAsync();
                        result["GitHubRepos"] = repos;
                        result["GitHubStats"] = stats;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting relevant content for query '{query}'");
                throw;
            }
        }

        #endregion
    }
}
