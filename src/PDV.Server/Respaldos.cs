using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Dapper;
using Microsoft.AspNetCore.SignalR;
using Npgsql;
using PDV.Core;
using PDV.Data;

namespace PDV.Server;

/// <summary>
/// Respaldos de la base de datos con pg_dump / pg_restore (formato comprimido "-F c").
/// <list type="bullet">
/// <item>Cada caja avisa al cerrar el programa → se hace un respaldo en segundo plano.</item>
/// <item>Si llegan varios avisos mientras se respalda, se juntan en UNO más al terminar (la cola tiene un solo lugar):
///       así el último respaldo siempre incluye todo lo que pasó antes del último cierre.</item>
/// <item>Restaurar primero guarda una copia de cómo está la base ("por si se arrepiente"), corta a las cajas,
///       restaura en UNA transacción (si algo falla, la base queda como estaba) y vuelve a aplicar las migraciones.</item>
/// </list>
/// Configuración (appsettings.json, sección "Respaldos"): Carpeta, PgBin, DiasConservar, MinimoConservar, MaximoArchivos.
/// </summary>
public sealed class Respaldos : BackgroundService
{
    public const string Extension = ".backup";

    private readonly NpgsqlConnectionStringBuilder _conexion;
    private readonly Db _db;
    private readonly AccesoCajas _acceso;
    private readonly IHubContext<PdvHub> _hub;
    private readonly ILogger<Respaldos> _log;
    private readonly IConfiguration _config;
    private readonly SemaphoreSlim _uno = new(1, 1); // un pg_dump / pg_restore a la vez
    private readonly Channel<string> _cola = Channel.CreateBounded<string>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
    private string? _pgBin;

