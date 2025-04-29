using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.DependencyInjection;
using StackExchange.Redis;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Linq;
using System.Text;
using Portfolio_server.Models;

namespace Portfolio_server.Services
{
    public class GitHubDataFetcherService : IHostedService, IDisposable
    {
        private readonly ILogger<GitHubDataFetcherService> _logger;
        private readonly IServiceProvider _serviceProvider;
        private readonly HttpClient _httpClient;
        private Timer _timer;
        private const string PortfolioDataPrefix = "portfolio:data:";

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

            // Schedule to run at midnight every day
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
                _logger.LogInformation("Fetching portfolio data from GitHub...");

                // GitHub's raw content URL for the JSON file
                var rawUrl = "https://raw.githubusercontent.com/RazvanBordinc/about-me/main/me.json";

                var response = await _httpClient.GetAsync(rawUrl);
                response.EnsureSuccessStatusCode();

                var content = await response.Content.ReadAsStringAsync();
                var portfolioItems = JsonSerializer.Deserialize<List<PortfolioItem>>(content, new JsonSerializerOptions
                {
                    PropertyNameCaseInsensitive = true
                });

                if (portfolioItems == null || !portfolioItems.Any())
                {
                    _logger.LogWarning("No portfolio items found in the GitHub repository");
                    return;
                }

                _logger.LogInformation($"Successfully fetched {portfolioItems.Count} portfolio items from GitHub");

