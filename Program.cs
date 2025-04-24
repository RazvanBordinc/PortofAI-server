using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StackExchange.Redis;
using System;

var builder = WebApplication.CreateBuilder(args);

 
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

 
builder.Services.AddHttpClient();

// Configure Redis with error handling
var redisConnectionString = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
try
{
    var options = new ConfigurationOptions
    {
        AbortOnConnectFail = false, // Don't abort if connection fails initially
        ConnectTimeout = 5000, // Timeout after 5 seconds
        ConnectRetry = 3 // Retry 3 times
    };

    // Parse the connection string
    foreach (var endpoint in redisConnectionString.Split(','))
    {
        options.EndPoints.Add(endpoint.Trim());
    }

    var redis = ConnectionMultiplexer.Connect(options);
    builder.Services.AddSingleton<IConnectionMultiplexer>(redis);
    Console.WriteLine("Successfully connected to Redis");
}
catch (Exception ex)
{
    Console.WriteLine($"WARNING: Failed to connect to Redis. Some functionality will be limited: {ex.Message}");
    // Provide a dummy/mock implementation if needed
    // This is optional - you can decide if you want the app to run without Redis
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
if (app.Environment.IsDevelopment())
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