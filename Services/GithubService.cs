using Portfolio_server.Models;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text;
using Portfolio_server.Data;
using Microsoft.EntityFrameworkCore;

namespace Portfolio_server.Services
{
    public class GitHubService : IGitHubService
    {
        private readonly HttpClient _httpClient;
        private readonly IConfiguration _configuration;
        private readonly AppDbContext _dbContext;
        private readonly ILogger<GitHubService> _logger;
        private readonly string _githubToken;

        public GitHubService(
            HttpClient httpClient,
            IConfiguration configuration,
            AppDbContext dbContext,
            ILogger<GitHubService> logger)
        {
            _httpClient = httpClient;
            _configuration = configuration;
            _dbContext = dbContext;
            _logger = logger;

            // Get GitHub token from configuration
            _githubToken = _configuration["GitHub:Token"];

            // Configure HttpClient
            _httpClient.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github.v3+json"));
            _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PortfolioApp", "1.0"));

            // Add authorization if token is available
            if (!string.IsNullOrEmpty(_githubToken))
            {
                _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", _githubToken);
            }
        }

        /// <summary>
        /// Syncs GitHub repositories for a user and saves them to the database
        /// </summary>
        /// <param name="username">GitHub username</param>
        /// <returns>Success indicator</returns>
        public async Task<bool> SyncRepositoriesAsync(string username)
        {
            try
            {
                _logger.LogInformation($"Syncing repositories for {username}");

                // Get repositories from GitHub API
                var repos = await GetRepositoriesFromApiAsync(username);
                if (repos == null)
                {
                    return false;
                }

                // Process each repository - iterate through the array correctly
                foreach (var repoJson in repos.Value.EnumerateArray())
                {
                    try
                    {
                        string repoName = repoJson.GetProperty("name").GetString() ?? string.Empty;
                        string repoOwner = repoJson.GetProperty("owner").GetProperty("login").GetString() ?? string.Empty;

                        // Skip forks unless needed
                        bool isFork = repoJson.GetProperty("fork").GetBoolean();
                        if (isFork)
                        {
                            continue;
                        }

                        // Rest of your code remains the same...
                        var existingRepo = await _dbContext.GitHubRepos
                            .FirstOrDefaultAsync(r => r.RepoOwner == repoOwner && r.RepoName == repoName);

                        if (existingRepo == null)
                        {
                            // Create new repo record
                            existingRepo = new GitHubRepo
                            {
                                RepoName = repoName,
                                RepoOwner = repoOwner
                            };
                            _dbContext.GitHubRepos.Add(existingRepo);
                        }

                        // Update repo details
                        existingRepo.Description = repoJson.GetProperty("description").ValueKind != JsonValueKind.Null
                            ? repoJson.GetProperty("description").GetString() ?? string.Empty
                            : string.Empty;
                        existingRepo.Stars = repoJson.GetProperty("stargazers_count").GetInt32();
                        existingRepo.Forks = repoJson.GetProperty("forks_count").GetInt32();
                        existingRepo.Watchers = repoJson.GetProperty("watchers_count").GetInt32();
                        existingRepo.DefaultBranch = repoJson.GetProperty("default_branch").GetString() ?? "main";
                        existingRepo.LastUpdatedAt = repoJson.GetProperty("updated_at").ValueKind != JsonValueKind.Null
                            ? DateTime.Parse(repoJson.GetProperty("updated_at").GetString() ?? DateTime.UtcNow.ToString())
                            : null;
                        existingRepo.LastSyncedAt = DateTime.UtcNow;

                        // Get README content
                        string readmeContent = await GetReadmeContentAsync(repoOwner, repoName);
                        existingRepo.ReadmeContent = readmeContent;

                        // Get commit count
                        int commitCount = await GetCommitCountAsync(repoOwner, repoName);
                        existingRepo.CommitCount = commitCount;

                        // Try to link to an existing project
                        await LinkRepoToProjectAsync(existingRepo);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, $"Error processing repository: {ex.Message}");
                    }
                }

                await _dbContext.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to sync repositories for {username}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Updates GitHub statistics for a user and saves them to the database
        /// </summary>
        /// <param name="username">GitHub username</param>
        /// <returns>Success indicator</returns>
        public async Task<bool> UpdateGitHubStatsAsync(string username)
        {
            try
            {
                _logger.LogInformation($"Updating GitHub stats for {username}");

                // Get user information
                var userJson = await GetUserInfoFromApiAsync(username);
                if (userJson == null)
                {
                    return false;
                }

                // Get existing stats or create new ones
                var stats = await _dbContext.GitHubStats
                    .FirstOrDefaultAsync(s => s.Username == username);

                if (stats == null)
                {
                    stats = new GitHubStats
                    {
                        Username = username
                    };
                    _dbContext.GitHubStats.Add(stats);
                }

                // Update basic stats
                stats.TotalRepositories = userJson.Value.GetProperty("public_repos").GetInt32();

                // Get all repos for this user to calculate aggregated stats
                var repos = await _dbContext.GitHubRepos
                    .Where(r => r.RepoOwner == username)
                    .ToListAsync();

                // Calculate total stars
                stats.TotalStars = repos.Sum(r => r.Stars);

                // Calculate total commits
                stats.TotalCommits = repos.Sum(r => r.CommitCount);

                // Calculate language statistics
                var languageStats = new Dictionary<string, int>();
                foreach (var repo in repos)
                {
                    var repoLanguages = await GetLanguagesAsync(username, repo.RepoName);
                    foreach (var lang in repoLanguages)
                    {
                        if (languageStats.ContainsKey(lang.Key))
                        {
                            languageStats[lang.Key] += lang.Value;
                        }
                        else
                        {
                            languageStats[lang.Key] = lang.Value;
                        }
                    }
                }

                // Set top language
                if (languageStats.Any())
                {
                    stats.TopLanguage = languageStats.OrderByDescending(l => l.Value).First().Key;
                }

                // Save language stats as JSON
                stats.LanguageStatsDict = languageStats;

                // Get contributions for the last year (need to scrape from GitHub profile page or use GraphQL API)
                stats.ContributionLastYear = await GetContributionsLastYearAsync(username);

                stats.LastSyncedAt = DateTime.UtcNow;
                stats.UpdatedAt = DateTime.UtcNow;

                await _dbContext.SaveChangesAsync();
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Failed to update GitHub stats for {username}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Gets GitHub statistics for a user
        /// </summary>
        /// <param name="username">GitHub username</param>
        /// <returns>GitHub stats</returns>
        public async Task<GitHubStats> GetGitHubStatsAsync(string username)
        {
            var stats = await _dbContext.GitHubStats
                .FirstOrDefaultAsync(s => s.Username == username);

            // If stats don't exist or are older than 24 hours, update them
            if (stats == null || stats.LastSyncedAt == null ||
                (DateTime.UtcNow - stats.LastSyncedAt.Value).TotalHours > 24)
            {
                await UpdateGitHubStatsAsync(username);
                stats = await _dbContext.GitHubStats
                    .FirstOrDefaultAsync(s => s.Username == username);
            }

            return stats;
        }

        /// <summary>
        /// Gets repositories for a user
        /// </summary>
        /// <param name="username">GitHub username</param>
        /// <param name="forceRefresh">Whether to force a refresh from the API</param>
        /// <returns>List of GitHub repositories</returns>
        public async Task<List<GitHubRepo>> GetRepositoriesAsync(string username, bool forceRefresh = false)
        {
            // Get repos from database
            var repos = await _dbContext.GitHubRepos
                .Where(r => r.RepoOwner == username)
                .ToListAsync();

            // Refresh from API if needed
            if (forceRefresh || !repos.Any() ||
                repos.Any(r => r.LastSyncedAt == null || (DateTime.UtcNow - r.LastSyncedAt.Value).TotalHours > 24))
            {
                await SyncRepositoriesAsync(username);
                repos = await _dbContext.GitHubRepos
                    .Where(r => r.RepoOwner == username)
                    .ToListAsync();
            }

            return repos;
        }

        /// <summary>
        /// Gets detailed information for a specific repository
        /// </summary>
        /// <param name="owner">Repository owner</param>
        /// <param name="repo">Repository name</param>
        /// <param name="forceRefresh">Whether to force a refresh from the API</param>
        /// <returns>GitHub repository details</returns>
        public async Task<GitHubRepo> GetRepositoryDetailsAsync(string owner, string repo, bool forceRefresh = false)
        {
            // Get repo from database
            var repoEntity = await _dbContext.GitHubRepos
                .FirstOrDefaultAsync(r => r.RepoOwner == owner && r.RepoName == repo);

            // If repo doesn't exist or needs refresh, get from API
            if (forceRefresh || repoEntity == null ||
                repoEntity.LastSyncedAt == null || (DateTime.UtcNow - repoEntity.LastSyncedAt.Value).TotalHours > 24)
            {
                try
                {
                    var repoJson = await GetRepositoryFromApiAsync(owner, repo);
                    if (repoJson != null)
                    {
                        if (repoEntity == null)
                        {
                            repoEntity = new GitHubRepo
                            {
                                RepoName = repo,
                                RepoOwner = owner
                            };
                            _dbContext.GitHubRepos.Add(repoEntity);
                        }

                        // Update repo details
                        repoEntity.Description = repoJson.Value.GetProperty("description").ValueKind != JsonValueKind.Null
                            ? repoJson.Value.GetProperty("description").GetString()
                            : "";
                        repoEntity.Stars = repoJson.Value.GetProperty("stargazers_count").GetInt32();
                        repoEntity.Forks = repoJson.Value.GetProperty("forks_count").GetInt32();
                        repoEntity.Watchers = repoJson.Value.GetProperty("watchers_count").GetInt32();
                        repoEntity.DefaultBranch = repoJson.Value.GetProperty("default_branch").GetString();
                        repoEntity.LastUpdatedAt = repoJson.Value.GetProperty("updated_at").ValueKind != JsonValueKind.Null
                            ? DateTime.Parse(repoJson.Value.GetProperty("updated_at").GetString())
                            : null;
                        repoEntity.LastSyncedAt = DateTime.UtcNow;

                        // Get README content
                        string readmeContent = await GetReadmeContentAsync(owner, repo);
                        repoEntity.ReadmeContent = readmeContent;

                        // Get commit count
                        int commitCount = await GetCommitCountAsync(owner, repo);
                        repoEntity.CommitCount = commitCount;

                        await _dbContext.SaveChangesAsync();
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, $"Failed to get repository details for {owner}/{repo}: {ex.Message}");

                    // If repo entity is null, we have nothing to return
                    if (repoEntity == null)
                    {
                        return null;
                    }
                }
            }

            return repoEntity;
        }

        #region Private Helper Methods

        private async Task<JsonElement?> GetRepositoriesFromApiAsync(string username)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.github.com/users/{username}/repos?per_page=100");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonDocumentOptions { AllowTrailingCommas = true };
                    var document = JsonDocument.Parse(content, options);
                    return document.RootElement.Clone();
                }

                _logger.LogWarning($"Failed to get repositories for {username}: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting repositories from GitHub API: {ex.Message}");
                return null;
            }
        }


