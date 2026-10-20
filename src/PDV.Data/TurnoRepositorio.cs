using Dapper;
using Npgsql;
using PDV.Core;

namespace PDV.Data;

public sealed class TurnoRepositorio(Db db, ConfiguracionRepositorio configuracion)
{
    private const string Select = """
        SELECT t.id, t.caja_id, t.usuario_id, u.nombre AS usuario_nombre, t.abierto_en, t.cerrado_en,
               t.fondo_inicial, t.efectivo_declarado, t.estado
        FROM turnos t JOIN usuarios u ON u.id = t.usuario_id
        """;

    public async Task<Turno?> ObtenerAsync(long id)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QuerySingleOrDefaultAsync<Turno>(Select + " WHERE t.id = @id", new { id });
    }

    public async Task<Turno?> ObtenerAbiertoAsync(int cajaId)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QuerySingleOrDefaultAsync<Turno>(
            Select + " WHERE t.caja_id = @cajaId AND t.estado = 'ABIERTO'", new { cajaId });
    }

    public async Task<Turno> AbrirAsync(AbrirTurnoRequest r, int usuarioId)
    {
        if (r.FondoInicial < 0)
            throw new ReglaNegocioException("El dinero inicial no puede ser negativo.");

        await using var cn = await db.AbrirAsync();
        var cajaActiva = await cn.ExecuteScalarAsync<bool?>("SELECT activa FROM cajas WHERE id = @CajaId", r);
        if (cajaActiva != true)
            throw new ReglaNegocioException("Esta caja no está dada de alta. Avise al encargado.");

        try
        {
            var id = await cn.ExecuteScalarAsync<long>(
                "INSERT INTO turnos (caja_id, usuario_id, fondo_inicial) VALUES (@CajaId, @usuarioId, @FondoInicial) RETURNING id",
                new { r.CajaId, usuarioId, r.FondoInicial });
            return (await ObtenerAsync(id))!;
        }
        catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new ReglaNegocioException("Esta caja ya está abierta.");
        }
    }

    public async Task RegistrarRetiroAsync(long turnoId, int usuarioId, int autorizoId, RetiroRequest r)
    {
        if (r.Monto <= 0 || string.IsNullOrWhiteSpace(r.Motivo))
            throw new ReglaNegocioException("Escriba cuánto dinero saca y para qué.");

        await using var cn = await db.AbrirAsync();
        var filas = await cn.ExecuteAsync("""
            INSERT INTO retiros (turno_id, usuario_id, monto, motivo, autorizado_por)
            SELECT id, @usuarioId, @Monto, @Motivo, @autorizoId FROM turnos WHERE id = @turnoId AND estado = 'ABIERTO'
            """, new { turnoId, usuarioId, r.Monto, r.Motivo, autorizoId });
        if (filas == 0)
            throw new ReglaNegocioException("La caja ya está cerrada. Vuelva a entrar para abrirla.");
    }

    public async Task<CorteDto> CerrarAsync(long turnoId, CerrarTurnoRequest r)
    {
        if (r.EfectivoDeclarado < 0)
            throw new ReglaNegocioException("El dinero contado no puede ser negativo.");

        await using var cn = await db.AbrirAsync();
        var filas = await cn.ExecuteAsync("""
            UPDATE turnos SET estado = 'CERRADO', cerrado_en = now(), efectivo_declarado = @EfectivoDeclarado
            WHERE id = @turnoId AND estado = 'ABIERTO'
            """, new { turnoId, r.EfectivoDeclarado });
        if (filas == 0)
            throw new ReglaNegocioException("La caja ya está cerrada. Vuelva a entrar para abrirla.");

        return await CorteAsync(turnoId);
    }

    // Resumen de dinero de cada turno; se completa con un WHERE.
    private const string SelectCorte = """
        SELECT t.id AS turno_id, t.caja_id, c.nombre AS caja, u.nombre AS cajero, t.estado, t.abierto_en, t.cerrado_en,
               t.fondo_inicial, t.efectivo_declarado,
               (SELECT count(*) FROM ventas v WHERE v.turno_id = t.id AND v.estado = 'PAGADA')::int    AS num_ventas,
               (SELECT count(*) FROM ventas v WHERE v.turno_id = t.id AND v.estado = 'CANCELADA')::int AS num_canceladas,
               COALESCE(p.efectivo, 0) AS ventas_efectivo,
               COALESCE(p.tarjeta, 0) AS ventas_tarjeta,
               COALESCE(p.transferencia, 0) AS ventas_transferencia,
               COALESCE(p.credito, 0) AS ventas_credito,
               (SELECT COALESCE(sum(-mc.monto) FILTER (WHERE mc.forma_pago = 'EFECTIVO'), 0)
                  FROM movimientos_cliente mc WHERE mc.turno_id = t.id AND mc.tipo = 'ABONO') AS abonos_efectivo,
               (SELECT COALESCE(sum(-mc.monto) FILTER (WHERE mc.forma_pago <> 'EFECTIVO'), 0)
                  FROM movimientos_cliente mc WHERE mc.turno_id = t.id AND mc.tipo = 'ABONO') AS abonos_otros,
               (SELECT COALESCE(sum(monto), 0) FROM retiros r WHERE r.turno_id = t.id) AS retiros
        FROM turnos t
        JOIN cajas c ON c.id = t.caja_id
        JOIN usuarios u ON u.id = t.usuario_id
        LEFT JOIN LATERAL (
            SELECT sum(vp.monto) FILTER (WHERE vp.forma_pago = 'EFECTIVO')      AS efectivo,
                   sum(vp.monto) FILTER (WHERE vp.forma_pago = 'TARJETA')       AS tarjeta,
                   sum(vp.monto) FILTER (WHERE vp.forma_pago = 'TRANSFERENCIA') AS transferencia,
                   sum(vp.monto) FILTER (WHERE vp.forma_pago = 'CREDITO')       AS credito
            FROM venta_pagos vp JOIN ventas v ON v.id = vp.venta_id
            WHERE v.turno_id = t.id AND v.estado = 'PAGADA'
        ) p ON TRUE
        """;

    /// <summary>Corte X (turno abierto) o corte Z (turno cerrado), con el detalle de lo que se sacó.</summary>
    public async Task<CorteDto> CorteAsync(long turnoId)
    {
        await using var cn = await db.AbrirAsync();
        var corte = await cn.QuerySingleOrDefaultAsync<CorteDto>(SelectCorte + " WHERE t.id = @turnoId", new { turnoId })
            ?? throw new ReglaNegocioException("No se encontró la caja abierta.");

        corte.DetalleRetiros = (await cn.QueryAsync<RetiroDto>("""
            SELECT r.fecha, r.monto, r.motivo, u.nombre AS usuario,
                   CASE WHEN r.autorizado_por <> r.usuario_id THEN a.nombre END AS autorizo
            FROM retiros r JOIN usuarios u ON u.id = r.usuario_id
            LEFT JOIN usuarios a ON a.id = r.autorizado_por
            WHERE r.turno_id = @turnoId ORDER BY r.fecha, r.id
            """, new { turnoId })).ToList();
        corte.Negocio = await configuracion.ObtenerNegocioAsync();
        return corte;
    }

    /// <summary>
    /// Cortes de caja (turnos) que estuvieron abiertos en algún momento del rango [desde, hasta), lo más reciente primero.
    /// Incluye los que siguen abiertos. Las fechas van en UTC.
    /// </summary>
    public async Task<List<CorteDto>> ListarCortesAsync(DateTime desde, DateTime hasta, int? cajaId)
    {
        if (hasta <= desde)
            throw new ReglaNegocioException("La fecha final tiene que ser después de la inicial.");

        await using var cn = await db.AbrirAsync();
        var sql = SelectCorte + " WHERE t.abierto_en < @hasta AND COALESCE(t.cerrado_en, now()) >= @desde"
                  + (cajaId is null ? "" : " AND t.caja_id = @cajaId")
                  + " ORDER BY t.abierto_en DESC LIMIT 2000";
        return (await cn.QueryAsync<CorteDto>(sql, new { desde, hasta, cajaId })).ToList();
    }
}
