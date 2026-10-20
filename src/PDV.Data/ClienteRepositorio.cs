using Dapper;
using PDV.Core;

namespace PDV.Data;

public sealed class ClienteRepositorio(Db db, ConfiguracionRepositorio configuracion)
{
    private const string Select = """
        SELECT id, nombre, telefono, direccion, limite_credito, saldo, descuento_porcentaje, precio_mayoreo,
               rfc, razon_social, codigo_postal_fiscal, regimen_fiscal, uso_cfdi, correo, activo
        FROM clientes
        """;

    /// <summary>Clientes vigentes; busca por nombre (sin acentos), teléfono o RFC.</summary>
    public async Task<IEnumerable<Cliente>> ListarAsync(string? buscar)
    {
        await using var cn = await db.AbrirAsync();
        var texto = buscar?.Trim() ?? "";
        return await cn.QueryAsync<Cliente>(Select + """
             WHERE activo AND (@texto = ''
                OR unaccent(nombre) ILIKE '%' || unaccent(@texto) || '%'
                OR telefono LIKE '%' || @texto || '%'
                OR rfc = upper(@texto))
             ORDER BY nombre
             LIMIT 300
            """, new { texto });
    }

    public async Task<Cliente?> ObtenerAsync(int id)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QuerySingleOrDefaultAsync<Cliente>(Select + " WHERE id = @id", new { id });
    }

    /// <summary>Alta o cambio. El saldo NO se toca aquí: solo cambia con ventas a crédito y abonos.</summary>
    public async Task<Cliente> GuardarAsync(Cliente c)
    {
        Normalizar(c);
        await using var cn = await db.AbrirAsync();
        if (c.Id == 0)
        {
            c.Id = await cn.ExecuteScalarAsync<int>("""
                INSERT INTO clientes (nombre, telefono, direccion, limite_credito, descuento_porcentaje, precio_mayoreo,
                                      rfc, razon_social, codigo_postal_fiscal, regimen_fiscal, uso_cfdi, correo)
                VALUES (@Nombre, @Telefono, @Direccion, @LimiteCredito, @DescuentoPorcentaje, @PrecioMayoreo,
                        @Rfc, @RazonSocial, @CodigoPostalFiscal, @RegimenFiscal, @UsoCfdi, @Correo)
                RETURNING id
                """, c);
        }
        else
        {
            var saldo = await cn.ExecuteScalarAsync<decimal?>("SELECT saldo FROM clientes WHERE id = @Id AND activo", c)
                ?? throw new ReglaNegocioException("No se encontró el cliente.");
            if (saldo > 0 && c.LimiteCredito < saldo)
                throw new ReglaNegocioException($"El límite de crédito no puede ser menor a lo que ya debe ({saldo:C2}).");

            var filas = await cn.ExecuteAsync("""
                UPDATE clientes SET nombre = @Nombre, telefono = @Telefono, direccion = @Direccion,
                       limite_credito = @LimiteCredito, descuento_porcentaje = @DescuentoPorcentaje, precio_mayoreo = @PrecioMayoreo,
                       rfc = @Rfc, razon_social = @RazonSocial, codigo_postal_fiscal = @CodigoPostalFiscal,
                       regimen_fiscal = @RegimenFiscal, uso_cfdi = @UsoCfdi, correo = @Correo
                WHERE id = @Id AND activo
                """, c);
            if (filas == 0)
                throw new ReglaNegocioException("No se encontró el cliente.");
        }
        return (await ObtenerAsync(c.Id))!;
    }

    /// <summary>Solo se puede quitar un cliente que no debe nada (su historial se conserva).</summary>
    public async Task QuitarAsync(int id)
    {
        await using var cn = await db.AbrirAsync();
        var saldo = await cn.ExecuteScalarAsync<decimal?>("SELECT saldo FROM clientes WHERE id = @id AND activo", new { id })
            ?? throw new ReglaNegocioException("No se encontró el cliente.");
        if (saldo != 0)
            throw new ReglaNegocioException($"No se puede quitar: todavía debe {saldo:C2}. Primero tiene que liquidar.");
        await cn.ExecuteAsync("UPDATE clientes SET activo = FALSE WHERE id = @id", new { id });
    }

    public async Task<IEnumerable<MovimientoCliente>> EstadoDeCuentaAsync(int clienteId)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QueryAsync<MovimientoCliente>("""
            SELECT m.id, m.fecha, m.tipo, m.monto, m.saldo_resultante, v.folio, m.forma_pago, u.nombre AS usuario, m.nota
            FROM movimientos_cliente m
            JOIN usuarios u ON u.id = m.usuario_id
            LEFT JOIN ventas v ON v.id = m.venta_id
            WHERE m.cliente_id = @clienteId
            ORDER BY m.fecha DESC, m.id DESC
            LIMIT 500
            """, new { clienteId });
    }

    /// <summary>
    /// El cliente paga (todo o parte de) lo que debe. Queda registrado en el turno: si es en efectivo,
    /// entra al cajón y cuenta en el corte.
    /// </summary>
    public async Task<ComprobanteAbono> AbonarAsync(int clienteId, AbonoRequest r, int usuarioId)
    {
        if (!FormasPago.ParaAbonos.Contains(r.FormaPago))
            throw new ReglaNegocioException("Elija cómo paga: efectivo, tarjeta o transferencia.");
        var monto = Dinero.Redondear(r.Monto);
        if (monto <= 0)
            throw new ReglaNegocioException("Escriba cuánto abona.");

        await using var cn = await db.AbrirAsync();
        await using var tx = await cn.BeginTransactionAsync();

        var turno = await cn.QuerySingleOrDefaultAsync<(string Estado, string Caja, string Cajero)>("""
            SELECT t.estado, c.nombre, u.nombre FROM turnos t JOIN cajas c ON c.id = t.caja_id JOIN usuarios u ON u.id = @usuarioId
            WHERE t.id = @TurnoId FOR UPDATE OF t
            """, new { r.TurnoId, usuarioId }, tx);
        if (turno.Estado != EstadosTurno.Abierto)
            throw new ReglaNegocioException("La caja ya está cerrada. Vuelva a entrar para abrirla.");

        var cliente = await cn.QuerySingleOrDefaultAsync<Cliente>(Select + " WHERE id = @clienteId AND activo FOR UPDATE", new { clienteId }, tx)
            ?? throw new ReglaNegocioException("No se encontró el cliente.");
        if (cliente.Saldo <= 0)
            throw new ReglaNegocioException($"{cliente.Nombre} no debe nada.");
        if (monto > cliente.Saldo)
            throw new ReglaNegocioException($"{cliente.Nombre} solo debe {cliente.Saldo:C2}. No puede abonar más.");

        var saldoNuevo = cliente.Saldo - monto;
        await cn.ExecuteAsync("UPDATE clientes SET saldo = @saldoNuevo WHERE id = @clienteId", new { saldoNuevo, clienteId }, tx);
        await cn.ExecuteAsync("""
            INSERT INTO movimientos_cliente (cliente_id, tipo, monto, saldo_resultante, turno_id, forma_pago, referencia, usuario_id)
            VALUES (@clienteId, 'ABONO', @monto, @saldoNuevo, @TurnoId, @FormaPago, @Referencia, @usuarioId)
            """, new { clienteId, monto = -monto, saldoNuevo, r.TurnoId, r.FormaPago, r.Referencia, usuarioId }, tx);
        await tx.CommitAsync();

        return new ComprobanteAbono
        {
            Cliente = cliente.Nombre,
            Fecha = DateTime.Now,
            Caja = turno.Caja,
            Cajero = turno.Cajero,
            SaldoAnterior = cliente.Saldo,
            Monto = monto,
            FormaPago = r.FormaPago,
            SaldoNuevo = saldoNuevo,
            Negocio = await configuracion.ObtenerNegocioAsync()
        };
    }

    private static void Normalizar(Cliente c)
    {
        static string? Limpio(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

        c.Nombre = c.Nombre.Trim();
        c.Telefono = Limpio(c.Telefono);
        c.Direccion = Limpio(c.Direccion);
        c.Rfc = Limpio(c.Rfc)?.ToUpperInvariant().Replace(" ", "").Replace("-", "");
        c.RazonSocial = Limpio(c.RazonSocial)?.ToUpperInvariant();
        c.CodigoPostalFiscal = Limpio(c.CodigoPostalFiscal);
        c.RegimenFiscal = Limpio(c.RegimenFiscal);
        c.UsoCfdi = Limpio(c.UsoCfdi);
        c.Correo = Limpio(c.Correo);

        if (c.Nombre.Length == 0)
            throw new ReglaNegocioException("Falta el nombre del cliente.");
        if (c.LimiteCredito < 0)
            throw new ReglaNegocioException("El límite de crédito no puede ser negativo.");
        if (c.DescuentoPorcentaje is < 0 or > 90)
            throw new ReglaNegocioException("El descuento debe estar entre 0% y 90%.");
        if (c.Rfc is not null && !Cliente.FormatoRfc().IsMatch(c.Rfc))
            throw new ReglaNegocioException("El RFC no es válido. Ejemplos: LOPM800101AB1 (persona) o ABC010101AB1 (empresa).");
        if (c.CodigoPostalFiscal is not null && (c.CodigoPostalFiscal.Length != 5 || !c.CodigoPostalFiscal.All(char.IsAsciiDigit)))
            throw new ReglaNegocioException("El código postal debe tener 5 números.");
        if (c.Rfc is not null && (c.RazonSocial is null || c.CodigoPostalFiscal is null || c.RegimenFiscal is null))
            throw new ReglaNegocioException("Para facturar se necesitan RFC, razón social, código postal y régimen fiscal.");
        if (c.Correo is not null && !c.Correo.Contains('@'))
            throw new ReglaNegocioException("El correo no es válido.");
    }
}
