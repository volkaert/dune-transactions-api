# syntax=docker/dockerfile:1

# ---- Build ----
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restauration en couche séparée pour profiter du cache Docker
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/DuneTransactionsApi/DuneTransactionsApi.csproj src/DuneTransactionsApi/
RUN dotnet restore src/DuneTransactionsApi/DuneTransactionsApi.csproj

COPY src/ src/
RUN dotnet publish src/DuneTransactionsApi/DuneTransactionsApi.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

# ---- Runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID

ENTRYPOINT ["dotnet", "DuneTransactionsApi.dll"]
