# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
# Central Package Management (Directory.Packages.props) and the SDK pin
# (global.json) live at the repo root; csproj has <PackageReference> without
# versions and would NU1015 without the props file. Both are pulled in
# before restore so the layer cache is honored when only source changes.
COPY Directory.Packages.props global.json ./
# OrderDeck.PdfParsing is referenced as a ProjectReference by LicenseServer
# (Faz 0a, 2026-05-20). Both csproj files must be in place before restore,
# and both source trees must be copied before publish.
COPY ["OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj", "OrderDeck.LicenseServer/"]
COPY ["OrderDeck.PdfParsing/OrderDeck.PdfParsing.csproj", "OrderDeck.PdfParsing/"]
RUN dotnet restore "OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj"
COPY OrderDeck.PdfParsing/ OrderDeck.PdfParsing/
COPY OrderDeck.LicenseServer/ OrderDeck.LicenseServer/
RUN dotnet publish "OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj" -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OrderDeck.LicenseServer.dll"]
