using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using PDV.Caja.Servicios;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Caja.Ventanas;

/// <summary>Fila de la lista de personas (con el rol y las cajas explicados en palabras).</summary>
public sealed record FilaPersona(int Id, string Nombre, string Usuario, string Puede, string Rol, int[] CajaIds, string Cajas, string[] Permisos,
    bool TieneClave)
{
    public bool NecesitaClave => Core.Permisos.NecesitaClave(Rol, Permisos);

    /// <summary>"Cambiar precios, Cancelar ventas" · "Todo" (encargado) · "—", con aviso si le falta la clave de autorización.</summary>
    public string PermisosTexto => (Rol == Roles.Administrador ? "Todo"
        : Permisos.Length == 0 ? "—" : string.Join(", ", Permisos.Select(Core.Permisos.Corto)))
        + (NecesitaClave && !TieneClave ? "  ⚠ falta su clave" : "");
}

/// <summary>Menú principal del encargado: tarjetas grandes con explicación.</summary>
public sealed class MenuAdminWindow : Window
{
    private static readonly Dictionary<string, string> RolPorTexto = Roles.Todos.ToDictionary(Roles.Descripcion);

    public MenuAdminWindow()
    {
        Title = "Punto de Venta - Menú";
        Width = 1180;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontSize = 17;
        Background = (Brush)FindResource("Fondo");

        var panel = new StackPanel { Margin = new Thickness(28, 22, 28, 22) };
        var encabezado = new DockPanel { Margin = new Thickness(4, 0, 0, 16) };
        var logo = new Image { Source = (ImageSource)FindResource("Logo"), Width = 64, Height = 64, Margin = new Thickness(0, 0, 16, 0) };
        DockPanel.SetDock(logo, Dock.Left);
        encabezado.Children.Add(logo);
        var saludo = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        saludo.Children.Add(new TextBlock { Text = $"Hola, {Sesion.Usuario?.Nombre}", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Primario") });
        saludo.Children.Add(new TextBlock { Text = "¿Qué quiere hacer?", FontSize = 19, Foreground = (Brush)FindResource("TextoSuave") });
        encabezado.Children.Add(saludo);
        panel.Children.Add(encabezado);

        var tarjetas = new UniformGrid { Columns = 3 };
        tarjetas.Children.Add(Tarjeta("🛒", "Vender", "Cobrar a los clientes en esta caja.", () => new VentaWindow { Owner = this }.ShowDialog()));
        tarjetas.Children.Add(Tarjeta("📦", "Productos y mercancía", "Dar de alta productos, cambiar precios, registrar lo que llega.", () => new ProductosWindow { Owner = this }.ShowDialog()));
        tarjetas.Children.Add(Tarjeta("🧾", "Tickets", "Ver cualquier ticket ya cobrado y volver a imprimirlo.", () => Historiales.Tickets(this)));
        tarjetas.Children.Add(Tarjeta("📊", "Cortes de caja", "Cuánto se vendió en cada caja y si cuadró el dinero.", () => Historiales.Cortes(this)));
        tarjetas.Children.Add(Tarjeta("👤", "Clientes", "Crédito, precios especiales y datos para factura.", Clientes));
        tarjetas.Children.Add(Tarjeta("👥", "Personas", "Quién puede entrar al sistema y qué puede hacer.", Personas));
        tarjetas.Children.Add(Tarjeta("🏪", "Datos del ticket", "Nombre de la tienda, dirección y mensaje del ticket.", async () => await DatosTicketAsync()));
        tarjetas.Children.Add(Tarjeta("🖥", "Cajas", "Agregar cajas, su IP, y bloquear o desbloquear.", Cajas));
        tarjetas.Children.Add(Tarjeta("⚖", "Probar báscula", "Revisar que la báscula esté bien conectada.", async () => await ProbarBasculaAsync()));
        tarjetas.Children.Add(Tarjeta("⌨", "Teclas de la caja", "Qué hacen las flechas ← → al vender.", async () => await TeclasAsync()));
        tarjetas.Children.Add(Tarjeta("💾", "Respaldos", "Copias de la base de datos. Regresar a como estaba si algo sale mal.", async () => await RespaldosAsync()));
        panel.Children.Add(tarjetas);

        var salir = new Button { Content = "🚪  Salir", HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(5, 16, 5, 0) };
        salir.Click += (_, _) => Close();
        panel.Children.Add(salir);

        Content = panel;
        ContentRendered += (_, _) => _ = PedirMiClaveAsync();
    }

    private const string ExplicacionClave =
        "Con esta clave se autoriza, en cualquier caja, lo que otra persona no puede hacer sola " +
        "(cambiar precios, cancelar ventas, sacar dinero). Basta con teclearla. " +
        "Es distinta a la contraseña para entrar; no se la dé a nadie.";

    /// <summary>
    /// Pide una clave de autorización nueva (dos veces, para no equivocarse). null = se canceló.
    /// </summary>
    internal static string? PedirClaveNueva(Window owner, string persona, string explicacion)
    {
        while (true)
        {
            var r = CampoDialog.MostrarCon(owner, $"Clave de autorización de {persona}", explicacion, "Guardar clave",
                new Campo("Clave de autorización", Password: true,
                    Ayuda: $"Mínimo {Permisos.LargoMinimoClave} letras o números. Que no sea la misma que la contraseña para entrar."),
                new Campo("Repita la clave", Password: true));
            if (r is null)
                return null;
            if (r[0].Length < Permisos.LargoMinimoClave)
                Mensajes.Error(owner, $"La clave debe tener al menos {Permisos.LargoMinimoClave} letras o números.");
            else if (r[0] != r[1])
                Mensajes.Error(owner, "Las dos claves no son iguales. Escríbalas otra vez.");
            else
                return r[0];
        }
    }

    /// <summary>
    /// Pide la clave y la guarda; si el servidor la rechaza (por ejemplo, ya es de otra persona) avisa y la vuelve
    /// a pedir, sin perder lo demás que se capturó. false = se canceló.
    /// </summary>
    internal static async Task<bool> GuardarConClaveAsync(Window owner, string persona, string explicacion, Func<string, Task> guardar)
    {
        while (PedirClaveNueva(owner, persona, explicacion) is { } clave)
        {
            try
            {
                await guardar(clave);
                return true;
            }
            catch (ApiException ex)
            {
                Mensajes.Error(owner, ex.Message);
            }
        }
        return false;
    }

    /// <summary>El encargado sin clave de autorización (por ejemplo, el primero del sistema) la crea al entrar.</summary>
    private async Task PedirMiClaveAsync()
    {
        if (Sesion.Usuario is not { Rol: Roles.Administrador, TieneClaveAutorizacion: false } yo)
            return;
        if (await GuardarConClaveAsync(this, yo.Nombre,
                "Usted es encargado y todavía no tiene clave de autorización. " + ExplicacionClave +
                "\n\nSi no la crea ahora, se le volverá a pedir la próxima vez que entre.",
                clave => Sesion.Api.PutAsync($"api/usuarios/{yo.UsuarioId}/clave-autorizacion", new ClaveAutorizacionRequest(clave))))
        {
            Sesion.MarcarClaveAutorizacion();
            Mensajes.Exito(this, "Listo. Ya puede autorizar con su clave en cualquier caja.");
        }
    }

    private Button Tarjeta(string icono, string titulo, string descripcion, Action accion)
    {
        var contenido = new DockPanel();
        var ico = new TextBlock { Text = icono, FontSize = 40, Margin = new Thickness(0, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center };
        DockPanel.SetDock(ico, Dock.Left);
        contenido.Children.Add(ico);
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        textos.Children.Add(new TextBlock { Text = titulo, FontSize = 22, FontWeight = FontWeights.SemiBold });
        textos.Children.Add(new TextBlock { Text = descripcion, FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextoSuave") });
        contenido.Children.Add(textos);

        var b = new Button { Content = contenido, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(18, 16, 18, 16), MinHeight = 110, Margin = new Thickness(6) };
        System.Windows.Automation.AutomationProperties.SetName(b, titulo); // lectores de pantalla y pruebas automáticas
        b.Click += (_, _) => accion();
        return b;
    }

    // ------------------------------------------------------------------ Clientes

    private void Clientes()
    {
        new TablaWindow("Clientes",
            "Escriba arriba para buscar. Los abonos se reciben en la pantalla de Vender (\"Recibir abono de cliente\").",
            [
                new Columna("Nombre", nameof(Cliente.Nombre)),
                new Columna("Teléfono", nameof(Cliente.Telefono), Ancho: 150),
                new Columna("Debe", nameof(Cliente.SaldoTexto), Ancho: 120),
                new Columna("Crédito disponible", nameof(Cliente.CreditoTexto), Ancho: 210),
                new Columna("Precio especial", nameof(Cliente.PrecioEspecialTexto), Ancho: 170),
                new Columna("RFC", nameof(Cliente.Rfc), Ancho: 150)
            ],
            async () => (IEnumerable)(await Sesion.Api.GetAsync<List<Cliente>>("api/clientes") ?? []),
            new AccionTabla("➕  Nuevo cliente", _ => Task.FromResult(ClienteWindow.Editar(this, new Cliente()) is not null),
                RequiereSeleccion: false, Estilo: "BotonVerde"),
            new AccionTabla("✏  Cambiar datos", sel => Task.FromResult(ClienteWindow.Editar(this, (Cliente)sel!) is not null)),
            new AccionTabla("📒  Estado de cuenta", sel =>
            {
                EstadoDeCuenta((Cliente)sel!);
                return Task.FromResult(false);
            }),
            new AccionTabla("🗑  Quitar", async sel =>
            {
                var c = (Cliente)sel!;
                if (c.Saldo > 0)
                {
                    Mensajes.Info(this, $"{c.Nombre} todavía debe {Formato.Dinero(c.Saldo)}. Se puede quitar cuando liquide.");
                    return false;
                }
                if (!Mensajes.Confirmar(this, $"¿Quitar a {c.Nombre}?\n\nYa no aparecerá para elegirlo. Sus compras anteriores no se borran.", "Sí, quitar", "No"))
                    return false;
                await Sesion.Api.DeleteAsync($"api/clientes/{c.Id}");
                return true;
            }, Estilo: "BotonRojo"))
        {
            Owner = this,
            FiltroBusqueda = (o, t) => o is Cliente c && (TablaWindow.Contiene(c.Nombre, t) || (c.Telefono?.Contains(t) ?? false) || TablaWindow.Contiene(c.Rfc, t))
        }.ShowDialog();
    }

    /// <summary>Estado de cuenta de un cliente (también se usa desde la pantalla de venta).</summary>
    public static void EstadoDeCuenta(Window owner, Cliente c)
    {
        new TablaWindow($"Estado de cuenta: {c.Nombre}",
            $"Debe: {Formato.Dinero(c.Saldo)}   ·   Crédito disponible: {c.CreditoTexto}.   Lo más reciente arriba.",
            [
                new Columna("Fecha", nameof(MovimientoCliente.FechaLocal), "{0:dd/MM/yyyy  h:mm tt}", 200),
                new Columna("Qué pasó", nameof(MovimientoCliente.TipoTexto), Ancho: 230),
                new Columna("Monto", nameof(MovimientoCliente.Monto), "{0:+$#,##0.00;-$#,##0.00}", 130),
                new Columna("Debía después", nameof(MovimientoCliente.SaldoResultante), "{0:C2}", 150),
                new Columna("Quién", nameof(MovimientoCliente.Usuario), Ancho: 160),
                new Columna("Ticket", nameof(MovimientoCliente.Detalle))
            ],
            async () => (IEnumerable)(await Sesion.Api.GetAsync<List<MovimientoCliente>>($"api/clientes/{c.Id}/movimientos") ?? []))
        { Owner = owner }.ShowDialog();
    }

    private void EstadoDeCuenta(Cliente c) => EstadoDeCuenta(this, c);

    // ------------------------------------------------------------------ Personas

    private void Personas()
    {
        var opcionesRol = RolPorTexto.Keys.ToList();
        List<CajaInfo> cajas = [];

        new TablaWindow("Personas", "Las personas que pueden entrar al sistema. Cada una entra con su usuario y su contraseña.",
            [
                new Columna("Nombre", nameof(FilaPersona.Nombre)),
                new Columna("Usuario para entrar", nameof(FilaPersona.Usuario), Ancho: 190),
                new Columna("Qué puede hacer", nameof(FilaPersona.Puede), Ancho: 300),
                new Columna("Permisos especiales", nameof(FilaPersona.PermisosTexto), Ancho: 260),
                new Columna("En qué cajas", nameof(FilaPersona.Cajas), Ancho: 180)
            ],
            async () =>
            {
                cajas = await Sesion.Api.GetAsync<List<CajaInfo>>("api/cajas") ?? [];
                var nombres = cajas.ToDictionary(c => c.Id, c => c.Nombre);
                return ((await Sesion.Api.GetAsync<List<UsuarioDto>>("api/usuarios")) ?? [])
                    .Where(u => u.Activo)
                    .Select(u => new FilaPersona(u.Id, u.Nombre, u.Login, Roles.Descripcion(u.Rol), u.Rol, u.Cajas,
                        u.Rol == Roles.Administrador || u.Cajas.Length == 0
                            ? "Todas"
                            : string.Join(", ", u.Cajas.Select(id => nombres.GetValueOrDefault(id, $"Caja {id}"))),
                        u.Permisos ?? [], u.TieneClaveAutorizacion))
                    .ToList();
            },
            new AccionTabla("➕  Agregar persona", async _ =>
            {
                var r = CampoDialog.MostrarCon(this, "Agregar persona", null, "Guardar",
                    new Campo("Nombre completo", Ayuda: "Ejemplo: María López"),
                    new Campo("Usuario para entrar", Ayuda: "Una palabra corta, sin espacios. Ejemplo: maria"),
                    new Campo("Contraseña", Password: true, Ayuda: "Mínimo 4 letras o números. Désela a la persona."),
                    new Campo("¿Qué puede hacer?", Roles.Descripcion(Roles.Cajero), Opciones: opcionesRol));
                if (r is null) return false;
                var rol = RolPorTexto.GetValueOrDefault(r[3], Roles.Cajero);
                // Permisos especiales (opcional): el encargado ya los tiene todos
                string[] permisos = rol == Roles.Administrador ? [] : ElegirPermisosWindow.Elegir(this, r[0], [],
                    "¿Le da algún permiso especial? Lo que marque lo podrá hacer sin pedir la clave del encargado. Puede dejarlo sin marcar.") ?? [];
                // El encargado y quien tiene permisos especiales necesitan su clave de autorización (obligatoria)
                var conClave = Permisos.NecesitaClave(rol, permisos);
                if (!conClave)
                    await Sesion.Api.PostAsync<System.Text.Json.JsonElement>("api/usuarios", new CrearUsuarioRequest(r[1], r[0], r[2], rol));
                else if (!await GuardarConClaveAsync(this, r[0], (rol == Roles.Administrador
                             ? "Como es encargado, necesita una clave de autorización. "
                             : "Como tiene permisos especiales, necesita una clave de autorización. ") + ExplicacionClave,
                             clave => Sesion.Api.PostAsync<System.Text.Json.JsonElement>("api/usuarios",
                                 new CrearUsuarioRequest(r[1], r[0], r[2], rol, permisos, clave))))
                {
                    Mensajes.Info(this, $"No se agregó a {r[0]}: le falta su clave de autorización.");
                    return false;
                }
                Mensajes.Exito(this, $"Listo. {r[0]} ya puede entrar con el usuario \"{r[1]}\"." +
                    (conClave ? "\n\nY autoriza con su clave de autorización." : ""));
                return true;
            }, RequiereSeleccion: false, Estilo: "BotonVerde"),
            new AccionTabla("🖥  Elegir cajas", async sel =>
            {
                var u = (FilaPersona)sel!;
                if (u.Rol == Roles.Administrador)
                {
                    Mensajes.Info(this, "El encargado siempre puede trabajar en todas las cajas.");
                    return false;
                }
                if (ElegirCajasWindow.Elegir(this, u.Nombre, cajas, u.CajaIds) is not { } elegidas)
                    return false;
                await Sesion.Api.PutAsync($"api/usuarios/{u.Id}/cajas", elegidas);
                return true;
            }),
            new AccionTabla("⭐  Permisos especiales", async sel =>
            {
                var u = (FilaPersona)sel!;
                if (u.Rol == Roles.Administrador)
                {
                    Mensajes.Info(this, "El encargado ya puede hacer todo.");
                    return false;
                }
                if (ElegirPermisosWindow.Elegir(this, u.Nombre, u.Permisos) is not { } permisos)
                    return false;
                if (permisos.Length == 0 || u.TieneClave)
                    await Sesion.Api.PutAsync($"api/usuarios/{u.Id}/permisos", new PermisosRequest(permisos));
                else if (!await GuardarConClaveAsync(this, u.Nombre,
                             "Como ahora tiene permisos especiales, necesita una clave de autorización. " + ExplicacionClave,
                             clave => Sesion.Api.PutAsync($"api/usuarios/{u.Id}/permisos", new PermisosRequest(permisos, clave))))
                {
                    Mensajes.Info(this, "No se guardaron los permisos: le falta su clave de autorización.");
                    return false;
                }
                return true;
            }),
            new AccionTabla("🔐  Clave de autorización", async sel =>
            {
                var u = (FilaPersona)sel!;
                if (!u.NecesitaClave)
                {
                    Mensajes.Info(this, $"{u.Nombre} no tiene permisos especiales, así que no necesita clave de autorización.\n\nSi se los da (⭐ Permisos especiales), ahí mismo se le pide.");
                    return false;
                }
                if (!await GuardarConClaveAsync(this, u.Nombre,
                        u.TieneClave ? "Escriba la clave nueva. La anterior deja de servir." : ExplicacionClave,
                        clave => Sesion.Api.PutAsync($"api/usuarios/{u.Id}/clave-autorizacion", new ClaveAutorizacionRequest(clave))))
                    return false;
                if (u.Id == Sesion.Usuario?.UsuarioId)
                    Sesion.MarcarClaveAutorizacion();
                Mensajes.Exito(this, $"Listo. Clave de autorización de {u.Nombre} guardada.");
                return true;
            }),
            new AccionTabla("🔑  Cambiar contraseña", async sel =>
            {
                var u = (FilaPersona)sel!;
                var r = CampoDialog.MostrarCon(this, $"Nueva contraseña para {u.Nombre}", null, "Guardar",
                    new Campo("Nueva contraseña", Password: true, Ayuda: "Mínimo 4 letras o números."));
                if (r is null) return false;
                await Sesion.Api.PutAsync($"api/usuarios/{u.Id}/password", new LoginRequest(u.Usuario, r[0]));
                Mensajes.Exito(this, "Contraseña cambiada.");
                return false;
            }),
            new AccionTabla("🗑  Quitar persona", async sel =>
            {
                var u = (FilaPersona)sel!;
                if (u.Id == Sesion.Usuario?.UsuarioId)
                {
                    Mensajes.Info(this, "No puede quitarse a usted mismo.");
                    return false;
                }
                if (!Mensajes.Confirmar(this, $"¿Quitar a {u.Nombre}?\n\nYa no podrá entrar al sistema. Sus ventas anteriores no se borran.", "Sí, quitar", "No"))
                    return false;
                await Sesion.Api.PutAsync($"api/usuarios/{u.Id}/activo/false");
                return true;
            }, Estilo: "BotonRojo"))
        { Owner = this }.ShowDialog();
    }

    // ------------------------------------------------------------------ Cajas

    private const string AyudaIp = "La IP fija de la computadora de esa caja. Para saberla: abra el programa en esa computadora; aparece abajo en la pantalla de entrada (\"Esta computadora: …\").";

    private void Cajas()
    {
        new TablaWindow("Cajas",
            "Cada computadora que cobra es una caja. Solo la computadora con la IP registrada puede usar su caja. " +
            "Una caja bloqueada no deja entrar a nadie (si alguien la está usando, se le cierra al momento).",
            [
                new Columna("Número", nameof(CajaInfo.Id), Ancho: 100),
                new Columna("Nombre", nameof(CajaInfo.Nombre)),
                new Columna("Computadora (IP)", nameof(CajaInfo.IpTexto), Ancho: 280),
                new Columna("Estado", nameof(CajaInfo.EstadoTexto), Ancho: 180)
            ],
            async () => (IEnumerable)(await Sesion.Api.GetAsync<List<CajaInfo>>("api/cajas") ?? []),
            new AccionTabla("➕  Agregar caja", async _ =>
            {
                var cajas = await Sesion.Api.GetAsync<List<CajaInfo>>("api/cajas") ?? [];
                var siguiente = cajas.Count == 0 ? 1 : cajas.Max(c => c.Id) + 1;
                var r = CampoDialog.MostrarCon(this, "Agregar caja", null, "Guardar",
                    new Campo("Nombre de la caja", $"Caja {siguiente}", Ayuda: "Ejemplo: Caja 2, Caja de la entrada…"),
                    new Campo("IP de la computadora", Ayuda: AyudaIp));
                if (r is null || r[0].Length == 0) return false;
                var caja = await Sesion.Api.PostAsync<CajaInfo>("api/cajas", new CrearCajaRequest(r[0], $"C{siguiente}", r[1]));
                Mensajes.Exito(this, $"Caja agregada con el número {caja!.Id}.\n\nEn esa computadora, en la pantalla de entrada, use \"⚙ Configuración\" y escriba el número {caja.Id}.");
                return true;
            }, RequiereSeleccion: false, Estilo: "BotonVerde"),
            new AccionTabla("✏  Cambiar nombre o IP", async sel =>
            {
                var c = (CajaInfo)sel!;
                var campos = new List<Campo> { new("Nombre de la caja", c.Nombre) };
                if (!c.EsMaestra)
                    campos.Add(new Campo("IP de la computadora", c.Ip ?? "", Ayuda: AyudaIp));
                var r = CampoDialog.MostrarCon(this, $"Cambiar {c.Nombre}",
                    c.EsMaestra ? "La caja principal no lleva IP: solo funciona en esta computadora." : null, "Guardar", [.. campos]);
                if (r is null) return false;
                await Sesion.Api.PutAsync<CajaInfo>($"api/cajas/{c.Id}", new ActualizarCajaRequest(r[0], c.EsMaestra ? null : r[1], c.Activa));
                return true;
            }),
            new AccionTabla("🔒  Bloquear / desbloquear", async sel =>
            {
                var c = (CajaInfo)sel!;
                if (c.EsMaestra)
                {
                    Mensajes.Info(this, "La caja principal no se puede bloquear.");
                    return false;
                }
                var pregunta = c.Activa
                    ? $"¿Bloquear la {c.Nombre}?\n\nNadie podrá entrar ni cobrar en ella. Si alguien la está usando ahorita, se le cerrará."
                    : $"¿Desbloquear la {c.Nombre}?\n\nSe podrá volver a usar desde su computadora ({c.Ip}).";
                if (!Mensajes.Confirmar(this, pregunta, c.Activa ? "Sí, bloquear" : "Sí, desbloquear", "No"))
                    return false;
                await Sesion.Api.PutAsync<CajaInfo>($"api/cajas/{c.Id}", new ActualizarCajaRequest(c.Nombre, c.Ip, !c.Activa));
                return true;
            }))
        { Owner = this }.ShowDialog();
    }

    // ------------------------------------------------------------------ Datos del ticket

    private async Task DatosTicketAsync()
    {
        try
        {
            var n = await Sesion.Api.GetAsync<NegocioDto>("api/configuracion/negocio") ?? new NegocioDto();
            var r = CampoDialog.MostrarCon(this, "Datos del ticket", "Esto aparece impreso en cada ticket.", "Guardar",
                new Campo("Nombre de la tienda", n.Nombre),
                new Campo("Dirección", n.Direccion),
                new Campo("Teléfono", n.Telefono),
                new Campo("RFC (opcional)", n.Rfc),
                new Campo("Mensaje al final del ticket", n.MensajeTicket, Ayuda: "Ejemplo: ¡Gracias por su compra!"));
            if (r is null)
                return;
            await Sesion.Api.PutAsync("api/configuracion/negocio", new NegocioDto
            {
                Nombre = r[0], Direccion = r[1], Telefono = r[2], Rfc = r[3], MensajeTicket = r[4]
            });
            Mensajes.Exito(this, "Datos guardados. Saldrán en los siguientes tickets.");
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
        }
    }

    // ------------------------------------------------------------------ Teclas de la caja

    private const string IzquierdaCantidad = "← cambia la CANTIDAD   ·   → cambia el PRECIO";
    private const string IzquierdaPrecio = "← cambia el PRECIO   ·   → cambia la CANTIDAD";

    /// <summary>Qué hacen las flechas en Vender (igual en todas las cajas).</summary>
    private async Task TeclasAsync()
    {
        try
        {
            var actual = await Sesion.Api.GetAsync<ConfigVentaDto>("api/configuracion/venta") ?? new ConfigVentaDto();
            var r = CampoDialog.MostrarCon(this, "Teclas de la caja",
                "En la pantalla de Vender, con un producto marcado en la lista, las flechas abren una ventanita para cambiarle " +
                "la cantidad o el precio (el precio solo en esa cuenta). Elija cuál flecha hace qué.", "Guardar",
                new Campo("Flechas", actual.FlechaIzquierda == AccionesFlecha.Precio ? IzquierdaPrecio : IzquierdaCantidad,
                    Opciones: [IzquierdaCantidad, IzquierdaPrecio],
                    Ayuda: "Si se está escribiendo un código, las flechas mueven el cursor como siempre."));
            if (r is null)
                return;
            var nuevo = new ConfigVentaDto { FlechaIzquierda = r[0] == IzquierdaPrecio ? AccionesFlecha.Precio : AccionesFlecha.Cantidad };
            await Sesion.Api.PutAsync("api/configuracion/venta", nuevo);
            Sesion.ConfigVenta = nuevo;
            Mensajes.Exito(this, "Guardado. Las demás cajas lo toman la próxima vez que alguien entre.");
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
        }
    }

    // ------------------------------------------------------------------ Respaldos

    /// <summary>Lista de respaldos (se hace uno cada vez que se cierra el programa), hacer uno ahora y restaurar.</summary>
    private async Task RespaldosAsync()
    {
        string carpeta;
        try
        {
            carpeta = (await Sesion.Api.GetAsync<ListaRespaldos>("api/respaldos"))?.Carpeta ?? "";
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
            return;
        }

        TablaWindow? w = null;
        w = new TablaWindow("Respaldos",
            "Cada vez que se cierra el programa en una caja se guarda una copia de la base de datos. Se guardan los de los últimos 30 días.\n" +
            $"Están en la computadora principal, en {carpeta}. Cópielos de vez en cuando a una USB o a la nube.",
            [
                new Columna("Fecha", nameof(RespaldoDto.FechaLocal), "{0:dddd dd/MM/yyyy  h:mm tt}", 300),
                new Columna("Cuándo", nameof(RespaldoDto.HaceTexto), Ancho: 175),
                new Columna("Por qué", nameof(RespaldoDto.Motivo)),
                new Columna("Tamaño", nameof(RespaldoDto.TamanoTexto), Ancho: 110)
            ],
            async () => (IEnumerable)((await Sesion.Api.GetAsync<ListaRespaldos>("api/respaldos"))?.Respaldos ?? []),
            new AccionTabla("💾  Hacer un respaldo ahora", async _ =>
            {
                var r = await ConEspera(w!, () => Sesion.Api.PostAsync<RespaldoDto>("api/respaldos", new { }, TimeSpan.FromMinutes(10)));
                Mensajes.Exito(w, $"Listo. Se guardó el respaldo ({r!.TamanoTexto}).");
                return true;
            }, RequiereSeleccion: false, Estilo: "BotonVerde"),
            new AccionTabla("↩  Regresar a este respaldo", async sel => await RestaurarAsync(w!, (RespaldoDto)sel!), Estilo: "BotonRojo"))
        { Owner = this };
        w.ShowDialog();
    }

    /// <summary>Regresa la base a como estaba en el respaldo. Pide confirmación y la clave de autorización del encargado.</summary>
    private static async Task<bool> RestaurarAsync(Window w, RespaldoDto r)
    {
        var cuando = $"{r.FechaLocal:dddd dd/MM/yyyy} a las {r.FechaLocal:h:mm tt}";
        if (!Mensajes.Confirmar(w,
                $"¿Regresar la base de datos a como estaba el {cuando}?\n\n" +
                "⚠ Se perderá todo lo que se hizo después: ventas, productos, precios, clientes, cortes…\n\n" +
                "Antes de hacerlo se guarda una copia de cómo está ahora, por si se arrepiente.\n" +
                "Todas las cajas se cerrarán y tendrán que volver a entrar.",
                "Sí, regresar a ese respaldo", "No", icono: "⚠"))
            return false;

        var datos = CampoDialog.MostrarCon(w, "Confirmar con el encargado",
            "Para regresar a un respaldo, el encargado teclea su clave de autorización.", "Regresar al respaldo",
            new Campo("Clave de autorización", Password: true));
        if (datos is null)
            return false;

        await ConEspera(w, () => Sesion.Api.PostAsync<RespaldoDto>("api/respaldos/restaurar",
            new RestaurarRespaldoRequest(r.Nombre, datos[0]), TimeSpan.FromMinutes(15)));
        // El servidor avisa a todas las cajas (también a esta); por si no llega el aviso, se corta aquí también
        App.CortarSesion($"Listo. La base de datos regresó a como estaba el {cuando}\n\nVuelva a entrar.", error: false);
        return false;
    }

    /// <summary>Reloj de arena y ventana desactivada mientras el servidor trabaja.</summary>
    private static async Task<T> ConEspera<T>(Window w, Func<Task<T>> trabajo)
    {
        w.IsEnabled = false;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            return await trabajo();
        }
        finally
        {
            Mouse.OverrideCursor = null;
            w.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ Báscula

    /// <summary>Prueba para el encargado o el técnico: lee la báscula y muestra lo que recibe.</summary>
    private async Task ProbarBasculaAsync()
    {
        var cfg = Sesion.Config.Bascula;
        if (Sesion.Bascula is not { } bascula)
        {
            Mensajes.Info(this, "Esta caja no tiene báscula configurada.\n\nPara configurarla: Salir → en la pantalla de entrada, \"⚙ Configuración\" → Báscula.");
            return;
        }
        Mensajes.Info(this, "Ponga algo encima de la báscula y presione Aceptar.");
        try
        {
            var l = await bascula.LeerPesoAsync();
            Mensajes.Exito(this, $"La báscula funciona.\n\nPeso: {l.Kilogramos:0.000} kg {(l.Estable ? "" : "(se estaba moviendo)")}\n\n" +
                                 $"Datos técnicos — marca: {cfg.Perfil}, puerto: {cfg.Puerto}, recibido: {BasculaSerial.Visible(l.Crudo)}");
        }
        catch (Exception ex)
        {
            Mensajes.Error(this, "No se pudo leer la báscula.\n\nRevise que esté prendida y conectada, y que en \"⚙ Configuración\" estén bien la marca y el puerto.\n\n" +
                                 $"Datos técnicos — marca: {cfg.Perfil}, puerto: {cfg.Puerto}. {ex.Message}");
        }
    }
}
