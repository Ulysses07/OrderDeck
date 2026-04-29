# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj", "LiveDeck.LicenseServer/"]
RUN dotnet restore "LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj"
COPY LiveDeck.LicenseServer/ LiveDeck.LicenseServer/
RUN dotnet publish "LiveDeck.LicenseServer/LiveDeck.LicenseServer.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "LiveDeck.LicenseServer.dll"]
