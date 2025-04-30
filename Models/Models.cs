using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Portfolio_server.Models
{
    // Base entity for common properties
    public class BaseEntity
    {
        public int Id { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    // Category of content (skills, projects, etc.)
    public class ContentCategory : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;
    }

    // Content item - used for any type of portfolio content
    public class ContentItem : BaseEntity
    {
        public int CategoryId { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public string ContentType { get; set; } = "Text"; // Text, HTML, Markdown
        public string Tags { get; set; } = string.Empty;
        public string Metadata { get; set; } = "{}"; // JSON string
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; } = true;

        // Helper property for metadata - for convenience
        [JsonIgnore]
        public Dictionary<string, object> MetadataDict
        {
            get => !string.IsNullOrEmpty(Metadata)
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(Metadata) ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();
            set => Metadata = JsonSerializer.Serialize(value);
        }
    }

    // Technical or soft skill
    public class Skill : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Category { get; set; } = string.Empty;
        public int ProficiencyLevel { get; set; } // 1-5 scale
        public string Description { get; set; } = string.Empty;
        public float YearsOfExperience { get; set; }
        public bool IsHighlighted { get; set; }
        public int DisplayOrder { get; set; }
    }

    // Project information
    public class Project : BaseEntity
    {
        public string Name { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Highlights { get; set; } = string.Empty;
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string ProjectUrl { get; set; } = string.Empty;
        public bool IsOpenSource { get; set; }
        public string GitHubRepoUrl { get; set; } = string.Empty;
        public bool IsHighlighted { get; set; }
        public int DisplayOrder { get; set; }

        // List of skill IDs associated with this project
        public List<int> SkillIds { get; set; } = new List<int>();
    }

    // GitHub repository information
    public class GitHubRepo : BaseEntity
    {
        public int? ProjectId { get; set; }
        public string RepoName { get; set; } = string.Empty;
        public string RepoOwner { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public int Stars { get; set; }
        public int Forks { get; set; }
        public int Watchers { get; set; }
        public string DefaultBranch { get; set; } = string.Empty;
        public string ReadmeContent { get; set; } = string.Empty;
        public int CommitCount { get; set; }
        public DateTime? LastUpdatedAt { get; set; }
        public DateTime? LastSyncedAt { get; set; }
    }

    // GitHub statistics
    public class GitHubStats : BaseEntity
    {
        public string Username { get; set; } = string.Empty;
        public int TotalCommits { get; set; }
        public int TotalStars { get; set; }
        public int TotalRepositories { get; set; }
        public string TopLanguage { get; set; } = string.Empty;
        public string LanguageStats { get; set; } = "{}"; // JSON string
        public int ContributionLastYear { get; set; }
        public DateTime? LastSyncedAt { get; set; }

        // Helper property for language stats
        [JsonIgnore]
        public Dictionary<string, int> LanguageStatsDict
        {
            get => !string.IsNullOrEmpty(LanguageStats)
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(LanguageStats) ?? new Dictionary<string, int>()
                : new Dictionary<string, int>();
            set => LanguageStats = JsonSerializer.Serialize(value);
        }
    }

    // Contact information
    public class Contact : BaseEntity
    {
        public string Type { get; set; } = string.Empty;
        public string Value { get; set; } = string.Empty;
        public bool IsPublic { get; set; } = true;
        public int DisplayOrder { get; set; }
    }

    // Simple join model for skills associated with projects
    public class ProjectSkill
    {
        public int ProjectId { get; set; }
        public int SkillId { get; set; }
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    }

    // Response from Gemini service
    public class ProcessedResponse
    {
        public string Text { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public object? FormatData { get; set; } = null;
    }

    // Portfolio content DTO for API responses
 

    // Model for chat requests
    public class ChatRequest
    {
        public string Message { get; set; } = string.Empty;
        public string? Style { get; set; }
    }
    public class PortfolioItem
    {
        public string Title { get; set; }
        public string Content { get; set; }
        public string Description { get; set; }
        public string Links { get; set; }
    }

    // Redis storage model classes
    public class PortfolioCategory
    {
        public string Name { get; set; }
        public string Description { get; set; }
        public int DisplayOrder { get; set; }
        public bool IsActive { get; set; }
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

    public class SkillEntity
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Category { get; set; }
        public int ProficiencyLevel { get; set; }
        public string Description { get; set; }
        public float YearsOfExperience { get; set; }
        public bool IsHighlighted { get; set; }
        public int DisplayOrder { get; set; }
    }
    // Update this in Models.cs
    public class ContactRequest
    {
        public string Name { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string? Phone { get; set; }
        public string Message { get; set; } = string.Empty;
        public string? ClientIp { get; set; }  
    }
    public class ProjectEntity
    {
        public int Id { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string Role { get; set; }
        public string Highlights { get; set; }
        public DateTime? StartDate { get; set; }
        public DateTime? EndDate { get; set; }
        public string ProjectUrl { get; set; }
        public bool IsOpenSource { get; set; }
        public string GitHubRepoUrl { get; set; }
        public bool IsHighlighted { get; set; }
        public int DisplayOrder { get; set; }
    }

    public class ContactEntity
    {
        public int Id { get; set; }
        public string Type { get; set; }
        public string Value { get; set; }
        public bool IsPublic { get; set; }
        public int DisplayOrder { get; set; }
    }
    public class SmtpOptions
    {
        public string Host { get; set; }
        public int Port { get; set; }
        public bool EnableSsl { get; set; }
        public string Username { get; set; }
        public string Password { get; set; }
    }
    public class SendGridOptions
    {
        public string ApiKey { get; set; } = string.Empty;
        public string FromEmail { get; set; } = string.Empty;
        public string FromName { get; set; } = string.Empty;
        public string ToEmail { get; set; } = string.Empty;
        public string ToName { get; set; } = string.Empty;
        public int EmailRateLimit { get; set; } = 2; // Default to 2 emails per IP
    }
}