using Festival.Store.Mobile.Services;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Logging;

namespace Festival.Store.Mobile;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
                fonts.AddFont("OpenSans-Semibold.ttf", "OpenSansSemibold");
            });

        // Igual que en clase: AuthHandler como "Peaje" automático de JWT
        builder.Services.AddTransient<AuthHandler>();

        // Cliente HTTP → API Gateway (mismo patrón de clase)
        // 10.0.2.2 = localhost del PC visto desde el emulador Android
        builder.Services.AddHttpClient("GatewayClient", client =>
        {
            client.BaseAddress = new Uri("http://10.0.2.2:8080");
            client.Timeout = TimeSpan.FromSeconds(30);
        })
        .AddHttpMessageHandler<AuthHandler>();

        // SignalR: conexión en tiempo real con Ticket.Api (vía Gateway)
        // El token va en el query string porque WebSockets no soporta headers
        builder.Services.AddSingleton<HubConnection>(sp =>
        {
            return new HubConnectionBuilder()
                .WithUrl("http://10.0.2.2:8080/hubs/boletas", options =>
                {
                    options.AccessTokenProvider = async () =>
                        await SecureStorage.Default.GetAsync("jwt_token");
                })
                .WithAutomaticReconnect([TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10)])
                .Build();
        });

        // Servicios de la app
        builder.Services.AddSingleton<SignalRService>();
        builder.Services.AddTransient<MainPage>();
        builder.Services.AddTransient<BuyTicketPage>();

#if DEBUG
        builder.Logging.AddDebug();
#endif

        return builder.Build();
    }
}
