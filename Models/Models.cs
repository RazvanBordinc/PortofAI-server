using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfolio_server.Models
{

    // Response from Gemini service
    public class ProcessedResponse
    {
        public string Text { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public object? FormatData { get; set; } = null;
    }

 

    // Model for chat requests
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string? Style { get; set; }
    }

    public class PortfolioContent
    {
        public int Id { get; set; }
        public int CategoryId { get; set; }
        public string Title { get; set; }
        public string Content { get; set; }
        public List<string> Tags { get; set; } = new List<string>();
        public int DisplayOrder { get; set; }
    }

    public class ContactRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? ClientIp { get; set; }  
    }
   

    public class SendGridOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string FromEmail { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public string ToEmail { get; set; } = string.Empty;
        public string ToName { get; set; } = string.Empty;
        public int EmailRateLimit { get; set; } = 2; // Default to 2 emails per IP
    }// Add this class to Models/Models.cs

    // Model for chat message data transfer
    public class MessageDto
    {
        public string Id { get; set; } = Guid.NewGuid().ToString();
        public string Content { get; set; } = string.Empty;
        public string Sender { get; set; } = string.Empty;
        public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    }
}