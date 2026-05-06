using Microsoft.AspNetCore.SignalR.Client;

namespace Festival.Store.Mobile.Services;

/// <summary>
/// Servicio singleton que mantiene la conexión SignalR activa.
/// Cuando Ticket.Api genera la boleta, este servicio recibe la notificación
/// y dispara el evento BoletaRecibida para que la UI lo muestre.
/// </summary>
public class SignalRService
{
    private readonly HubConnection _connection;

    /// <summary>
    /// Evento que dispara la UI cuando llega una boleta.
    /// La MainPage se suscribe a este evento.
    /// </summary>
    public event Action<BoletaNotificacion>? BoletaRecibida;

    public SignalRService(HubConnection connection)
    {
        _connection = connection;

        // Escuchamos el mensaje "BoletaLista" que envía Ticket.Api vía SignalR
        _connection.On<BoletaNotificacion>("BoletaLista", notificacion =>
        {
            BoletaRecibida?.Invoke(notificacion);
        });
    }

    public async Task ConectarAsync(string email)
    {
        if (_connection.State == HubConnectionState.Disconnected)
        {
            await _connection.StartAsync();
            // Registramos nuestro email para recibir solo NUESTRAS boletas
            await _connection.InvokeAsync("RegistrarEmail", email);
        }
    }

    public async Task DesconectarAsync()
    {
        if (_connection.State == HubConnectionState.Connected)
            await _connection.StopAsync();
    }

    public HubConnectionState Estado => _connection.State;
}

// DTO de la notificación que llega por SignalR
public record BoletaNotificacion(
    Guid OrderId,
    Guid TicketId,
    string CodigoQr,
    string Sede,
    int Cantidad,
    decimal TotalPagado,
    string Mensaje
);
