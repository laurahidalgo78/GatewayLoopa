# syntax=docker/dockerfile:1.7
# ---------- build ----------
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
WORKDIR /src
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
COPY global.json nuget.config Directory.Build.props Directory.Packages.props ./
COPY src/ApiGatewayPay.Core/ApiGatewayPay.Core.csproj src/ApiGatewayPay.Core/packages.lock.json src/ApiGatewayPay.Core/
COPY src/ApiGatewayPay.Infrastructure/ApiGatewayPay.Infrastructure.csproj src/ApiGatewayPay.Infrastructure/packages.lock.json src/ApiGatewayPay.Infrastructure/
COPY src/ApiGatewayPay.Api/ApiGatewayPay.Api.csproj src/ApiGatewayPay.Api/packages.lock.json src/ApiGatewayPay.Api/
# --locked-mode: falla si las dependencias no coinciden con los lock files (integridad de la cadena de suministro).
RUN dotnet restore src/ApiGatewayPay.Api/ApiGatewayPay.Api.csproj --locked-mode
COPY src/ src/
RUN dotnet publish src/ApiGatewayPay.Api/ApiGatewayPay.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false \
 && rm -f /app/appsettings.Development.json

# ---------- runtime ----------
# "chiseled": sin shell, sin gestor de paquetes, usuario no root (UID 1654) por defecto. "extra" incluye ICU/tzdata (SqlClient).
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra AS runtime
WORKDIR /app
COPY --from=build --chown=1654:1654 /app ./
USER 1654
ENV ASPNETCORE_URLS=http://+:8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_EnableDiagnostics=0 \
    DOTNET_gcServer=1
EXPOSE 8080
# Sin HEALTHCHECK (la imagen no tiene shell ni curl): use las sondas HTTP /health y /ready del orquestador.
ENTRYPOINT ["dotnet", "ApiGatewayPay.Api.dll"]
