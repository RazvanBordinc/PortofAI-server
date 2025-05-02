using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;

namespace Portfolio_server.Services
{
    public class GitHubDataFetcherService : IHostedService, IDisposable
    {
        private readonly ILogger<GitHubDataFetcherService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly HttpClient _httpClient;
        private Timer _timer;
        private const string PortfolioDataKey = "portfolio:me:txt";

        public GitHubDataFetcherService(
            ILogger<GitHubDataFetcherService> logger,
            IServiceProvider serviceProvider,
            IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _serviceProvider = serviceProvider;
            _httpClient = httpClientFactory.CreateClient("GitHubClient");
            _httpClient.DefaultRequestHeaders.Add("User-Agent", "Portfolio-Server/1.0");
        }

        public async Task StartAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("GitHub Data Fetcher Service is starting");

            // Run immediately on startup
            await FetchAndUpdateDataAsync();

            // Schedule to run daily at midnight
            var now = DateTime.Now;
            var midnight = DateTime.Today.AddDays(1); // Next midnight
            var timeToMidnight = midnight - now;

            _timer = new Timer(
                async _ => await FetchAndUpdateDataAsync(),
                null,
                timeToMidnight,
                TimeSpan.FromDays(1)); // Repeat every 24 hours

            _logger.LogInformation($"Next scheduled run: {midnight}");
        }

        private async Task FetchAndUpdateDataAsync()
        {
            try
            {
                _logger.LogInformation("Fetching me.txt data from GitHub...");

                // GitHub's raw content URL for the text file
                var rawUrl = "https://raw.githubusercontent.com/RazvanBordinc/about-me/main/me.txt";

                var response = await _httpClient.GetAsync(rawUrl);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();

                if (string.IsNullOrWhiteSpace(content))
                {
                    _logger.LogWarning("Empty me.txt content found in the GitHub repository");
                    return;
                }

                _logger.LogInformation($"Successfully fetched me.txt content from GitHub ({content.Length} characters)");

                // Update Redis with fetched data
                await UpdateRedisAsync(content);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching or processing data from GitHub");
            }
        }

        private async Task UpdateRedisAsync(string content)
        {
            try
            {
                _logger.LogInformation("Updating Redis with me.txt content...");

                using var scope = _serviceProvider.CreateScope();
                var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
                var db = redis.GetDatabase();

                // Store the me.txt content directly
                await db.StringSetAsync(PortfolioDataKey, content);

                // Set an expiration for the data (30 days)
                await db.KeyExpireAsync(PortfolioDataKey, TimeSpan.FromDays(30));

                _logger.LogInformation("Redis update completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Redis with me.txt data");
            }
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            _logger.LogInformation("GitHub Data Fetcher Service is stopping");
            _timer?.Change(Timeout.Infinite, 0);
            return Task.CompletedTask;
        }

        public void Dispose()
        {
            _timer?.Dispose();
        }
    }
}