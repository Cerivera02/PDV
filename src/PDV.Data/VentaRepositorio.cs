using Dapper;
using PDV.Core;

namespace PDV.Data;

public sealed class VentaRepositorio(Db db, ConfiguracionRepositorio configuracion)
{
    /// <summary>
    /// Registra la venta completa en UNA transacción: folio, encabezado, detalle, pagos,
    /// descuento de existencias y kardex. Los precios se toman de la BD, nunca del cliente.
    /// </summary>
    /// <param name="cajaId">Caja de la sesión: el turno tiene que ser de esa caja.</param>
    /// <param name="autorizoPrecio">Para un renglón con precio cambiado: quién lo autorizó, o null si nadie (se rechaza).</param>
    public async Task<long> RegistrarAsync(VentaRequest req, int usuarioId, int cajaId, Func<VentaItemRequest, int?>? autorizoPrecio = null)
    {
        if (req.Items is not { Count: > 0 })
            throw new ReglaNegocioException("No hay productos en la venta.");
        if (req.Pagos is not { Count: > 0 })
            throw new ReglaNegocioException("Falta indicar cómo paga el cliente.");

        await using var cn = await db.AbrirAsync();
        await using var tx = await cn.BeginTransactionAsync();

        var turno = await cn.QuerySingleOrDefaultAsync<Turno>(
            "SELECT * FROM turnos WHERE id = @TurnoId FOR UPDATE", new { req.TurnoId }, tx);
        if (turno is null || turno.Estado != EstadosTurno.Abierto)
            throw new ReglaNegocioException("La caja ya está cerrada. Vuelva a entrar para abrirla.");
        if (turno.UsuarioId != usuarioId)
            throw new ReglaNegocioException("Esta caja la abrió otra persona.");
        if (turno.CajaId != cajaId)
            throw new ReglaNegocioException("Esta computadora no está registrada como esa caja. Contacte a soporte.");

        // ---- Detalle con precios de la BD
        var ids = req.Items.Select(i => i.ProductoId).Distinct().ToArray();
        var productos = (await cn.QueryAsync<Producto>(
            "SELECT * FROM productos WHERE id = ANY(@ids)", new { ids }, tx)).ToDictionary(p => p.Id);
        var ofertas = await OfertaRepositorio.ListarPorProductosAsync(cn, ids, tx);
        foreach (var p in productos.Values)
            p.Ofertas = [.. ofertas[p.Id]];

        var renglones = new List<(Producto Producto, decimal Cantidad, decimal? PrecioManual, int? Autorizo)>();
        foreach (var item in req.Items)
        {
            if (!productos.TryGetValue(item.ProductoId, out var p) || !p.Activo)
                throw new ReglaNegocioException("Uno de los productos ya no existe. Quítelo de la venta.");
            var cantidad = Dinero.RedondearCantidad(item.Cantidad);
            if (cantidad <= 0)
                throw new ReglaNegocioException($"La cantidad de {p.Nombre} no es válida.");
            if (!p.PorPeso && cantidad != decimal.Truncate(cantidad))
                throw new ReglaNegocioException($"{p.Nombre} se vende por pieza: la cantidad debe ser un número entero.");
            // Precio cambiado a mano solo en esta venta: tiene que estar autorizado (permiso o firma del encargado)
            decimal? manual = null;
            int? autorizo = null;
            if (item.PrecioManual is { } precioManual)
            {
                manual = Dinero.Redondear(precioManual);
                if (manual <= 0)
                    throw new ReglaNegocioException($"El precio de {p.Nombre} tiene que ser mayor a $0.");
                autorizo = autorizoPrecio?.Invoke(item)
                    ?? throw new ReglaNegocioException($"Para cambiar el precio de {p.Nombre} hace falta la autorización del encargado.");
            }
            renglones.Add((p, cantidad, manual, autorizo));
        }

        // ---- Cliente (opcional): precio especial y crédito. Se bloquea su fila para que el saldo no cambie a la vez en dos cajas.
        Cliente? cliente = null;
        if (req.ClienteId is { } clienteId)
        {
            cliente = await cn.QuerySingleOrDefaultAsync<Cliente>(
                "SELECT * FROM clientes WHERE id = @clienteId AND activo FOR UPDATE", new { clienteId }, tx)
                ?? throw new ReglaNegocioException("No se encontró el cliente. Quítelo de la venta y elíjalo otra vez.");
        }

        // Promociones: la misma calculadora que usa la caja para mostrar el precio
        var precios = CalculadoraPrecios.AplicarPromociones(renglones.Select(r => (r.Producto, r.Cantidad, r.PrecioManual)).ToList(), DateTime.Today, cliente);
        var lineas = renglones.Zip(precios, (r, c) => (r.Producto, r.Cantidad, c.Importe, Calculo: c, r.Autorizo)).ToList();

        var total = lineas.Sum(l => l.Importe);
        var impuestos = Dinero.Redondear(lineas.Sum(l => l.Importe - l.Importe / (1 + l.Producto.TasaIva)));
        var subtotal = total - impuestos;

        // ---- Pagos: solo el efectivo puede generar cambio
        foreach (var pago in req.Pagos)
        {
            if (!FormasPago.Todas.Contains(pago.FormaPago) || pago.Monto <= 0)
                throw new ReglaNegocioException("La forma de pago no es válida.");
        }
        // ---- Crédito: necesita cliente con crédito suficiente (se revisa primero para dar el mensaje más claro)
        var credito = Dinero.Redondear(req.Pagos.Where(p => p.FormaPago == FormasPago.Credito).Sum(p => p.Monto));
        if (credito > 0)
        {
            if (cliente is null)
                throw new ReglaNegocioException("Para vender a crédito, primero elija al cliente.");
            if (cliente.LimiteCredito <= 0)
                throw new ReglaNegocioException($"{cliente.Nombre} no tiene crédito.");
            if (cliente.Saldo + credito > cliente.LimiteCredito)
                throw new ReglaNegocioException($"No alcanza el crédito de {cliente.Nombre}: tiene {cliente.CreditoDisponible:C2} disponibles y la compra a crédito es de {credito:C2}.");
        }

        var efectivoRecibido = req.Pagos.Where(p => p.FormaPago == FormasPago.Efectivo).Sum(p => p.Monto);
        var noEfectivo = req.Pagos.Where(p => p.FormaPago != FormasPago.Efectivo).Sum(p => p.Monto);
        if (noEfectivo > total)
            throw new ReglaNegocioException("Con tarjeta o a crédito no se puede cobrar más que el total.");
        var pagado = efectivoRecibido + noEfectivo;
        if (pagado < total)
            throw new ReglaNegocioException($"Falta dinero: faltan {total - pagado:C2}.");
        var cambio = pagado - total;

        // ---- Folio consecutivo por caja (el UPDATE bloquea la fila: sin folios duplicados)
        var (serie, numero) = await cn.QuerySingleAsync<(string, int)>(
            "UPDATE cajas SET ultimo_folio = ultimo_folio + 1 WHERE id = @CajaId RETURNING serie, ultimo_folio",
            new { turno.CajaId }, tx);
        var folio = $"{serie}-{numero:D6}";

        var ventaId = await cn.ExecuteScalarAsync<long>("""
            INSERT INTO ventas (folio, caja_id, turno_id, usuario_id, subtotal, impuestos, total, pagado, cambio, cliente_id)
            VALUES (@folio, @CajaId, @TurnoId, @usuarioId, @subtotal, @impuestos, @total, @pagado, @cambio, @clienteId)
            RETURNING id
            """, new { folio, turno.CajaId, req.TurnoId, usuarioId, subtotal, impuestos, total, pagado, cambio, clienteId = cliente?.Id }, tx);

        await cn.ExecuteAsync("""
            INSERT INTO venta_detalle (venta_id, producto_id, descripcion, unidad, cantidad, precio_unitario, importe, tasa_iva,
                                       precio_lista, descuento, promocion, precio_cambiado_por)
            VALUES (@ventaId, @ProductoId, @Descripcion, @Unidad, @Cantidad, @Precio, @Importe, @TasaIva,
                    @PrecioLista, @Descuento, @Promocion, @Autorizo)
            """,
            lineas.Select(l => new
            {
                ventaId, ProductoId = l.Producto.Id, Descripcion = l.Producto.Nombre, l.Producto.Unidad,
                l.Cantidad, Precio = l.Calculo.PrecioUnitario, l.Importe, l.Producto.TasaIva,
                PrecioLista = l.Producto.PrecioVenta, l.Calculo.Descuento, l.Calculo.Promocion, l.Autorizo
            }), tx);

        await cn.ExecuteAsync(
            "INSERT INTO venta_pagos (venta_id, forma_pago, monto, referencia) VALUES (@ventaId, @FormaPago, @Monto, @Referencia)",
            req.Pagos.Select(p => new
            {
                ventaId, p.FormaPago,
                Monto = p.FormaPago == FormasPago.Efectivo ? Dinero.Redondear(p.Monto - cambio * (p.Monto / efectivoRecibido)) : p.Monto,
                p.Referencia
            }), tx);

        // ---- Lo vendido a crédito se suma a lo que debe el cliente (estado de cuenta)
        if (credito > 0)
            await MoverSaldoClienteAsync(cn, tx, cliente!.Id, credito, TiposMovimientoCliente.Cargo, ventaId, req.TurnoId, usuarioId);

        // ---- Existencias + kardex (en orden de id para evitar deadlocks entre cajas)
        var porProducto = lineas.GroupBy(l => l.Producto.Id).OrderBy(g => g.Key);
        foreach (var grupo in porProducto)
            await MoverExistenciaAsync(cn, tx, grupo.Key, -grupo.Sum(l => l.Cantidad), TiposMovimiento.Venta, folio, usuarioId, turno.CajaId);

        await tx.CommitAsync();
        return ventaId;
    }

