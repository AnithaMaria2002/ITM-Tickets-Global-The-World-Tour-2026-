using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Elastic.Clients.Elasticsearch;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// =============================================================================
// FESTIVAL DE LOS DOS MUNDOS - Search.Api
// Buscador Inteligente: Elasticsearch (texto) + Qdrant (semántica por IA)
//
// Elasticsearch: Busca por palabras exactas o aproximadas.
//   "rock" → encuentra "Rock en Medellín 2026"
//   "rok" (typo) → también lo encuentra (fuzzy search)
//
// Qdrant: Busca por INTENCIÓN, no por palabras exactas.
//   "quiero algo para bailar toda la noche" → recomienda eventos de electrónica
//   "música para reflexionar" → recomienda jazz o acústico
//   Usa vectores semánticos generados por un modelo de IA (embeddings)
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// --- 1. ZONA DE SERVICIOS ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Festival Search API - Buscador Inteligente",
        Version = "v1",
        Description = "Búsqueda por texto (Elasticsearch) + Semántica por IA (Qdrant)"
    });
});

var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true, ValidIssuer = jwtSettings["Issuer"],
            ValidateAudience = true, ValidAudience = jwtSettings["Audience"],
            ValidateLifetime = true, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(secretKey)
        };
    });
builder.Services.AddAuthorization();

// Elasticsearch: cliente oficial de Elastic
var elasticUrl = builder.Configuration["Elasticsearch:Url"] ?? "http://localhost:9200";
builder.Services.AddSingleton(new ElasticsearchClient(new Uri(elasticUrl)));

