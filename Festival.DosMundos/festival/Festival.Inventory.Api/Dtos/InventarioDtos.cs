namespace Festival.Inventory.Api.Dtos;

// Usamos 'record' igual que en clase: inmutabilidad + semántica de valor
public record BoletaInventarioDto(int EventId, string Sede, int BoletasDisponibles, int BoletasVendidas);

public record ReservarBoletaDto(int EventId, string Sede, int Quantity);
