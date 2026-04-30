# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ["OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj", "OrderDeck.LicenseServer/"]
RUN dotnet restore "OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj"
COPY OrderDeck.LicenseServer/ OrderDeck.LicenseServer/
RUN dotnet publish "OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OrderDeck.LicenseServer.dll"]
