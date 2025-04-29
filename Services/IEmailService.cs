using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public interface IEmailService
    {
        Task<bool> SendContactEmailAsync(ContactRequest contactRequest);
    }
}
