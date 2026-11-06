FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["NotificationService.csproj", "./"]
RUN dotnet restore "NotificationService.csproj"
COPY . .
RUN dotnet publish "NotificationService.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8082
EXPOSE 8082
HEALTHCHECK --interval=10s --timeout=3s --retries=5 CMD curl -fs http://localhost:8082/health || exit 1
ENTRYPOINT ["dotnet", "NotificationService.dll"]
