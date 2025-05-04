using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public interface IConversationService
    {
        Task<string> GetConversationHistoryAsync(string sessionId);
        Task SaveConversationAsync(string sessionId, string userMessage, string aiResponse);
        Task<bool> ClearConversationAsync(string sessionId);
        Task<List<MessageDto>> GetFormattedConversationHistoryAsync(string sessionId);
    }

}
