# syntax=docker/dockerfile:1
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Kaynak ağacın tamamı tek seferde kopyalanıyor; hangi projelerin gerektiğini
# `dotnet publish` csproj'lardaki ProjectReference'lardan kendisi çözüyor.
# Kök dosyaları (Directory.Packages.props, Directory.Build.targets, global.json)
# da böylece kendiliğinden geliyor: csproj'lar sürümsüz <PackageReference>
# kullandığı için props dosyası olmadan NU1015, Directory.Build.targets olmadan
# da SQLitePCLRaw NU1903 uyarısı hataya dönüşür.
#
# Burada eskiden elle bakılan bir COPY listesi vardı (yalnız LicenseServer ve
# PdfParsing). O liste proje grafiğinin İKİNCİ bir kopyasıydı: csproj bir yerde
# "bana şu proje lazım" derken Dockerfile başka bir yerde "şu klasörleri kopyala"
# diyordu ve ikisini tutarlı tutan hiçbir şey yoktu. 2026-08-13'te ayrıştılar —
# PR #258 LicenseServer'a OrderDeck.Shared referansı ekledi, Dockerfile geri
# kaldı, restore projeyi SESSİZCE atladı ("Skipping project ... because it was
# not found") ve publish CS0234 ile patladı. Çözümün tamamını derleyen CI bunu
# göremez, çünkü orada bütün projeler zaten yerindedir; hata ilk kez master'a
# merge sonrası prod deploy'unda çıktı ve sunucu sessizce eski sürümde kaldı.
#
# Kaybedilen: csproj'ları ayrı katmana alıp restore'u önbelleğe alma. Ölçüldü —
# restore ~7 sn, repo ~4 MiB. Bu kadar küçük bir kazanç için tekrar eden bir
# doğruluk riski taşımaya değmez. Bağlamın şişmemesi .dockerignore'un işi.
COPY . .
RUN dotnet publish "OrderDeck.LicenseServer/OrderDeck.LicenseServer.csproj" -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "OrderDeck.LicenseServer.dll"]
