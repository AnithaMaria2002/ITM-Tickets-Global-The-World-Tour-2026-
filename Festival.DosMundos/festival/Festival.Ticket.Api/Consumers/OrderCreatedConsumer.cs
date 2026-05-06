using Festival.Shared.Events;
using Festival.Ticket.Api.Hubs;
using MassTransit;
using Microsoft.AspNetCore.SignalR;

namespace Festival.Ticket.Api.Consumers;

/// <summary>
/// Consumer de MassTransit: escucha OrderCreatedEvent desde RabbitMQ.
///
/// Patrón: Productora (Order.Api) → Cola RabbitMQ → Consumidora (este Consumer)
///
/// Ventaja: Si Ticket.Api está caído, RabbitMQ guarda el mensaje.
/// Cuando vuelve a levantarse, procesa la cola. NUNCA se pierde una boleta.
/// </summary>
public class OrderCreatedConsumer : IConsumer<OrderCreatedEvent>
{
    private readonly IHubContext<BoletaHub> _hubContext;
    private readonly IPublishEndpoint _bus;
    private readonly ILogger<OrderCreatedConsumer> _logger;

    public OrderCreatedConsumer(
        IHubContext<BoletaHub> hubContext,
        IPublishEndpoint bus,
        ILogger<OrderCreatedConsumer> logger)
    {
        _hubContext  = hubContext;
        _bus         = bus;
        _logger      = logger;
    }

    public async Task Consume(ConsumeContext<OrderCreatedEvent> context)
    {
        var evento = context.Message;

        _logger.LogInformation("[TICKET] Generando boleta para OrderId={OrderId} | " +
                               "Email={Email} | Sede={Sede}",
                               evento.OrderId, evento.CustomerEmail, evento.Sede);

        // Simulación de generación de boleta (en la vida real: PDF, QR, etc.)
        await Task.Delay(2000); // 2 segundos de "procesamiento"

        var ticketId  = Guid.NewGuid();
        var codigoQr  = GenerarCodigoQr(evento.OrderId, evento.EventId, evento.Sede);

        var ticketGenerado = new TicketGeneratedEvent(
            OrderId:       evento.OrderId,
            TicketId:      ticketId,
            CustomerEmail: evento.CustomerEmail,
            CodigoQr:      codigoQr,
            Sede:          evento.Sede,
            GeneratedAt:   DateTime.UtcNow
        );

        // PASO 1: Notificar al usuario vía SignalR en tiempo real
        // El MAUI recibirá este mensaje sin hacer polling
        await _hubContext.Clients
            .Group(evento.CustomerEmail)  // Solo le llega al usuario dueño de la boleta
            .SendAsync("BoletaLista", new
            {
                ticketGenerado.OrderId,
                ticketGenerado.TicketId,
                ticketGenerado.CodigoQr,
                ticketGenerado.Sede,
                Cantidad    = evento.Quantity,
                TotalPagado = evento.TotalAmount,
                Mensaje     = $"🎉 ¡Tu boleta para el Festival de los Dos Mundos en {evento.Sede} está lista!"
            });

        // PASO 2: Publicar TicketGeneratedEvent para auditoría/email
        await _bus.Publish(ticketGenerado);

        _logger.LogInformation("[TICKET GENERADO] TicketId={TicketId} | QR={QR}",
                               ticketId, codigoQr);
    }

    private static string GenerarCodigoQr(Guid orderId, int eventId, string sede)
    {
        // En la vida real: QR Code con firma criptográfica para evitar falsificaciones
        var hash = Convert.ToBase64String(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{orderId}{eventId}{sede}")
            )
        )[..12];  // Tomamos los primeros 12 caracteres

        return $"FDM-{sede.ToUpper()[..3]}-{eventId:D3}-{hash}";
    }
}
