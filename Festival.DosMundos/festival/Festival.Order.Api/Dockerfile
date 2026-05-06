FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Festival.Order.Api/Festival.Order.Api.csproj", "Festival.Order.Api/"]
COPY ["Festival.Shared.Events/Festival.Shared.Events.csproj", "Festival.Shared.Events/"]
RUN dotnet restore "Festival.Order.Api/Festival.Order.Api.csproj"
COPY . .
WORKDIR "/src/Festival.Order.Api"
RUN dotnet build "Festival.Order.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Festival.Order.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser
COPY --from=publish /app/publish .
EXPOSE 5000
ENTRYPOINT ["dotnet", "Festival.Order.Api.dll"]
