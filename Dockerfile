# BUILD aşaması
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Tüm repo içeriğini kopyala
COPY . .

# Proje klasörüne geç
WORKDIR /src/Maclar.Web

# (İsteğe bağlı ama faydalı) restore
RUN dotnet restore Maclar.Web.csproj

# Projeyi publish et - PROJEYİ AÇIKÇA BELİRTİYORUZ
RUN dotnet publish Maclar.Web.csproj -c Release -o /app/publish

# RUNTIME aşaması
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

COPY --from=build /app/publish .

# Railway'in PORT env değişkenini kullan
ENV ASPNETCORE_URLS=http://0.0.0.0:${PORT}

ENTRYPOINT ["dotnet", "Maclar.Web.dll"]