using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Portfolio_server.Models;

namespace Portfolio_server.Data
{
    public class AppDbContext : DbContext
    {
        private readonly IConfiguration _configuration;

        public AppDbContext(DbContextOptions<AppDbContext> options, IConfiguration configuration)
            : base(options)
        {
            _configuration = configuration;
        }

        public DbSet<ContentCategory> ContentCategories { get; set; }
        public DbSet<ContentItem> ContentItems { get; set; }
        public DbSet<Skill> Skills { get; set; }
        public DbSet<Project> Projects { get; set; }
        public DbSet<GitHubRepo> GitHubRepos { get; set; }
        public DbSet<GitHubStats> GitHubStats { get; set; }
        public DbSet<Contact> Contacts { get; set; }
        public DbSet<ProjectSkill> ProjectSkills { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Configure composite key for ProjectSkill
            modelBuilder.Entity<ProjectSkill>()
                .HasKey(ps => new { ps.ProjectId, ps.SkillId });

            // Configure relationships
            modelBuilder.Entity<ProjectSkill>()
                .HasOne(ps => ps.Project)
                .WithMany(p => p.ProjectSkills)
                .HasForeignKey(ps => ps.ProjectId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ProjectSkill>()
                .HasOne(ps => ps.Skill)
                .WithMany(s => s.ProjectSkills)
                .HasForeignKey(ps => ps.SkillId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<ContentItem>()
                .HasOne(ci => ci.Category)
                .WithMany(cc => cc.ContentItems)
                .HasForeignKey(ci => ci.CategoryId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<GitHubRepo>()
                .HasOne(gr => gr.Project)
                .WithOne(p => p.GitHubRepo)
                .HasForeignKey<GitHubRepo>(gr => gr.ProjectId)
                .OnDelete(DeleteBehavior.SetNull);

            // Set up indexes
            modelBuilder.Entity<ContentItem>()
                .HasIndex(ci => ci.Tags);

            modelBuilder.Entity<Skill>()
                .HasIndex(s => s.Name);

            modelBuilder.Entity<Project>()
                .HasIndex(p => p.Name);

            modelBuilder.Entity<GitHubRepo>()
                .HasIndex(gr => new { gr.RepoOwner, gr.RepoName })
                .IsUnique();

            // Apply seed data during database initialization
            SeedData(modelBuilder);
        }

        private void SeedData(ModelBuilder modelBuilder)
        {
            // Seed ContentCategories
            modelBuilder.Entity<ContentCategory>().HasData(
                new ContentCategory { Id = 1, Name = "AboutMe", Description = "Information about me", DisplayOrder = 1 },
                new ContentCategory { Id = 2, Name = "Skills", Description = "Technical skills and competencies", DisplayOrder = 2 },
                new ContentCategory { Id = 3, Name = "Projects", Description = "Projects I've worked on", DisplayOrder = 3 },
                new ContentCategory { Id = 4, Name = "Contact", Description = "Contact information", DisplayOrder = 4 },
                new ContentCategory { Id = 5, Name = "Education", Description = "Educational background", DisplayOrder = 5 },
                new ContentCategory { Id = 6, Name = "Experience", Description = "Work experience", DisplayOrder = 6 }
            );

            // Seed some basic content items for demonstration
            modelBuilder.Entity<ContentItem>().HasData(
                new ContentItem
                {
                    Id = 1,
                    CategoryId = 1,
                    Title = "About Me Introduction",
                    Content = "I am a full-stack developer with extensive experience in .NET, React, and cloud technologies. I enjoy solving complex problems and building efficient, user-friendly applications.",
                    Tags = "introduction,summary,overview",
                    DisplayOrder = 1
                },
                new ContentItem
                {
                    Id = 2,
                    CategoryId = 1,
                    Title = "Current Focus",
                    Content = "Currently, I'm focused on mastering microservices architecture and expanding my knowledge of AI and machine learning integration in web applications.",
                    Tags = "current,focus,interests",
                    DisplayOrder = 2
                }
            );

            // Seed skills (examples)
            modelBuilder.Entity<Skill>().HasData(
                new Skill
                {
                    Id = 1,
                    Name = "C#",
                    Category = "Programming Language",
                    ProficiencyLevel = 5,
                    Description = "Advanced knowledge of C# including latest features and best practices.",
                    YearsOfExperience = 5.0f,
                    IsHighlighted = true,
                    DisplayOrder = 1
                },
                new Skill
                {
                    Id = 2,
                    Name = "React",
                    Category = "Frontend Framework",
                    ProficiencyLevel = 4,
                    Description = "Strong experience with React, including hooks, context API, and custom components.",
                    YearsOfExperience = 3.0f,
                    IsHighlighted = true,
                    DisplayOrder = 2
                },
                new Skill
                {
                    Id = 3,
                    Name = "SQL Server",
                    Category = "Database",
                    ProficiencyLevel = 4,
                    Description = "Experienced with SQL Server database design, query optimization, and maintenance.",
                    YearsOfExperience = 4.0f,
                    IsHighlighted = true,
                    DisplayOrder = 3
                }
            );

            // Sample project
            modelBuilder.Entity<Project>().HasData(
                new Project
                {
                    Id = 1,
                    Name = "Portfolio Chatbot",
                    Description = "An interactive portfolio website with AI chatbot functionality.",
                    Role = "Full Stack Developer",
                    Highlights = "- Built with .NET Core and React\n- Integrated with AI services\n- Responsive design",
                    StartDate = new DateTime(2023, 10, 1),
                    IsOpenSource = true,
                    IsHighlighted = true,
                    DisplayOrder = 1
                }
            );

            // Project-Skill relationships
            modelBuilder.Entity<ProjectSkill>().HasData(
                new ProjectSkill { ProjectId = 1, SkillId = 1 },
                new ProjectSkill { ProjectId = 1, SkillId = 2 },
                new ProjectSkill { ProjectId = 1, SkillId = 3 }
            );

            // Contact information
            modelBuilder.Entity<Contact>().HasData(
                new Contact { Id = 1, Type = "Email", Value = "example@example.com", IsPublic = true, DisplayOrder = 1 },
                new Contact { Id = 2, Type = "LinkedIn", Value = "https://linkedin.com/in/yourprofile", IsPublic = true, DisplayOrder = 2 },
                new Contact { Id = 3, Type = "GitHub", Value = "https://github.com/yourusername", IsPublic = true, DisplayOrder = 3 }
            );
        }
    }
}