        private async Task<JsonElement?> GetUserInfoFromApiAsync(string username)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.github.com/users/{username}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonDocumentOptions { AllowTrailingCommas = true };
                    var document = JsonDocument.Parse(content, options);
                    return document.RootElement.Clone();
                }

                _logger.LogWarning($"Failed to get user info for {username}: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting user info from GitHub API: {ex.Message}");
                return null;
            }
        }

        private async Task<JsonElement?> GetRepositoryFromApiAsync(string owner, string repo)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonDocumentOptions { AllowTrailingCommas = true };
                    var document = JsonDocument.Parse(content, options);
                    return document.RootElement.Clone();
                }

                _logger.LogWarning($"Failed to get repository {owner}/{repo}: {response.StatusCode}");
                return null;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting repository from GitHub API: {ex.Message}");
                return null;
            }
        }

        private async Task<string> GetReadmeContentAsync(string owner, string repo)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/readme");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonDocumentOptions { AllowTrailingCommas = true };
                    var document = JsonDocument.Parse(content, options);

                    // GitHub returns the content as base64 encoded
                    var base64Content = document.RootElement.GetProperty("content").GetString() ?? string.Empty;
                    base64Content = base64Content.Replace("\n", "");
                    var contentBytes = Convert.FromBase64String(base64Content);
                    return Encoding.UTF8.GetString(contentBytes);
                }

                return string.Empty; // No README found
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting README for {owner}/{repo}: {ex.Message}");
                return string.Empty;
            }
        }

        private async Task<int> GetCommitCountAsync(string owner, string repo)
        {
            try
            {
                // GitHub API doesn't provide a direct way to get total commits
                // We'll use the participation stats endpoint which gives weekly commit counts for the past year
                var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/stats/participation");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonDocumentOptions { AllowTrailingCommas = true };
                    var document = JsonDocument.Parse(content, options);

                    // Sum up the weekly commits for a rough estimate
                    var all = document.RootElement.GetProperty("all");
                    int totalCommits = 0;

                    foreach (var week in all.EnumerateArray())
                    {
                        totalCommits += week.GetInt32();
                    }

                    return totalCommits;
                }

                return 0;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting commit count for {owner}/{repo}: {ex.Message}");
                return 0;
            }
        }

        private async Task<Dictionary<string, int>> GetLanguagesAsync(string owner, string repo)
        {
            try
            {
                var response = await _httpClient.GetAsync($"https://api.github.com/repos/{owner}/{repo}/languages");

                if (response.IsSuccessStatusCode)
                {
                    var content = await response.Content.ReadAsStringAsync();
                    var options = new JsonDocumentOptions { AllowTrailingCommas = true };
                    var document = JsonDocument.Parse(content, options);

                    var languages = new Dictionary<string, int>();
                    foreach (var property in document.RootElement.EnumerateObject())
                    {
                        languages[property.Name] = property.Value.GetInt32();
                    }

                    return languages;
                }

                return new Dictionary<string, int>();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting languages for {owner}/{repo}: {ex.Message}");
                return new Dictionary<string, int>();
            }
        }

        private async Task<int> GetContributionsLastYearAsync(string username)
        {
            try
            {
                // The GitHub API doesn't provide this information directly
                // This is a very simplified approach - in a real app, you'd need to use the GitHub GraphQL API
                // or scrape the contributions calendar from the GitHub profile page

                // For demo purposes, we'll estimate based on recent repo activity
                var repos = await GetRepositoriesAsync(username);
                return repos.Sum(r => r.CommitCount);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error getting contributions for {username}: {ex.Message}");
                return 0;
            }
        }

        private async Task LinkRepoToProjectAsync(GitHubRepo repo)
        {
            try
            {
                // Check if there's a project with a matching GitHub URL
                var githubUrl = $"https://github.com/{repo.RepoOwner}/{repo.RepoName}";
                var project = await _dbContext.Projects
                    .FirstOrDefaultAsync(p => p.GitHubRepoUrl == githubUrl);

                if (project != null)
                {
                    repo.ProjectId = project.Id;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, $"Error linking repo to project: {ex.Message}");
            }
        }

        #endregion
    }
}
