using Dapper;
using PDV.Core;

namespace PDV.Data;

public sealed class InventarioRepositorio(Db db)
{
    /// <summary>
    /// ENTRADA: suma la cantidad (y actualiza el costo si se indica).
    /// MERMA:   resta la cantidad.
    /// AJUSTE:  la cantidad es el conteo físico; se registra la diferencia.
    /// </summary>
    /// <returns>Existencia resultante.</returns>
    public async Task<decimal> RegistrarAsync(MovimientoInventarioRequest r, int usuarioId)
    {
        if (!TiposMovimiento.Manuales.Contains(r.Tipo))
            throw new ReglaNegocioException($"Tipo inválido. Opciones: {string.Join(", ", TiposMovimiento.Manuales)}.");
        if (r.Cantidad < 0 || (r.Cantidad == 0 && r.Tipo != TiposMovimiento.Ajuste))
            throw new ReglaNegocioException("Escriba una cantidad mayor a cero.");

        await using var cn = await db.AbrirAsync();
        await using var tx = await cn.BeginTransactionAsync();

        var existencia = await cn.QuerySingleOrDefaultAsync<decimal?>(
            "SELECT existencia FROM productos WHERE id = @id FOR UPDATE", new { id = r.ProductoId }, tx)
            ?? throw new ReglaNegocioException("No se encontró el producto.");

        var cantidad = Dinero.RedondearCantidad(r.Cantidad);
        var delta = r.Tipo switch
        {
            TiposMovimiento.Entrada => cantidad,
            TiposMovimiento.Merma => -cantidad,
            _ => cantidad - existencia // AJUSTE
        };
        var nueva = existencia + delta;

        // Si con la mercancía cambia el costo, queda en el historial de precios
        await ProductoRepositorio.AnotarQuienAsync(cn, tx, usuarioId, OrigenesPrecio.Mercancia);

        await cn.ExecuteAsync("""
            UPDATE productos
            SET existencia = @nueva,
                precio_costo = CASE WHEN @costo IS NOT NULL THEN @costo ELSE precio_costo END,
                actualizado_en = now()
            WHERE id = @id
            """, new { id = r.ProductoId, nueva, costo = r.Tipo == TiposMovimiento.Entrada ? r.CostoUnitario : null }, tx);

        await cn.ExecuteAsync("""
            INSERT INTO movimientos_inventario (producto_id, tipo, cantidad, existencia_resultante, costo_unitario, nota, usuario_id)
            VALUES (@ProductoId, @Tipo, @delta, @nueva, @CostoUnitario, @Nota, @usuarioId)
            """, new { r.ProductoId, r.Tipo, delta, nueva, r.CostoUnitario, r.Nota, usuarioId }, tx);

        await tx.CommitAsync();
        return nueva;
    }

    public async Task<IEnumerable<MovimientoInventario>> KardexAsync(int productoId, int limite = 200)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QueryAsync<MovimientoInventario>("""
            SELECT m.id, m.fecha, m.tipo, m.cantidad, m.existencia_resultante, m.costo_unitario,
                   m.referencia, u.nombre AS usuario, m.nota
            FROM movimientos_inventario m JOIN usuarios u ON u.id = m.usuario_id
            WHERE m.producto_id = @productoId
            ORDER BY m.fecha DESC, m.id DESC
            LIMIT @limite
            """, new { productoId, limite });
    }
}
