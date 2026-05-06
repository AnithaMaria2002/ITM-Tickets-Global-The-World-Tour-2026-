using System.Text;
using System.Net.Http.Json;
using Festival.Order.Api.Handlers;
using Festival.Shared.Events;
using Festival.Inventory.Api.Protos;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi.Models;

// =============================================================================
// FESTIVAL DE LOS DOS MUNDOS - Order.Api
// Orquestador central de la SAGA de compra de boletas
//
// Flujo completo de una compra:
// 1. Cliente MAUI → Gateway (JWT) → Order.Api
// 2. Order.Api → Inventory.Api vía gRPC (¿hay boletas?)
// 3. Order.Api → Price.Api vía HTTP (¿cuánto cuesta?)
// 4. Order.Api procesa el pago
// 5. Si OK → publica OrderCreatedEvent en RabbitMQ
// 6. Ticket.Api consume el evento → genera la boleta → publica TicketGeneratedEvent
// 7. SignalR notifica al MAUI: "¡Tu boleta está lista!"
//
// Si ALGO falla en el paso 3 o 4 → COMPENSACIÓN SAGA → se devuelven las boletas
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// --- 1. ZONA DE SERVICIOS ---
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Festival Order API - SAGA Orchestrator",
        Version = "v1",
        Description = "Gestión de transacciones de boletas con patrón SAGA"
    });
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization", Type = SecuritySchemeType.Http,
        Scheme = "Bearer", BearerFormat = "JWT", In = ParameterLocation.Header
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
            ValidateIssuer = true, ValidIssuer = jwtSettings["Issuer"],
            ValidateAudience = true, ValidAudience = jwtSettings["Audience"],
            ValidateLifetime = true, ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(secretKey)
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddTransient<CorrelationIdDelegatingHandler>();

// Cliente gRPC → Inventory.Api (comunicación binaria de alta velocidad)
// ¿Por qué gRPC? Serialización binaria 10x más rápida que JSON en 50k usuarios concurrentes
builder.Services.AddGrpcClient<InventoryService.InventoryServiceClient>(o =>
{
    o.Address = new Uri(builder.Configuration["GrpcEndpoints:Inventory"] ?? "http://localhost:5101");
});

// Cliente HTTP → Price.Api (con resiliencia: reintentos + circuit breaker)
builder.Services
    .AddHttpClient("PriceClient", client =>
    {
        client.BaseAddress = new Uri(builder.Configuration["HttpEndpoints:Price"] ?? "http://localhost:5200");
        client.Timeout = TimeSpan.FromSeconds(5);
    })
    .AddHttpMessageHandler<CorrelationIdDelegatingHandler>()
    .AddStandardResilienceHandler();

// MassTransit + RabbitMQ: Mensajería asíncrona
// Publica OrderCreatedEvent → Ticket.Api lo consume en background
// Si RabbitMQ se cae, el mensaje espera en la cola (no se pierde)
builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((context, cfg) =>
    {
        // En Docker: rabbitmq:5672 | En local: localhost:5672
        // En CloudAMQP: amqps://user:pass@host/vhost
        var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMQ:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMQ:Password"] ?? "guest");
        });
    });
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}
app.UseAuthentication();
app.UseAuthorization();

