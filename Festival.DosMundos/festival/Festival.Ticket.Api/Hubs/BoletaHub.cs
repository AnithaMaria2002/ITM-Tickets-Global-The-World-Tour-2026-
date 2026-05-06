using Microsoft.AspNetCore.SignalR;

namespace Festival.Ticket.Api.Hubs;

/// <summary>
/// Hub de SignalR: canal de comunicación en tiempo real entre el servidor y la App MAUI.
///
/// Flujo:
/// 1. MAUI se conecta a este Hub al iniciar.
/// 2. Cuando Ticket.Api genera la boleta, llama a "BoletaLista" en el cliente específico.
/// 3. MAUI muestra la notificación sin necesidad de hacer polling.
///
/// ¿Por qué SignalR y no polling?
/// - Polling: el MAUI pregunta cada 2s "¿ya está mi boleta?" → desperdicia red.
/// - SignalR (WebSocket): el servidor AVISA cuando está lista. Más eficiente.
/// </summary>
public class BoletaHub : Hub
{
    private readonly ILogger<BoletaHub> _logger;

    public BoletaHub(ILogger<BoletaHub> logger)
    {
        _logger = logger;
    }

    public override async Task OnConnectedAsync()
    {
        _logger.LogInformation("[SIGNALR] Cliente conectado: {ConnectionId}", Context.ConnectionId);
        // Enviamos una confirmación de conexión al cliente
        await Clients.Caller.SendAsync("Conectado", new
        {
            ConnectionId = Context.ConnectionId,
            Message = "Conectado al servicio de boletas en tiempo real. Esperando confirmación..."
        });
        await base.OnConnectedAsync();
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        _logger.LogInformation("[SIGNALR] Cliente desconectado: {ConnectionId}", Context.ConnectionId);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// El MAUI llama a este método para "registrarse" con su email,
    /// así podemos notificarle individualmente cuando su boleta esté lista.
    /// </summary>
    public async Task RegistrarEmail(string email)
    {
        // Agregamos la conexión a un grupo con el nombre del email del usuario
        await Groups.AddToGroupAsync(Context.ConnectionId, email);
        _logger.LogInformation("[SIGNALR] Email {Email} registrado para notificaciones", email);
    }
}
