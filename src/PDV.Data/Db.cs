using Dapper;
using Npgsql;

namespace PDV.Data;

/// <summary>Punto único de acceso a PostgreSQL (pool de conexiones de Npgsql).</summary>
public sealed class Db : IDisposable
{
    private readonly NpgsqlDataSource _dataSource;

    static Db()
    {
        // productos.precio_venta -> Producto.PrecioVenta
        DefaultTypeMap.MatchNamesWithUnderscores = true;
    }

    public Db(string cadenaConexion) => _dataSource = NpgsqlDataSource.Create(cadenaConexion);

    public ValueTask<NpgsqlConnection> AbrirAsync(CancellationToken ct = default) => _dataSource.OpenConnectionAsync(ct);

    /// <summary>Descarta las conexiones guardadas en el pool (después de restaurar un respaldo ya no sirven).</summary>
    public void LimpiarConexiones() => _dataSource.Clear();

    public void Dispose() => _dataSource.Dispose();
}
