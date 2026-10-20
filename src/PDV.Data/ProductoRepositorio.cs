using Dapper;
using PDV.Core;

namespace PDV.Data;

public sealed class ProductoRepositorio(Db db)
{
    // Columnas calificadas con "p." porque departamentos también tiene id, nombre y activo.
    private const string Select = """
        SELECT p.id, p.codigo_barras, p.codigo_interno, p.nombre, p.departamento_id, d.nombre AS departamento_nombre,
               p.unidad, p.por_peso, p.precio_costo, p.precio_venta, p.tasa_iva, p.existencia, p.stock_minimo, p.activo
        FROM productos p
        LEFT JOIN departamentos d ON d.id = p.departamento_id
        """;

    /// <summary>Catálogo completo (con sus ofertas); las cajas lo guardan en memoria para buscar sin red.</summary>
    public async Task<IEnumerable<Producto>> ListarAsync(bool incluirInactivos)
    {
        await using var cn = await db.AbrirAsync();
        var productos = (await cn.QueryAsync<Producto>(Select + (incluirInactivos ? "" : " WHERE p.activo") + " ORDER BY p.nombre")).ToList();
        return await ConOfertasAsync(cn, productos);
    }

    public async Task<Producto?> ObtenerAsync(int id)
    {
        await using var cn = await db.AbrirAsync();
        var p = await cn.QuerySingleOrDefaultAsync<Producto>(Select + " WHERE p.id = @id", new { id });
        return p is null ? null : (await ConOfertasAsync(cn, [p]))[0];
    }

    /// <summary>Busca por código de barras principal, código alterno o PLU.</summary>
    public async Task<Producto?> ObtenerPorCodigoAsync(string codigo)
    {
        await using var cn = await db.AbrirAsync();
        var p = await cn.QueryFirstOrDefaultAsync<Producto>(Select + """
             WHERE p.activo AND (p.codigo_barras = @codigo
                OR p.codigo_interno = @codigo
                OR p.id = (SELECT producto_id FROM producto_codigos WHERE codigo_barras = @codigo))
            """, new { codigo = codigo.Trim() });
        return p is null ? null : (await ConOfertasAsync(cn, [p]))[0];
    }

    public async Task<IEnumerable<Producto>> BuscarAsync(string texto)
    {
        await using var cn = await db.AbrirAsync();
        var productos = (await cn.QueryAsync<Producto>(Select + """
             WHERE p.activo AND (unaccent(p.nombre) ILIKE '%' || unaccent(@texto) || '%' OR p.codigo_barras = @texto)
             ORDER BY similarity(p.nombre, @texto) DESC, p.nombre
             LIMIT 50
            """, new { texto = texto.Trim() })).ToList();
        return await ConOfertasAsync(cn, productos);
    }

    private static async Task<List<Producto>> ConOfertasAsync(Npgsql.NpgsqlConnection cn, List<Producto> productos)
    {
        if (productos.Count == 0)
            return productos;
        var ofertas = await OfertaRepositorio.ListarPorProductosAsync(cn, productos.Select(p => p.Id).ToArray());
        foreach (var p in productos)
            p.Ofertas = [.. ofertas[p.Id]];
        return productos;
    }

