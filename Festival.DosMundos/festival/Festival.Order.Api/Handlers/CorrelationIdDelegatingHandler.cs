namespace Festival.Order.Api.Handlers;

// Igual que en clase: interceptor que propaga el X-Correlation-ID
// de petición en petición, para trazabilidad distribuida.
public class CorrelationIdDelegatingHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public CorrelationIdDelegatingHandler(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var correlationId = _httpContextAccessor.HttpContext?
            .Request.Headers["X-Correlation-ID"]
            .FirstOrDefault();

        if (!string.IsNullOrEmpty(correlationId) && !request.Headers.Contains("X-Correlation-ID"))
            request.Headers.Add("X-Correlation-ID", correlationId);

        return await base.SendAsync(request, cancellationToken);
    }
}
