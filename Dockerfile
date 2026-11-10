# BUILD aşaması
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Restore katmanının cache'lenebilmesi için önce proje dosyasını kopyala
COPY Maclar.Web/Maclar.Web.csproj Maclar.Web/
RUN dotnet restore Maclar.Web/Maclar.Web.csproj

# Ardından kaynak kodu kopyalayıp publish et
COPY Maclar.Web/ Maclar.Web/
WORKDIR /src/Maclar.Web
RUN dotnet publish Maclar.Web.csproj -c Release -o /app/publish --no-restore

# RUNTIME aşaması
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# SQLite veritabanı /app/data/maclar.db içinde tutulur. Container yeniden oluşturulunca
# verinin kaybolmaması için bu klasör kalıcı volume olarak bağlanmalı (Coolify: Persistent Storage -> /app/data).
RUN mkdir -p /app/data
VOLUME ["/app/data"]

# aspnet:8.0 imajı ASPNETCORE_HTTP_PORTS=8080 ile gelir; ayrıca ASPNETCORE_URLS vermek
# "Overriding HTTP_PORTS" uyarısına yol açıyordu.
EXPOSE 8080

ENTRYPOINT ["dotnet", "Maclar.Web.dll"]
