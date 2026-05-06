FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Festival.Search.Api/Festival.Search.Api.csproj", "Festival.Search.Api/"]
RUN dotnet restore "Festival.Search.Api/Festival.Search.Api.csproj"
COPY . .
WORKDIR "/src/Festival.Search.Api"
RUN dotnet build "Festival.Search.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Festival.Search.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser
COPY --from=publish /app/publish .
EXPOSE 5400
ENTRYPOINT ["dotnet", "Festival.Search.Api.dll"]
