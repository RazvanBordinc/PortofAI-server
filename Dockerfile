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

# Enable console logging
ENV ASPNETCORE_LOGGING__CONSOLE__DISABLECOLORS=false
ENV ASPNETCORE_LOGGING__CONSOLE__FORMATTERNAME=Simple

# Make sure to listen on all interfaces
ENV ASPNETCORE_URLS=http://+:80

# Enable developer exception page (shows detailed errors)
ENV ASPNETCORE_ENVIRONMENT=Development

# Health check
HEALTHCHECK --interval=30s --timeout=30s --start-period=5s --retries=3 \
    CMD curl -f http://localhost/api/diagnostic/health || exit 1

EXPOSE 80
ENTRYPOINT ["dotnet", "Portfolio-server.dll"]