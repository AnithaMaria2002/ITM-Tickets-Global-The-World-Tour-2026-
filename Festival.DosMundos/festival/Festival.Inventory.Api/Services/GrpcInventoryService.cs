using Grpc.Core;
using Festival.Inventory.Api.Protos;
using Castle.Core.Internal;

namespace Festival.Inventory.Api.Services;

/// <summary>
/// Servicio gRPC: implementa la comunicación binaria de alta velocidad.
/// 
/// ¿Por qué gRPC aquí y no HTTP?
/// - En el minuto cero de venta, 50,000 usuarios llaman a este servicio.
/// - gRPC usa Protocol Buffers (binario), no JSON (texto).
/// - Es ~10x más rápido en serialización y ~5x menor consumo de ancho de banda.
/// - Es la "línea directa" entre Order.Api e Inventory.Api.
/// </summary>
public class GrpcInventoryService : InventoryService.InventoryServiceBase
{
    // Simulación de BD en memoria con lock para thread-safety en 50k usuarios concurrentes.
    // En la vida real: SQL Server + Redis con SETNX para garantía de unicidad.
    private static readonly Dictionary<string, int> _inventario = new()
    {
        { "1_Medellin", 25000 },  // 25,000 boletas para Medellín
        { "1_Madrid",   25000 },  // 25,000 boletas para Madrid
        { "2_Medellin", 5000 },
        { "2_Madrid",   5000 },
    };

    private static readonly Lock _lock = new(); // Lock de .NET 9 / C# 13 pattern

    public override Task<StockResponse> CheckStock(StockRequest request, ServerCallContext context)
    {
        var key = $"{request.EventId}_{request.Sede}";

        lock (_lock)
        {
            var disponibles = _inventario.GetValueOrDefault(key, 0);
            return Task.FromResult(new StockResponse
            {
                EventId = request.EventId,
                BoletasDisponibles = disponibles,
                IsAvailable = disponibles > 0,
                Sede = request.Sede
            });
        }
    }

    public override Task<ReserveResponse> ReserveStock(ReserveRequest request, ServerCallContext context)
    {
        var key = $"{request.EventId}_{request.Sede}";

        lock (_lock)
        {
            var disponibles = _inventario.GetValueOrDefault(key, 0);

            if (disponibles < request.Quantity)
            {
                return Task.FromResult(new ReserveResponse
                {
                    Success = false,
                    BoletasRestantes = disponibles,
                    Message = $"No hay suficientes boletas. Disponibles: {disponibles}"
                });
            }

            _inventario[key] = disponibles - request.Quantity;

            Console.WriteLine($"[RESERVA gRPC] CorrelationId={request.CorrelationId} | " +
                              $"Evento={request.EventId} | Sede={request.Sede} | " +
                              $"Cantidad={request.Quantity} | Restantes={_inventario[key]}");

            return Task.FromResult(new ReserveResponse
            {
                Success = true,
                BoletasRestantes = _inventario[key],
                Message = "Boletas reservadas exitosamente"
            });
        }
    }

    public override Task<ReleaseResponse> ReleaseStock(ReleaseRequest request, ServerCallContext context)
    {
        var key = $"{request.EventId}_{request.Sede}";

        lock (_lock)
        {
            var actuales = _inventario.GetValueOrDefault(key, 0);
            _inventario[key] = actuales + request.Quantity;

            // Igual que en clase: el [COMPENSACIÓN] es el Ctrl+Z del inventario
            Console.WriteLine($"[COMPENSACIÓN gRPC] CorrelationId={request.CorrelationId} | " +
                              $"Se devolvieron {request.Quantity} boletas. " +
                              $"Nuevo stock: {_inventario[key]}");

            return Task.FromResult(new ReleaseResponse
            {
                Success = true,
                BoletasRestantes = _inventario[key]
            });
        }
    }
}
