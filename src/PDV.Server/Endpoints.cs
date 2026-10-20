using System.Security.Claims;
using Microsoft.AspNetCore.SignalR;
using PDV.Core;
using PDV.Data;

namespace PDV.Server;

public static class Endpoints
{
    public static void MapPdvEndpoints(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api");

        // ------------------------------------------------------------ Autenticación
        // Orden: 1) la caja (bloqueo e IP), 2) usuario y contraseña, 3) que la persona pueda usar esa caja.
        api.MapPost("/auth/login", async (LoginRequest r, HttpContext ctx, UsuarioRepositorio usuarios, GeneradorToken tokens, AccesoCajas acceso) =>
        {
            if (r.CajaId is not { } cajaId)
                return Results.Problem("Este programa de caja es de una versión anterior. Pida que lo actualicen.", statusCode: StatusCodes.Status400BadRequest);
            if (await acceso.ValidarCajaAsync(cajaId, ctx.Connection.RemoteIpAddress) is { } errorCaja)
                return SinAcceso.Resultado(errorCaja);

            var u = await usuarios.AutenticarAsync(r.Usuario, r.Password);
            if (u is null)
                return Results.Problem("El usuario o la contraseña no son correctos.", statusCode: StatusCodes.Status401Unauthorized);

            if (await acceso.ValidarPersonaAsync(u.Id, u.Nombre, u.Rol, cajaId) is { } errorPersona)
                return SinAcceso.Resultado(errorPersona);

            return Results.Ok(new LoginResponse(tokens.Generar(u, cajaId), u.Id, u.Nombre, u.Rol,
                u.Rol == Roles.Administrador ? Permisos.Todos : u.Permisos, u.ClaveAutorizacionHash is not null));
        });

        // IP con la que el servidor ve a esta computadora (para registrar cajas y para soporte)
        api.MapGet("/mi-ip", (HttpContext ctx) => Results.Ok(new { ip = AccesoCajas.Normalizar(ctx.Connection.RemoteIpAddress)?.ToString() }));

        // ------------------------------------------------------------ Usuarios (solo admin)
        var usuariosApi = api.MapGroup("/usuarios").RequireAuthorization(Politicas.Admin);
        usuariosApi.MapGet("/", (UsuarioRepositorio r) => r.ListarAsync());
        usuariosApi.MapPost("/", async (CrearUsuarioRequest req, UsuarioRepositorio r, AccesoCajas acceso) =>
        {
            var id = await r.CrearAsync(req);
            acceso.Invalidar(); // pudo recuperarse una persona quitada
            return Results.Ok(new { id });
        });
        usuariosApi.MapPut("/{id:int}/password", async (int id, LoginRequest req, UsuarioRepositorio r) =>
        {
            await r.CambiarPasswordAsync(id, req.Password);
            return Results.NoContent();
        });
        usuariosApi.MapPut("/{id:int}/activo/{activo:bool}", async (int id, bool activo, UsuarioRepositorio r, AccesoCajas acceso) =>
        {
            await r.CambiarEstadoAsync(id, activo);
            acceso.Invalidar(); // si se quitó, su sesión se corta en la siguiente operación
            return Results.NoContent();
        });
        // Cajas en las que puede trabajar la persona ([] = todas)
        // Permisos especiales (cambiar precios, cancelar ventas, sacar dinero). Con permisos, la clave de autorización es obligatoria.
        usuariosApi.MapPut("/{id:int}/permisos", async (int id, PermisosRequest req, UsuarioRepositorio r) =>
        {
            await r.AsignarPermisosAsync(id, req.Permisos ?? [], req.ClaveAutorizacion);
            return Results.NoContent();
        });
        usuariosApi.MapPut("/{id:int}/clave-autorizacion", async (int id, ClaveAutorizacionRequest req, UsuarioRepositorio r) =>
        {
            if (await r.ObtenerAsync(id) is not { } u)
                return Results.NotFound();
            if (!Permisos.NecesitaClave(u.Rol, u.Permisos))
                return Results.Problem($"{u.Nombre} no tiene permisos especiales: no necesita clave de autorización.", statusCode: StatusCodes.Status400BadRequest);
            await r.GuardarClaveAutorizacionAsync(id, req.Clave ?? "");
            return Results.NoContent();
        });
        usuariosApi.MapPut("/{id:int}/cajas", async (int id, int[] cajas, UsuarioRepositorio r, AccesoCajas acceso) =>
        {
            await r.AsignarCajasAsync(id, cajas);
            acceso.Invalidar();
            return Results.NoContent();
        });

        // ------------------------------------------------------------ Cajas y configuración
        api.MapGet("/cajas", (CajaRepositorio r) => r.ListarAsync()).RequireAuthorization();
        api.MapPost("/cajas", async (CrearCajaRequest req, CajaRepositorio r, AccesoCajas acceso) =>
        {
            var caja = await r.CrearAsync(req);
            acceso.Invalidar();
            return caja;
        }).RequireAuthorization(Politicas.Admin);
        // Nombre, IP y bloqueo. El cambio surte efecto al instante en todas las cajas.
        api.MapPut("/cajas/{id:int}", async (int id, ActualizarCajaRequest req, CajaRepositorio r, AccesoCajas acceso) =>
        {
            var caja = await r.ActualizarAsync(id, req);
            acceso.Invalidar();
            return caja;
        }).RequireAuthorization(Politicas.Admin);
        api.MapGet("/configuracion/negocio", (ConfiguracionRepositorio r) => r.ObtenerNegocioAsync()).RequireAuthorization();
        // Qué hacen las flechas en Vender (igual en todas las cajas)
        api.MapGet("/configuracion/venta", (ConfiguracionRepositorio r) => r.ObtenerConfigVentaAsync()).RequireAuthorization();
        api.MapPut("/configuracion/venta", async (ConfigVentaDto c, ConfiguracionRepositorio r) =>
        {
            await r.GuardarConfigVentaAsync(c);
            return Results.NoContent();
        }).RequireAuthorization(Politicas.Admin);

        // El encargado (o alguien con el permiso) teclea su clave de autorización en la caja de otra persona.
        // Después de varios intentos fallidos seguidos, esa sesión espera un par de minutos.
        api.MapPost("/autorizaciones", async (AutorizacionRequest req, ClaimsPrincipal u, UsuarioRepositorio usuarios, Autorizaciones a) =>
        {
            if (!Permisos.Todos.Contains(req.Permiso))
                return Results.Problem("No se sabe qué se quiere autorizar.", statusCode: StatusCodes.Status400BadRequest);
            var pide = u.UsuarioId();
            if (a.Bloqueada(pide) is { } falta)
                return Results.Problem($"Se equivocaron de clave muchas veces. Espere {Math.Max(1, (int)Math.Ceiling(falta.TotalMinutes))} minuto(s) y vuelva a intentar.",
                    statusCode: StatusCodes.Status429TooManyRequests);
            var quien = await usuarios.BuscarPorClaveAsync(req.Clave ?? "");
            if (quien is null)
            {
                a.Fallo(pide);
                return Results.Problem("La clave de autorización no es correcta.", statusCode: StatusCodes.Status403Forbidden);
            }
            a.Acierto(pide);
            if (!Permisos.Tiene(quien.Rol, quien.Permisos, req.Permiso))
                return Results.Problem($"{quien.Nombre} no tiene permiso para autorizar esto. Pídaselo al encargado.", statusCode: StatusCodes.Status403Forbidden);
            return Results.Ok(new AutorizacionDto(a.Emitir(quien.Id, req.Permiso), quien.Nombre));
        }).RequireAuthorization(Politicas.Venta);
        api.MapPut("/configuracion/negocio", async (NegocioDto n, ConfiguracionRepositorio r) =>
        {
            await r.GuardarNegocioAsync(n);
            return Results.NoContent();
        }).RequireAuthorization(Politicas.Admin);

        // ------------------------------------------------------------ Departamentos
        var departamentos = api.MapGroup("/departamentos").RequireAuthorization();
        departamentos.MapGet("/", (DepartamentoRepositorio r, bool incluirInactivos = false) => r.ListarAsync(incluirInactivos));
        departamentos.MapPost("/", (DepartamentoRequest req, DepartamentoRepositorio r) => r.GuardarAsync(0, req))
            .RequireAuthorization(Politicas.Inventario);
        departamentos.MapPut("/{id:int}", (int id, DepartamentoRequest req, DepartamentoRepositorio r) => r.GuardarAsync(id, req))
            .RequireAuthorization(Politicas.Inventario);
        departamentos.MapDelete("/{id:int}", async (int id, DepartamentoRepositorio r, ProductoRepositorio productos, IHubContext<PdvHub> hub) =>
        {
            // Las cajas tienen el nombre del departamento en memoria: avisar solo los productos que cambiaron.
            foreach (var productoId in await r.QuitarAsync(id))
            {
                if (await productos.ObtenerAsync(productoId) is { } p)
                    await hub.Clients.All.SendAsync(PdvHub.ProductoActualizado, p);
            }
            return Results.NoContent();
        }).RequireAuthorization(Politicas.Inventario);

        // ------------------------------------------------------------ Productos
        var productos = api.MapGroup("/productos").RequireAuthorization();
        productos.MapGet("/", (ProductoRepositorio r, bool incluirInactivos = false) => r.ListarAsync(incluirInactivos));
        productos.MapGet("/buscar", (string q, ProductoRepositorio r) => r.BuscarAsync(q));
        productos.MapGet("/codigo/{codigo}", async (string codigo, ProductoRepositorio r) =>
            await r.ObtenerPorCodigoAsync(codigo) is { } p ? Results.Ok(p) : Results.NotFound());
        productos.MapPost("/", (Producto p, ClaimsPrincipal u, ProductoRepositorio r, IHubContext<PdvHub> hub) => GuardarYNotificar(p, u, r, hub))
            .RequireAuthorization(Politicas.Inventario);
        productos.MapPut("/{id:int}", (int id, Producto p, ClaimsPrincipal u, ProductoRepositorio r, IHubContext<PdvHub> hub) =>
        {
            p.Id = id;
            return GuardarYNotificar(p, u, r, hub);
        }).RequireAuthorization(Politicas.Inventario);
        // Historial de precios: cada cambio de precio de venta o de costo
        productos.MapGet("/{id:int}/precios", (int id, ProductoRepositorio r) => r.HistorialPreciosAsync(id))
            .RequireAuthorization(Politicas.Inventario);

        // ------------------------------------------------------------ Ofertas (precio por fechas, NxM, mayoreo)
        productos.MapGet("/{id:int}/ofertas", (int id, OfertaRepositorio r) => r.ListarDeProductoAsync(id));
        productos.MapPost("/{id:int}/ofertas", async (int id, OfertaRequest req, OfertaRepositorio r, ProductoRepositorio prod, IHubContext<PdvHub> hub) =>
        {
            var oferta = await r.CrearAsync(id, req);
            await NotificarProductoAsync(id, prod, hub);
            return oferta;
        }).RequireAuthorization(Politicas.Inventario);
        api.MapDelete("/ofertas/{id:int}", async (int id, OfertaRepositorio r, ProductoRepositorio prod, IHubContext<PdvHub> hub) =>
        {
            if (await r.QuitarAsync(id) is { } productoId)
                await NotificarProductoAsync(productoId, prod, hub);
            return Results.NoContent();
        }).RequireAuthorization(Politicas.Inventario);

        // ------------------------------------------------------------ Clientes
        // Ver y elegir: cualquier persona (el cajero los elige al vender). Alta, límites y precios: solo el encargado.
        var clientes = api.MapGroup("/clientes").RequireAuthorization();
        clientes.MapGet("/", (ClienteRepositorio r, string? buscar) => r.ListarAsync(buscar));
        clientes.MapGet("/{id:int}", async (int id, ClienteRepositorio r) =>
            await r.ObtenerAsync(id) is { Activo: true } c ? Results.Ok(c) : Results.NotFound());
        clientes.MapPost("/", (Cliente c, ClienteRepositorio r) =>
        {
            c.Id = 0;
            return r.GuardarAsync(c);
        }).RequireAuthorization(Politicas.Admin);
        clientes.MapPut("/{id:int}", (int id, Cliente c, ClienteRepositorio r) =>
        {
            c.Id = id;
            return r.GuardarAsync(c);
        }).RequireAuthorization(Politicas.Admin);
        clientes.MapDelete("/{id:int}", async (int id, ClienteRepositorio r) =>
        {
            await r.QuitarAsync(id);
            return Results.NoContent();
        }).RequireAuthorization(Politicas.Admin);
        clientes.MapGet("/{id:int}/movimientos", (int id, ClienteRepositorio r) => r.EstadoDeCuentaAsync(id));
        // Abono: se registra en la caja abierta de quien lo recibe (cuenta en su corte)
        clientes.MapPost("/{id:int}/abonos", async (int id, AbonoRequest req, ClaimsPrincipal u, ClienteRepositorio r, TurnoRepositorio turnos) =>
        {
            await ValidarDuenoTurnoAsync(req.TurnoId, u, turnos);
            return await r.AbonarAsync(id, req, u.UsuarioId());
        }).RequireAuthorization(Politicas.Venta);

        // ------------------------------------------------------------ Inventario
        var inventario = api.MapGroup("/inventario").RequireAuthorization(Politicas.Inventario);
        inventario.MapPost("/movimientos", async (MovimientoInventarioRequest req, ClaimsPrincipal u,
            InventarioRepositorio inv, ProductoRepositorio prod, IHubContext<PdvHub> hub) =>
        {
            var existencia = await inv.RegistrarAsync(req, u.UsuarioId());
            if (await prod.ObtenerAsync(req.ProductoId) is { } p)
                await hub.Clients.All.SendAsync(PdvHub.ProductoActualizado, p);
            return Results.Ok(new { existencia });
        });
        inventario.MapGet("/kardex/{productoId:int}", (int productoId, InventarioRepositorio r) => r.KardexAsync(productoId));

        // ------------------------------------------------------------ Turnos / cortes
        var turnos = api.MapGroup("/turnos").RequireAuthorization(Politicas.Venta);
        // Una sesión solo puede operar la caja desde la que entró.
        turnos.MapGet("/actual", async (int cajaId, ClaimsPrincipal u, TurnoRepositorio r) =>
        {
            ValidarMismaCaja(cajaId, u);
            return await r.ObtenerAbiertoAsync(cajaId) is { } t ? Results.Ok(t) : Results.NoContent();
        });
        turnos.MapPost("/abrir", (AbrirTurnoRequest req, ClaimsPrincipal u, TurnoRepositorio r) =>
        {
            ValidarMismaCaja(req.CajaId, u);
            return r.AbrirAsync(req, u.UsuarioId());
        });
        // Sacar dinero: quien tiene el permiso (o el encargado) lo hace directo; los demás, con la autorización firmada del encargado.
        turnos.MapPost("/{id:long}/retiros", async (long id, RetiroRequest req, ClaimsPrincipal u, TurnoRepositorio r,
            UsuarioRepositorio usuarios, Autorizaciones a) =>
        {
            await ValidarDuenoTurnoAsync(id, u, r);
            if (await QuienAutorizaAsync(u.UsuarioId(), req.Autorizacion, Permisos.SacarDinero, usuarios, a) is not { } autorizo)
                return Results.Problem("Para sacar dinero de la caja hace falta la autorización del encargado.", statusCode: StatusCodes.Status403Forbidden);
            await r.RegistrarRetiroAsync(id, u.UsuarioId(), autorizo, req);
            return Results.NoContent();
        });
        turnos.MapGet("/{id:long}/corte", async (long id, ClaimsPrincipal u, TurnoRepositorio r) =>
        {
            await ValidarDuenoTurnoAsync(id, u, r);
            return await r.CorteAsync(id);
        });
        turnos.MapPost("/{id:long}/cerrar", async (long id, CerrarTurnoRequest req, ClaimsPrincipal u, TurnoRepositorio r) =>
        {
            await ValidarDuenoTurnoAsync(id, u, r);
            return await r.CerrarAsync(id, req);
        });

        // ------------------------------------------------------------ Cortes de caja anteriores (solo el encargado)
        var cortes = api.MapGroup("/cortes").RequireAuthorization(Politicas.Admin);
        cortes.MapGet("/", (DateTimeOffset desde, DateTimeOffset hasta, int? cajaId, TurnoRepositorio r) =>
            r.ListarCortesAsync(desde.UtcDateTime, hasta.UtcDateTime, cajaId));
        cortes.MapGet("/{id:long}", (long id, TurnoRepositorio r) => r.CorteAsync(id));

        // ------------------------------------------------------------ Respaldos de la base de datos
        var respaldos = api.MapGroup("/respaldos");
        // La caja avisa que se cierra el programa (ya no hay sesión): solo se acepta de una caja registrada (bloqueo e IP)
        respaldos.MapPost("/al-cerrar", async (AvisoCierreRequest req, HttpContext ctx, AccesoCajas acceso, Respaldos r) =>
        {
            if (await acceso.ValidarCajaAsync(req.CajaId, ctx.Connection.RemoteIpAddress) is { } error)
                return SinAcceso.Resultado(error);
            r.Solicitar($"Se cerró el programa de la Caja {req.CajaId}");
            return Results.Accepted();
        }).AllowAnonymous();
        respaldos.MapGet("/", (Respaldos r) => r.Listar()).RequireAuthorization(Politicas.Admin);
        respaldos.MapPost("/", (ClaimsPrincipal u, Respaldos r) => r.CrearAsync($"Hecho a mano por {u.Identity?.Name}"))
            .RequireAuthorization(Politicas.Admin);
        // Restaurar pide la clave de autorización de un encargado
        respaldos.MapPost("/restaurar", async (RestaurarRespaldoRequest req, ClaimsPrincipal u, UsuarioRepositorio usuarios,
            Autorizaciones a, Respaldos r) =>
        {
            if (a.Bloqueada(u.UsuarioId()) is not null)
                return Results.Problem("Se equivocaron de clave muchas veces. Espere un par de minutos y vuelva a intentar.", statusCode: StatusCodes.Status429TooManyRequests);
            var admin = await usuarios.BuscarPorClaveAsync(req.ClaveAutorizacion ?? "");
            if (admin is null || admin.Rol != Roles.Administrador)
            {
                a.Fallo(u.UsuarioId());
                return Results.Problem("La clave de autorización del encargado no es correcta.", statusCode: StatusCodes.Status403Forbidden);
            }
            a.Acierto(u.UsuarioId());
            return Results.Ok(await r.RestaurarAsync(req.Nombre, admin.Nombre));
        }).RequireAuthorization(Politicas.Admin);

        // ------------------------------------------------------------ Ventas
        var ventas = api.MapGroup("/ventas").RequireAuthorization();
        // Historial de tickets. El encargado ve todas las cajas; los demás, solo la caja desde la que entraron.
        ventas.MapGet("/", (DateTimeOffset? desde, DateTimeOffset? hasta, int? cajaId, long? turnoId, ClaimsPrincipal u, VentaRepositorio r) =>
            r.ListarAsync(desde?.UtcDateTime, hasta?.UtcDateTime, u.EsAdmin() ? cajaId : u.CajaId(), turnoId));
        ventas.MapPost("/", async (VentaRequest req, ClaimsPrincipal u, VentaRepositorio r, UsuarioRepositorio usuarios, Autorizaciones a) =>
        {
            var autorizoPrecio = await AutorizacionesDePrecioAsync(req, u.UsuarioId(), usuarios, a);
            var id = await r.RegistrarAsync(req, u.UsuarioId(), u.CajaId()!.Value, autorizoPrecio);
            return await r.ObtenerTicketAsync(id);
        }).RequireAuthorization(Politicas.Venta);
        ventas.MapGet("/{id:long}/ticket", async (long id, VentaRepositorio r) =>
            await r.ObtenerTicketAsync(id) is { } t ? Results.Ok(t) : Results.NotFound());
        ventas.MapGet("/folio/{folio}", async (string folio, VentaRepositorio r) =>
            await r.BuscarIdPorFolioAsync(folio) is { } id && await r.ObtenerTicketAsync(id) is { } t ? Results.Ok(t) : Results.NotFound());
        // Cancelar: quien tiene el permiso (o el encargado) lo hace directo; los demás, con la autorización firmada del encargado.
        ventas.MapPost("/{id:long}/cancelar", async (long id, CancelarVentaRequest req, ClaimsPrincipal u, UsuarioRepositorio usuarios,
            Autorizaciones a, VentaRepositorio r) =>
        {
            if (await QuienAutorizaAsync(u.UsuarioId(), req.Autorizacion, Permisos.CancelarVenta, usuarios, a) is not { } autorizo)
                return Results.Problem("Para cancelar una venta hace falta la autorización del encargado.", statusCode: StatusCodes.Status403Forbidden);
            await r.CancelarAsync(id, autorizo, req.Motivo);
            return Results.NoContent();
        }).RequireAuthorization(Politicas.Venta);
    }

