using System.Net;
using Dapper;
using Npgsql;
using PDV.Core;

namespace PDV.Data;

public sealed class CajaRepositorio(Db db)
{
    private const string Select = "SELECT id, nombre, serie, ultimo_folio, es_maestra, activa, ip FROM cajas";

    public async Task<IEnumerable<CajaInfo>> ListarAsync()
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QueryAsync<CajaInfo>(Select + " ORDER BY id");
    }

    public async Task<CajaInfo> CrearAsync(CrearCajaRequest r)
    {
        var serie = r.Serie.Trim().ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(r.Nombre) || serie.Length is 0 or > 10)
            throw new ReglaNegocioException("Falta el nombre de la caja.");
        var ip = NormalizarIp(r.Ip) ?? throw new ReglaNegocioException("Escriba la IP fija de la computadora de esta caja.");

        await using var cn = await db.AbrirAsync();
        try
        {
            return await cn.QuerySingleAsync<CajaInfo>(
                "INSERT INTO cajas (nombre, serie, ip) VALUES (@nombre, @serie, @ip) RETURNING id, nombre, serie, ultimo_folio, es_maestra, activa, ip",
                new { nombre = r.Nombre.Trim(), serie, ip });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "ux_cajas_ip")
        {
            throw new ReglaNegocioException($"La IP {ip} ya la tiene otra caja.");
        }
    }

    /// <summary>Cambia nombre, IP y si está bloqueada. La caja principal no se puede bloquear ni lleva IP.</summary>
    public async Task<CajaInfo> ActualizarAsync(int id, ActualizarCajaRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Nombre))
            throw new ReglaNegocioException("Falta el nombre de la caja.");

        await using var cn = await db.AbrirAsync();
        var actual = await cn.QuerySingleOrDefaultAsync<CajaInfo>(Select + " WHERE id = @id", new { id })
            ?? throw new ReglaNegocioException("Esa caja no existe.");

        string? ip = null;
        if (actual.EsMaestra)
        {
            if (!r.Activa)
                throw new ReglaNegocioException("La caja principal no se puede bloquear.");
        }
        else
        {
            ip = NormalizarIp(r.Ip) ?? throw new ReglaNegocioException("Escriba la IP fija de la computadora de esta caja.");
        }

        try
        {
            return await cn.QuerySingleAsync<CajaInfo>(
                "UPDATE cajas SET nombre = @nombre, ip = @ip, activa = @activa WHERE id = @id RETURNING id, nombre, serie, ultimo_folio, es_maestra, activa, ip",
                new { id, nombre = r.Nombre.Trim(), ip, activa = actual.EsMaestra || r.Activa });
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation && e.ConstraintName == "ux_cajas_ip")
        {
            throw new ReglaNegocioException($"La IP {ip} ya la tiene otra caja.");
        }
    }

    /// <summary>Todas las asignaciones persona → caja (para el control de acceso en memoria).</summary>
    public async Task<IEnumerable<(int UsuarioId, int CajaId)>> ListarAsignacionesAsync()
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QueryAsync<(int, int)>("SELECT usuario_id, caja_id FROM usuario_cajas");
    }

    /// <summary>"192.168.001.020 " → "192.168.1.20". Vacío → null. Formato inválido → error.</summary>
    public static string? NormalizarIp(string? texto)
    {
        if (string.IsNullOrWhiteSpace(texto))
            return null;
        var partes = texto.Trim().Split('.');
        if (partes.Length != 4 || !partes.All(p => byte.TryParse(p, out _)))
            throw new ReglaNegocioException("La IP no es válida. Debe ser como 192.168.1.20");
        return IPAddress.Parse(string.Join('.', partes.Select(byte.Parse))).ToString();
    }
}

public sealed class ConfiguracionRepositorio(Db db)
{
    public async Task<NegocioDto> ObtenerNegocioAsync()
    {
        await using var cn = await db.AbrirAsync();
        var valores = (await cn.QueryAsync<(string Clave, string Valor)>("SELECT clave, valor FROM configuracion"))
            .ToDictionary(x => x.Clave, x => x.Valor);

        return new NegocioDto
        {
            Nombre = valores.GetValueOrDefault("negocio_nombre", ""),
            Rfc = valores.GetValueOrDefault("negocio_rfc", ""),
            Direccion = valores.GetValueOrDefault("negocio_direccion", ""),
            Telefono = valores.GetValueOrDefault("negocio_telefono", ""),
            MensajeTicket = valores.GetValueOrDefault("ticket_mensaje", "")
        };
    }

    public async Task<ConfigVentaDto> ObtenerConfigVentaAsync()
    {
        await using var cn = await db.AbrirAsync();
        var izquierda = await cn.ExecuteScalarAsync<string?>("SELECT valor FROM configuracion WHERE clave = 'flecha_izquierda'");
        return new ConfigVentaDto { FlechaIzquierda = izquierda == AccionesFlecha.Precio ? AccionesFlecha.Precio : AccionesFlecha.Cantidad };
    }

    public async Task GuardarConfigVentaAsync(ConfigVentaDto c)
    {
        if (c.FlechaIzquierda is not (AccionesFlecha.Cantidad or AccionesFlecha.Precio))
            throw new ReglaNegocioException("Elija qué hace la flecha izquierda.");
        await using var cn = await db.AbrirAsync();
        await cn.ExecuteAsync("""
            INSERT INTO configuracion (clave, valor) VALUES ('flecha_izquierda', @FlechaIzquierda)
            ON CONFLICT (clave) DO UPDATE SET valor = EXCLUDED.valor
            """, c);
    }

    public async Task GuardarNegocioAsync(NegocioDto n)
    {
        await using var cn = await db.AbrirAsync();
        await cn.ExecuteAsync("""
            INSERT INTO configuracion (clave, valor) VALUES (@clave, @valor)
            ON CONFLICT (clave) DO UPDATE SET valor = EXCLUDED.valor
            """,
            new[]
            {
                new { clave = "negocio_nombre", valor = n.Nombre },
                new { clave = "negocio_rfc", valor = n.Rfc },
                new { clave = "negocio_direccion", valor = n.Direccion },
                new { clave = "negocio_telefono", valor = n.Telefono },
                new { clave = "ticket_mensaje", valor = n.MensajeTicket }
            });
    }
}
