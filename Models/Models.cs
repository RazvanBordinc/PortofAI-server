using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Portfolio_server.Models
{
    public class BaseEntity
    {
        [Key]
        public int Id { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    }

    public class ContentCategory : BaseEntity
    {
        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;
        
        [MaxLength(255)]
        public string Description { get; set; } = string.Empty;
        
        public int DisplayOrder { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        // Navigation property
        public virtual ICollection<ContentItem> ContentItems { get; set; } = new List<ContentItem>();
    }

    public class ContentItem : BaseEntity
    {
        [Required]
        public int CategoryId { get; set; }
        
        [Required]
        [MaxLength(150)]
        public string Title { get; set; } = string.Empty;
        
        [Required]
        public string Content { get; set; } = string.Empty;
        
        [MaxLength(50)]
        public string ContentType { get; set; } = "Text"; // Text, HTML, Markdown
        
        [MaxLength(255)]
        public string Tags { get; set; } = string.Empty;
        
        public string Metadata { get; set; } = "{}"; // JSON string
        
        public int DisplayOrder { get; set; }
        
        public bool IsActive { get; set; } = true;
        
        // Navigation property
        [ForeignKey("CategoryId")]
        public virtual ContentCategory? Category { get; set; }
        
        // Helper methods for metadata
        [NotMapped]
        public Dictionary<string, object> MetadataDict
        {
            get => !string.IsNullOrEmpty(Metadata) 
                ? JsonSerializer.Deserialize<Dictionary<string, object>>(Metadata) ?? new Dictionary<string, object>()
                : new Dictionary<string, object>();
            set => Metadata = JsonSerializer.Serialize(value);
        }
    }

    public class Skill : BaseEntity
    {
        [Required]
        [MaxLength(100)]
        public string Name { get; set; } = string.Empty;
        
        [MaxLength(100)]
        public string Category { get; set; } = string.Empty;
        
        public int ProficiencyLevel { get; set; } // 1-5 or 1-10
        
        public string Description { get; set; } = string.Empty;
        
        public float YearsOfExperience { get; set; }
        
        public bool IsHighlighted { get; set; }
        
        public int DisplayOrder { get; set; }
        
        // Navigation properties
        public virtual ICollection<ProjectSkill> ProjectSkills { get; set; } = new List<ProjectSkill>();
    }

    public class Project : BaseEntity
    {
        [Required]
        [MaxLength(150)]
        public string Name { get; set; } = string.Empty;
        
        public string Description { get; set; } = string.Empty;
        
        [MaxLength(150)]
        public string Role { get; set; } = string.Empty;
        
        public string Highlights { get; set; } = string.Empty;
        
        public DateTime? StartDate { get; set; }
        
        public DateTime? EndDate { get; set; }
        
        [MaxLength(255)]
        public string ProjectUrl { get; set; } = string.Empty;
        
        public bool IsOpenSource { get; set; }
        
        [MaxLength(255)]
        public string GitHubRepoUrl { get; set; } = string.Empty;
        
        public bool IsHighlighted { get; set; }
        
        public int DisplayOrder { get; set; }
        
        // Navigation properties
        public virtual ICollection<ProjectSkill> ProjectSkills { get; set; } = new List<ProjectSkill>();
        
        public virtual GitHubRepo? GitHubRepo { get; set; }
    }

    public class GitHubRepo : BaseEntity
    {
        public int? ProjectId { get; set; }
        
        [Required]
        [MaxLength(150)]
        public string RepoName { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(100)]
        public string RepoOwner { get; set; } = string.Empty;
        
        public string Description { get; set; } = string.Empty;
        
        public int Stars { get; set; }
        
        public int Forks { get; set; }
        
        public int Watchers { get; set; }
        
        [MaxLength(100)]
        public string DefaultBranch { get; set; } = string.Empty;
        
        public string ReadmeContent { get; set; } = string.Empty;
        
        public int CommitCount { get; set; }
        
        public DateTime? LastUpdatedAt { get; set; }
        
        public DateTime? LastSyncedAt { get; set; }
        
        // Navigation property
        [ForeignKey("ProjectId")]
        public virtual Project? Project { get; set; }
    }

    public class GitHubStats : BaseEntity
    {
        [Required]
        [MaxLength(100)]
        public string Username { get; set; } = string.Empty;
        
        public int TotalCommits { get; set; }
        
        public int TotalStars { get; set; }
        
        public int TotalRepositories { get; set; }
        
        [MaxLength(50)]
        public string TopLanguage { get; set; } = string.Empty;
        
        public string LanguageStats { get; set; } = "{}"; // JSON string
        
        public int ContributionLastYear { get; set; }
        
        public DateTime? LastSyncedAt { get; set; }
        
        // Helper methods for language stats
        [NotMapped]
        public Dictionary<string, int> LanguageStatsDict
        {
            get => !string.IsNullOrEmpty(LanguageStats) 
                ? JsonSerializer.Deserialize<Dictionary<string, int>>(LanguageStats) ?? new Dictionary<string, int>()
                : new Dictionary<string, int>();
            set => LanguageStats = JsonSerializer.Serialize(value);
        }
    }

    public class Contact : BaseEntity
    {
        [Required]
        [MaxLength(50)]
        public string Type { get; set; } = string.Empty;
        
        [Required]
        [MaxLength(255)]
        public string Value { get; set; } = string.Empty;
        
        public bool IsPublic { get; set; } = true;
        
        public int DisplayOrder { get; set; }
    }

    // Join table for many-to-many relationship
    public class ProjectSkill
    {
        [Key, Column(Order = 0)]
        public int ProjectId { get; set; }
        
        [Key, Column(Order = 1)]
        public int SkillId { get; set; }
        
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        [ForeignKey("ProjectId")]
        public virtual Project? Project { get; set; }
        
        [ForeignKey("SkillId")]
        public virtual Skill? Skill { get; set; }
    }
    public class ProcessedResponse
    {
        public string Text { get; set; } = string.Empty;
        public string Format { get; set; } = string.Empty;
        public object? FormatData { get; set; } = null;
    }
}