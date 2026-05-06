using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// =============================================================================
// FESTIVAL DE LOS DOS MUNDOS - Price.Api
// Microservicio de Precios Dinámicos con Caché Distribuida Redis
//
// La gran idea: En el minuto cero, 50k usuarios piden precios.
// Si cada uno fuera a SQL → base de datos explota.
// Con Redis → 90% de las peticiones se responden desde caché en <5ms.
// =============================================================================

internal class Program
{
    private static void Main(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);

        // --- 1. ZONA DE SERVICIOS ---
        builder.Services.AddEndpointsApiExplorer();
        builder.Services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "Festival Price API", Version = "v1" });
            c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
            {
                Name = "Authorization",
                Type = SecuritySchemeType.Http,
                Scheme = "Bearer",
                BearerFormat = "JWT",
                In = ParameterLocation.Header
            });
            c.AddSecurityRequirement(new OpenApiSecurityRequirement
            {
        { new OpenApiSecurityScheme { Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" } }, Array.Empty<string>() }
            });
        });

        var jwtSettings = builder.Configuration.GetSection("JwtSettings");
        var secretKey = Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!);
        builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtSettings["Issuer"],
                    ValidateAudience = true,
                    ValidAudience = jwtSettings["Audience"],
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = new SymmetricSecurityKey(secretKey)
                };
            });
        builder.Services.AddAuthorization();

        // REDIS: Caché distribuida (el secreto del 90% de cache hit)
        // En Docker/K8s apunta a redis:6379. En local a localhost:6379
        builder.Services.AddStackExchangeRedisCache(options =>
        {
            options.Configuration = builder.Configuration.GetConnectionString("Redis") ?? "localhost:6379";
            options.InstanceName = "FestivalPrices:";
        });

        var app = builder.Build();

        if (app.Environment.IsDevelopment())
        {
            app.UseSwagger();
            app.UseSwaggerUI();
        }
        app.UseAuthentication();
        app.UseAuthorization();

        // --- 2. ZONA DE DATOS: "Base de datos" de precios (simulación de SQL) ---
        // En la vida real: Entity Framework + SQL Server
        var preciosDb = new Dictionary<string, decimal>
{
    { "1_Medellin_General",  350_000m },  // COP
    { "1_Medellin_Vip",      850_000m },
    { "1_Madrid_General",    120m    },   // EUR
    { "1_Madrid_Vip",        300m    },
    { "2_Medellin_General",  200_000m },
    { "2_Madrid_General",     80m    },
};

        // --- 3. ENDPOINTS ---

        // GET /api/prices/{eventId}/{sede}/{categoria}
        // Flujo: Primero busca en Redis → Si no está, busca en "SQL" → Guarda en Redis
        var getPrecioRouteHandler = app.MapGet("/api/prices/{eventId}/{sede}/{categoria}", async (
            int eventId, string sede, string categoria,
            IDistributedCache cache, ILogger<Program> logger) =>
        {
            var cacheKey = $"precio:{eventId}:{sede}:{categoria}";

            // PASO 1: Intentar leer desde Redis (el 90% de los casos termina aquí)
            var cachedValue = await cache.GetStringAsync(cacheKey);
            if (cachedValue is not null)
            {
                logger.LogInformation("[REDIS HIT] Precio servido desde caché. Key={CacheKey}", cacheKey);
                var cachedDto = JsonSerializer.Deserialize<PrecioDto>(cachedValue);
                return Results.Ok(cachedDto with { FuenteDatos = "Redis Cache ⚡" });
            }

            // PASO 2: Cache miss → ir a la "base de datos"
            logger.LogInformation("[REDIS MISS] Consultando base de datos. Key={CacheKey}", cacheKey);

            var dbKey = $"{eventId}_{sede}_{categoria}";
            if (!preciosDb.TryGetValue(dbKey, out var precio))
                return Results.NotFound(new { Error = "Precio no encontrado para esta combinación" });

            var moneda = sede == "Madrid" ? "EUR" : "COP";
            var dto = new PrecioDto(eventId, sede, categoria, precio, moneda, "SQL Database 🗄️");

            // PASO 3: Guardar en Redis con TTL de 5 minutos (precios son relativamente estables)
            var opciones = new DistributedCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            };
            await cache.SetStringAsync(cacheKey, JsonSerializer.Serialize(dto), opciones);

            return Results.Ok(dto);
        })
        .RequireAuthorization()
        .WithName("GetPrecio");

        // POST /api/prices/invalidate/{eventId} → Admin: invalida la caché cuando cambia el precio
        app.MapPost("/api/prices/invalidate/{eventId}", async (int eventId, IDistributedCache cache) =>
        {
            // Invalidamos todas las variaciones del evento (por sede y categoría)
            var combinaciones = new[] { "Medellin_General", "Medellin_Vip", "Madrid_General", "Madrid_Vip" };
            foreach (var combo in combinaciones)
                await cache.RemoveAsync($"precio:{eventId}:{combo}");

            return Results.Ok(new { Message = $"Caché invalidada para evento {eventId}" });
        });

        app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Price.Api" }));

        app.Run();
    }
}

// DTO local: record = inmutable, mismo patrón del profe
public record PrecioDto(
    int EventId,
    string Sede,
    string Categoria,
    decimal Precio,
    string Moneda,
    string FuenteDatos  // "Redis Cache ⚡" o "SQL Database 🗄️" — para demostrar en clase
);
