namespace Festival.Shared.Events;

// Usamos 'record' porque los eventos son hechos históricos INMUTABLES.
// Lo que pasó en el Festival, pasó. No podemos editar la realidad.

/// <summary>
/// Evento disparado cuando una orden de boleta es creada exitosamente.
/// Viaja por RabbitMQ para que Ticket.Api genere la boleta en segundo plano.
/// </summary>
public record OrderCreatedEvent(
    Guid OrderId,
    int EventId,
    string Sede,           // "Medellin" | "Madrid"
    string CustomerEmail,
    int Quantity,
    decimal TotalAmount,
    DateTime CreatedAt
);

/// <summary>
/// Evento disparado cuando la boleta ya fue generada por Ticket.Api.
/// Viaja por RabbitMQ → SignalR notifica al MAUI en tiempo real.
/// </summary>
public record TicketGeneratedEvent(
    Guid OrderId,
    Guid TicketId,
    string CustomerEmail,
    string CodigoQr,       // Código único de la boleta
    string Sede,
    DateTime GeneratedAt
);

/// <summary>
/// Evento de compensación SAGA: la orden fue cancelada por fallo en pago o inventario.
/// </summary>
public record OrderCancelledEvent(
    Guid OrderId,
    int EventId,
    int Quantity,
    string Reason,
    DateTime CancelledAt
);
