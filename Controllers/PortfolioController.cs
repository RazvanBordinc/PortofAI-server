using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Portfolio_server.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class PortfolioController : ControllerBase
    {
        private readonly ILogger<PortfolioController> _logger;
        private readonly Dictionary<string, List<PortfolioContent>> _staticContent;

        public PortfolioController(ILogger<PortfolioController> logger)
        {
            _logger = logger;
            // Initialize static content
            _staticContent = new Dictionary<string, List<PortfolioContent>>(StringComparer.OrdinalIgnoreCase)
            {
                {
                    "skills", new List<PortfolioContent>
                    {
                        new PortfolioContent
                        {
                            Id = 1,
                            Title = "Frontend Development",
                            Content = "Experienced in React, Next.js, HTML5, CSS3, Tailwind CSS, and JavaScript/TypeScript.",
                            Tags = new List<string> { "react", "nextjs", "javascript", "typescript", "html", "css", "tailwind" }
                        },
                        new PortfolioContent
                        {
                            Id = 2,
                            Title = "Backend Development",
                            Content = "Proficient in ASP.NET Core, FastAPI, Node.js, and building RESTful APIs.",
                            Tags = new List<string> { "dotnet", "aspnet", "fastapi", "python", "nodejs", "api" }
                        },
                        new PortfolioContent
                        {
                            Id = 3,
                            Title = "Database Technologies",
                            Content = "Experience with SQL Server, PostgreSQL, MongoDB, Redis, and Entity Framework.",
                            Tags = new List<string> { "sql", "nosql", "redis", "mongodb", "entityframework" }
                        },
                        new PortfolioContent
                        {
                            Id = 4,
                            Title = "DevOps & Cloud",
                            Content = "Familiar with Docker, Kubernetes, Azure, AWS, CI/CD pipelines, and GitHub Actions.",
                            Tags = new List<string> { "docker", "kubernetes", "azure", "aws", "cicd", "github" }
                        }
                    }
                },
                {
                    "projects", new List<PortfolioContent>
                    {
                        new PortfolioContent
                        {
                            Id = 5,
                            Title = "Chatbot Portfolio",
                            Content = "Interactive portfolio website with AI chatbot integration using ASP.NET Core, FastAPI with LangChain, Redis, and Next.js.",
                            Tags = new List<string> { "portfolio", "chatbot", "ai", "fullstack" }
                        },
                        new PortfolioContent
                        {
                            Id = 6,
                            Title = "E-commerce Platform",
                            Content = "Built a scalable e-commerce solution with product catalog, shopping cart, and payment processing using .NET Core and React.",
                            Tags = new List<string> { "ecommerce", "dotnet", "react", "payments" }
                        },
                        new PortfolioContent
                        {
                            Id = 7,
                            Title = "Task Management System",
                            Content = "Developed a collaborative task manager with real-time updates using SignalR, React, and SQL Server.",
                            Tags = new List<string> { "tasks", "realtime", "signalr", "react" }
                        }
                    }
                },
                {
                    "experience", new List<PortfolioContent>
                    {
                        new PortfolioContent
                        {
                            Id = 8,
                            Title = "Senior Full Stack Developer",
                            Content = "Led development of enterprise applications using .NET Core, React, and Azure. Implemented CI/CD pipelines and mentored junior developers.",
                            Tags = new List<string> { "senior", "leadership", "mentoring", "fullstack" }
                        },
                        new PortfolioContent
                        {
                            Id = 9,
                            Title = "Backend Developer",
                            Content = "Designed and implemented RESTful APIs and microservices using .NET Core and Docker. Optimized database performance and implemented caching strategies.",
                            Tags = new List<string> { "backend", "apis", "microservices", "optimization" }
                        },
                        new PortfolioContent
                        {
                            Id = 10,
                            Title = "Frontend Developer",
                            Content = "Created responsive and accessible web interfaces using React, Redux, and modern CSS frameworks. Implemented automated testing with Jest and React Testing Library.",
                            Tags = new List<string> { "frontend", "react", "testing", "accessibility" }
                        }
                    }
                },
                {
                    "education", new List<PortfolioContent>
                    {
                        new PortfolioContent
                        {
                            Id = 11,
                            Title = "Master's in Computer Science",
                            Content = "Specialized in artificial intelligence and distributed systems.",
                            Tags = new List<string> { "masters", "computerscience", "ai" }
                        },
                        new PortfolioContent
                        {
                            Id = 12,
                            Title = "Bachelor's in Software Engineering",
                            Content = "Graduated with honors. Focused on software design and development methodologies.",
                            Tags = new List<string> { "bachelor", "softwareengineering" }
                        }
                    }
                },
                {
                    "about", new List<PortfolioContent>
                    {
                        new PortfolioContent
                        {
                            Id = 13,
                            Title = "About Me",
                            Content = "I'm a passionate full-stack developer with experience building modern web applications. I'm constantly learning new technologies and enjoy solving complex problems with elegant solutions.",
                            Tags = new List<string> { "about", "personal" }
                        }
                    }
                }
            };
        }

        [HttpGet]
        public IActionResult GetAllCategories()
        {
            try
            {
                var categories = _staticContent.Keys.ToList();
                return Ok(categories);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting portfolio categories");
                return StatusCode(500, new { message = "An error occurred while fetching categories" });
            }
        }

        [HttpGet("{category}")]
        public IActionResult GetCategoryContent(string category)
        {
            try
            {
                _logger.LogInformation("Getting portfolio data. Category: {Category}", category);

                if (_staticContent.TryGetValue(category, out var content))
                {
                    return Ok(content);
                }

                return NotFound(new { message = $"Category '{category}' not found" });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error getting portfolio content for category {Category}", category);
                return StatusCode(500, new { message = "An error occurred while fetching content" });
            }
        }

        [HttpGet("search")]
        public IActionResult SearchPortfolio([FromQuery] string query)
        {
            try
            {
                _logger.LogInformation("Getting portfolio data. Query: {Query}", query);

                if (string.IsNullOrWhiteSpace(query))
                {
                    return BadRequest(new { message = "Search query cannot be empty" });
                }

                var results = new List<PortfolioContent>();
                var searchTerms = query.ToLower().Split(new[] { ' ', ',', '.', '?', '!' }, StringSplitOptions.RemoveEmptyEntries);

                foreach (var category in _staticContent.Values)
                {
                    foreach (var item in category)
                    {
                        // Search in title, content, and tags
                        bool isMatch = searchTerms.Any(term =>
                            item.Title.ToLower().Contains(term) ||
                            item.Content.ToLower().Contains(term) ||
                            item.Tags.Any(tag => tag.ToLower().Contains(term)));

                        if (isMatch && !results.Contains(item))
                        {
                            results.Add(item);
                        }
                    }
                }

                return Ok(results);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error searching portfolio content with query {Query}", query);
                return StatusCode(500, new { message = "An error occurred while searching content" });
            }
        }

        [HttpGet("enrich")]
        public IActionResult EnrichChatContext([FromQuery] string message)
        {
            try
            {
                _logger.LogInformation("Enriching chat context with portfolio data");

                if (string.IsNullOrWhiteSpace(message))
                {
                    return Ok(new { context = "" });
                }

                // Try to determine the most relevant portfolio category based on the message
                var relevantContent = new List<PortfolioContent>();
                var messageLower = message.ToLower();

                // Simple keyword matching to find relevant content
                if (messageLower.Contains("skill") || messageLower.Contains("know") || messageLower.Contains("technology"))
                {
                    relevantContent.AddRange(_staticContent["skills"]);
                }

                if (messageLower.Contains("project") || messageLower.Contains("portfolio") || messageLower.Contains("work") || messageLower.Contains("build"))
                {
                    relevantContent.AddRange(_staticContent["projects"]);
                }

                if (messageLower.Contains("experience") || messageLower.Contains("job") || messageLower.Contains("career") || messageLower.Contains("company"))
                {
                    relevantContent.AddRange(_staticContent["experience"]);
                }

                if (messageLower.Contains("education") || messageLower.Contains("study") || messageLower.Contains("degree") || messageLower.Contains("university"))
                {
                    relevantContent.AddRange(_staticContent["education"]);
                }

                if (messageLower.Contains("about") || messageLower.Contains("tell me about") || messageLower.Contains("who are") || messageLower.Contains("introduction"))
                {
                    relevantContent.AddRange(_staticContent["about"]);
                }

                // If no specific category is detected, include a general overview
                if (relevantContent.Count == 0)
                {
                    // Include one item from each category
                    foreach (var category in _staticContent.Values)
                    {
                        relevantContent.Add(category.First());
                    }
                }

                // Format the content as a string to be included in the chat context
                var contextBuilder = new System.Text.StringBuilder();
                contextBuilder.AppendLine("PORTFOLIO INFORMATION:");

                foreach (var item in relevantContent.Take(5)) // Limit to 5 items to keep context manageable
                {
                    contextBuilder.AppendLine($"- {item.Title}: {item.Content}");
                }

                return Ok(new { context = contextBuilder.ToString() });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error enriching chat context");
                return Ok(new { context = "" }); // Return empty context on error rather than failing
            }
        }
    }

    // Simple data model for portfolio content
    public class PortfolioContent
    {
        public int Id { get; set; }
        public string Title { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public List<string> Tags { get; set; } = new List<string>();
    }
}