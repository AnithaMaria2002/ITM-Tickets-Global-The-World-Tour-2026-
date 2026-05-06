using Festival.Ticket.Api.Consumers;
using Festival.Ticket.Api.Hubs;
using MassTransit;
using Microsoft.AspNetCore.SignalR;

// =============================================================================
// FESTIVAL DE LOS DOS MUNDOS - Ticket.Api
// Generador de boletas en segundo plano + Notificador en tiempo real
//
// Este servicio hace DOS cosas:
// 1. Consume mensajes de RabbitMQ (OrderCreatedEvent) → genera la boleta
// 2. Notifica al MAUI vía SignalR cuando la boleta está lista
// =============================================================================

var builder = WebApplication.CreateBuilder(args);

// --- 1. ZONA DE SERVICIOS ---

// SignalR: el canal de tiempo real hacia el MAUI
builder.Services.AddSignalR();

// MassTransit + RabbitMQ: consumidor de eventos
builder.Services.AddMassTransit(x =>
{
    // Registramos el Consumer (el que "escucha" y procesa los mensajes)
    x.AddConsumer<OrderCreatedConsumer>();

    x.UsingRabbitMq((context, cfg) =>
    {
        var rabbitHost = builder.Configuration["RabbitMQ:Host"] ?? "localhost";
        cfg.Host(rabbitHost, "/", h =>
        {
            h.Username(builder.Configuration["RabbitMQ:Username"] ?? "guest");
            h.Password(builder.Configuration["RabbitMQ:Password"] ?? "guest");
        });

        // Cola exclusiva de Ticket.Api para recibir OrderCreatedEvent
        cfg.ReceiveEndpoint("festival-ticket-queue", e =>
        {
            e.ConfigureConsumer<OrderCreatedConsumer>(context);

            // Política de reintentos: si falla, reintenta 3 veces con 5s de espera
            e.UseMessageRetry(r => r.Intervals(
                TimeSpan.FromSeconds(5),
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(30)
            ));
        });
    });
});

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// --- 2. MAPEAR EL HUB DE SIGNALR ---
// El MAUI se conectará a ws://gateway/hubs/boletas (enrutado por YARP)
app.MapHub<BoletaHub>("/hubs/boletas");

// Health check para Kubernetes
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Ticket.Api" }));

// Endpoint de prueba: simula una notificación SignalR manual (útil en clase)
app.MapPost("/api/tickets/test-notify/{email}", async (
    string email,
    Microsoft.AspNetCore.SignalR.IHubContext<BoletaHub> hub) =>
{
    await hub.Clients.Group(email).SendAsync("BoletaLista", new
    {
        OrderId   = Guid.NewGuid(),
        TicketId  = Guid.NewGuid(),
        CodigoQr  = "FDM-MED-001-TEST1234",
        Sede      = "Medellin",
        Mensaje   = "🎉 Boleta de prueba generada vía SignalR"
    });
    return Results.Ok(new { Message = $"Notificación enviada a {email}" });
});

app.Run();