// Qdrant: cliente HTTP (Qdrant tiene SDK oficial, usamos HttpClient por simplicidad)
builder.Services.AddHttpClient("QdrantClient", client =>
{
    client.BaseAddress = new Uri(builder.Configuration["Qdrant:Url"] ?? "http://localhost:6333");
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();

// =============================================================================
// INICIALIZACIÓN: Indexar eventos en Elasticsearch al arrancar
// En la vida real: esto lo haría un proceso de seeding separado
// =============================================================================
var elasticClient = app.Services.GetRequiredService<ElasticsearchClient>();
await InicializarElasticsearchAsync(elasticClient);

// --- 2. ENDPOINTS ---

// GET /api/search/text?q=rock&sede=Medellin
// Búsqueda por texto usando Elasticsearch (fuzzy: tolera errores ortográficos)
app.MapGet("/api/search/text", async (
    string q, string? sede,
    ElasticsearchClient elastic,
    ILogger<Program> logger) =>
{
    logger.LogInformation("[ELASTICSEARCH] Búsqueda: '{Query}' | Sede: {Sede}", q, sede ?? "Todas");

    // Búsqueda multi-campo con fuzzy (tolera typos como "rok" → "rock")
    var response = await elastic.SearchAsync<EventoIndexDto>(s => s
        .Index("festival-eventos")
        .Query(q => q
            .Bool(b => b
                .Must(
                    m => m.MultiMatch(mm => mm
                        .Query(q)
                        .Fields(new[] { "nombre^3", "descripcion", "genero", "artistas" })
                        .Fuzziness(new Fuzziness("AUTO"))  // "rok" encuentra "rock"
                    )
                )
                .Filter(f => sede != null
                    ? f.Term(t => t.Field("sede").Value(sede))
                    : f.MatchAll())
            )
        )
        .Size(10)
    );

    if (!response.IsValidResponse)
    {
        logger.LogError("[ELASTICSEARCH] Error: {Error}", response.ElasticsearchServerError?.Error?.Reason);
        return Results.Problem("Error al consultar Elasticsearch");
    }

    var resultados = response.Documents.ToList();
    return Results.Ok(new
    {
        Motor      = "Elasticsearch 🔍",
        Query      = q,
        Total      = resultados.Count,
        Resultados = resultados
    });
})
.WithName("SearchText")
.WithOpenApi();

// GET /api/search/semantic?vibe=quiero algo para bailar
// Búsqueda semántica usando Qdrant (búsqueda por intención/significado)
app.MapGet("/api/search/semantic", async (
    string vibe,
    IHttpClientFactory factory,
    ILogger<Program> logger) =>
{
    logger.LogInformation("[QDRANT] Búsqueda semántica: '{Vibe}'", vibe);

    // En producción: llamaríamos a un modelo de embeddings (OpenAI, Ollama, etc.)
    // para convertir el texto en un vector. Aquí simulamos con un vector fijo.
    var embeddings = GenerarEmbeddingSimulado(vibe);

    var qdrantClient = factory.CreateClient("QdrantClient");

    // Llamada a Qdrant REST API: búsqueda por similitud de vectores
    var searchRequest = new
    {
        vector = embeddings,
        limit  = 5,
        with_payload = true
    };

    try
    {
        var response = await qdrantClient.PostAsJsonAsync(
            "/collections/festival-eventos/points/search",
            searchRequest
        );

        if (response.IsSuccessStatusCode)
        {
            var resultado = await response.Content.ReadFromJsonAsync<QdrantSearchResponse>();
            return Results.Ok(new
            {
                Motor      = "Qdrant IA 🧠",
                Vibe       = vibe,
                Resultados = resultado?.Result ?? []
            });
        }
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "[QDRANT] No disponible, usando fallback");
    }

    // Fallback si Qdrant no está disponible: recomendaciones basadas en keywords
    var fallback = RecomendacionesPorKeywords(vibe);
    return Results.Ok(new
    {
        Motor      = "Fallback Keywords 🔤",
        Vibe       = vibe,
        Nota       = "Qdrant no disponible. Usando análisis de palabras clave.",
        Resultados = fallback
    });
})
.WithName("SearchSemantic")
.WithOpenApi();

// POST /api/search/index → Admin: re-indexar eventos en Elasticsearch
app.MapPost("/api/search/index", async (EventoIndexDto evento, ElasticsearchClient elastic) =>
{
    var response = await elastic.IndexAsync(evento, i => i
        .Index("festival-eventos")
        .Id(evento.EventId.ToString())
    );

    return response.IsValidResponse
        ? Results.Ok(new { Message = "Evento indexado en Elasticsearch", Id = evento.EventId })
        : Results.Problem("Error al indexar");
});

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Search.Api" }));

app.Run();

// =============================================================================
// FUNCIONES AUXILIARES
// =============================================================================

static async Task InicializarElasticsearchAsync(ElasticsearchClient elastic)
{
    // Crear el índice con mapping optimizado para búsqueda en español
    var indexExists = await elastic.Indices.ExistsAsync("festival-eventos");
    if (!indexExists.Exists)
    {
        await elastic.Indices.CreateAsync("festival-eventos", c => c
            .Settings(s => s
                .Analysis(a => a
                    .Analyzers(an => an
                        .Custom("spanish_analyzer", ca => ca
                            .Tokenizer("standard")
                            .Filter(["lowercase", "spanish_stop", "spanish_stemmer"])
                        )
                    )
                    .TokenFilters(tf => tf
                        .Stop("spanish_stop", sf => sf.Stopwords("_spanish_"))
                        .Stemmer("spanish_stemmer", sf => sf.Language("spanish"))
                    )
                )
            )
        );

        // Sembrar datos iniciales
        var eventos = new List<EventoIndexDto>
        {
            new(1, "Festival Rock Medellín", "El mayor festival de rock latinoamericano", "Rock", "Medellin", new[] { "Foo Fighters", "Metallica", "Pearl Jam" }, 350_000m, "COP"),
            new(1, "Festival Rock Madrid", "Rock en vivo en el Wanda Metropolitano", "Rock", "Madrid", new[] { "Foo Fighters", "Metallica" }, 120m, "EUR"),
            new(2, "Electronic Nights Medellín", "La mejor electrónica para bailar toda la noche", "Electrónica", "Medellin", new[] { "Martin Garrix", "Tiësto" }, 200_000m, "COP"),
            new(2, "Electronic Nights Madrid", "Techno y house en el Palacio Vistalegre", "Electrónica", "Madrid", new[] { "Carl Cox", "David Guetta" }, 80m, "EUR"),
            new(3, "Jazz & Soul Medellín", "Una noche íntima para reflexionar y sentir", "Jazz", "Medellin", new[] { "Norah Jones", "Gregory Porter" }, 180_000m, "COP"),
        };

        foreach (var e in eventos)
            await elastic.IndexAsync(e, i => i.Index("festival-eventos").Id(e.EventId.ToString()));
    }
}

static float[] GenerarEmbeddingSimulado(string texto)
{
    // En producción: llamar a OpenAI embeddings API o Ollama local
    // Aquí generamos un vector pseudo-aleatorio basado en el hash del texto
    // para que sea determinista (mismo texto = mismo vector)
    var hash = System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(texto));
    var vector = new float[384]; // Dimensión típica de modelos de embeddings
    for (var i = 0; i < vector.Length; i++)
        vector[i] = (hash[i % hash.Length] - 128f) / 128f;
    return vector;
}

static List<object> RecomendacionesPorKeywords(string vibe)
{
    // Fallback simple: análisis de palabras clave cuando Qdrant no está disponible
    var vibeL = vibe.ToLower();
    if (vibeL.Contains("bailar") || vibeL.Contains("electr") || vibeL.Contains("dance"))
        return [new { EventId = 2, Nombre = "Electronic Nights", Score = 0.92, Razon = "Música electrónica para bailar" }];
    if (vibeL.Contains("rock") || vibeL.Contains("guitar") || vibeL.Contains("metal"))
        return [new { EventId = 1, Nombre = "Festival Rock", Score = 0.95, Razon = "Rock en vivo" }];
    if (vibeL.Contains("jazz") || vibeL.Contains("reflej") || vibeL.Contains("tranquil"))
        return [new { EventId = 3, Nombre = "Jazz & Soul", Score = 0.88, Razon = "Música para relajarse y reflexionar" }];

    return [new { EventId = 1, Nombre = "Festival Rock (default)", Score = 0.5, Razon = "Recomendación general" }];
}

// DTOs de búsqueda
public record EventoIndexDto(
    int EventId,
    string Nombre,
    string Descripcion,
    string Genero,
    string Sede,
    string[] Artistas,
    decimal PrecioDesde,
    string Moneda
);

public record QdrantSearchResponse(List<QdrantResult> Result);
public record QdrantResult(string Id, float Score, Dictionary<string, object> Payload);
