namespace Festival.Store.Mobile.Services;

/// <summary>
/// DelegatingHandler: El "Peaje" automático de JWT.
/// Se ejecuta antes de CADA petición HTTP, igual que en la clase del profe.
/// Agrega el token JWT del SecureStorage al header Authorization.
/// </summary>
public class AuthHandler : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 1. Buscamos el token en la bóveda criptográfica del celular
        var token = await SecureStorage.Default.GetAsync("jwt_token");

        // 2. Si hay token, lo pegamos a la petición automáticamente
        if (!string.IsNullOrEmpty(token))
            request.Headers.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);

        // 3. Continuamos con la petición hacia el Gateway
        return await base.SendAsync(request, cancellationToken);
    }
}
