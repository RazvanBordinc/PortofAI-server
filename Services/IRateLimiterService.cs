namespace Portfolio_server.Services
{
    public interface IRateLimiterService
    {
        Task<bool> CheckRateLimitAsync(string ipAddress);
        Task<bool> IncrementRateLimitAsync(string ipAddress);
        Task<int> GetRemainingRequestsAsync(string ipAddress, int maxRequests = 15);
    }
}
