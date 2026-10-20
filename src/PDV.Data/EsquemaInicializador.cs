using Dapper;

namespace PDV.Data;

public static class EsquemaInicializador
{
    public const string AdminLogin = "admin";
    public const string AdminPasswordInicial = "admin";

    /// <summary>
    /// Ejecuta los scripts Sql/*.sql (embebidos, en orden alfabético) y crea el usuario
    /// administrador inicial si la tabla de usuarios está vacía.
    /// </summary>
    /// <returns>true si se creó el administrador inicial.</returns>
    public static async Task<bool> AplicarAsync(Db db, CancellationToken ct = default)
    {
        var ensamblado = typeof(EsquemaInicializador).Assembly;
        await using var cn = await db.AbrirAsync(ct);

        foreach (var recurso in ensamblado.GetManifestResourceNames().Where(n => n.EndsWith(".sql")).Order())
        {
            await using var stream = ensamblado.GetManifestResourceStream(recurso)!;
            using var lector = new StreamReader(stream);
            await cn.ExecuteAsync(new CommandDefinition(await lector.ReadToEndAsync(ct), cancellationToken: ct));
        }

        var hayUsuarios = await cn.ExecuteScalarAsync<bool>("SELECT EXISTS (SELECT 1 FROM usuarios)");
        if (hayUsuarios)
            return false;

        await cn.ExecuteAsync(
            "INSERT INTO usuarios (login, nombre, password_hash, rol_id) VALUES (@login, 'Administrador', @hash, 1)",
            new { login = AdminLogin, hash = BCrypt.Net.BCrypt.HashPassword(AdminPasswordInicial) });
        return true;
    }
}