    private static async Task MoverSaldoClienteAsync(Npgsql.NpgsqlConnection cn, Npgsql.NpgsqlTransaction tx,
        int clienteId, decimal monto, string tipo, long ventaId, long turnoId, int usuarioId)
    {
        var saldo = await cn.ExecuteScalarAsync<decimal>(
            "UPDATE clientes SET saldo = saldo + @monto WHERE id = @clienteId RETURNING saldo", new { clienteId, monto }, tx);
        await cn.ExecuteAsync("""
            INSERT INTO movimientos_cliente (cliente_id, tipo, monto, saldo_resultante, venta_id, turno_id, forma_pago, usuario_id)
            VALUES (@clienteId, @tipo, @monto, @saldo, @ventaId, @turnoId, 'CREDITO', @usuarioId)
            """, new { clienteId, tipo, monto, saldo, ventaId, turnoId, usuarioId }, tx);
    }

    /// <summary>Cancela una venta del turno abierto y regresa la mercancía al inventario.</summary>
    public async Task CancelarAsync(long ventaId, int autorizoId, string motivo)
    {
        if (string.IsNullOrWhiteSpace(motivo))
            throw new ReglaNegocioException("Escriba por qué se cancela la venta.");

        await using var cn = await db.AbrirAsync();
        await using var tx = await cn.BeginTransactionAsync();

        var venta = await cn.QuerySingleOrDefaultAsync<(string Folio, string Estado, int CajaId, string EstadoTurno)>("""
            SELECT v.folio, v.estado, v.caja_id, t.estado
            FROM ventas v JOIN turnos t ON t.id = v.turno_id
            WHERE v.id = @ventaId FOR UPDATE OF v
            """, new { ventaId }, tx);
        if (venta.Folio is null)
            throw new ReglaNegocioException("No se encontró esa venta.");
        if (venta.Estado == EstadosVenta.Cancelada)
            throw new ReglaNegocioException("Esa venta ya estaba cancelada.");
        if (venta.EstadoTurno != EstadosTurno.Abierto)
            throw new ReglaNegocioException("Solo se pueden cancelar ventas de hoy, antes de cerrar la caja.");

        await cn.ExecuteAsync("""
            UPDATE ventas SET estado = 'CANCELADA', cancelada_por = @autorizoId, cancelada_en = now(), motivo_cancelacion = @motivo
            WHERE id = @ventaId
            """, new { ventaId, autorizoId, motivo }, tx);

        var detalle = await cn.QueryAsync<(int ProductoId, decimal Cantidad)>("""
            SELECT producto_id, sum(cantidad) FROM venta_detalle WHERE venta_id = @ventaId
            GROUP BY producto_id ORDER BY producto_id
            """, new { ventaId }, tx);
        foreach (var (productoId, cantidad) in detalle)
            await MoverExistenciaAsync(cn, tx, productoId, cantidad, TiposMovimiento.Cancelacion, venta.Folio, autorizoId, venta.CajaId);

        // Si se vendió credito, se le quita esa deuda al cliente
        var cargo = await cn.QuerySingleOrDefaultAsync<(int ClienteId, decimal Monto, long TurnoId)>("""
            SELECT cliente_id, monto, turno_id FROM movimientos_cliente WHERE venta_id = @ventaId AND tipo = 'CARGO'
            """, new { ventaId }, tx);
        if (cargo.Monto > 0)
        {
            await cn.ExecuteAsync("SELECT 1 FROM clientes WHERE id = @ClienteId FOR UPDATE", new { cargo.ClienteId }, tx);
            await MoverSaldoClienteAsync(cn, tx, cargo.ClienteId, -cargo.Monto, TiposMovimientoCliente.Cancelacion, ventaId, cargo.TurnoId, autorizoId);
        }

        await tx.CommitAsync();
    }