    /// <summary>
    /// Quién autoriza una operación: la misma persona si tiene el permiso (o es encargado); si no, quien firmó la autorización
    /// (válida, vigente y de alguien activo que hoy todavía tiene el permiso). Null = nadie.
    /// </summary>
    private static async Task<int?> QuienAutorizaAsync(int usuarioId, string? autorizacion, string permiso,
        UsuarioRepositorio usuarios, Autorizaciones a)
    {
        if (await usuarios.ObtenerAsync(usuarioId) is { } yo && Permisos.Tiene(yo.Rol, yo.Permisos, permiso))
            return usuarioId;
        if (autorizacion is not null && a.Validar(autorizacion, permiso) is { } id
            && await usuarios.ObtenerAsync(id) is { Activo: true } quien && Permisos.Tiene(quien.Rol, quien.Permisos, permiso))
            return id;
        return null;
    }

    /// <summary>
    /// Quién autoriza cada precio cambiado: quien cobra si tiene el permiso (o es encargado); si no, la firma del encargado
    /// que viene en el renglón (válida, vigente y de alguien que hoy todavía tiene el permiso).
    /// </summary>
    private static async Task<Func<VentaItemRequest, int?>?> AutorizacionesDePrecioAsync(VentaRequest req, int usuarioId,
        UsuarioRepositorio usuarios, Autorizaciones a)
    {
        if (!req.Items.Any(i => i.PrecioManual is not null))
            return null;
        var yo = await usuarios.ObtenerAsync(usuarioId);
        if (yo is not null && Permisos.Tiene(yo.Rol, yo.Permisos, Permisos.CambiarPrecio))
            return _ => usuarioId;

        var validas = new Dictionary<string, int>();
        foreach (var token in req.Items.Select(i => i.Autorizacion).OfType<string>().Distinct())
        {
            if (a.Validar(token, Permisos.CambiarPrecio) is { } id
                && await usuarios.ObtenerAsync(id) is { Activo: true } quien
                && Permisos.Tiene(quien.Rol, quien.Permisos, Permisos.CambiarPrecio))
                validas[token] = id;
        }
        return i => i.Autorizacion is { } t && validas.TryGetValue(t, out var id) ? id : null;
    }

