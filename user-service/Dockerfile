FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["UserService.csproj", "./"]
RUN dotnet restore "UserService.csproj"
COPY . .
RUN dotnet publish "UserService.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
COPY --from=build /app/publish .
ENV ASPNETCORE_URLS=http://+:8081
EXPOSE 8081
HEALTHCHECK --interval=10s --timeout=3s --retries=5 CMD curl -fs http://localhost:8081/health || exit 1
ENTRYPOINT ["dotnet", "UserService.dll"]
