# PortofAI Backend

<div align="center">

[![.NET](https://img.shields.io/badge/.NET-9.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/)
[![Redis](https://img.shields.io/badge/Redis-6.2-DC382D?style=flat-square&logo=redis)](https://redis.io/)
[![Gemini AI](https://img.shields.io/badge/Gemini_AI-2.0-8BBEE8?style=flat-square&logo=google)](https://ai.google.dev/)
[![SendGrid](https://img.shields.io/badge/SendGrid-Email-00A9E0?style=flat-square&logo=sendgrid)](https://sendgrid.com/)
[![Docker](https://img.shields.io/badge/Docker-Ready-2496ED?style=flat-square&logo=docker)](https://www.docker.com/)
[![Render](https://img.shields.io/badge/Deployed_on-Render-46E3B7?style=flat-square&logo=render)](https://render.com/)
[![Upstash](https://img.shields.io/badge/Redis_by-Upstash-00E9A3?style=flat-square&logo=upstash)](https://upstash.com/)

**ASP.NET Core backend powering the PortofAI chatbot interface with AI-driven responses about my skills and projects**

</div>

## 📋 Table of Contents

- [Overview](#-overview)
- [Features](#-features)
- [Architecture](#-architecture)
- [Tech Stack](#-tech-stack)
- [API Endpoints](#-api-endpoints)
- [Deployment](#-deployment)
- [Configuration](#-configuration)
- [Local Development](#-local-development)
- [Environment Variables](#-environment-variables)

## 🔍 Overview

The PortofAI backend is an ASP.NET Core web API that powers the AI-driven portfolio chat interface. It connects to Google's Gemini AI model to generate responses about my skills, projects, and experience, while maintaining conversation context through Redis. The service includes real-time response streaming, rate limiting, and contact form functionality through SendGrid.

The backend automatically fetches my latest information from a GitHub repository, ensuring that all responses contain current and accurate details about my work and capabilities.

## ✨ Features

### 💬 AI Chat Capabilities

- **Streaming Responses** - Real-time text streaming using Server-Sent Events (SSE)
- **Context Enrichment** - Enhances AI prompts with portfolio data from GitHub
- **Conversation History** - Persists chat history using Redis
- **Response Styles** - Supports multiple response styles (normal, formal, explanatory, minimalist, HR)
- **Error Resilience** - Implements exponential backoff retry mechanisms for API calls

### 🛡️ Security & Stability

- **Rate Limiting** - IP-based request limits to prevent abuse (15 requests per 24 hours)
- **Error Handling** - Comprehensive error handling with appropriate client feedback
- **Logging** - Structured logging for monitoring and debugging
- **Heartbeat System** - Maintains connection stability during streaming responses

### 📨 Contact Functionality

- **Contact Form API** - Processes and validates contact form submissions
- **Email Rate Limiting** - Prevents abuse (2 emails per IP address per day)
- **Email Backup** - Stores contact requests in Redis as backup if email sending fails
- **HTML Email Formatting** - Clean, branded HTML emails with proper encoding

### 🔄 Data Synchronization

- **GitHub Integration** - Background service that fetches latest portfolio content
- **Redis Caching** - Stores portfolio data with 30-day expiration
- **Automatic Updates** - Refreshes data daily to ensure information is current

## 🏗️ Architecture

The application follows a clean, service-oriented architecture with dependency injection:

```
┌─────────────────────────────────────────────┐
│                 Controllers                  │
│                                             │
│  ┌─────────────┐ ┌──────────────────────┐  │
│  │ MainControl.│ │ Other Controllers     │  │
│  └─────────────┘ └──────────────────────┘  │
└───────────────────┬─────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────┐
│                  Services                    │
│                                             │
│  ┌────────────┐ ┌─────────────┐ ┌────────┐  │
│  │GeminiSvc   │ │RedisPortf.  │ │RateLim.│  │
│  └────────────┘ └─────────────┘ └────────┘  │
│                                             │
│  ┌────────────┐ ┌─────────────┐ ┌────────┐  │
│  │Conversation│ │GitHubDataF. │ │EmailSvc│  │
│  └────────────┘ └─────────────┘ └────────┘  │
└───────────────────┬─────────────────────────┘
                    │
                    ▼
┌─────────────────────────────────────────────┐
│              External Services               │
│                                             │
│  ┌────────────┐ ┌─────────────┐ ┌────────┐  │
│  │ Gemini AI  │ │ Upstash     │ │SendGrid│  │
│  └────────────┘ └─────────────┘ └────────┘  │
└─────────────────────────────────────────────┘
```

### Key Components

- **Controllers** - Handle HTTP requests and manage response streams
- **Services** - Implement core business logic with dependency injection
- **Background Services** - Handle data synchronization from GitHub
- **External Integrations** - Connect to AI, data storage, and email services

## 🛠️ Tech Stack

### Core Framework

- **.NET 9.0** - Latest version of Microsoft's framework
- **ASP.NET Core** - Web API framework
- **Entity Framework Core** - ORM for potential database expansion

### Storage

- **Redis** (via Upstash) - For session management, rate limiting, conversation history
- **StackExchange.Redis** - Client library for Redis interaction

### AI Integration

- **Gemini AI** - Google's AI model for natural language processing
- **Custom Prompt Engineering** - Tailored prompts for portfolio-specific responses

### Email

- **SendGrid** - Email delivery service
- **HTML Email Templates** - Custom-formatted contact emails

### Deployment & Infrastructure

- **Docker** - Containerization for consistent deployment
- **Render** - Cloud hosting platform
- **Upstash** - Serverless Redis provider

### Development Tools

- **Swagger/OpenAPI** - API documentation
- **Structured Logging** - Through ILogger
- **HTTP Client Factory** - For resilient external API calls

## 📡 API Endpoints

### Chat Endpoints

```
POST /api/chat/stream
```

Streams AI responses in real-time using Server-Sent Events (SSE).

**Request Body:**

```json
{
  "message": "Tell me about your projects",
  "style": "NORMAL" // Optional: NORMAL, FORMAL, EXPLANATORY, MINIMALIST, HR
}
```

**Response:**
Server-Sent Events stream with the following event types:

- `message` - Contains chunks of the response text
- `done` - Marks completion with the full text
- `:` - Heartbeat comments to keep connection alive

### Conversation Endpoints

```
GET /api/conversation/history
```

Retrieves conversation history for the current session.

```
POST /api/conversation/clear
```

Clears conversation history for the current session.

### Rate Limiting Endpoint

```
GET /api/remaining
```

Returns the number of remaining requests for the current IP address.

**Response:**

```json
{
  "remaining": 10 // Number of remaining requests out of 15 per day
}
```

### Contact Endpoint

```
POST /api/contact
```

Processes contact form submissions.

**Request Body:**

```json
{
  "name": "John Doe",
  "email": "john@example.com",
  "phone": "123-456-7890", // Optional
  "message": "I'd like to discuss a project opportunity."
}
```

### Health Endpoints

```
GET /api/health
```

Health check endpoint.

```
GET /api/ping
```

Simple ping-pong endpoint for testing.

## 🚀 Deployment

The application is designed for easy deployment on Render using Docker, with Upstash Redis for data persistence.

### Render Deployment

1. Create a new Web Service in Render
2. Connect your GitHub repository
3. Select "Docker" as the environment
4. Configure the following environment variables (see [Environment Variables](#-environment-variables))
5. Set the health check path to `/api/health`

### Upstash Redis Setup

1. Create a new Redis database in Upstash
2. Copy the connection string from Upstash dashboard
3. Set the `ConnectionStrings__Redis` environment variable in Render

### Docker Configuration

The included Dockerfile configures the application for production deployment:

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY ["Portfolio-server.csproj", "./"]
RUN dotnet restore "Portfolio-server.csproj"
COPY . .
RUN dotnet build "Portfolio-server.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Portfolio-server.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS final
WORKDIR /app
COPY --from=publish /app/publish .

# Install curl for healthcheck
RUN apt-get update && apt-get install -y curl && rm -rf /var/lib/apt/lists/*

# Environment variables - PORT is provided by Render
ENV PORT=10000
ENV ASPNETCORE_URLS=http://+:${PORT}

# Health check - using the PORT environment variable
HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 \
    CMD curl -f http://localhost:${PORT}/api/health || exit 1

EXPOSE ${PORT}
ENTRYPOINT ["dotnet", "Portfolio-server.dll"]
```

## ⚙️ Configuration

### appsettings.json

The application is configured via `appsettings.json` and environment variables:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Warning",
      "Microsoft.AspNetCore": "Warning",
      "Microsoft.EntityFrameworkCore": "Warning",
      "Portfolio_server.Services.GeminiService": "Warning",
      "Portfolio_server.Controllers.MainController": "Information"
    }
  },
  "AllowedHosts": "*",
  "ConnectionStrings": {
    "Redis": "redis:6379,password=VeryPasswordStrongIs2"
  },
  "AllowedOrigins": "http://localhost:3000",
  "GeminiApi": {
    "ApiKey": "YOUR_GEMINI_API_KEY",
    "ModelName": "gemini-2.0-flash"
  },
  "SendGrid": {
    "ApiKey": "YOUR_SENDGRID_API_KEY",
    "FromEmail": "bordincrazvan2004@gmail.com",
    "FromName": "Razvan Bordinc Portfolio",
    "ToEmail": "bordincrazvan2004@gmail.com",
    "ToName": "Razvan Bordinc",
    "EmailRateLimit": 2
  }
}
```

## 💻 Local Development

### Prerequisites

- .NET 9.0 SDK
- Docker (optional, for Redis)
- Redis instance (local or remote)

### Setup Steps

1. Clone the repository

   ```bash
   git clone https://github.com/RazvanBordinc/portofai-backend.git
   cd portofai-backend
   ```

2. Set up Redis (optional if using remote Redis)

   ```bash
   docker run -d -p 6379:6379 --name redis-local redis:6
   ```

3. Configure app settings

   - Create a `appsettings.Development.json` file with your development settings
   - Or set environment variables for sensitive information

4. Run the application

   ```bash
   dotnet run
   ```

5. Access Swagger documentation
   ```
   https://localhost:7148/swagger
   ```

## 🔐 Environment Variables

The following environment variables can be set to configure the application:

| Variable                   | Description                          | Default                                   |
| -------------------------- | ------------------------------------ | ----------------------------------------- |
| `PORT`                     | Port for the HTTP server             | 10000                                     |
| `GOOGLE_API_KEY`           | API key for Gemini AI                | -                                         |
| `SENDGRID_API_KEY`         | API key for SendGrid                 | -                                         |
| `ConnectionStrings__Redis` | Redis connection string              | redis:6379,password=VeryPasswordStrongIs2 |
| `AllowedOrigins`           | CORS allowed origins                 | http://localhost:3000                     |
| `ASPNETCORE_ENVIRONMENT`   | Environment (Development/Production) | Production                                |

### Using GitHub Data

The backend automatically fetches portfolio data from a public GitHub repository:

```
https://raw.githubusercontent.com/RazvanBordinc/about-me/main/me.txt
```

This file should contain structured information about skills, projects, and experience that will be used to enhance AI responses.

## 📊 Operational Details

### Rate Limiting

- **Chat Requests**: 15 requests per IP address per 24 hours
- **Contact Form**: 2 email submissions per IP address per 24 hours

### Data Persistence

- **Conversation History**: Stored in Redis with 24-hour expiration
- **Portfolio Data**: Cached in Redis with 30-day expiration, refreshed daily
- **Contact Submissions**: Stored in Redis with 30-day backup retention

### Error Handling

- Exponential backoff for API calls (max 5 retries)
- Graceful degradation if Redis is unavailable
- User-friendly error messages for common failure scenarios

---

<div align="center">

### Built with ❤️ by Razvan Bordinc

[![GitHub](https://img.shields.io/badge/GitHub-RazvanBordinc-181717?style=for-the-badge&logo=github)](https://github.com/RazvanBordinc)
[![LinkedIn](https://img.shields.io/badge/LinkedIn-Razvan_Bordinc-0A66C2?style=for-the-badge&logo=linkedin)](https://linkedin.com/in/valentin-r%C4%83zvan-bord%C3%AEnc-30686a298/)
[![Email](https://img.shields.io/badge/Email-razvan.bordinc@yahoo.com-D14836?style=for-the-badge&logo=gmail)](mailto:razvan.bordinc@yahoo.com)

</div>
