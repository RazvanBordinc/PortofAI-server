using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public interface IGitHubService
    {
        Task<bool> SyncRepositoriesAsync(string username);
        Task<bool> UpdateGitHubStatsAsync(string username);
        Task<GitHubStats> GetGitHubStatsAsync(string username);
        Task<List<GitHubRepo>> GetRepositoriesAsync(string username, bool forceRefresh = false);
        Task<GitHubRepo> GetRepositoryDetailsAsync(string owner, string repo, bool forceRefresh = false);
    }
}
