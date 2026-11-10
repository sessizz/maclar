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

# Coolify/Railway gibi platformlar PORT değerini runtime'da verebilir.
# PORT verilmezse ASP.NET Core varsayılan olarak 8080 portunu dinler.
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
EXPOSE 8080

ENTRYPOINT ["dotnet", "Maclar.Web.dll"]
