using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Add HTTP client
builder.Services.AddHttpClient();

// Redis connection configuration - Update this section in Program.cs
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

// Configure HTTP client for FastAPI
builder.Services.AddHttpClient("FastAPI", client =>
{
    var fastApiUrl = builder.Configuration["FastAPIUrl"] ?? "http://localhost:8000";
    client.BaseAddress = new Uri(fastApiUrl);
    client.Timeout = TimeSpan.FromSeconds(30); // Set a reasonable timeout
});

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