    static Respaldos() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public Respaldos(IConfiguration config, Db db, AccesoCajas acceso, IHubContext<PdvHub> hub, ILogger<Respaldos> log)
    {
        _config = config;
        _conexion = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Pdv"));
        _db = db;
        _acceso = acceso;
        _hub = hub;
        _log = log;
        Carpeta = config["Respaldos:Carpeta"] is { Length: > 0 } c
            ? c
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "PDV", "Respaldos");
    }

    /// <summary>Carpeta de los respaldos en la computadora del servidor.</summary>
    public string Carpeta { get; }

    /// <summary>Mientras se restaura, el servidor no atiende a las cajas (responde 503).</summary>
    public bool Restaurando { get; private set; }

    // ------------------------------------------------------------------ Respaldar

    /// <summary>Pide un respaldo en segundo plano. Si ya hay uno esperando, se junta con ese.</summary>
    public void Solicitar(string motivo)
    {
        if (!_cola.Writer.TryWrite(motivo))
            _log.LogInformation("Respaldo pedido ({Motivo}): se junta con el que ya está en espera", motivo);
    }

    protected override async Task ExecuteAsync(CancellationToken detener)
    {
        await foreach (var motivo in _cola.Reader.ReadAllAsync(detener))
        {
            try
            {
                await CrearAsync(motivo, detener);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.LogError(e, "No se pudo hacer el respaldo ({Motivo})", motivo);
            }
        }
    }

    /// <summary>Hace un respaldo ahora y espera a que termine.</summary>
    public async Task<RespaldoDto> CrearAsync(string motivo, CancellationToken ct = default)
    {
        await _uno.WaitAsync(ct);
        try
        {
            return await RespaldarAsync(motivo, ct);
        }
        finally
        {
            _uno.Release();
        }
    }

    private async Task<RespaldoDto> RespaldarAsync(string motivo, CancellationToken ct)
    {
        Directory.CreateDirectory(Carpeta);
        var fecha = DateTimeOffset.Now;
        var nombre = $"pdv_{fecha:yyyyMMdd_HHmmss}{Extension}";
        for (var i = 2; File.Exists(Path.Combine(Carpeta, nombre)); i++)
            nombre = $"pdv_{fecha:yyyyMMdd_HHmmss}_{i}{Extension}";
        var archivo = Path.Combine(Carpeta, nombre);
        var parcial = archivo + ".parcial"; // si se corta a la mitad, no queda un respaldo "a medias" en la lista

        var reloj = Stopwatch.StartNew();
        try
        {
            await EjecutarAsync("pg_dump.exe", ["--format=custom", "--file", parcial, ..Conexion()], ct);
            File.Move(parcial, archivo);
        }
        finally
        {
            File.Delete(parcial);
        }

        var dto = new RespaldoDto { Nombre = nombre, Fecha = fecha, Motivo = motivo, Bytes = new FileInfo(archivo).Length };
        await File.WriteAllTextAsync(archivo + ".json", JsonSerializer.Serialize(dto), ct);
        _log.LogInformation("Respaldo {Nombre} ({Tamano}, {Segundos:0.0} s): {Motivo}", nombre, dto.TamanoTexto, reloj.Elapsed.TotalSeconds, motivo);

        BorrarViejos();
        return dto;
    }

    public ListaRespaldos Listar()
    {
        var lista = new ListaRespaldos { Carpeta = Carpeta };
        if (!Directory.Exists(Carpeta))
            return lista;
        foreach (var archivo in Directory.EnumerateFiles(Carpeta, "*" + Extension))
            lista.Respaldos.Add(Leer(archivo));
        lista.Respaldos.Sort((a, b) => b.Fecha.CompareTo(a.Fecha));
        return lista;
    }

    /// <summary>Datos del respaldo (del .json de al lado; si falta, de la fecha del archivo).</summary>
    private static RespaldoDto Leer(string archivo)
    {
        var info = new FileInfo(archivo);
        try
        {
            if (JsonSerializer.Deserialize<RespaldoDto>(File.ReadAllText(archivo + ".json")) is { } dto)
            {
                dto.Nombre = info.Name;
                dto.Bytes = info.Length;
                return dto;
            }
        }
        catch (Exception e) when (e is IOException or JsonException) { }
        return new RespaldoDto { Nombre = info.Name, Fecha = info.LastWriteTime, Motivo = "(copiado a la carpeta)", Bytes = info.Length };
    }

    private void BorrarViejos()
    {
        var todos = Listar().Respaldos.Select(r => (r.Nombre, r.Fecha));
        var borrar = RetencionRespaldos.ParaBorrar(todos, DateTimeOffset.Now,
            _config.GetValue("Respaldos:DiasConservar", 30),
            _config.GetValue("Respaldos:MinimoConservar", 10),
            _config.GetValue("Respaldos:MaximoArchivos", 300));
        foreach (var nombre in borrar)
        {
            try
            {
                File.Delete(Path.Combine(Carpeta, nombre));
                File.Delete(Path.Combine(Carpeta, nombre + ".json"));
            }
            catch (IOException e)
            {
                _log.LogWarning(e, "No se pudo borrar el respaldo viejo {Nombre}", nombre);
            }
        }
    }

    // ------------------------------------------------------------------ Restaurar

    /// <summary>
    /// Regresa la base a como estaba en el respaldo <paramref name="nombre"/>.
    /// Antes guarda una copia de cómo está ahora. Si algo falla, la base queda como estaba.
    /// </summary>
    /// <returns>El respaldo automático hecho antes de restaurar.</returns>
    public async Task<RespaldoDto> RestaurarAsync(string nombre, string quien)
    {
        // Solo un nombre de archivo de la carpeta (nada de rutas como ..\..\otro)
        if (Path.GetFileName(nombre) != nombre || !nombre.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
            throw new ReglaNegocioException("Ese respaldo no existe.");
        var archivo = Path.Combine(Carpeta, nombre);
        if (!File.Exists(archivo))
            throw new ReglaNegocioException("Ese respaldo ya no existe. Cierre la lista y vuelva a abrirla.");
        var respaldo = Leer(archivo);

        await _uno.WaitAsync();
        RespaldoDto antes;
        try
        {
            antes = await RespaldarAsync($"Antes de regresar al del {respaldo.FechaLocal:dd/MM HH:mm} ({quien})", CancellationToken.None);

            Restaurando = true;
            _db.LimpiarConexiones();
            await CerrarOtrasConexionesAsync();

            await CargarAsync(archivo);

            _db.LimpiarConexiones();
            // Un respaldo viejo puede no tener las tablas nuevas: se vuelven a aplicar las migraciones
            await EsquemaInicializador.AplicarAsync(_db);
            _acceso.Invalidar();
            _log.LogWarning("{Quien} restauró el respaldo {Nombre} ({Fecha}). Copia previa: {Antes}", quien, nombre, respaldo.FechaLocal, antes.Nombre);
        }
        finally
        {
            Restaurando = false;
            _uno.Release();
        }

        // Todas las cajas tienen que volver a entrar (sus datos en memoria ya no corresponden)
        await _hub.Clients.All.SendAsync(PdvHub.BaseRestaurada,
            $"El encargado regresó la base de datos a como estaba el {respaldo.FechaLocal:dd/MM/yyyy} a las {respaldo.FechaLocal:HH:mm}.\n\nVuelva a entrar.");
        return antes;
    }

    /// <summary>
    /// Reemplaza todo el contenido de la base con el del respaldo, en UNA transacción.
    /// <para>
    /// No se usa "pg_restore --clean": solo borra lo que viene en el respaldo, y si la base tiene tablas más nuevas
    /// (por ejemplo producto_precios) que apuntan a las del respaldo, no las puede borrar y falla.
    /// En su lugar: 1) pg_restore convierte el respaldo en un script SQL (sin tocar la base; si el archivo está dañado
    /// falla aquí); 2) psql, en una sola transacción, borra todo lo del usuario de la base (DROP OWNED) y ejecuta el script.
    /// Si algo falla, la transacción se deshace y la base queda como estaba.
    /// </para>
    /// </summary>
    /// <summary>
    /// Borra todo lo de la app en el esquema public: vistas, tablas (con sus llaves, índices y triggers),
    /// secuencias y funciones propias. Las extensiones (pg_trgm, unaccent) se quedan: el respaldo las pide con IF NOT EXISTS.
    /// </summary>
    private const string BorrarTodo = """
        DO $$
        DECLARE o record;
        BEGIN
            FOR o IN SELECT viewname AS n FROM pg_views WHERE schemaname = 'public' LOOP
                EXECUTE format('DROP VIEW IF EXISTS public.%I CASCADE', o.n);
            END LOOP;
            FOR o IN SELECT tablename AS n FROM pg_tables WHERE schemaname = 'public' LOOP
                EXECUTE format('DROP TABLE IF EXISTS public.%I CASCADE', o.n);
            END LOOP;
            FOR o IN SELECT sequencename AS n FROM pg_sequences WHERE schemaname = 'public' LOOP
                EXECUTE format('DROP SEQUENCE IF EXISTS public.%I CASCADE', o.n);
            END LOOP;
            FOR o IN SELECT p.oid::regprocedure AS f FROM pg_proc p JOIN pg_namespace s ON s.oid = p.pronamespace
                     WHERE s.nspname = 'public'
                       AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e') LOOP
                EXECUTE format('DROP FUNCTION IF EXISTS %s CASCADE', o.f);
            END LOOP;
        END $$;

        """;

    private async Task CargarAsync(string archivo)
    {
        var id = Guid.NewGuid().ToString("N");
        var borrar = Path.Combine(Path.GetTempPath(), $"pdv_restaurar_{id}_1.sql");
        var script = Path.Combine(Path.GetTempPath(), $"pdv_restaurar_{id}_2.sql");
        try
        {
            await EjecutarAsync("pg_restore.exe", ["--no-owner", "--no-acl", "--file", script, archivo], CancellationToken.None);
            await File.WriteAllTextAsync(borrar, BorrarTodo);
            // lock_timeout: si otro programa (por ejemplo pgAdmin) tiene la base ocupada, falla en vez de quedarse esperando
            await EjecutarAsync("psql.exe",
                ["--no-psqlrc", "--quiet", "--single-transaction", "--set", "ON_ERROR_STOP=1", "--file", borrar, "--file", script, ..Conexion()],
                CancellationToken.None, ("PGOPTIONS", "-c lock_timeout=20000"));
        }
        finally
        {
            File.Delete(borrar);
            File.Delete(script);
        }
    }

    /// <summary>Cierra las conexiones de otros programas a la base (con el mismo usuario), para que pg_restore pueda borrar y crear.</summary>
    private async Task CerrarOtrasConexionesAsync()
    {
        await using var cn = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_conexion.ConnectionString) { Pooling = false }.ConnectionString);
        await cn.OpenAsync();
        await cn.ExecuteAsync("""
            SELECT pg_terminate_backend(pid) FROM pg_stat_activity
            WHERE datname = current_database() AND pid <> pg_backend_pid() AND usename = current_user
            """);
    }

    // ------------------------------------------------------------------ pg_dump / pg_restore

    private string[] Conexion(bool sinBase = false)
    {
        string[] datos = ["--host", _conexion.Host ?? "localhost", "--port", _conexion.Port.ToString(), "--username", _conexion.Username ?? "pdv", "--no-password"];
        return sinBase ? datos : [.. datos, "--dbname", _conexion.Database!];
    }

    private async Task EjecutarAsync(string programa, string[] argumentos, CancellationToken ct, params (string Nombre, string Valor)[] entorno)
    {
        var info = new ProcessStartInfo(Path.Combine(await PgBinAsync(), programa))
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var a in argumentos)
            info.ArgumentList.Add(a);
        // La contraseña va en el entorno del proceso, nunca en la línea de comandos
        info.Environment["PGPASSWORD"] = _conexion.Password ?? "";
        foreach (var (nombre, valor) in entorno)
            info.Environment[nombre] = valor;

        using var proceso = Process.Start(info) ?? throw new InvalidOperationException($"No se pudo ejecutar {programa}");
        var errores = LeerMensajesAsync(proceso.StandardError.BaseStream);
        _ = proceso.StandardOutput.BaseStream.CopyToAsync(Stream.Null, ct);
        using var limite = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limite.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await proceso.WaitForExitAsync(limite.Token);
        }
        catch (OperationCanceledException)
        {
            proceso.Kill(entireProcessTree: true);
            throw;
        }
        if (proceso.ExitCode != 0)
        {
            var detalle = (await errores).Trim();
            _log.LogError("{Programa} terminó con código {Codigo}: {Detalle}", programa, proceso.ExitCode, detalle);
            throw new ReglaNegocioException($"No se pudo {(programa.StartsWith("pg_dump") ? "hacer el respaldo" : "restaurar el respaldo")}. " +
                                            $"La base de datos no se cambió.\n\n(Detalle para soporte: {Recortar(detalle)})");
        }
    }

    /// <summary>
    /// Mensajes de pg_dump/pg_restore/psql: los del servidor llegan en UTF-8 y los del programa en la página de códigos
    /// de Windows (1252). Si no es UTF-8 válido, se lee como 1252 (si no, sale "restricci├│n" o "vßlido").
    /// </summary>
    private static async Task<string> LeerMensajesAsync(Stream salida)
    {
        using var memoria = new MemoryStream();
        await salida.CopyToAsync(memoria);
        var bytes = memoria.ToArray();
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
        }
    }

    private static string Recortar(string s) => s.Length <= 400 ? s : s[..400] + "…";

    /// <summary>
    /// Carpeta de pg_dump/pg_restore: Respaldos:PgBin, o la instalación de PostgreSQL de la misma versión que el servidor
    /// (o más nueva: pg_dump no puede respaldar un servidor más nuevo que él).
    /// </summary>
    private async Task<string> PgBinAsync()
    {
        if (_pgBin is not null)
            return _pgBin;
        if (_config["Respaldos:PgBin"] is { Length: > 0 } configurada)
            return _pgBin = configurada;

        await using var cn = await _db.AbrirAsync();
        var version = await cn.ExecuteScalarAsync<int>("SELECT current_setting('server_version_num')::int") / 10000;
        var raiz = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL");
        var instaladas = Directory.Exists(raiz)
            ? Directory.GetDirectories(raiz)
                .Select(d => (Carpeta: Path.Combine(d, "bin"), Version: int.TryParse(Path.GetFileName(d).Split('.')[0], out var v) ? v : 0))
                .Where(x => File.Exists(Path.Combine(x.Carpeta, "pg_dump.exe")))
                .OrderBy(x => x.Version)
                .ToList()
            : [];
        var elegida = instaladas.FirstOrDefault(x => x.Version == version).Carpeta
                      ?? instaladas.FirstOrDefault(x => x.Version > version).Carpeta
                      ?? throw new ReglaNegocioException(
                          $"No se encontró pg_dump de PostgreSQL {version} en esta computadora. Configure Respaldos:PgBin en appsettings.json.");
        return _pgBin = elegida;
    }
}

/// <summary>Mientras se restaura un respaldo, el servidor no atiende a las cajas.</summary>
public sealed class EnMantenimiento(RequestDelegate siguiente)
{
    public async Task InvokeAsync(HttpContext ctx, Respaldos respaldos)
    {
        if (respaldos.Restaurando && !ctx.Request.Path.StartsWithSegments("/api/respaldos"))
        {
            await Results.Problem("Se está restaurando un respaldo de la base de datos. Espere un minuto y vuelva a intentar.",
                statusCode: StatusCodes.Status503ServiceUnavailable).ExecuteAsync(ctx);
            return;
        }
        await siguiente(ctx);
    }
}
