using Portfolio_server.Controllers;
using Portfolio_server.Models;


namespace Portfolio_server.Services
{
    public interface IGeminiService
    {
        Task<ProcessedResponse> ProcessMessageAsync(string message, string sessionId, string style = "NORMAL");
    }
}