    /// <summary>
    /// Historial de tickets, lo más reciente primero: por rango de fechas [desde, hasta) en UTC, o todos los de un turno.
    /// </summary>
    public async Task<List<VentaResumen>> ListarAsync(DateTime? desde, DateTime? hasta, int? cajaId, long? turnoId)
    {
        var filtros = new List<string>();
        if (turnoId is not null)
            filtros.Add("v.turno_id = @turnoId");
        else if (desde is not null && hasta is not null)
        {
            if (hasta <= desde)
                throw new ReglaNegocioException("La fecha final tiene que ser después de la inicial.");
            filtros.Add("v.fecha >= @desde AND v.fecha < @hasta");
        }
        else
            throw new ReglaNegocioException("Elija de qué días quiere ver los tickets.");
        if (cajaId is not null)
            filtros.Add("v.caja_id = @cajaId");

        await using var cn = await db.AbrirAsync();
        return (await cn.QueryAsync<VentaResumen>($"""
            SELECT v.id AS venta_id, v.folio, v.fecha, v.caja_id, c.nombre AS caja, u.nombre AS cajero, cl.nombre AS cliente,
                   v.total, v.estado,
                   (SELECT string_agg(DISTINCT vp.forma_pago, ',') FROM venta_pagos vp WHERE vp.venta_id = v.id) AS formas_pago,
                   (SELECT count(*) FROM venta_detalle d WHERE d.venta_id = v.id)::int AS renglones
            FROM ventas v
            JOIN cajas c ON c.id = v.caja_id
            JOIN usuarios u ON u.id = v.usuario_id
            LEFT JOIN clientes cl ON cl.id = v.cliente_id
            WHERE {string.Join(" AND ", filtros)}
            ORDER BY v.fecha DESC, v.id DESC
            LIMIT 5000
            """, new { desde, hasta, cajaId, turnoId })).ToList();
    }