// --- 2. ENDPOINT PRINCIPAL: POST /api/orders ---
// La SAGA orquestada completa: acción → compensación si falla
app.MapPost("/api/orders", async (
    CreateOrderDto order,
    InventoryService.InventoryServiceClient grpcInventory,
    IHttpClientFactory factory,
    IPublishEndpoint bus,           // MassTransit: publica eventos a RabbitMQ
    HttpContext httpContext,
    ILogger<Program> logger) =>
{
    // Generamos un ID único para trazabilidad de extremo a extremo
    var orderId = Guid.NewGuid();
    var correlationId = httpContext.Request.Headers["X-Correlation-ID"].FirstOrDefault()
                        ?? orderId.ToString();

    logger.LogInformation("[SAGA INICIO] OrderId={OrderId} | CorrelationId={CorrelationId} | " +
                          "EventId={EventId} | Sede={Sede} | Qty={Qty}",
                          orderId, correlationId, order.EventId, order.Sede, order.Quantity);

    // =========================================================================
    // PASO 1: RESERVAR BOLETAS vía gRPC (acción de la SAGA)
    // Si esto falla → abortamos. No hay nada que compensar aún.
    // =========================================================================
    ReserveResponse reservaGrpc;
    try
    {
        reservaGrpc = await grpcInventory.ReserveStockAsync(new ReserveRequest
        {
            EventId       = order.EventId,
            Sede          = order.Sede,
            Quantity      = order.Quantity,
            CorrelationId = correlationId
        });

        if (!reservaGrpc.Success)
        {
            logger.LogWarning("[SAGA ABORT] Sin stock disponible. OrderId={OrderId}", orderId);
            return Results.BadRequest(new
            {
                Error            = "No hay boletas disponibles para esta sede",
                BoletasRestantes = reservaGrpc.BoletasRestantes
            });
        }
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "[SAGA ERROR] Inventory.Api no responde. OrderId={OrderId}", orderId);
        return Results.Problem("El servicio de inventario no está disponible. Intente más tarde.");
    }

    // A partir de aquí: YA RESTAMOS LAS BOLETAS → necesitamos compensación si algo falla
    try
    {
        // =======================================================================
        // PASO 2: CONSULTAR PRECIO vía HTTP + Redis Cache
        // =======================================================================
        var priceClient = factory.CreateClient("PriceClient");
        priceClient.DefaultRequestHeaders.Add("X-Correlation-ID", correlationId);

        var precioResponse = await priceClient.GetAsync(
            $"/api/prices/{order.EventId}/{order.Sede}/{order.Categoria}");

        if (!precioResponse.IsSuccessStatusCode)
            throw new InvalidOperationException($"No se pudo obtener el precio: {precioResponse.StatusCode}");

        var precio = await precioResponse.Content.ReadFromJsonAsync<PrecioDto>();
        if (precio is null) throw new InvalidOperationException("Precio no encontrado");

        var totalAPagar = precio.Precio * order.Quantity;

        // =======================================================================
        // PASO 3: PROCESAR PAGO (simulación)
        // En la vida real: llamada a pasarela de pagos (Stripe, Wompi, PayU)
        // =======================================================================
        var random = new Random();
        var pagoExitoso = random.Next(0, 10) > 2; // 70% éxito para demostrar SAGA

        if (!pagoExitoso)
            throw new InvalidOperationException("Fondos insuficientes o tarjeta rechazada.");

        // =======================================================================
        // PASO 4: PUBLICAR EVENTO → RabbitMQ → Ticket.Api genera la boleta
        // Esto es asíncrono: Order.Api no espera a que la boleta se genere.
        // El cliente recibirá la notificación vía SignalR cuando esté lista.
        // =======================================================================
        var customerEmail = httpContext.User.Claims
            .FirstOrDefault(c => c.Type == System.Security.Claims.ClaimTypes.Email)?.Value
            ?? order.CustomerEmail;

        await bus.Publish(new OrderCreatedEvent(
            OrderId:       orderId,
            EventId:       order.EventId,
            Sede:          order.Sede,
            CustomerEmail: customerEmail,
            Quantity:      order.Quantity,
            TotalAmount:   totalAPagar,
            CreatedAt:     DateTime.UtcNow
        ));

        logger.LogInformation("[SAGA EXITOSA] OrderId={OrderId} | Total={Total} {Moneda}",
                              orderId, totalAPagar, precio.Moneda);

        return Results.Accepted($"/api/orders/{orderId}", new
        {
            OrderId      = orderId,
            Status       = "Procesando",
            Message      = "¡Pago exitoso! Tu boleta se está generando. Recibirás una notificación en segundos.",
            TotalPagado  = totalAPagar,
            Moneda       = precio.Moneda,
            FuentePrecio = precio.FuenteDatos  // Demuestra si el precio vino de Redis o SQL
        });
    }
    catch (Exception ex)
    {
        // =======================================================================
        // COMPENSACIÓN SAGA: El pago o el precio fallaron.
        // Devolvemos las boletas que ya habíamos reservado.
        // =======================================================================
        logger.LogError(ex, "[COMPENSACIÓN SAGA] Fallando. Devolviendo boletas. OrderId={OrderId}", orderId);

        try
        {
            await grpcInventory.ReleaseStockAsync(new ReleaseRequest
            {
                EventId       = order.EventId,
                Sede          = order.Sede,
                Quantity      = order.Quantity,
                CorrelationId = correlationId
            });

            // También publicamos evento de cancelación para auditoría
            await bus.Publish(new OrderCancelledEvent(
                OrderId:      orderId,
                EventId:      order.EventId,
                Quantity:     order.Quantity,
                Reason:       ex.Message,
                CancelledAt:  DateTime.UtcNow
            ));

            return Results.Problem($"El pago falló: {ex.Message}. Las boletas fueron devueltas. Intente de nuevo.");
        }
        catch (Exception compensacionEx)
        {
            // Peor escenario: falló el pago Y falló la compensación
            logger.LogCritical(compensacionEx,
                "[CRITICAL] Fallo de compensación. Datos inconsistentes. OrderId={OrderId}", orderId);
            return Results.Problem("Error crítico del sistema. Contacte soporte urgente.");
        }
    }
})
.RequireAuthorization()
.WithName("CreateOrder")
.WithOpenApi();

// GET /api/orders/{orderId} → Estado de la orden (polling)
app.MapGet("/api/orders/{orderId}", (Guid orderId) =>
{
    // En la vida real: consultar DB. Aquí simulamos.
    return Results.Ok(new { OrderId = orderId, Status = "Procesando", Message = "Escucha el canal SignalR para actualización en tiempo real." });
})
.RequireAuthorization();

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Order.Api" }));

app.Run();

// DTOs locales de la orden
public record CreateOrderDto(int EventId, string Sede, string Categoria, int Quantity, string CustomerEmail);
public record PrecioDto(int EventId, string Sede, string Categoria, decimal Precio, string Moneda, string FuenteDatos);
