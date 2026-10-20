using System.Net;
using System.Net.NetworkInformation;
using System.Security.Claims;
using PDV.Core;
using PDV.Data;

namespace PDV.Server;

/// <summary>
/// Decide si una petición puede usar una caja:
///  1. la caja existe y no está bloqueada;
///  2. la IP es la registrada para esa caja (la principal: solo desde esta misma computadora);
///  3. la persona sigue activa y tiene permiso para esa caja (el Encargado puede en todas).
/// Los datos se guardan en memoria; se recargan al cambiar algo (Invalidar) o cada 30 s.
/// </summary>
public sealed class AccesoCajas(CajaRepositorio cajas, UsuarioRepositorio usuarios, ILogger<AccesoCajas> log)
{
    private static readonly TimeSpan Vigencia = TimeSpan.FromSeconds(30);
    private readonly SemaphoreSlim _candado = new(1, 1);
    private Dictionary<int, CajaInfo> _cajas = [];
    private ILookup<int, int> _asignaciones = Array.Empty<(int, int)>().ToLookup(x => x.Item1, x => x.Item2);
    private HashSet<int> _activos = [];
    private DateTime _cargado = DateTime.MinValue;
    private HashSet<IPAddress> _ipsLocales = [];
    private DateTime _ipsCargadas = DateTime.MinValue;

    public void Invalidar() => _cargado = DateTime.MinValue;

    /// <summary>IPv4 "mapeada" en IPv6 (::ffff:192.168.1.20) → 192.168.1.20.</summary>
    public static IPAddress? Normalizar(IPAddress? ip) => ip is { IsIPv4MappedToIPv6: true } ? ip.MapToIPv4() : ip;

    /// <returns>null si se puede usar; si no, el mensaje para la persona.</returns>
    public async Task<string?> ValidarCajaAsync(int cajaId, IPAddress? ipCliente)
    {
        await CargarAsync();
        var ip = Normalizar(ipCliente);

        if (!_cajas.TryGetValue(cajaId, out var caja))
            return $"Ups, la caja número {cajaId} no existe. Contacte a soporte.";
        if (!caja.Activa)
            return $"Ups, no se pudo iniciar en la {caja.Nombre}: está bloqueada. Contacte a soporte.";

        if (caja.EsMaestra)
        {
            if (!EsEstaComputadora(ip))
                return $"Ups, la {caja.Nombre} solo se puede usar en la computadora principal. Contacte a soporte. (IP de esta computadora: {ip})";
        }
        else if (string.IsNullOrEmpty(caja.Ip))
        {
            return $"Ups, la {caja.Nombre} todavía no tiene una IP registrada. Contacte a soporte. (IP de esta computadora: {ip})";
        }
        else if (ip?.ToString() != caja.Ip)
        {
            log.LogWarning("Intento de usar la caja {Caja} desde {Ip} (registrada: {IpCaja})", cajaId, ip, caja.Ip);
            return $"Ups, esta computadora no tiene permiso para usar la {caja.Nombre}. Contacte a soporte. (IP de esta computadora: {ip})";
        }
        return null;
    }

    /// <returns>null si la persona puede trabajar en esa caja; si no, el mensaje.</returns>
    public async Task<string?> ValidarPersonaAsync(int usuarioId, string nombre, string rol, int cajaId)
    {
        await CargarAsync();
        if (!_activos.Contains(usuarioId))
            return "Su usuario ya no tiene acceso al sistema. Hable con el encargado.";
        if (rol == Roles.Administrador)
            return null;

        var permitidas = _asignaciones[usuarioId].ToList();
        if (permitidas.Count == 0 || permitidas.Contains(cajaId))
            return null;

        var nombreCaja = _cajas.TryGetValue(cajaId, out var c) ? c.Nombre : $"caja {cajaId}";
        var lista = string.Join(", ", permitidas.Select(id => _cajas.TryGetValue(id, out var x) ? x.Nombre : $"caja {id}"));
        return $"Ups, {nombre} no tiene permiso para usar la {nombreCaja}. Puede trabajar en: {lista}. Contacte al encargado.";
    }

    /// <summary>Revisión completa de una petición ya autenticada (middleware).</summary>
    public async Task<string?> ValidarPeticionAsync(ClaimsPrincipal u, IPAddress? ip)
    {
        if (u.CajaId() is not { } cajaId)
            return "Su sesión es de una versión anterior. Vuelva a entrar.";
        return await ValidarCajaAsync(cajaId, ip)
            ?? await ValidarPersonaAsync(u.UsuarioId(), u.Identity?.Name ?? "", u.FindFirstValue(ClaimTypes.Role) ?? "", cajaId);
    }

    private bool EsEstaComputadora(IPAddress? ip)
    {
        if (ip is null)
            return false;
        if (IPAddress.IsLoopback(ip))
            return true;
        if (DateTime.UtcNow - _ipsCargadas > TimeSpan.FromMinutes(1))
        {
            _ipsLocales = NetworkInterface.GetAllNetworkInterfaces()
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Select(a => Normalizar(a.Address)!)
                .ToHashSet();
            _ipsCargadas = DateTime.UtcNow;
        }
        return _ipsLocales.Contains(ip);
    }

    private async Task CargarAsync()
    {
        if (DateTime.UtcNow - _cargado < Vigencia)
            return;
        await _candado.WaitAsync();
        try
        {
            if (DateTime.UtcNow - _cargado < Vigencia)
                return;
            _cajas = (await cajas.ListarAsync()).ToDictionary(c => c.Id);
            _asignaciones = (await cajas.ListarAsignacionesAsync()).ToLookup(x => x.UsuarioId, x => x.CajaId);
            _activos = await usuarios.ListarIdsActivosAsync();
            _cargado = DateTime.UtcNow;
        }
        finally
        {
            _candado.Release();
        }
    }
}
