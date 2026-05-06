FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Festival.Gateway.Api/Festival.Gateway.Api.csproj", "Festival.Gateway.Api/"]
RUN dotnet restore "Festival.Gateway.Api/Festival.Gateway.Api.csproj"
COPY . .
WORKDIR "/src/Festival.Gateway.Api"
RUN dotnet build "Festival.Gateway.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Festival.Gateway.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser
COPY --from=publish /app/publish .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Festival.Gateway.Api.dll"]
