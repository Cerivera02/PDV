using Dapper;
using PDV.Core;

namespace PDV.Data;

public sealed class DepartamentoRepositorio(Db db)
{
    public async Task<IEnumerable<Departamento>> ListarAsync(bool incluirInactivos)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QueryAsync<Departamento>(
            "SELECT id, nombre, activo FROM departamentos" + (incluirInactivos ? "" : " WHERE activo") + " ORDER BY nombre");
    }

    /// <summary>Crea (id = 0) o actualiza un departamento. Desactivarlo no afecta a sus productos.</summary>
    public async Task<Departamento> GuardarAsync(int id, DepartamentoRequest r)
    {
        var nombre = r.Nombre.Trim();
        if (nombre.Length is 0 or > 80)
            throw new ReglaNegocioException("El nombre del departamento es obligatorio (máximo 80 caracteres).");

        await using var cn = await db.AbrirAsync();
        var departamento = id == 0
            ? await cn.QuerySingleAsync<Departamento>(
                "INSERT INTO departamentos (nombre, activo) VALUES (@nombre, @Activo) RETURNING id, nombre, activo",
                new { nombre, r.Activo })
            : await cn.QuerySingleOrDefaultAsync<Departamento>(
                "UPDATE departamentos SET nombre = @nombre, activo = @Activo WHERE id = @id RETURNING id, nombre, activo",
                new { id, nombre, r.Activo });

        return departamento ?? throw new ReglaNegocioException("El departamento no existe.");
    }

    /// <summary>Quita el departamento. Sus productos NO se borran: quedan "sin departamento".</summary>
    /// <returns>Ids de los productos que quedaron sin departamento.</returns>
    public async Task<List<int>> QuitarAsync(int id)
    {
        await using var cn = await db.AbrirAsync();
        await using var tx = await cn.BeginTransactionAsync();
        var afectados = (await cn.QueryAsync<int>(
            "UPDATE productos SET departamento_id = NULL, actualizado_en = now() WHERE departamento_id = @id RETURNING id",
            new { id }, tx)).ToList();
        await cn.ExecuteAsync("DELETE FROM departamentos WHERE id = @id", new { id }, tx);
        await tx.CommitAsync();
        return afectados;
    }
}