    private static async Task<Producto> GuardarYNotificar(Producto p, ClaimsPrincipal u, ProductoRepositorio r, IHubContext<PdvHub> hub)
    {
        var guardado = await r.GuardarAsync(p, u.UsuarioId());
        await hub.Clients.All.SendAsync(PdvHub.ProductoActualizado, guardado);
        return guardado;
    }

    /// <summary>Manda a todas las cajas el producto actualizado (con sus ofertas).</summary>
    private static async Task NotificarProductoAsync(int productoId, ProductoRepositorio prod, IHubContext<PdvHub> hub)
    {
        if (await prod.ObtenerAsync(productoId) is { } p)
            await hub.Clients.All.SendAsync(PdvHub.ProductoActualizado, p);
    }

    private static async Task ValidarDuenoTurnoAsync(long turnoId, ClaimsPrincipal u, TurnoRepositorio r)
    {
        var turno = await r.ObtenerAsync(turnoId) ?? throw new ReglaNegocioException("No se encontró la caja abierta.");
        ValidarMismaCaja(turno.CajaId, u);
        if (turno.UsuarioId != u.UsuarioId() && !u.EsAdmin())
            throw new ReglaNegocioException("Esta caja la abrió otra persona.");
    }

    private static void ValidarMismaCaja(int cajaId, ClaimsPrincipal u)
    {
        if (u.CajaId() != cajaId)
            throw new ReglaNegocioException("Esta computadora no está registrada como esa caja. Contacte a soporte.");
    }
}
