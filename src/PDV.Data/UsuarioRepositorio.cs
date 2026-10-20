using Dapper;
using PDV.Core;

namespace PDV.Data;

public sealed class UsuarioRepositorio(Db db)
{
    private const string Select = """
        SELECT u.id, u.login, u.nombre, u.password_hash, r.nombre AS rol, u.activo, u.permisos, u.clave_autorizacion_hash
        FROM usuarios u JOIN roles r ON r.id = u.rol_id
        """;

    /// <summary>
    /// La persona activa dueña de esa clave de autorización, o null. Se compara contra el hash de cada persona
    /// que tiene clave (son pocas), porque cada hash lleva su propia sal.
    /// </summary>
    public async Task<Usuario?> BuscarPorClaveAsync(string clave)
    {
        if (string.IsNullOrEmpty(clave))
            return null;
        await using var cn = await db.AbrirAsync();
        var conClave = await cn.QueryAsync<Usuario>(Select + " WHERE u.activo AND u.clave_autorizacion_hash IS NOT NULL");
        return conClave.FirstOrDefault(u => BCrypt.Net.BCrypt.Verify(clave, u.ClaveAutorizacionHash));
    }

    /// <summary>
    /// Revisa una clave nueva: largo mínimo y que no sea la de otra persona (si dos tuvieran la misma,
    /// no se sabría quién autorizó). Devuelve el hash para guardarlo.
    /// </summary>
    private static async Task<string> HashClaveNuevaAsync(System.Data.Common.DbConnection cn, string clave, int? exceptoId)
    {
        if (clave.Length < Permisos.LargoMinimoClave)
            throw new ReglaNegocioException($"La clave de autorización debe tener al menos {Permisos.LargoMinimoClave} letras o números.");
        var otras = await cn.QueryAsync<string>(
            "SELECT clave_autorizacion_hash FROM usuarios WHERE activo AND clave_autorizacion_hash IS NOT NULL AND id <> @id",
            new { id = exceptoId ?? 0 });
        if (otras.Any(h => BCrypt.Net.BCrypt.Verify(clave, h)))
            throw new ReglaNegocioException("Esa clave de autorización no se puede usar. Elija otra.");
        return BCrypt.Net.BCrypt.HashPassword(clave);
    }

    /// <summary>Pone o cambia la clave de autorización de la persona.</summary>
    public async Task GuardarClaveAutorizacionAsync(int usuarioId, string clave)
    {
        await using var cn = await db.AbrirAsync();
        var hash = await HashClaveNuevaAsync(cn, clave ?? "", usuarioId);
        var filas = await cn.ExecuteAsync("UPDATE usuarios SET clave_autorizacion_hash = @hash WHERE id = @usuarioId", new { usuarioId, hash });
        if (filas == 0)
            throw new ReglaNegocioException("No se encontró a esa persona.");
    }

    /// <summary>Devuelve el usuario si el login y la contraseña son correctos y está activo.</summary>
    public async Task<Usuario?> AutenticarAsync(string login, string password)
    {
        await using var cn = await db.AbrirAsync();
        var usuario = await cn.QuerySingleOrDefaultAsync<Usuario>(
            Select + " WHERE lower(u.login) = lower(@login)", new { login = login.Trim() });

        if (usuario is null || !usuario.Activo || !BCrypt.Net.BCrypt.Verify(password, usuario.PasswordHash))
            return null;
        return usuario;
    }

    public async Task<IReadOnlyList<UsuarioDto>> ListarAsync()
    {
        await using var cn = await db.AbrirAsync();
        var usuarios = await cn.QueryAsync<Usuario>(Select + " ORDER BY u.nombre");
        var cajas = (await cn.QueryAsync<(int UsuarioId, int CajaId)>("SELECT usuario_id, caja_id FROM usuario_cajas ORDER BY caja_id"))
            .ToLookup(x => x.UsuarioId, x => x.CajaId);
        return usuarios.Select(u => new UsuarioDto(u.Id, u.Login, u.Nombre, u.Rol, u.Activo, cajas[u.Id].ToArray(), u.Permisos,
            u.ClaveAutorizacionHash is not null)).ToList();
    }

    /// <summary>La persona (activa o no), para revisar sus permisos en el momento.</summary>
    public async Task<Usuario?> ObtenerAsync(int id)
    {
        await using var cn = await db.AbrirAsync();
        return await cn.QuerySingleOrDefaultAsync<Usuario>(Select + " WHERE u.id = @id", new { id });
    }

