using System.Net.Http.Json;
using System.Text.Json;
using Festival.Store.Mobile.Services;

namespace Festival.Store.Mobile;

public partial class MainPage : ContentPage
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SignalRService _signalR;

    // Inyectamos igual que en el backend (DI)
    public MainPage(IHttpClientFactory httpClientFactory, SignalRService signalR)
    {
        InitializeComponent();
        _httpClientFactory = httpClientFactory;
        _signalR           = signalR;

        // Suscribimos el handler de SignalR: cuando llegue la boleta, la mostramos
        _signalR.BoletaRecibida += OnBoletaRecibida;
    }

    // =========================================================================
    // LOGIN: Guarda el JWT en SecureStorage (igual que en clase)
    // =========================================================================
    private async void OnLoginClicked(object sender, EventArgs e)
    {
        // Token de prueba generado en jwt.io con:
        // iss=FestivalIdentityServer, aud=FestivalApis, role=Administrador
        // Firmado con "Festival-Dos-Mundos-Super-Secret-Key-2026-ITM-Nivel5"
        // En producción: POST /api/auth/login con user/password
        string simulatedToken = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9." +
            "eyJpc3MiOiJGZXN0aXZhbElkZW50aXR5U2VydmVyIiwiYXVkIjoiRmVzdGl2YWxBcGlzIiwiZW1haWwiOiJhZG1pbkBmZXN0aXZhbC5jb20iLCJyb2xlIjoiQWRtaW5pc3RyYWRvciIsImV4cCI6OTk5OTk5OTk5OX0." +
            "FIRMA_PLACEHOLDER";

        await SecureStorage.Default.SetAsync("jwt_token", simulatedToken);

        var email = EmailEntry.Text?.Trim() ?? "admin@festival.com";

        // Conectamos SignalR con el email del usuario
        try
        {
            await _signalR.ConectarAsync(email);
            SignalRStatus.Text  = "🟢 Conectado a tiempo real";
            SignalRStatus.TextColor = Colors.LightGreen;
        }
        catch (Exception ex)
        {
            SignalRStatus.Text = $"⚠️ SignalR no disponible: {ex.Message}";
        }

        LoginStatus.Text      = $"✅ Autenticado como {email}";
        LoginStatus.TextColor = Colors.LightGreen;
    }

    // =========================================================================
    // BÚSQUEDA POR TEXTO (Elasticsearch)
    // =========================================================================
    private async void OnSearchTextClicked(object sender, EventArgs e)
    {
        var query = SearchEntry.Text?.Trim();
        if (string.IsNullOrEmpty(query)) { await DisplayAlert("Error", "Escribe algo para buscar", "OK"); return; }

        await EjecutarConLoading(async () =>
        {
            var client = _httpClientFactory.CreateClient("GatewayClient");
            var response = await client.GetAsync($"/api/search/text?q={Uri.EscapeDataString(query)}");

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadAsStringAsync();
                var resultado = JsonSerializer.Deserialize<JsonElement>(data);
                var total = resultado.GetProperty("total").GetInt32();
                var motor = resultado.GetProperty("motor").GetString();

                ResultLabel.Text      = $"🔍 {motor}\n✅ {total} evento(s) encontrado(s) para '{query}'";
                ResultLabel.TextColor = Colors.LightBlue;
            }
            else
            {
                ResultLabel.Text      = $"❌ Error: {response.StatusCode}";
                ResultLabel.TextColor = Colors.Red;
            }
        });
    }

    // =========================================================================
    // BÚSQUEDA SEMÁNTICA POR IA (Qdrant)
    // =========================================================================
    private async void OnSearchSemanticClicked(object sender, EventArgs e)
    {
        var vibe = VibeEntry.Text?.Trim();
        if (string.IsNullOrEmpty(vibe)) { await DisplayAlert("Error", "Cuéntanos tu vibe", "OK"); return; }

        await EjecutarConLoading(async () =>
        {
            var client = _httpClientFactory.CreateClient("GatewayClient");
            var response = await client.GetAsync($"/api/search/semantic?vibe={Uri.EscapeDataString(vibe)}");

            if (response.IsSuccessStatusCode)
            {
                var data = await response.Content.ReadAsStringAsync();
                var resultado = JsonSerializer.Deserialize<JsonElement>(data);
                var motor = resultado.GetProperty("motor").GetString();

                ResultLabel.Text      = $"🧠 {motor}\n✅ Recomendaciones para: '{vibe}'";
                ResultLabel.TextColor = Colors.Purple;
            }
            else
            {
                ResultLabel.Text      = $"❌ Error: {response.StatusCode}";
                ResultLabel.TextColor = Colors.Red;
            }
        });
    }

    // =========================================================================
    // COMPRAR BOLETA (SAGA completa)
    // =========================================================================
    private async void OnComprarClicked(object sender, EventArgs e)
    {
        if (EventoPicker.SelectedIndex < 0 || SedePicker.SelectedIndex < 0 || CategoriaPicker.SelectedIndex < 0)
        {
            await DisplayAlert("Campos requeridos", "Selecciona evento, sede y categoría", "OK");
            return;
        }

        var eventId    = EventoPicker.SelectedIndex + 1;
        var sede       = SedePicker.SelectedIndex == 0 ? "Medellin" : "Madrid";
        var categoria  = CategoriaPicker.SelectedItem?.ToString() ?? "General";
        var cantidad   = (int)CantidadStepper.Value;
        var email      = EmailEntry.Text?.Trim() ?? "admin@festival.com";

        await EjecutarConLoading(async () =>
        {
            LoadingIndicator.IsRunning = true;
            ResultLabel.Text      = $"⏳ Procesando compra vía SAGA + gRPC...\nEspera la notificación en tiempo real...";
            ResultLabel.TextColor = Colors.Orange;

            var client = _httpClientFactory.CreateClient("GatewayClient");
            var payload = new
            {
                EventId       = eventId,
                Sede          = sede,
                Categoria     = categoria,
                Quantity      = cantidad,
                CustomerEmail = email
            };

            var response = await client.PostAsJsonAsync("/api/orders", payload);
            var data     = await response.Content.ReadAsStringAsync();
            var json     = JsonSerializer.Deserialize<JsonElement>(data);

            if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Accepted)
            {
                ResultLabel.Text      = $"✅ ¡Pago procesado!\n{json.GetProperty("message").GetString()}\n\n📡 Escuchando SignalR para la boleta...";
                ResultLabel.TextColor = Colors.LightGreen;
            }
            else
            {
                var error = json.TryGetProperty("error", out var e) ? e.GetString() : data;
                ResultLabel.Text      = $"❌ {error}";
                ResultLabel.TextColor = Colors.Red;
                LoadingIndicator.IsRunning = false;
            }
        });
    }

    // =========================================================================
    // CALLBACK DE SIGNALR: Cuando llega la boleta, actualizamos la UI
    // =========================================================================
    private void OnBoletaRecibida(BoletaNotificacion boleta)
    {
        // MainThread: SignalR llega en un hilo de background, la UI solo se actualiza en el hilo principal
        MainThread.BeginInvokeOnMainThread(() =>
        {
            LoadingIndicator.IsRunning = false;
            BoletaFrame.IsVisible      = true;
            BoletaQr.Text              = $"📱 QR: {boleta.CodigoQr}";
            BoletaInfo.Text            = $"Sede: {boleta.Sede} | Cant: {boleta.Cantidad} | Total: {boleta.TotalPagado:C}";
            ResultLabel.Text           = boleta.Mensaje;
            ResultLabel.TextColor      = Colors.LightGreen;

            // Vibración del celular como confirmación (si el dispositivo lo soporta)
            Vibration.Default.Vibrate(TimeSpan.FromMilliseconds(500));
        });
    }

    private void OnCantidadChanged(object sender, ValueChangedEventArgs e)
    {
        CantidadLabel.Text = $"Cantidad: {(int)e.NewValue} boleta(s)";
    }

    // Helper: muestra loading mientras ejecuta la tarea
    private async Task EjecutarConLoading(Func<Task> tarea)
    {
        ComprarBtn.IsEnabled     = false;
        LoadingIndicator.IsRunning = true;
        try   { await tarea(); }
        finally
        {
            LoadingIndicator.IsRunning = false;
            ComprarBtn.IsEnabled     = true;
        }
    }

    protected override void OnDisappearing()
    {
        _signalR.BoletaRecibida -= OnBoletaRecibida;
        base.OnDisappearing();
    }
}
