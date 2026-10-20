using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace PDV.Caja.Servicios;

public class ApiException(string mensaje) : Exception(mensaje);

/// <summary>La caja se bloqueó, la IP no corresponde o la persona ya no tiene acceso a esta caja.</summary>
public sealed class CajaSinAccesoException(string mensaje) : ApiException(mensaje);

/// <summary>Cliente HTTP hacia PDV.Server. Convierte los errores del servidor en ApiException con mensaje legible.</summary>
public sealed class ApiClient
{
    private readonly HttpClient _http;

    public ApiClient(string servidorUrl)
    {
        // Red local: sin proxy (la autodetección WPAD puede tardar segundos en la primera petición).
        _http = new HttpClient(new SocketsHttpHandler { UseProxy = false, PooledConnectionIdleTimeout = TimeSpan.FromMinutes(10) })
        {
            BaseAddress = Normalizar(servidorUrl),
            // El límite de tiempo va en cada petición (15 s, o más en las que tardan, como restaurar un respaldo)
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    /// <summary>
    /// Agrega la "/" final y cambia "localhost" por 127.0.0.1: en Windows, "localhost" intenta primero
    /// IPv6 (::1) y, si el servidor solo escucha en IPv4, cada conexión nueva pierde ~2 segundos.
    /// </summary>
    public static Uri Normalizar(string servidorUrl)
    {
        var b = new UriBuilder(servidorUrl.EndsWith('/') ? servidorUrl : servidorUrl + "/");
        if (b.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase))
            b.Host = "127.0.0.1";
        return b.Uri;
    }

    /// <summary>Se dispara cuando el servidor corta el acceso a esta caja (bloqueo, IP, persona sin permiso).</summary>
    public static event Action<string>? SinAccesoACaja;

    public string? Token
    {
        set => _http.DefaultRequestHeaders.Authorization = value is null ? null : new AuthenticationHeaderValue("Bearer", value);
    }

    private static readonly TimeSpan TiempoNormal = TimeSpan.FromSeconds(15);

    /// <summary>GET; devuelve null si el servidor responde 404 o 204.</summary>
    public Task<T?> GetAsync<T>(string ruta) => Enviar<T>(ct => _http.GetAsync(ruta, ct));

    public Task<T?> PostAsync<T>(string ruta, object cuerpo) => Enviar<T>(ct => _http.PostAsJsonAsync(ruta, cuerpo, ct));

    /// <param name="tiempo">Cuánto esperar la respuesta (por omisión 15 segundos).</param>
    public Task<T?> PostAsync<T>(string ruta, object cuerpo, TimeSpan tiempo) => Enviar<T>(ct => _http.PostAsJsonAsync(ruta, cuerpo, ct), tiempo);

    public Task PostAsync(string ruta, object cuerpo) => Enviar<object>(ct => _http.PostAsJsonAsync(ruta, cuerpo, ct));

    public Task<T?> PutAsync<T>(string ruta, object cuerpo) => Enviar<T>(ct => _http.PutAsJsonAsync(ruta, cuerpo, ct));

    public Task PutAsync(string ruta, object? cuerpo = null) => Enviar<object>(ct => _http.PutAsJsonAsync(ruta, cuerpo ?? new { }, ct));

    public Task DeleteAsync(string ruta) => Enviar<object>(ct => _http.DeleteAsync(ruta, ct));

    private static async Task<T?> Enviar<T>(Func<CancellationToken, Task<HttpResponseMessage>> peticion, TimeSpan? tiempo = null)
    {
        using var limite = new CancellationTokenSource(tiempo ?? TiempoNormal);
        HttpResponseMessage r;
        try
        {
            r = await peticion(limite.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            throw new ApiException("No hay conexión con el servidor. Verifique la red y que la caja maestra esté encendida.");
        }

        using (r)
        {
            if (r.StatusCode == HttpStatusCode.NotFound && r.RequestMessage?.Method == HttpMethod.Get)
                return default;
            if (!r.IsSuccessStatusCode)
            {
                var (mensaje, codigo) = await LeerError(r);
                if (codigo == PDV.Core.CodigosError.CajaSinAcceso)
                {
                    SinAccesoACaja?.Invoke(mensaje);
                    throw new CajaSinAccesoException(mensaje);
                }
                throw new ApiException(mensaje);
            }
            if (typeof(T) == typeof(object) || r.StatusCode == HttpStatusCode.NoContent || r.Content.Headers.ContentLength == 0)
                return default;
            return await r.Content.ReadFromJsonAsync<T>(limite.Token);
        }
    }

    /// <returns>Mensaje para la persona y, si viene, el código de error del servidor.</returns>
    private static async Task<(string Mensaje, string? Codigo)> LeerError(HttpResponseMessage r)
    {
        try
        {
            using var json = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
            var codigo = json.RootElement.TryGetProperty("codigo", out var c) ? c.GetString() : null;
            if (json.RootElement.TryGetProperty("detail", out var detalle) && detalle.GetString() is { Length: > 0 } d)
                return (d, codigo);
            if (json.RootElement.TryGetProperty("title", out var titulo) && titulo.GetString() is { Length: > 0 } t)
                return (t, codigo);
        }
        catch (JsonException) { }

        return (r.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "La sesión expiró. Vuelva a iniciar sesión.",
            HttpStatusCode.Forbidden => "Su usuario no tiene permiso para esta operación.",
            _ => $"Error del servidor ({(int)r.StatusCode})."
        }, null);
    }
}