    /// <summary>
    /// Permisos especiales de la persona (reemplaza los que tenía). Si se le dan permisos y no tiene clave de
    /// autorización, la clave es obligatoria. Si se le quitan todos, su clave se borra (si después se le vuelven
    /// a dar, se le pide una nueva).
    /// </summary>
    public async Task AsignarPermisosAsync(int usuarioId, string[] permisos, string? claveNueva = null)
    {
        var desconocidos = permisos.Except(Permisos.Todos).ToList();
        if (desconocidos.Count > 0)
            throw new ReglaNegocioException($"Permiso desconocido: {string.Join(", ", desconocidos)}.");
        permisos = permisos.Distinct().ToArray();

        await using var cn = await db.AbrirAsync();
        var u = await cn.QuerySingleOrDefaultAsync<Usuario>(Select + " WHERE u.id = @usuarioId", new { usuarioId })
            ?? throw new ReglaNegocioException("No se encontró a esa persona.");

        var hash = u.ClaveAutorizacionHash;
        if (!string.IsNullOrEmpty(claveNueva))
            hash = await HashClaveNuevaAsync(cn, claveNueva, usuarioId);
        else if (Permisos.NecesitaClave(u.Rol, permisos) && hash is null)
            throw new ReglaNegocioException("Para darle permisos especiales, escriba su clave de autorización.");
        if (!Permisos.NecesitaClave(u.Rol, permisos))
            hash = null;

        await cn.ExecuteAsync("UPDATE usuarios SET permisos = @permisos, clave_autorizacion_hash = @hash WHERE id = @usuarioId",
            new { usuarioId, permisos, hash });
    }

    /// <summary>Ids de las personas que pueden entrar (para cortar al instante la sesión de alguien que se quitó).</summary>
    public async Task<HashSet<int>> ListarIdsActivosAsync()
    {
        await using var cn = await db.AbrirAsync();
        return (await cn.QueryAsync<int>("SELECT id FROM usuarios WHERE activo")).ToHashSet();
    }

    /// <summary>Define en qué cajas puede trabajar la persona. Lista vacía = todas las cajas.</summary>
    public async Task AsignarCajasAsync(int usuarioId, int[] cajas)
    {
        await using var cn = await db.AbrirAsync();
        await using var tx = await cn.BeginTransactionAsync();
        await cn.ExecuteAsync("DELETE FROM usuario_cajas WHERE usuario_id = @usuarioId", new { usuarioId }, tx);
        await cn.ExecuteAsync(
            "INSERT INTO usuario_cajas (usuario_id, caja_id) SELECT @usuarioId, unnest(@cajas) ON CONFLICT DO NOTHING",
            new { usuarioId, cajas = cajas.Distinct().ToArray() }, tx);
        await tx.CommitAsync();
    }

    public async Task<int> CrearAsync(CrearUsuarioRequest r)
    {
        if (string.IsNullOrWhiteSpace(r.Nombre))
            throw new ReglaNegocioException("Falta el nombre de la persona.");
        if (string.IsNullOrWhiteSpace(r.Login))
            throw new ReglaNegocioException("Falta el usuario con el que va a entrar.");
        if (r.Password.Length < 4)
            throw new ReglaNegocioException("La contraseña debe tener al menos 4 letras o números.");
        if (!Roles.Todos.Contains(r.Rol))
            throw new ReglaNegocioException("Elija qué puede hacer la persona.");
        var permisos = (r.Permisos ?? []).Distinct().ToArray();
        var desconocidos = permisos.Except(Permisos.Todos).ToList();
        if (desconocidos.Count > 0)
            throw new ReglaNegocioException($"Permiso desconocido: {string.Join(", ", desconocidos)}.");
        if (r.Rol == Roles.Administrador)
            permisos = []; // el encargado ya los tiene todos
        var necesitaClave = Permisos.NecesitaClave(r.Rol, permisos);
        if (necesitaClave && string.IsNullOrEmpty(r.ClaveAutorizacion))
            throw new ReglaNegocioException("Falta la clave de autorización (la necesita el encargado y quien tiene permisos especiales).");

        await using var cn = await db.AbrirAsync();
        var datos = new
        {
            login = r.Login.Trim(), nombre = r.Nombre.Trim(), hash = BCrypt.Net.BCrypt.HashPassword(r.Password), rol = r.Rol, permisos,
            clave = necesitaClave ? await HashClaveNuevaAsync(cn, r.ClaveAutorizacion!, null) : null
        };

        // Si el usuario existía pero se había quitado, se recupera con los datos nuevos.
        var recuperado = await cn.ExecuteScalarAsync<int?>("""
            UPDATE usuarios SET nombre = @nombre, password_hash = @hash, activo = TRUE,
                   rol_id = (SELECT id FROM roles WHERE nombre = @rol), permisos = @permisos, clave_autorizacion_hash = @clave
            WHERE lower(login) = lower(@login) AND NOT activo
            RETURNING id
            """, datos);
        if (recuperado is { } id)
            return id;

        return await cn.ExecuteScalarAsync<int>("""
            INSERT INTO usuarios (login, nombre, password_hash, rol_id, permisos, clave_autorizacion_hash)
            VALUES (@login, @nombre, @hash, (SELECT id FROM roles WHERE nombre = @rol), @permisos, @clave)
            RETURNING id
            """, datos);
    }

    public async Task CambiarPasswordAsync(int id, string password)
    {
        if (password.Length < 4)
            throw new ReglaNegocioException("La contraseña debe tener al menos 4 letras o números.");
        await using var cn = await db.AbrirAsync();
        await cn.ExecuteAsync("UPDATE usuarios SET password_hash = @hash WHERE id = @id",
            new { id, hash = BCrypt.Net.BCrypt.HashPassword(password) });
    }

    public async Task CambiarEstadoAsync(int id, bool activo)
    {
        await using var cn = await db.AbrirAsync();
        await cn.ExecuteAsync("UPDATE usuarios SET activo = @activo WHERE id = @id", new { id, activo });
    }
}