                // Update Redis with fetched data
                await UpdateRedisAsync(portfolioItems);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error fetching or processing data from GitHub");
            }
        }

        private async Task UpdateRedisAsync(List<PortfolioItem> portfolioItems)
        {
            try
            {
                _logger.LogInformation("Updating Redis with fetched data...");

                using var scope = _serviceProvider.CreateScope();
                var redis = scope.ServiceProvider.GetRequiredService<IConnectionMultiplexer>();
                var db = redis.GetDatabase();

                // First, clear existing portfolio data
                var existingKeys = await GetPortfolioKeysAsync(db);
                if (existingKeys.Any())
                {
                    await db.KeyDeleteAsync(existingKeys.ToArray());
                    _logger.LogInformation($"Cleared {existingKeys.Count} existing portfolio data keys");
                }

                // Process each portfolio item and store in Redis
                foreach (var item in portfolioItems)
                {
                    await ProcessPortfolioItemAsync(db, item);
                }

                // Set an expiration for all portfolio data (30 days)
                foreach (var key in await GetPortfolioKeysAsync(db))
                {
                    await db.KeyExpireAsync(key, TimeSpan.FromDays(30));
                }

                _logger.LogInformation("Redis update completed successfully");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error updating Redis with GitHub data");
            }
        }

        private async Task<List<RedisKey>> GetPortfolioKeysAsync(IDatabase db)
        {
            var server = db.Multiplexer.GetServer(db.Multiplexer.GetEndPoints().First());
            var keys = new List<RedisKey>();

            try
            {
                var pattern = $"{PortfolioDataPrefix}*";
                var redisKeys = server.Keys(pattern: pattern);
                keys.AddRange(redisKeys);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting portfolio keys from Redis");
            }

            return keys;
        }

        private async Task ProcessPortfolioItemAsync(IDatabase db, PortfolioItem item)
        {
            switch (item.Title.ToLower())
            {
                case "projects":
                    await ProcessProjectsAsync(db, item.Content);
                    break;
                case "tech skills":
                    await ProcessTechSkillsAsync(db, item.Content);
                    break;
                case "soft skills":
                    await ProcessSoftSkillsAsync(db, item.Content);
                    break;
                case "interests":
                    await ProcessInterestsAsync(db, item.Content);
                    break;
                case "experience":
                    await ProcessExperienceAsync(db, item.Content);
                    break;
                case "about_me":
                    await ProcessAboutMeAsync(db, item.Content);
                    break;
                case "contact":
                    await ProcessContactAsync(db, item.Content);
                    break;
                case "project links":
                    await ProcessProjectLinksAsync(db, item.Content);
                    break;
                default:
                    _logger.LogWarning($"Unknown portfolio item type: {item.Title}");
                    break;
            }
        }

        private async Task ProcessProjectsAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing projects data");

            // Parse numbered projects from content
            var projects = ParseNumberedItems(content);

            // Store projects in Redis
            var categoryKey = $"{PortfolioDataPrefix}category:projects";
            var categoryData = new PortfolioCategory
            {
                Name = "Projects",
                Description = "Projects I've worked on",
                DisplayOrder = 3,
                IsActive = true
            };

            await db.StringSetAsync(categoryKey, JsonSerializer.Serialize(categoryData));

            // Store each project as a content item
            for (int i = 0; i < projects.Count; i++)
            {
                var projectText = projects[i];
                var parts = projectText.Split(" - ", 2);
                var projectName = parts[0].Trim();
                var projectDescription = parts.Length > 1 ? parts[1].Trim() : string.Empty;

                var contentItem = new PortfolioContent
                {
                    Id = i + 1,
                    CategoryId = 3, // Projects category ID
                    Title = projectName,
                    Content = projectDescription,
                    Tags = new List<string> { "project", "portfolio" },
                    DisplayOrder = i + 1
                };

                var contentKey = $"{PortfolioDataPrefix}content:projects:{i + 1}";
                await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));

                // Also store as project entity
                var project = new ProjectEntity
                {
                    Id = i + 1,
                    Name = projectName,
                    Description = projectDescription,
                    Role = "Full Stack Developer",
                    Highlights = projectDescription,
                    StartDate = DateTime.UtcNow.AddMonths(-6),
                    IsHighlighted = true,
                    DisplayOrder = i + 1
                };

                var projectKey = $"{PortfolioDataPrefix}project:{i + 1}";
                await db.StringSetAsync(projectKey, JsonSerializer.Serialize(project));
            }
        }

        private async Task ProcessTechSkillsAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing tech skills data");

            // Store skills category
            var categoryKey = $"{PortfolioDataPrefix}category:skills";
            var categoryData = new PortfolioCategory
            {
                Name = "Skills",
                Description = "Technical skills and competencies",
                DisplayOrder = 2,
                IsActive = true
            };

            await db.StringSetAsync(categoryKey, JsonSerializer.Serialize(categoryData));

            // Process each skill category (BACKEND, FRONTEND, etc.)
            var skillGroups = content.Split(". ", StringSplitOptions.RemoveEmptyEntries);
            int skillIndex = 0;
            int skillContentIndex = 0;

            foreach (var group in skillGroups)
            {
                // Skip empty groups
                if (string.IsNullOrWhiteSpace(group)) continue;

                // Split into category and skills list
                var categoryParts = group.Split(": ", 2);
                if (categoryParts.Length != 2) continue;

                var category = categoryParts[0].Trim();
                var skillsList = categoryParts[1].Split(", ", StringSplitOptions.RemoveEmptyEntries);

                // Create content item for this category
                var contentItem = new PortfolioContent
                {
                    Id = skillContentIndex + 100,
                    CategoryId = 2, // Skills category ID
                    Title = $"{category} Skills",
                    Content = categoryParts[1].Trim(),
                    Tags = new List<string> { "skills", category.ToLower() },
                    DisplayOrder = skillContentIndex
                };

                var contentKey = $"{PortfolioDataPrefix}content:skills:{skillContentIndex}";
                await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));
                skillContentIndex++;

                // Create individual skills
                foreach (var skillName in skillsList)
                {
                    var skill = new SkillEntity
                    {
                        Id = skillIndex + 1,
                        Name = skillName.Trim(),
                        Category = category,
                        ProficiencyLevel = GetProficiencyLevel(skillName),
                        Description = $"Experience with {skillName.Trim()} in {category} development",
                        YearsOfExperience = GetYearsOfExperience(skillName),
                        IsHighlighted = IsHighlightedSkill(skillName),
                        DisplayOrder = skillIndex
                    };

                    var skillKey = $"{PortfolioDataPrefix}skill:{skillIndex + 1}";
                    await db.StringSetAsync(skillKey, JsonSerializer.Serialize(skill));
                    skillIndex++;
                }
            }
        }

        private async Task ProcessSoftSkillsAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing soft skills data");

            // Process strengths and weaknesses
            var sections = content.Split(". ", StringSplitOptions.RemoveEmptyEntries);
            int displayOrder = 100;  // Use higher order for soft skills

            foreach (var section in sections)
            {
                var parts = section.Split(": ", 2);
                if (parts.Length != 2) continue;

                var title = parts[0].Trim();
                var skillsList = parts[1].Trim();

                var contentItem = new PortfolioContent
                {
                    Id = displayOrder,
                    CategoryId = 2, // Skills category ID
                    Title = title,
                    Content = skillsList,
                    Tags = new List<string> { "soft skills", title.ToLower() },
                    DisplayOrder = displayOrder
                };

                var contentKey = $"{PortfolioDataPrefix}content:softskills:{displayOrder}";
                await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));
                displayOrder++;
            }
        }

        private async Task ProcessInterestsAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing interests data");

            // Check if about category exists, if not create it
            var categoryKey = $"{PortfolioDataPrefix}category:aboutme";
            var categoryData = new PortfolioCategory
            {
                Name = "AboutMe",
                Description = "Information about me",
                DisplayOrder = 1,
                IsActive = true
            };

            await db.StringSetAsync(categoryKey, JsonSerializer.Serialize(categoryData));

            // Create interests content item
            var contentItem = new PortfolioContent
            {
                Id = 200,
                CategoryId = 1, // AboutMe category ID
                Title = "Interests & Aspirations",
                Content = content,
                Tags = new List<string> { "interests", "aspirations", "future" },
                DisplayOrder = 2
            };

            var contentKey = $"{PortfolioDataPrefix}content:interests";
            await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));
        }

        private async Task ProcessExperienceAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing experience data");

            // Create experience category
            var categoryKey = $"{PortfolioDataPrefix}category:experience";
            var categoryData = new PortfolioCategory
            {
                Name = "Experience",
                Description = "Work experience",
                DisplayOrder = 4,
                IsActive = true
            };

            await db.StringSetAsync(categoryKey, JsonSerializer.Serialize(categoryData));

            // Create experience content item
            var contentItem = new PortfolioContent
            {
                Id = 300,
                CategoryId = 4, // Experience category ID
                Title = "Professional Experience",
                Content = content,
                Tags = new List<string> { "experience", "work", "education" },
                DisplayOrder = 1
            };

            var contentKey = $"{PortfolioDataPrefix}content:experience";
            await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));
        }

        private async Task ProcessAboutMeAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing about me data");

            // Create about me content item
            var contentItem = new PortfolioContent
            {
                Id = 100,
                CategoryId = 1, // AboutMe category ID
                Title = "About Me Introduction",
                Content = content,
                Tags = new List<string> { "introduction", "summary", "overview" },
                DisplayOrder = 1
            };

            var contentKey = $"{PortfolioDataPrefix}content:aboutme";
            await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));
        }

        private async Task ProcessContactAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing contact data");

            // Create contact category
            var categoryKey = $"{PortfolioDataPrefix}category:contact";
            var categoryData = new PortfolioCategory
            {
                Name = "Contact",
                Description = "Contact information",
                DisplayOrder = 6,
                IsActive = true
            };

            await db.StringSetAsync(categoryKey, JsonSerializer.Serialize(categoryData));

            // Process contact information
            var contactItems = content.Split(", ", StringSplitOptions.RemoveEmptyEntries);
            int displayOrder = 1;

            foreach (var item in contactItems)
            {
                var parts = item.Split(": ", 2);
                if (parts.Length != 2) continue;

                var contactType = parts[0].Trim();
                var contactValue = parts[1].Trim();

                var contact = new ContactEntity
                {
                    Id = displayOrder,
                    Type = contactType,
                    Value = contactValue,
                    IsPublic = true,
                    DisplayOrder = displayOrder
                };

                var contactKey = $"{PortfolioDataPrefix}contact:{displayOrder}";
                await db.StringSetAsync(contactKey, JsonSerializer.Serialize(contact));
                displayOrder++;
            }

            // Also add as content item
            var contentItem = new PortfolioContent
            {
                Id = 400,
                CategoryId = 6, // Contact category ID
                Title = "Contact Information",
                Content = content,
                Tags = new List<string> { "contact", "email", "linkedin", "github" },
                DisplayOrder = 1
            };

            var contentKey = $"{PortfolioDataPrefix}content:contact";
            await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));
        }

        private async Task ProcessProjectLinksAsync(IDatabase db, string content)
        {
            _logger.LogInformation("Processing project links data");

            // Process numbered project links
            var projectLinks = ParseNumberedItems(content);

            for (int i = 0; i < projectLinks.Count; i++)
            {
                var linkText = projectLinks[i];
                var parts = linkText.Split(": ", 2);
                if (parts.Length != 2) continue;

                var projectName = parts[0].Trim();
                var projectUrl = parts[1].Trim();

                // Look for project keys in Redis
                var server = db.Multiplexer.GetServer(db.Multiplexer.GetEndPoints().First());
                var pattern = $"{PortfolioDataPrefix}project:*";
                var projectKeys = server.Keys(pattern: pattern).ToArray();

                // Check each project for a name match
                foreach (var projectKey in projectKeys)
                {
                    var projectJson = await db.StringGetAsync(projectKey);
                    if (!projectJson.HasValue) continue;

                    var project = JsonSerializer.Deserialize<ProjectEntity>(projectJson);
                    if (project?.Name?.Contains(projectName, StringComparison.OrdinalIgnoreCase) == true)
                    {
                        // Update project with URL
                        project.ProjectUrl = projectUrl;

                        // Set GitHub URL if it's a GitHub link
                        if (projectUrl.Contains("github.com"))
                        {
                            project.GitHubRepoUrl = projectUrl;
                            project.IsOpenSource = true;
                        }

                        // Save updated project
                        await db.StringSetAsync(projectKey, JsonSerializer.Serialize(project));
                        break;
                    }
                }
            }

            // Store links as content item
            var contentItem = new PortfolioContent
            {
                Id = 500,
                CategoryId = 3, // Projects category ID
                Title = "Project Links",
                Content = content,
                Tags = new List<string> { "projects", "links", "github" },
                DisplayOrder = 999 // Put at the end
            };

            var contentKey = $"{PortfolioDataPrefix}content:projectlinks";
            await db.StringSetAsync(contentKey, JsonSerializer.Serialize(contentItem));
        }

        // Helper method to parse numbered lists like "1. Item one. 2. Item two."
        private List<string> ParseNumberedItems(string content)
        {
            var result = new List<string>();
            var pattern = @"(\d+\.\s*)([^0-9\.]+)";
            var matches = Regex.Matches(content, pattern);

            foreach (Match match in matches)
            {
                if (match.Groups.Count >= 3)
                {
                    result.Add(match.Groups[2].Value.Trim());
                }
            }

            return result;
        }

        // Helper method to determine skill proficiency level (1-5)
        private int GetProficiencyLevel(string skillName)
        {
            if (skillName.Contains("basic", StringComparison.OrdinalIgnoreCase) ||
                skillName.Contains("little knowledge", StringComparison.OrdinalIgnoreCase))
            {
                return 3; // Basic proficiency
            }

            return 4; // Good proficiency
        }

        // Helper method to estimate years of experience
        private float GetYearsOfExperience(string skillName)
        {
            if (skillName.Contains("basic", StringComparison.OrdinalIgnoreCase) ||
                skillName.Contains("little knowledge", StringComparison.OrdinalIgnoreCase))
            {
                return 1.0f;
            }

            return 2.0f; // Based on "self-taught for 4 years" in the data
        }

        // Helper method to determine if a skill should be highlighted
        private bool IsHighlightedSkill(string skillName)
        {
            var keySkills = new[]
            {
                "react", "next.js", ".net core", "web api", "sql", "tailwind",
                "entity framework", "docker"
            };

            return keySkills.Any(ks =>
                skillName.Contains(ks, StringComparison.OrdinalIgnoreCase));
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