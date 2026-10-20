using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using PDV.Core;

namespace PDV.Server;

public static class Politicas
{
    public const string Admin = "Admin";
    public const string Inventario = "Inventario";
    public const string Venta = "Venta";
}

/// <summary>Canal en tiempo real hacia las cajas (cambios de precio, productos nuevos…).</summary>
[Authorize]
public sealed class PdvHub : Hub
{
    public const string ProductoActualizado = "ProductoActualizado";
    /// <summary>Se restauró un respaldo: todas las cajas tienen que volver a entrar.</summary>
    public const string BaseRestaurada = "BaseRestaurada";
}

public static class ClaveJwt
{
    /// <summary>
    /// Usa Jwt:Clave si está configurada; si no, genera una clave aleatoria y la guarda en jwt.key
    /// junto al servidor (así nunca queda una clave conocida en el código).
    /// </summary>
    public static byte[] Obtener(IConfiguration config, string carpeta)
    {
        var configurada = config["Jwt:Clave"];
        if (!string.IsNullOrWhiteSpace(configurada))
            return Convert.FromBase64String(configurada);

        var archivo = Path.Combine(carpeta, "jwt.key");
        if (File.Exists(archivo))
            return Convert.FromBase64String(File.ReadAllText(archivo).Trim());

        var clave = RandomNumberGenerator.GetBytes(64);
        File.WriteAllText(archivo, Convert.ToBase64String(clave));
        return clave;
    }
}

public sealed class GeneradorToken(byte[] clave, TimeSpan vigencia)
{
    private readonly JsonWebTokenHandler _handler = new();

    /// <param name="cajaId">Caja desde la que entró: la sesión solo sirve para esa caja.</param>
    public string Generar(Usuario u, int cajaId) => _handler.CreateToken(new SecurityTokenDescriptor
    {
        Subject = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, u.Id.ToString()),
            new Claim(ClaimTypes.Name, u.Nombre),
            new Claim(ClaimTypes.Role, u.Rol),
            new Claim(ClaimsExtensiones.ClaimCaja, cajaId.ToString())
        ]),
        Expires = DateTime.UtcNow.Add(vigencia),
        SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(clave), SecurityAlgorithms.HmacSha256)
    });
}

public static class ClaimsExtensiones
{
    public const string ClaimCaja = "caja";

    public static int UsuarioId(this ClaimsPrincipal u) => int.Parse(u.FindFirstValue(ClaimTypes.NameIdentifier)!);
    public static bool EsAdmin(this ClaimsPrincipal u) => u.IsInRole(Roles.Administrador);
    public static int? CajaId(this ClaimsPrincipal u) => int.TryParse(u.FindFirstValue(ClaimCaja), out var id) ? id : null;
}

/// <summary>Respuesta 403 que la caja reconoce para cerrar la sesión y mostrar el mensaje.</summary>
public static class SinAcceso
{
    public static IResult Resultado(string mensaje) => Results.Problem(mensaje, statusCode: StatusCodes.Status403Forbidden,
        extensions: new Dictionary<string, object?> { ["codigo"] = CodigosError.CajaSinAcceso });
}

/// <summary>
/// En CADA petición autenticada revisa que la caja siga desbloqueada, que la IP corresponda
/// y que la persona siga con acceso a esa caja. Así un bloqueo surte efecto al instante.
/// </summary>
public sealed class ControlAccesoCajas(RequestDelegate siguiente)
{
    public async Task InvokeAsync(HttpContext ctx, AccesoCajas acceso)
    {
        if (ctx.User.Identity?.IsAuthenticated == true
            && await acceso.ValidarPeticionAsync(ctx.User, ctx.Connection.RemoteIpAddress) is { } error)
        {
            await SinAcceso.Resultado(error).ExecuteAsync(ctx);
            return;
        }
        await siguiente(ctx);
    }
}

/// <summary>Convierte errores de negocio y de BD en respuestas ProblemDetails legibles para la caja.</summary>
public sealed class ManejoErrores(RequestDelegate siguiente, ILogger<ManejoErrores> log)
{
    public async Task InvokeAsync(HttpContext ctx)
    {
        try
        {
            await siguiente(ctx);
        }
        catch (ReglaNegocioException e)
        {
            await Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(ctx);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            await Results.Problem(MensajeDuplicado(e.ConstraintName), statusCode: StatusCodes.Status409Conflict).ExecuteAsync(ctx);
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.ForeignKeyViolation)
        {
            log.LogWarning("Llave foránea inválida: {Restriccion}", e.ConstraintName);
            await Results.Problem("Uno de los datos elegidos ya no existe. Cierre la ventana y vuelva a intentar.", statusCode: StatusCodes.Status400BadRequest).ExecuteAsync(ctx);
        }
        catch (Exception e) when (!ctx.Response.HasStarted)
        {
            log.LogError(e, "Error no controlado en {Ruta}", ctx.Request.Path);
            await Results.Problem("Algo salió mal en el servidor. Intente de nuevo; si sigue pasando, avise al encargado.", statusCode: StatusCodes.Status500InternalServerError).ExecuteAsync(ctx);
        }
    }

    /// <summary>Mensaje entendible para cada dato que no se puede repetir.</summary>
    private static string MensajeDuplicado(string? restriccion) => restriccion switch
    {
        "productos_codigo_barras_key" => "Ya hay otro producto con ese código de barras.",
        "productos_codigo_interno_key" => "Ya hay otro producto con ese número de báscula.",
        "departamentos_nombre_key" or "categorias_nombre_key" => "Ya existe un departamento con ese nombre.",
        "ux_usuarios_login" => "Ese usuario ya lo tiene otra persona. Elija otro.",
        "cajas_serie_key" => "Ya existe una caja con ese nombre corto.",
        _ => "Ese dato ya está registrado."
    };
}
