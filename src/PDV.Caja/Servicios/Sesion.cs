using System.Globalization;
using Microsoft.AspNetCore.SignalR.Client;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Caja.Servicios;

/// <summary>Estado global de la caja: configuración, usuario conectado, catálogo y hardware.</summary>
public static class Sesion
{
    private static HubConnection? _hub;
    private static IBascula? _bascula;
    private static bool _basculaCreada;

    public static ConfigCaja Config { get; private set; } = null!;
    public static ApiClient Api { get; private set; } = null!;
    public static LoginResponse? Usuario { get; private set; }
    public static Catalogo Catalogo { get; } = new();

    public static bool EsAdmin => Usuario?.Rol == Roles.Administrador;

    /// <summary>¿La persona conectada puede hacer esto sin pedir la clave del encargado? (ver <see cref="Permisos"/>)</summary>
    public static bool Puede(string permiso) => Usuario is { } u && Permisos.Tiene(u.Rol, u.Permisos, permiso);

    /// <summary>La persona que entró acaba de crear su clave de autorización.</summary>
    public static void MarcarClaveAutorizacion()
    {
        if (Usuario is { } u)
            Usuario = u with { TieneClaveAutorizacion = true };
    }

    /// <summary>Qué hacen las flechas en Vender (lo elige el encargado; igual en todas las cajas).</summary>
    public static ConfigVentaDto ConfigVenta { get; set; } = new();

    /// <summary>El encargado restauró un respaldo (en esta u otra caja): hay que volver a entrar. Trae el mensaje.</summary>
    public static event Action<string>? BaseRestaurada;

    /// <summary>
    /// true mientras se cierra la sesión porque el servidor cortó el acceso a la caja:
    /// evita mensajes repetidos y preguntas como "¿salir y borrar la venta?".
    /// </summary>
    public static bool SesionCortada { get; set; }

    public static void Configurar(ConfigCaja config)
    {
        Config = config;
        Api = new ApiClient(config.ServidorUrl);
        _bascula?.Dispose();
        _bascula = null;
        _basculaCreada = false;
    }

    /// <summary>Báscula configurada para esta caja, o null si no tiene.</summary>
    public static IBascula? Bascula
    {
        get
        {
            if (!_basculaCreada)
            {
                _bascula = PerfilesBascula.Crear(Config.Bascula);
                _basculaCreada = true;
            }
            return _bascula;
        }
    }

    public static async Task IniciarAsync(LoginResponse usuario)
    {
        Usuario = usuario;
        Api.Token = usuario.Token;
        await Catalogo.CargarAsync(Api);
        try
        {
            ConfigVenta = await Api.GetAsync<ConfigVentaDto>("api/configuracion/venta") ?? new ConfigVentaDto();
        }
        catch (ApiException)
        {
            ConfigVenta = new ConfigVentaDto(); // servidor anterior: flecha izquierda = cantidad
        }
        // El tiempo real se conecta en segundo plano: la caja ya puede vender con el catálogo cargado.
        _ = ConectarTiempoRealAsync();
    }

    public static async Task CerrarAsync()
    {
        if (_hub is not null)
        {
            await _hub.DisposeAsync();
            _hub = null;
        }
        Usuario = null;
        Api.Token = null;
    }

    public static bool HayImpresora => !string.IsNullOrWhiteSpace(Config.Impresora.Nombre);

    /// <summary>
    /// Al cerrar el programa: le pide al servidor un respaldo de la base de datos.
    /// No hay sesión (ya se salió), por eso el servidor solo revisa que sea una caja registrada.
    /// Espera como máximo 3 segundos para no detener el cierre si no hay red.
    /// </summary>
    public static void AvisarCierre()
    {
        if (Config is null || Api is null)
            return;
        try
        {
            Task.Run(() => Api.PostAsync("api/respaldos/al-cerrar", new AvisoCierreRequest(Config.CajaId))).Wait(TimeSpan.FromSeconds(3));
        }
        catch
        {
            // Sin servidor no hay respaldo; el siguiente cierre lo hará.
        }
    }

    /// <summary>Envía ESC/POS a la impresora de tickets; no hace nada si la caja no tiene impresora.</summary>
    public static void Imprimir(byte[] datos)
    {
        if (HayImpresora)
            ImpresoraRaw.Enviar(Config.Impresora.Nombre, datos);
    }

    public static void AbrirCajon()
    {
        if (Config.Impresora.AbrirCajon)
            Imprimir(TicketEscPos.AbrirCajon);
    }

    private static async Task ConectarTiempoRealAsync()
    {
        _hub = new HubConnectionBuilder()
            .WithUrl(new Uri(ApiClient.Normalizar(Config.ServidorUrl), "hubs/pdv"), o =>
            {
                o.AccessTokenProvider = () => Task.FromResult(Usuario?.Token);
                // WebSocket directo: se omite la petición de negociación.
                o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets;
                o.SkipNegotiation = true;
                o.WebSocketConfiguration = ws => ws.Proxy = null;
            })
            .WithAutomaticReconnect()
            .Build();

        _hub.On<Producto>("ProductoActualizado", Catalogo.Actualizar);
        _hub.On<string>("BaseRestaurada", m => BaseRestaurada?.Invoke(m));
        // Al reconectar pudimos perder avisos: recargar todo el catálogo.
        _hub.Reconnected += _ => Catalogo.CargarAsync(Api);

        try
        {
            await _hub.StartAsync();
        }
        catch
        {
            // Sin tiempo real la caja sigue funcionando con el catálogo cargado.
        }
    }
}

public static class Formato
{
    public static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    public static string Dinero(decimal valor) => valor.ToString("C2", Mx);

    /// <summary>Lee números aceptando punto o coma decimal.</summary>
    public static bool TryDecimal(string? texto, out decimal valor) =>
        decimal.TryParse((texto ?? "").Trim().Replace("$", "").Replace(',', '.'),
            NumberStyles.Number, CultureInfo.InvariantCulture, out valor);
}
