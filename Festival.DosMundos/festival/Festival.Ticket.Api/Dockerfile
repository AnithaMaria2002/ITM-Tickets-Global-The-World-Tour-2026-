FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY ["Festival.Ticket.Api/Festival.Ticket.Api.csproj", "Festival.Ticket.Api/"]
COPY ["Festival.Shared.Events/Festival.Shared.Events.csproj", "Festival.Shared.Events/"]
RUN dotnet restore "Festival.Ticket.Api/Festival.Ticket.Api.csproj"
COPY . .
WORKDIR "/src/Festival.Ticket.Api"
RUN dotnet build "Festival.Ticket.Api.csproj" -c Release -o /app/build

FROM build AS publish
RUN dotnet publish "Festival.Ticket.Api.csproj" -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
RUN adduser --disabled-password --gecos '' appuser && chown -R appuser /app
USER appuser
COPY --from=publish /app/publish .
EXPOSE 5300
ENTRYPOINT ["dotnet", "Festival.Ticket.Api.dll"]
