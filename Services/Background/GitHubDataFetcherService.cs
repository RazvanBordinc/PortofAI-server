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
        private const int MaxRetries = 5;

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

            try 
            {
                // Run immediately on startup, but with a delay to ensure Redis is ready
                // This is especially important after Render wakes from sleep
                await Task.Delay(TimeSpan.FromSeconds(15), cancellationToken);
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
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error starting GitHubDataFetcherService");
                
                // Reschedule even if there was an error, but sooner (1 hour)
                _timer = new Timer(
                    async _ => await FetchAndUpdateDataAsync(),
                    null,
                    TimeSpan.FromHours(1),
                    TimeSpan.FromDays(1));
            }
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

                // Update Redis with fetched data (with retries)
                await UpdateRedisWithRetryAsync(content);
            }
            catch (HttpRequestException ex)
            {
                _logger.LogError(ex, "Error fetching data from GitHub. Will retry later.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unexpected error in GitHub data fetcher");
            }
        }

        private async Task UpdateRedisWithRetryAsync(string content)
        {
            int retryCount = 0;
            TimeSpan delay = TimeSpan.FromSeconds(2);
            bool success = false;

            while (retryCount < MaxRetries && !success)
            {
                try
                {
                    using var scope = _serviceProvider.CreateScope();
                    var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
                    
                    // Check Redis connection state
                    if (!redis.IsConnected)
                    {
                        _logger.LogWarning("Redis is not connected. Attempting to reconnect...");
                        await Task.Delay(delay);
                        retryCount++;
                        delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2)); // Exponential backoff with 30s cap
                        continue;
                    }

                    var db = redis.GetDatabase();

                    // Set operation timeout to handle potential connection issues
                    var options = new CommandFlags[] { CommandFlags.DemandMaster };
                    
                    // Store the me.txt content directly
                    await db.StringSetAsync(PortfolioDataKey, content, flags: options[0]);

                    // Set an expiration for the data (30 days)
                    await db.KeyExpireAsync(PortfolioDataKey, TimeSpan.FromDays(30), flags: options[0]);

                    _logger.LogInformation("Redis update completed successfully");
                    success = true;
                }
                catch (RedisConnectionException redisEx)
                {
                    retryCount++;
                    _logger.LogWarning($"Redis connection attempt {retryCount}/{MaxRetries} failed: {redisEx.Message}");
                    
                    if (retryCount >= MaxRetries)
                    {
                        _logger.LogError(redisEx, "Failed to update Redis after maximum retry attempts");
                        throw;
                    }
                    
                    await Task.Delay(delay);
                    delay = TimeSpan.FromSeconds(Math.Min(30, delay.TotalSeconds * 2)); // Exponential backoff with 30s cap
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Error updating Redis with me.txt data");
                    throw;
                }
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