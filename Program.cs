using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;
using System.Net;
using Portfolio_server.Services;
using Portfolio_server.Models;

var builder = WebApplication.CreateBuilder(args);

// Configure Kestrel to use Render's PORT environment variable
var port = Environment.GetEnvironmentVariable("PORT") ?? "10000";
Console.WriteLine($"Configuring server to listen on port {port}");
builder.WebHost.ConfigureKestrel(serverOptions =>
{
    serverOptions.Listen(IPAddress.Parse("0.0.0.0"), int.Parse(port));
});

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Configuration.AddEnvironmentVariables();
// Add HTTP client for GitHub API
builder.Services.AddHttpClient("GitHubClient");

// Register GitHubDataFetcherService with correct namespace
builder.Services.AddHostedService<GitHubDataFetcherService>();

// Redis connection configuration
try
{
    // For Upstash Redis specifically, use this approach
    var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
    Console.WriteLine($"Attempting to connect to Redis at {redisConnectionString}");
    
    var options = new ConfigurationOptions
    {
        AbortOnConnectFail = false,
        ConnectTimeout = 15000,      // 15 seconds
        ConnectRetry = 5,
        SyncTimeout = 15000,         // 15 seconds
        AsyncTimeout = 15000,        // 15 seconds for async operations
        ReconnectRetryPolicy = new ExponentialRetry(5000), // Add exponential backoff
        Ssl = true,                  // Enable SSL for Upstash
        AllowAdmin = false           // Not needed and may cause issues
    };
    
    // Use this approach for Upstash Redis
    if (redisConnectionString.Contains("upstash.io"))
    {
        // For Upstash, we need to handle the connection string differently
        var parts = redisConnectionString.Split(',');
        var endpoint = parts[0]; // host:port
        
        options.EndPoints.Add(endpoint);
        
        // Extract password from connection string if present
        foreach (var part in parts)
        {
            if (part.StartsWith("password="))
            {
                options.Password = part.Substring("password=".Length);
            }
        }
    }
    else
    {
        // For non-Upstash Redis
        foreach (var endpoint in redisConnectionString.Split(','))
        {
            if (!endpoint.Contains("password="))
                options.EndPoints.Add(endpoint.Trim());
        }
        
        // Use the separate password setting if not in connection string
        options.Password = builder.Configuration.GetConnectionString("RedisPassword") ?? "VeryPasswordStrongIs2";
    }

    var redis = ConnectionMultiplexer.Connect(options);
    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
    Console.WriteLine("Successfully connected to Redis");
}
catch (Exception ex)
{
    Console.WriteLine($"WARNING: Failed to connect to Redis. Some functionality will be limited: {ex.Message}");
    // Provide a dummy implementation for IConnectionMultiplexer
    builder.Services.AddSingleton<IConnectionMultiplexer>(ConnectionMultiplexer.Connect("localhost"));
}

// Configure SendGrid options
builder.Services.Configure<SendGridOptions>(options =>
{
    options.ApiKey = builder.Configuration["SendGrid:ApiKey"] ??
                     Environment.GetEnvironmentVariable("SENDGRID_API_KEY") ??
                     "";
    options.FromEmail = builder.Configuration["SendGrid:FromEmail"] ?? "bordincrazvan2004@gmail.com";
    options.FromName = builder.Configuration["SendGrid:FromName"] ?? "Razvan Bordinc Portfolio";
    options.ToEmail = builder.Configuration["SendGrid:ToEmail"] ?? "bordincrazvan2004@gmail.com";
    options.ToName = builder.Configuration["SendGrid:ToName"] ?? "Razvan Bordinc";

    // Set email rate limit (default is 2)
    if (int.TryParse(builder.Configuration["SendGrid:EmailRateLimit"], out int limit))
    {
        options.EmailRateLimit = limit;
    }
    else
    {
        options.EmailRateLimit = 2; // Default to 2 emails per IP
    }

    Console.WriteLine($"Configured SendGrid with FromEmail={options.FromEmail}, EmailRateLimit={options.EmailRateLimit}");
});

// Register our services
builder.Services.AddSingleton<IConversationService, ConversationService>();
builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
builder.Services.AddScoped<IGeminiService, GeminiService>();
builder.Services.AddScoped<IEmailService, EmailService>(); // Register the SendGrid EmailService

// Register Portfolio Service
builder.Services.AddSingleton<IPortfolioService, RedisPortfolioService>();

// Configure HTTP client for Gemini API
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        // Get allowed origins from configuration or environment variable
        var configOrigins = builder.Configuration["AllowedOrigins"]?.Split(",");
        var envOrigins = Environment.GetEnvironmentVariable("ALLOWED_ORIGINS")?.Split(",");
        
        // Combine both sources and provide defaults
        var allowedOrigins = configOrigins ?? envOrigins ?? new[] { "http://localhost:3000" };

        Console.WriteLine($"CORS configured with origins: {string.Join(", ", allowedOrigins)}");
        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials() 
              .WithExposedHeaders("X-Pagination");
    });
});

var app = builder.Build();

// Log configured URLs
Console.WriteLine($"Server configured with URLs: {string.Join(", ", app.Urls)}");

// Configure the HTTP request pipeline
if (app.Environment.IsDevelopment() || app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
    app.UseDeveloperExceptionPage();
}
else
{
    // Use custom exception handler in production
    app.UseExceptionHandler("/error");
}

// Configure forwarded headers for proxy scenarios (Render, etc.)
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedFor | 
                      Microsoft.AspNetCore.HttpOverrides.ForwardedHeaders.XForwardedProto
});

// Use CORS before any other middleware that might return responses
app.UseCors("AllowFrontend");

// Don't use HTTPS redirection on Render - they handle SSL termination at proxy level
// This prevents redirect loops and SSL certificate issues
// app.UseHttpsRedirection();

app.UseAuthorization();
app.MapControllers();

// Add a minimal endpoint for testing connectivity
app.MapGet("/", () => "PortofAI Server is running");
 

Console.WriteLine("Application startup complete. Listening for requests...");

app.Run();