    public async Task<long?> BuscarIdPorFolioAsync(string folio)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.ExecuteScalarAsync<long?>("SELECT id FROM ventas WHERE folio = @folio", new { folio = folio.Trim().ToUpperInvariant() });
    }

    public async Task<TicketDto?> ObtenerTicketAsync(long ventaId)
    {
        await using var cn = await db.AbrirAsync();
        var ticket = await cn.QuerySingleOrDefaultAsync<TicketDto>("""
            SELECT v.id AS venta_id, v.folio, v.fecha, c.nombre AS caja, u.nombre AS cajero, v.estado,
                   v.subtotal, v.impuestos, v.total, v.pagado, v.cambio,
                   cl.nombre AS cliente,
                   (SELECT m.saldo_resultante FROM movimientos_cliente m WHERE m.venta_id = v.id AND m.tipo = 'CARGO') AS saldo_cliente
            FROM ventas v JOIN cajas c ON c.id = v.caja_id JOIN usuarios u ON u.id = v.usuario_id
            LEFT JOIN clientes cl ON cl.id = v.cliente_id
            WHERE v.id = @ventaId
            """, new { ventaId });
        if (ticket is null)
            return null;

        ticket.Lineas = (await cn.QueryAsync<TicketLineaDto>(
            "SELECT descripcion, unidad, cantidad, precio_unitario, importe, descuento, promocion FROM venta_detalle WHERE venta_id = @ventaId ORDER BY id",
            new { ventaId })).ToList();
        ticket.Pagos = (await cn.QueryAsync<TicketPagoDto>(
            "SELECT forma_pago, monto, referencia FROM venta_pagos WHERE venta_id = @ventaId ORDER BY id",
            new { ventaId })).ToList();
        ticket.Negocio = await configuracion.ObtenerNegocioAsync();
        return ticket;
    }

    private static async Task MoverExistenciaAsync(Npgsql.NpgsqlConnection cn, Npgsql.NpgsqlTransaction tx,
        int productoId, decimal delta, string tipo, string referencia, int usuarioId, int cajaId)
    {
        var resultante = await cn.ExecuteScalarAsync<decimal>(
            "UPDATE productos SET existencia = existencia + @delta, actualizado_en = now() WHERE id = @productoId RETURNING existencia",
            new { productoId, delta }, tx);
        await cn.ExecuteAsync("""
            INSERT INTO movimientos_inventario (producto_id, tipo, cantidad, existencia_resultante, referencia, usuario_id, caja_id)
            VALUES (@productoId, @tipo, @delta, @resultante, @referencia, @usuarioId, @cajaId)
            """, new { productoId, tipo, delta, resultante, referencia, usuarioId, cajaId }, tx);
    }
}
