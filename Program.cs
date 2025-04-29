using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;
using Portfolio_server.Services;
using Portfolio_server.Services.Portfolio_server.Services;
using Portfolio_server.Models;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add HTTP client for GitHub API
builder.Services.AddHttpClient("GitHubClient");

// Register GitHubDataFetcherService with correct namespace
builder.Services.AddHostedService<GitHubDataFetcherService>();

// Redis connection configuration
try
{
    var options = new ConfigurationOptions
    {
        AbortOnConnectFail = false, // Don't abort if connection fails initially
        ConnectTimeout = 10000, // Increase timeout to 10 seconds
        ConnectRetry = 5, // Increase retry attempts
        SyncTimeout = 10000, // Increase sync timeout
        Password = builder.Configuration.GetConnectionString("RedisPassword") ?? "VeryPasswordStrongIs2",
        AllowAdmin = true // Enable admin commands if needed
    };

    // Parse the connection string
    var redisHost = builder.Configuration.GetConnectionString("Redis") ?? "redis:6379";
    foreach (var endpoint in redisHost.Split(','))
    {
        options.EndPoints.Add(endpoint.Trim());
    }

    Console.WriteLine($"Attempting to connect to Redis at {redisHost}");

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

// Register our services
builder.Services.AddSingleton<IConversationService, ConversationService>();
builder.Services.AddSingleton<IRateLimiterService, RateLimiterService>();
builder.Services.AddScoped<IGeminiService, GeminiService>();

// Register Portfolio Service
builder.Services.AddSingleton<IPortfolioService, RedisPortfolioService>();

// Configure HTTP client for Gemini API
builder.Services.AddHttpClient<IGeminiService, GeminiService>(client =>
{
    client.BaseAddress = new Uri("https://generativelanguage.googleapis.com/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.Configure<SmtpOptions>(
    builder.Configuration.GetSection("Smtp")
);
// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowFrontend", policy =>
    {
        var allowedOrigins = builder.Configuration["AllowedOrigins"]?.Split(",")
            ?? new[] { "http://localhost:3000" };

        policy.WithOrigins(allowedOrigins)
              .AllowAnyMethod()
              .AllowAnyHeader()
              .WithExposedHeaders("X-Pagination");
    });
});

var app = builder.Build();

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

// Use CORS before any other middleware that might return responses
app.UseCors("AllowFrontend");

app.UseHttpsRedirection();
app.UseAuthorization();
app.MapControllers();

// Add a minimal endpoint for testing connectivity
app.MapGet("/api/ping", () => new { message = "pong", timestamp = DateTime.UtcNow });

Console.WriteLine("Application startup complete. Listening for requests...");

app.Run();