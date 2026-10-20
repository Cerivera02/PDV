using Dapper;
using Npgsql;
using PDV.Core;

namespace PDV.Data;

public sealed class OfertaRepositorio(Db db)
{
    private const string Select = "SELECT id, producto_id, tipo, precio, cantidad, paga, desde, hasta FROM ofertas";

    /// <summary>Ofertas que aplican hoy o más adelante (las terminadas no se muestran ni se mandan a las cajas).</summary>
    public async Task<IEnumerable<Oferta>> ListarDeProductoAsync(int productoId)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QueryAsync<Oferta>(Select + """
             WHERE producto_id = @productoId AND (hasta IS NULL OR hasta >= CURRENT_DATE)
             ORDER BY tipo, desde NULLS FIRST, cantidad
            """, new { productoId });
    }

    /// <summary>Para el catálogo y las ventas: ofertas vigentes o futuras de varios productos.</summary>
    public static async Task<ILookup<int, Oferta>> ListarPorProductosAsync(NpgsqlConnection cn, int[] productoIds, NpgsqlTransaction? tx = null)
    {
        var ofertas = await cn.QueryAsync<Oferta>(Select + """
             WHERE producto_id = ANY(@productoIds) AND (hasta IS NULL OR hasta >= CURRENT_DATE)
            """, new { productoIds }, tx);
        return ofertas.ToLookup(o => o.ProductoId);
    }

    public async Task<Oferta> CrearAsync(int productoId, OfertaRequest r)
    {
        await using var cn = await db.AbrirAsync();
        var producto = await cn.QuerySingleOrDefaultAsync<Producto>(
            "SELECT id, nombre, por_peso, precio_venta FROM productos WHERE id = @productoId AND activo", new { productoId })
            ?? throw new ReglaNegocioException("No se encontró el producto.");

        Validar(producto, r);

        return await cn.QuerySingleAsync<Oferta>("""
            INSERT INTO ofertas (producto_id, tipo, precio, cantidad, paga, desde, hasta)
            VALUES (@productoId, @Tipo, @Precio, @Cantidad, @Paga, @Desde, @Hasta)
            RETURNING id, producto_id, tipo, precio, cantidad, paga, desde, hasta
            """,
            new { productoId, r.Tipo, r.Precio, r.Cantidad, r.Paga, Desde = r.Desde?.Date, Hasta = r.Hasta?.Date });
    }

    /// <returns>Id del producto (para avisar a las cajas), o null si no existía.</returns>
    public async Task<int?> QuitarAsync(int id)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.ExecuteScalarAsync<int?>("DELETE FROM ofertas WHERE id = @id RETURNING producto_id", new { id });
    }

    private static void Validar(Producto p, OfertaRequest r)
    {
        if (r.Desde is { } d && r.Hasta is { } h && h.Date < d.Date)
            throw new ReglaNegocioException("La fecha de fin no puede ser antes de la fecha de inicio.");
        if (r.Hasta is { } fin && fin.Date < DateTime.Today)
            throw new ReglaNegocioException("La fecha de fin ya pasó.");

        switch (r.Tipo)
        {
            case TiposOferta.Precio:
                if (r.Precio is not > 0)
                    throw new ReglaNegocioException("Escriba el precio de oferta.");
                if (r.Precio >= p.PrecioVenta)
                    throw new ReglaNegocioException($"El precio de oferta debe ser menor al precio normal ({p.PrecioVenta:C2}).");
                if (r.Desde is null || r.Hasta is null)
                    throw new ReglaNegocioException("Elija desde qué día y hasta qué día dura la oferta.");
                break;

            case TiposOferta.NxM:
                if (p.PorPeso)
                    throw new ReglaNegocioException("Las promociones tipo 2x1 o 3x2 son solo para productos que se venden por pieza.");
                if (r.Cantidad is not { } lleva || lleva != decimal.Truncate(lleva) || lleva < 2)
                    throw new ReglaNegocioException("\"Lleva\" debe ser un número entero de 2 o más.");
                if (r.Paga is not { } paga || paga < 1 || paga >= lleva)
                    throw new ReglaNegocioException("\"Paga\" debe ser menor que \"lleva\" (ejemplo: lleva 3, paga 2).");
                break;

            case TiposOferta.Mayoreo:
                if (r.Cantidad is not > 0)
                    throw new ReglaNegocioException("Escriba a partir de cuántas aplica el mayoreo.");
                if (!p.PorPeso && (r.Cantidad != decimal.Truncate(r.Cantidad.Value) || r.Cantidad < 2))
                    throw new ReglaNegocioException("Para productos por pieza, el mayoreo debe ser a partir de 2 piezas o más (número entero).");
                if (r.Precio is not > 0)
                    throw new ReglaNegocioException("Escriba el precio de mayoreo.");
                if (r.Precio >= p.PrecioVenta)
                    throw new ReglaNegocioException($"El precio de mayoreo debe ser menor al precio normal ({p.PrecioVenta:C2}).");
                break;

            default:
                throw new ReglaNegocioException("Tipo de oferta no válido.");
        }
    }
}
