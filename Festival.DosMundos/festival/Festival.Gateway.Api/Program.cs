using System.Text;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;

// =============================================================================
// FESTIVAL DE LOS DOS MUNDOS - Gateway.Api
// La "Frontera" del sistema: punto único de entrada para todos los clientes.
//
// Responsabilidades:
// 1. ENRUTAMIENTO: Recibe peticiones y las redirige al microservicio correcto.
// 2. SEGURIDAD PERIMETRAL: Valida el JWT ANTES de dejar pasar el tráfico.
//    → Los microservicios internos no necesitan validar JWT individualmente.
// 3. RATE LIMITING: Máximo 50 peticiones/minuto por IP.
//    → Mitigación de ataques de fuerza bruta en el minuto cero de venta.
// 4. PROPAGACIÓN DE CORRELATION ID: Para trazabilidad distribuida.
//
// Arquitectura: MAUI → HTTPS → [GATEWAY] → HTTP → Microservicios internos
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// --- 1. YARP: El cerebro del enrutamiento ---
// Lee la configuración de rutas y clusters desde appsettings.json
builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

// --- 2. JWT: El portero de seguridad ---
// El Gateway valida el token UNA VEZ aquí.
// Los microservicios internos confían en que el Gateway ya validó.
var jwtSettings = builder.Configuration.GetSection("JwtSettings");
var secretKey = Encoding.UTF8.GetBytes(jwtSettings["SecretKey"]!);

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer           = true,
            ValidIssuer              = jwtSettings["Issuer"],
            ValidateAudience         = true,
            ValidAudience            = jwtSettings["Audience"],
            ValidateLifetime         = true,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey         = new SymmetricSecurityKey(secretKey)
        };

        // Soporte para SignalR: el token también puede venir en el query string
        // (?access_token=...) porque WebSockets no soporta headers HTTP
        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                var path = context.HttpContext.Request.Path;
                if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/hubs"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });

builder.Services.AddAuthorization();

// --- 3. RATE LIMITING: El control de multitudes ---
// En el minuto cero, 50k usuarios atacan. Sin esto, el servidor colapsa.
builder.Services.AddRateLimiter(options =>
{
    // Política 1: Límite fijo - 50 req/minuto por IP
    // Ideal para endpoints de compra (evita bots y scalpers)
    options.AddFixedWindowLimiter("CompraPolicy", o =>
    {
        o.PermitLimit        = 50;
        o.Window             = TimeSpan.FromMinutes(1);
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit         = 10; // Máximo 10 en espera, el resto → 429
    });

    // Política 2: Sliding Window - 100 req/minuto para búsquedas (menos críticas)
    options.AddSlidingWindowLimiter("BusquedaPolicy", o =>
    {
        o.PermitLimit        = 100;
        o.Window             = TimeSpan.FromMinutes(1);
        o.SegmentsPerWindow  = 4; // Ventana dividida en 4 segmentos de 15s
        o.QueueProcessingOrder = QueueProcessingOrder.OldestFirst;
        o.QueueLimit         = 20;
    });

    // Respuesta cuando se supera el límite
    options.OnRejected = async (context, token) =>
    {
        context.HttpContext.Response.StatusCode = 429;
        context.HttpContext.Response.Headers["Retry-After"] = "60";
        await context.HttpContext.Response.WriteAsJsonAsync(new
        {
            Error   = "Demasiadas peticiones. El sistema está procesando miles de solicitudes. Intente en 60 segundos.",
            Codigo  = 429,
            RetryIn = "60 segundos"
        }, token);
    };
});

var app = builder.Build();

// --- 2. ZONA DE MIDDLEWARE (El orden importa mucho) ---

// Rate Limiting primero: bloqueamos antes de autenticar (ahorra recursos)
app.UseRateLimiter();

// Autenticación y autorización
app.UseAuthentication();
app.UseAuthorization();

// Middleware de Correlation ID: genera uno si no viene, lo propaga siempre
app.Use(async (context, next) =>
{
    var correlationId = context.Request.Headers["X-Correlation-ID"].FirstOrDefault()
                        ?? Guid.NewGuid().ToString("N")[..16];

    context.Request.Headers["X-Correlation-ID"]  = correlationId;
    context.Response.Headers["X-Correlation-ID"] = correlationId;

    await next();
});

// YARP: el proxy inverso que enruta al microservicio correcto
app.MapReverseProxy();

// Health check del propio Gateway
app.MapGet("/health", () => Results.Ok(new
{
    Status  = "Healthy",
    Service = "Festival.Gateway.Api",
    Version = "1.0.0"
}));

app.Run();