    /// <summary>
    /// Inserta o actualiza el producto. La existencia NO se modifica aquí:
    /// solo cambia mediante movimientos de inventario (kardex).
    /// Si cambia el precio o el costo, el trigger de la BD lo guarda en el historial de precios.
    /// </summary>
    /// <param name="usuarioId">Quién hizo el cambio (queda en el historial de precios).</param>
    public async Task<Producto> GuardarAsync(Producto p, int? usuarioId = null)
    {
        p.CodigoBarras = p.CodigoBarras.Trim();
        p.Nombre = p.Nombre.Trim();
        p.CodigoInterno = string.IsNullOrWhiteSpace(p.CodigoInterno) ? null : EtiquetaBascula.NormalizarPlu(p.CodigoInterno);
        p.Unidad = string.IsNullOrWhiteSpace(p.Unidad) ? "PZA" : p.Unidad.Trim().ToUpperInvariant();

        if (p.CodigoBarras.Length == 0)
            throw new ReglaNegocioException("Falta el código de barras.");
        if (p.Nombre.Length == 0)
            throw new ReglaNegocioException("Falta el nombre del producto.");
        if (p.PrecioVenta < 0 || p.PrecioCosto < 0)
            throw new ReglaNegocioException("Los precios no pueden ser negativos.");
        if (p.TasaIva is < 0 or >= 1)
            throw new ReglaNegocioException("El IVA no es válido.");

        await using var cn = await db.AbrirAsync();
        await using var tx = await cn.BeginTransactionAsync();
        await AnotarQuienAsync(cn, tx, usuarioId, origen: null);

        // Si se da de alta un código que pertenecía a un producto quitado, se recupera ese producto
        // (conserva su historial) en lugar de marcar "ya existe".
        if (p.Id == 0 && await cn.ExecuteScalarAsync<int?>(
                "SELECT id FROM productos WHERE codigo_barras = @CodigoBarras AND NOT activo", p, tx) is { } quitado)
        {
            p.Id = quitado;
            p.Activo = true;
        }

        if (p.Id == 0)
        {
            p.Id = await cn.ExecuteScalarAsync<int>("""
                INSERT INTO productos (codigo_barras, codigo_interno, nombre, departamento_id, unidad, por_peso,
                                       precio_costo, precio_venta, tasa_iva, stock_minimo, activo)
                VALUES (@CodigoBarras, @CodigoInterno, @Nombre, @DepartamentoId, @Unidad, @PorPeso,
                        @PrecioCosto, @PrecioVenta, @TasaIva, @StockMinimo, @Activo)
                RETURNING id
                """, p, tx);
        }
        else
        {
            var filas = await cn.ExecuteAsync("""
                UPDATE productos SET codigo_barras = @CodigoBarras, codigo_interno = @CodigoInterno, nombre = @Nombre,
                       departamento_id = @DepartamentoId, unidad = @Unidad, por_peso = @PorPeso, precio_costo = @PrecioCosto,
                       precio_venta = @PrecioVenta, tasa_iva = @TasaIva, stock_minimo = @StockMinimo, activo = @Activo,
                       actualizado_en = now()
                WHERE id = @Id
                """, p, tx);
            if (filas == 0)
                throw new ReglaNegocioException("El producto no existe.");
        }
        await tx.CommitAsync();

        return (await ObtenerAsync(p.Id))!;
    }

    /// <summary>Todos los cambios de precio y costo del producto, lo más reciente primero.</summary>
    public async Task<IEnumerable<CambioPrecio>> HistorialPreciosAsync(int productoId)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QueryAsync<CambioPrecio>("""
            SELECT h.id, h.fecha, h.precio_venta, h.precio_costo, h.precio_venta_anterior, h.precio_costo_anterior,
                   h.origen, u.nombre AS usuario
            FROM producto_precios h LEFT JOIN usuarios u ON u.id = h.usuario_id
            WHERE h.producto_id = @productoId
            ORDER BY h.fecha DESC, h.id DESC
            """, new { productoId });
    }

    /// <summary>
    /// Le dice al trigger del historial de precios quién hace el cambio y desde dónde
    /// (solo dura lo que la transacción).
    /// </summary>
    internal static Task AnotarQuienAsync(Npgsql.NpgsqlConnection cn, Npgsql.NpgsqlTransaction tx, int? usuarioId, string? origen) =>
        cn.ExecuteAsync("SELECT set_config('pdv.usuario_id', @usuario, true), set_config('pdv.origen', @origen, true)",
            new { usuario = usuarioId?.ToString() ?? "", origen = origen ?? "" }, tx);
}
