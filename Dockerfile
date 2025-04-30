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

# Make sure to listen on all interfaces
ENV ASPNETCORE_URLS=http://+:80

# Health check
HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 \
    CMD curl -f http://localhost/api/health || exit 1

EXPOSE 80
ENTRYPOINT ["dotnet", "Portfolio-server.dll"]