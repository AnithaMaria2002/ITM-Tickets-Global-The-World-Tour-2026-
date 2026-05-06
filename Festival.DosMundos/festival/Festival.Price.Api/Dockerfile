FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Festival.Price.Api/Festival.Price.Api.csproj", "Festival.Price.Api/"]
RUN dotnet restore "Festival.Price.Api/Festival.Price.Api.csproj"
COPY . .
WORKDIR "/src/Festival.Price.Api"
RUN dotnet build "Festival.Price.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Festival.Price.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser
COPY --from=publish /app/publish .
EXPOSE 5200
ENTRYPOINT ["dotnet", "Festival.Price.Api.dll"]
