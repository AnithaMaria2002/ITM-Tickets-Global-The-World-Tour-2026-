# =============================================================================
# Dockerfile - Festival.Inventory.Api
# Multi-stage build: imagen final mínima (sin SDK, solo runtime)
# =============================================================================

# ETAPA 1: Build (usamos el SDK completo para compilar)
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copiamos solo los .csproj primero (optimiza caché de Docker)
COPY ["Festival.Inventory.Api/Festival.Inventory.Api.csproj", "Festival.Inventory.Api/"]
COPY ["Festival.Shared.Events/Festival.Shared.Events.csproj", "Festival.Shared.Events/"]
RUN dotnet restore "Festival.Inventory.Api/Festival.Inventory.Api.csproj"

# Ahora copiamos el resto del código
COPY . .
WORKDIR "/src/Festival.Inventory.Api"
RUN dotnet build "Festival.Inventory.Api.csproj" -c Release -o /app/build

# ETAPA 2: Publish (genera binarios optimizados)
FROM build AS publish
RUN dotnet publish "Festival.Inventory.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

# ETAPA 3: Runtime (imagen final liviana, solo lo necesario para correr)
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app

# Usuario no-root por seguridad (buena práctica en Kubernetes)
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser

COPY --from=publish /app/publish .

# Puertos: 5100 HTTP/REST, 5101 gRPC
EXPOSE 5100
EXPOSE 5101

ENTRYPOINT ["dotnet", "Festival.Inventory.Api.dll"]
