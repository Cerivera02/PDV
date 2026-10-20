using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using PDV.Caja.Servicios;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Caja.Ventanas;

public sealed class LineaVenta(Producto producto, decimal cantidad) : INotifyPropertyChanged
{
    private decimal _cantidad = cantidad;
    private PrecioCalculado? _precio;

    public Producto Producto { get; } = producto;

    public decimal Cantidad
    {
        get => _cantidad;
        set
        {
            _cantidad = value;
            Avisar();
            Avisar(nameof(CantidadTexto));
        }
    }

    /// <summary>Precio cambiado a mano solo en esta cuenta (null = el del sistema, con promociones).</summary>
    public decimal? PrecioManual { get; private set; }
    /// <summary>Firma del encargado que lo autorizó (null si quien cobra tiene el permiso).</summary>
    public string? Autorizacion { get; private set; }
    /// <summary>Quién cambió o autorizó el precio (para mostrarlo).</summary>
    public string? AutorizoNombre { get; private set; }

    public void CambiarPrecio(decimal precio, string? autorizacion, string quien)
    {
        PrecioManual = precio;
        Autorizacion = autorizacion;
        AutorizoNombre = quien;
    }

    public void QuitarPrecioCambiado() => PrecioManual = null;

    /// <summary>Precio con promoción (lo calcula VentaWindow con CalculadoraPrecios, igual que el servidor).</summary>
    public void AplicarPrecio(PrecioCalculado precio)
    {
        _precio = precio;
        Avisar(nameof(Importe));
        Avisar(nameof(Descuento));
        Avisar(nameof(Descripcion));
        Avisar(nameof(PrecioTexto));
    }

    public string Descripcion => _precio switch
    {
        { TipoOferta: TiposOferta.Manual } => $"{Producto.Nombre}    ✏ Precio cambiado (normal {Formato.Dinero(Producto.PrecioVenta)})",
        { Promocion: { } promo } => $"{Producto.Nombre}    🏷 {promo}",
        _ => Producto.Nombre
    };
    public string CantidadTexto => Producto.PorPeso ? $"{Cantidad:0.000} kg" : Cantidad.ToString("0.###");
    public string PrecioTexto
    {
        get
        {
            var precio = _precio?.PrecioUnitario ?? Producto.PrecioVenta;
            return Producto.PorPeso ? $"{Formato.Dinero(precio)} el kilo" : Formato.Dinero(precio);
        }
    }
    public decimal Importe => _precio?.Importe ?? Dinero.Redondear(Cantidad * Producto.PrecioVenta);
    public decimal Descuento => _precio?.Descuento ?? 0;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Avisar([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>
/// Una cuenta abierta en la caja. Puede haber varias a la vez: si un cliente se va un momento
/// ("se me olvidó algo"), su cuenta se queda en espera y se atiende al siguiente en otra.
/// </summary>
public sealed class Cuenta(int numero)
{
    public int Numero { get; } = numero;
    public ObservableCollection<LineaVenta> Lineas { get; } = [];
    /// <summary>Cliente de esta cuenta (null = público en general).</summary>
    public Cliente? Cliente { get; set; }
    public bool Vacia => Lineas.Count == 0 && Cliente is null;
    public decimal Total => Lineas.Sum(l => l.Importe);
    public string Titulo => Cliente is not null ? $"👤 {Cliente.Nombre}" : $"🧾 Cuenta {Numero}";
}

public partial class VentaWindow : Window
{
    private readonly ConfigCaja _cfg = Sesion.Config;
    private Turno? _turno;
    private TicketDto? _ultimoTicket;
    private bool _ocupado;

    // ---- Cuentas abiertas (la que se ve en pantalla es _cuenta)
    private readonly List<Cuenta> _cuentas = [];
    private Cuenta _cuenta = null!;

    /// <summary>Productos de la cuenta que se está viendo.</summary>
    private ObservableCollection<LineaVenta> _lineas => _cuenta.Lineas;

    /// <summary>Cliente de la cuenta que se está viendo (null = público en general).</summary>
    private Cliente? _cliente
    {
        get => _cuenta.Cliente;
        set => _cuenta.Cliente = value;
    }

    public VentaWindow()
    {
        InitializeComponent();
        // Ocupa todo el monitor y se ajusta a su tamaño (1366×768, 1920×1080, etc.)
        PantallaCompleta.Aplicar(this, Sesion.Config.PantallaCompleta);
        LlenarAtajos();
        NuevaCuenta();

        var reloj = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        reloj.Tick += (_, _) => lblReloj.Text = DateTime.Now.ToString("dddd d 'de' MMMM  ·  h:mm tt");
        reloj.Start();

        Loaded += async (_, _) =>
        {
            IniciarBascula();
            await PrepararCajaAsync();
        };
        Closed += async (_, _) =>
        {
            if (_monitor is not null)
                await _monitor.DisposeAsync(); // suelta el puerto de la báscula
        };
        Closing += AlCerrar;
        // El foco siempre regresa a la caja de código (el lector de barras "escribe" ahí).
        Activated += (_, _) => EnfocarCodigo();
    }

    // ===================================================================== Abrir la caja

    private async Task PrepararCajaAsync()
    {
        try
        {
            _turno = await Sesion.Api.GetAsync<Turno>($"api/turnos/actual?cajaId={_cfg.CajaId}");
            if (_turno is not null && _turno.UsuarioId != Sesion.Usuario!.UsuarioId)
            {
                Mensajes.Error(this, $"Esta caja la tiene abierta {_turno.UsuarioNombre}.\n\nPrimero esa persona tiene que entrar y usar \"Cerrar caja\".");
                _turno = null;
                Close();
                return;
            }

            if (_turno is null)
            {
                var fondo = CampoDialog.PedirDecimal(this, "Abrir la caja", "¿Con cuánto dinero empieza la caja?", "0",
                    "Cuente el cambio que hay en el cajón antes de empezar. Si no hay nada, deje 0.");
                if (fondo is null)
                {
                    Close();
                    return;
                }
                _turno = await Sesion.Api.PostAsync<Turno>("api/turnos/abrir", new AbrirTurnoRequest(_cfg.CajaId, fondo.Value));
            }

            lblEncabezado.Text = $"Caja {_cfg.CajaId}   ·   Atiende: {Sesion.Usuario!.Nombre}   ·   Abierta desde las {_turno!.AbiertoEn.ToLocalTime():h:mm tt}";
            EnfocarCodigo();
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
            Close();
        }
    }

    // ===================================================================== Captura

    private void Codigo_TextChanged(object sender, TextChangedEventArgs e) =>
        lblPista.Visibility = txtCodigo.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

    private async void Codigo_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        var entrada = txtCodigo.Text.Trim();
        txtCodigo.Clear();
        if (entrada.Length > 0)
            await Ejecutar(() => ProcesarEntradaAsync(entrada));
    }

    private async Task ProcesarEntradaAsync(string entrada)
    {
        // "3*7501234567890" → 3 piezas
        decimal? cantidad = null;
        var codigo = entrada;
        var asterisco = entrada.IndexOf('*');
        if (asterisco > 0 && Formato.TryDecimal(entrada[..asterisco], out var multiplicador) && multiplicador > 0)
        {
            cantidad = multiplicador;
            codigo = entrada[(asterisco + 1)..].Trim();
        }

        var producto = Sesion.Catalogo.PorCodigo(codigo);

        // Etiqueta de báscula (EAN-13 con prefijo 2: número de producto + peso/importe)
        if (producto is null && EtiquetaBascula.TryParse(codigo, _cfg.EtiquetaBascula, out var plu, out var valor))
        {
            producto = Sesion.Catalogo.PorPlu(plu);
            if (producto is not null)
            {
                cantidad = _cfg.EtiquetaBascula.ValorEsPeso || producto.PrecioVenta == 0
                    ? valor
                    : Dinero.RedondearCantidad(valor / producto.PrecioVenta);
            }
        }

        // Códigos alternos que no están en el catálogo local
        producto ??= await Sesion.Api.GetAsync<Producto>($"api/productos/codigo/{Uri.EscapeDataString(codigo)}");

        if (producto is null)
        {
            // Si escribió letras, seguramente buscaba por nombre
            if (codigo.Any(char.IsLetter))
            {
                producto = BuscarProductoWindow.Seleccionar(this, codigo);
                if (producto is null)
                    return;
            }
            else
            {
                Estado($"No se encontró ningún producto con el código {codigo}.\nRevise el código o use \"Buscar producto por nombre\".", error: true);
                return;
            }
        }

        if (producto.PrecioVenta <= 0)
        {
            Estado($"\"{producto.Nombre}\" no tiene precio. Avise al encargado para que se lo ponga.", error: true);
            return;
        }

        if (producto.PorPeso && cantidad is null)
        {
            cantidad = await ObtenerPesoAsync(producto);
            if (cantidad is null)
                return;
        }

        cantidad ??= 1;
        if (!producto.PorPeso && cantidad != decimal.Truncate(cantidad.Value))
        {
            Estado($"\"{producto.Nombre}\" se vende por pieza: la cantidad debe ser un número entero.", error: true);
            return;
        }

        Agregar(producto, cantidad.Value);
    }

    private void Agregar(Producto producto, decimal cantidad)
    {
        // Las piezas del mismo producto se juntan en un renglón; lo que se pesa siempre va en renglón nuevo.
        var linea = producto.PorPeso ? null : _lineas.LastOrDefault(l => l.Producto.Id == producto.Id);
        if (linea is null)
        {
            linea = new LineaVenta(producto, cantidad);
            // Solo al cambiar la cantidad (si no, recalcular precios dispararía este evento otra vez)
            linea.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LineaVenta.Cantidad))
                    ActualizarTotales();
            };
            _lineas.Add(linea);
        }
        else
        {
            linea.Cantidad += cantidad;
        }

        gridLineas.SelectedItem = linea;
        gridLineas.ScrollIntoView(linea);
        panelCambio.Visibility = Visibility.Collapsed;
        Estado($"✔ {linea.Producto.Nombre}  —  {Formato.Dinero(linea.Importe)}");
    }

    /// <summary>
    /// Peso para un producto por kilo: si la báscula ya tiene un peso estable, se usa al instante;
    /// si no, se abre la báscula en grande hasta que se estabilice. Sin báscula, se escribe a mano.
    /// </summary>
    private Task<decimal?> ObtenerPesoAsync(Producto producto)
    {
        if (_monitor is not null)
        {
            if (_monitor.PesoEstableReciente(TimeSpan.FromSeconds(1)) is { } listo)
                return Task.FromResult<decimal?>(listo);
            return Task.FromResult(BasculaWindow.Mostrar(this, _monitor, producto));
        }

        var peso = CampoDialog.PedirDecimal(this, "Peso", $"¿Cuántos kilos pesa \"{producto.Nombre}\"?", "",
            "Ejemplos: medio kilo = 0.5   ·   un kilo y cuarto = 1.25   ·   300 gramos = 0.3", minimo: 0.001m);
        return Task.FromResult(peso is > 0 ? Dinero.RedondearCantidad(peso.Value) : (decimal?)null);
    }

    // ===================================================================== Báscula en vivo

    private MonitorBascula? _monitor;

    /// <summary>Empieza a leer la báscula en segundo plano para el indicador de abajo a la izquierda.</summary>
    private void IniciarBascula()
    {
        if (Sesion.Bascula is not { } bascula)
        {
            lblPeso.Text = "Sin báscula";
            lblPeso.FontSize = 16;
            lblPeso.Foreground = (Brush)FindResource("TextoSuave");
            return;
        }
        _monitor = new MonitorBascula(bascula, TimeSpan.FromMilliseconds(300));
        _monitor.Lectura += l => Dispatcher.BeginInvoke(() => MostrarPeso(l));
        _monitor.Falla += _ => Dispatcher.BeginInvoke(() =>
        {
            lblPeso.Text = "—";
            lblPesoEstado.Text = "⚠ sin señal";
            lblPesoEstado.Foreground = (Brush)FindResource("Peligro");
        });
        _monitor.Iniciar();
    }

    private void MostrarPeso(LecturaPeso l)
    {
        lblPeso.Text = $"{l.Kilogramos:0.000} kg";
        (lblPesoEstado.Text, var color) = l.Kilogramos <= 0 ? ("", "TextoSuave") : l.Estable ? ("✔ estable", "Exito") : ("⏳ moviéndose", "Aviso");
        lblPesoEstado.Foreground = (Brush)FindResource(color);
        lblPeso.Foreground = (Brush)FindResource(l.Kilogramos <= 0 ? "TextoSuave" : "Primario");
    }

    private async void Peso_Click(object sender, MouseButtonEventArgs e) => await Ejecutar(PesarAsync);

    // ===================================================================== Botones y teclas

    /// <summary>Atajos visibles en la barra de abajo (tecla en negritas). Mantener igual que <see cref="AccionPara"/>.</summary>
    private static readonly (string Tecla, string Accion)[] Atajos =
    [
        ("Enter", "Agregar"),
        ("3*código", "Varias piezas"),
        ("F1", "Nueva cuenta"),
        ("F2", "Pesar"),
        ("F3", "Buscar"),
        ("F4", "Cantidad"),
        ("F5", "Tickets"),
        ("F7", "Sacar dinero"),
        ("F8", "¿Cómo voy?"),
        ("F9", "Cancelar venta"),
        ("F10", "Cerrar caja"),
        ("F11", "Cliente"),
        ("F12", "COBRAR"),
        ("↑↓", "Moverse")
    ];

    /// <summary>Muestra las flechas ← → como las configuró el encargado (en la barra de arriba y en la de atajos).</summary>
    private (string Tecla, string Accion)[] AtajosConFlechas()
    {
        static string Nombre(string accion) => accion == AccionesFlecha.Precio ? "Precio" : "Cantidad";
        var c = Sesion.ConfigVenta;
        btnCantidad.MostrarTecla(c.FlechaIzquierda == AccionesFlecha.Cantidad ? "F4 · ←" : "F4 · →");
        btnPrecio.MostrarTecla(c.FlechaIzquierda == AccionesFlecha.Precio ? "←" : "→");
        var lista = Atajos.ToList();
        lista.Insert(lista.FindIndex(a => a.Tecla == "↑↓"), ("← →", $"{Nombre(c.FlechaIzquierda)} / {Nombre(c.FlechaDerecha)}"));
        return [.. lista];
    }

    private void LlenarAtajos()
    {
        var atajos = AtajosConFlechas();
        lblAtajos.Inlines.Clear();
        lblAtajos.Inlines.Add(new System.Windows.Documents.Run("⌨ Atajos:  ") { FontWeight = FontWeights.SemiBold });
        for (var i = 0; i < atajos.Length; i++)
        {
            if (i > 0)
                lblAtajos.Inlines.Add(new System.Windows.Documents.Run(" · ") { Foreground = Brushes.Gray });
            lblAtajos.Inlines.Add(new System.Windows.Documents.Run(atajos[i].Tecla) { FontWeight = FontWeights.Bold, Foreground = (Brush)FindResource("Primario") });
            lblAtajos.Inlines.Add(new System.Windows.Documents.Run(" " + atajos[i].Accion));
        }
    }

    /// <summary>Las acciones de dinero necesitan la caja ya abierta (por si alguien presiona antes de tiempo).</summary>
    private bool CajaLista()
    {
        if (_turno is not null)
            return true;
        Estado("Espere un momento, la caja se está abriendo…", error: true);
        return false;
    }

    private Func<Task>? AccionPara(Key tecla) => tecla switch
    {
        Key.F2 => PesarAsync,
        Key.F3 => BuscarAsync,
        Key.F4 => CambiarCantidadAsync,
        Key.F5 => TicketsAsync,
        Key.F6 or Key.Delete => QuitarSeleccionadoAsync,
        Key.F7 => SacarDineroAsync,
        Key.F8 => ComoVoyAsync,
        Key.F9 => CancelarVentaAsync,
        Key.F10 => CerrarCajaAsync,
        Key.F11 => ElegirClienteAsync,
        Key.F12 => CobrarAsync,
        Key.F1 => NuevaCuentaAsync,
        _ => null
    };

    private async void Boton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<Key>(tag, out var tecla) && AccionPara(tecla) is { } accion)
            await Ejecutar(accion);
    }

    private void Salir_Click(object sender, RoutedEventArgs e) => Close();

    private async void Ventana_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_ocupado)
            return;

        // Flechas para moverse en la lista sin soltar la caja de código
        if (e.Key is Key.Up or Key.Down && _lineas.Count > 0)
        {
            gridLineas.SelectedIndex = Math.Clamp(gridLineas.SelectedIndex + (e.Key == Key.Up ? -1 : 1), 0, _lineas.Count - 1);
            gridLineas.ScrollIntoView(gridLineas.SelectedItem);
            e.Handled = true;
            return;
        }

        // Flechas ← →: cantidad o precio del producto marcado (lo elige el encargado). Con texto en la caja de código, mueven el cursor.
        if (e.Key is Key.Left or Key.Right && txtCodigo.Text.Length == 0)
        {
            e.Handled = true;
            var accionFlecha = e.Key == Key.Left ? Sesion.ConfigVenta.FlechaIzquierda : Sesion.ConfigVenta.FlechaDerecha;
            await Ejecutar(accionFlecha == AccionesFlecha.Precio ? CambiarPrecioAsync : CambiarCantidadAsync);
            return;
        }

        // Supr con texto en la caja de código: borra texto (no quita el producto)
        if (e.Key == Key.Delete && txtCodigo.Text.Length > 0)
            return;

        // F10 es "tecla de sistema" en Windows (activa menús): llega como Key.System y el nombre real viene en SystemKey
        var tecla = e.Key == Key.System ? e.SystemKey : e.Key;
        if (AccionPara(tecla) is not { } accion)
            return;
        e.Handled = true;
        await Ejecutar(accion);
    }

    /// <summary>
    /// "+" suma una pieza y "−" resta una al producto seleccionado (teclado normal o numérico).
    /// Solo con la caja de código vacía; si se está escribiendo un código, se escriben normal.
    /// </summary>
    private async void Ventana_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        if (_ocupado || txtCodigo.Text.Length > 0 || e.Text is not ("+" or "-"))
            return;
        e.Handled = true;
        var delta = e.Text == "+" ? 1 : -1;
        await Ejecutar(() => CambiarUnaPiezaAsync(delta));
    }

    private Task CambiarUnaPiezaAsync(int delta)
    {
        if (Seleccionada is not { } linea)
        {
            Estado("Primero toque en la lista el producto al que le quiere sumar o restar.", error: true);
            return Task.CompletedTask;
        }
        if (linea.Producto.PorPeso)
        {
            Estado($"\"{linea.Producto.Nombre}\" se vende por kilo: use Pesar (F2) o Cantidad (F4).", error: true);
            return Task.CompletedTask;
        }
        if (linea.Cantidad + delta <= 0)
            return QuitarSeleccionadoAsync();

        linea.Cantidad += delta;
        Estado($"{linea.Producto.Nombre}: {linea.Cantidad:0} {(linea.Cantidad == 1 ? "pieza" : "piezas")}  —  {Formato.Dinero(linea.Importe)}");
        return Task.CompletedTask;
    }

    private LineaVenta? Seleccionada => gridLineas.SelectedItem as LineaVenta;

    /// <summary>
    /// F2 / botón Pesar / clic en el peso de abajo: muestra la báscula en grande para "solo pesar".
    /// Si en la lista está seleccionado un producto por kilo, ofrece usar ese peso para él.
    /// </summary>
    private Task PesarAsync()
    {
        var linea = Seleccionada is { Producto.PorPeso: true } l ? l : null;

        if (_monitor is null)
        {
            if (linea is null)
            {
                Mensajes.Info(this, "Esta caja no tiene báscula conectada.\n\nEl encargado la configura en la pantalla de entrada → \"⚙ Configuración\" → Báscula.");
                return Task.CompletedTask;
            }
            var manual = CampoDialog.PedirDecimal(this, "Peso", $"¿Cuántos kilos pesa \"{linea.Producto.Nombre}\"?", linea.Cantidad.ToString("0.000"), minimo: 0.001m);
            if (manual is { } m)
                linea.Cantidad = Dinero.RedondearCantidad(m);
            return Task.CompletedTask;
        }

        if (BasculaWindow.Mostrar(this, _monitor, linea?.Producto, soloOfrecer: true) is { } kg && linea is not null)
        {
            linea.Cantidad = kg;
            Estado($"✔ {linea.Producto.Nombre}: {kg:0.000} kg  —  {Formato.Dinero(linea.Importe)}");
        }
        return Task.CompletedTask;
    }

    private Task BuscarAsync()
    {
        if (BuscarProductoWindow.Seleccionar(this, "") is { } p)
            return ProcesarEntradaAsync(p.CodigoBarras);
        return Task.CompletedTask;
    }

    private Task CambiarCantidadAsync()
    {
        if (Seleccionada is not { } linea)
        {
            Estado("Primero toque en la lista el producto al que le quiere cambiar la cantidad.", error: true);
            return Task.CompletedTask;
        }
        var pregunta = linea.Producto.PorPeso ? "¿Cuántos kilos son?" : "¿Cuántos son?";
        var nueva = CampoDialog.PedirDecimal(this, linea.Producto.Nombre, pregunta, linea.Cantidad.ToString("0.###"),
            "Si escribe 0 se quita de la venta.");
        if (nueva is null)
            return Task.CompletedTask;
        if (nueva == 0)
            _lineas.Remove(linea);
        else if (!linea.Producto.PorPeso && nueva != decimal.Truncate(nueva.Value))
            Estado("Este producto se vende por pieza: escriba un número entero.", error: true);
        else
            linea.Cantidad = Dinero.RedondearCantidad(nueva.Value);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Cambia el precio del producto marcado SOLO en esta cuenta (el precio del producto no cambia).
    /// Quien no tiene el permiso especial necesita la clave de autorización del encargado.
    /// </summary>
    private async Task CambiarPrecioAsync()
    {
        if (Seleccionada is not { } linea)
        {
            Estado("Primero toque en la lista el producto al que le quiere cambiar el precio.", error: true);
            return;
        }
        var p = linea.Producto;
        var porUnidad = p.PorPeso ? "el kilo" : "cada uno";
        var r = CampoDialog.MostrarCon(this, $"Cambiar precio: {p.Nombre}",
            $"Precio normal: {p.PrecioTexto}.\nEl cambio es solo para esta cuenta; el precio del producto no se cambia.", "Cambiar precio",
            new Campo($"Nuevo precio ({porUnidad})", (linea.PrecioManual ?? p.PrecioVenta).ToString("0.00"),
                Numero: true, Opcional: true, Minimo: 0.01m, Ayuda: "Déjelo vacío para regresar al precio normal."));
        if (r is null)
            return;

        if (!Formato.TryDecimal(r[0], out var nuevo) || Dinero.Redondear(nuevo) == p.PrecioVenta)
        {
            linea.QuitarPrecioCambiado();
            ActualizarTotales();
            Estado($"{p.Nombre}: regresó a su precio normal ({p.PrecioTexto}).");
            return;
        }
        nuevo = Dinero.Redondear(nuevo);

        string? autorizacion = null;
        var quien = Sesion.Usuario!.Nombre;
        if (!Sesion.Puede(Permisos.CambiarPrecio))
        {
            if (await PedirAutorizacionAsync(Permisos.CambiarPrecio, $"Cambiar el precio de {p.Nombre} de {Formato.Dinero(p.PrecioVenta)} a {Formato.Dinero(nuevo)}.") is not { } a)
                return;
            autorizacion = a.Token;
            quien = a.Nombre;
        }
        linea.CambiarPrecio(nuevo, autorizacion, quien);
        ActualizarTotales();
        Estado($"✏ {p.Nombre}: {Formato.Dinero(nuevo)} {porUnidad} (antes {Formato.Dinero(p.PrecioVenta)}) · autorizó {quien}");
    }

    /// <summary>El encargado (o alguien con el permiso) teclea su clave de autorización; el servidor devuelve su autorización firmada.</summary>
    private async Task<AutorizacionDto?> PedirAutorizacionAsync(string permiso, string que)
    {
        var r = CampoDialog.MostrarCon(this, "Autorización del encargado",
            $"{que}\n\nPara hacerlo, el encargado teclea su clave de autorización.", "Autorizar",
            new Campo("Clave de autorización", Password: true));
        if (r is null)
            return null;
        return await Sesion.Api.PostAsync<AutorizacionDto>("api/autorizaciones", new AutorizacionRequest(r[0], permiso));
    }

    private async void Precio_Click(object sender, RoutedEventArgs e) => await Ejecutar(CambiarPrecioAsync);

    private Task QuitarSeleccionadoAsync()
    {
        if (Seleccionada is not { } linea)
        {
            Estado("Primero toque en la lista el producto que quiere quitar.", error: true);
            return Task.CompletedTask;
        }
        var i = gridLineas.SelectedIndex;
        _lineas.Remove(linea);
        if (_lineas.Count > 0)
            gridLineas.SelectedIndex = Math.Min(i, _lineas.Count - 1);
        Estado($"Se quitó \"{linea.Producto.Nombre}\".");
        return Task.CompletedTask;
    }

    /// <summary>Borra la cuenta que se está viendo (con confirmación). Las demás cuentas no se tocan.</summary>
    private Task BorrarCuentaAsync()
    {
        if (_cuenta.Vacia)
            return Task.CompletedTask;
        if (!Mensajes.Confirmar(this, $"¿Borrar \"{_cuenta.Titulo}\"?\nSe quitarán todos sus productos. Las demás cuentas no se tocan.", "Sí, borrar", "No"))
            return Task.CompletedTask;
        var titulo = _cuenta.Titulo;
        CerrarCuentaActual();
        Estado($"Se borró {titulo}.");
        return Task.CompletedTask;
    }

    private async void BorrarCuenta_Click(object sender, RoutedEventArgs e) => await Ejecutar(BorrarCuentaAsync);

    // ===================================================================== Varias cuentas abiertas

    /// <summary>
    /// Deja la cuenta actual en espera y pasa a una vacía (si ya hay una vacía, se usa esa).
    /// Ejemplo: el cliente dice "espéreme, se me olvidó algo" → F1 y se atiende al siguiente.
    /// </summary>
    private void NuevaCuenta()
    {
        var vacia = _cuentas.FirstOrDefault(c => c.Vacia);
        if (vacia is null)
        {
            // El número libre más bajo: normalmente "Cuenta 1"; con una en espera, "Cuenta 2"…
            var numero = Enumerable.Range(1, _cuentas.Count + 1).First(n => _cuentas.All(c => c.Numero != n));
            vacia = new Cuenta(numero);
            vacia.Lineas.CollectionChanged += (_, _) =>
            {
                if (ReferenceEquals(vacia, _cuenta))
                    ActualizarTotales();
                else
                    ActualizarPestanas();
            };
            _cuentas.Add(vacia);
        }
        Activar(vacia);
    }

    private Task NuevaCuentaAsync()
    {
        if (_cuenta.Vacia)
        {
            Estado("Ya está en una cuenta nueva: pase los productos del siguiente cliente.");
            return Task.CompletedTask;
        }
        var anterior = _cuenta.Titulo;
        NuevaCuenta();
        Estado($"{anterior} quedó en espera. Atienda al siguiente cliente en {_cuenta.Titulo}.");
        return Task.CompletedTask;
    }

    /// <summary>Muestra una cuenta en pantalla (productos, cliente y totales).</summary>
    private void Activar(Cuenta cuenta)
    {
        // Una cuenta vacía que se deja de ver no sirve: se quita (así la numeración vuelve a "Cuenta 1")
        _cuentas.RemoveAll(c => c.Vacia && !ReferenceEquals(c, cuenta));
        _cuenta = cuenta;
        gridLineas.ItemsSource = cuenta.Lineas;
        MostrarCliente(cuenta.Cliente); // también recalcula totales y pestañas
        if (cuenta.Lineas.Count > 0)
        {
            gridLineas.SelectedIndex = cuenta.Lineas.Count - 1;
            gridLineas.ScrollIntoView(gridLineas.SelectedItem);
        }
        EnfocarCodigo();
    }

    /// <summary>Quita la cuenta actual (ya cobrada o borrada) y pasa a una vacía, NO a una en espera.</summary>
    private void CerrarCuentaActual()
    {
        _cuentas.Remove(_cuenta);
        NuevaCuenta();
    }

    /// <summary>Cuentas con productos o cliente que no se están viendo.</summary>
    private int CuentasEnEspera => _cuentas.Count(c => !c.Vacia && !ReferenceEquals(c, _cuenta));

    /// <summary>Dibuja las pestañas: una por cuenta abierta, con su total. La actual va resaltada.</summary>
    private void ActualizarPestanas()
    {
        panelCuentas.Children.Clear();
        foreach (var c in _cuentas.Where(c => !c.Vacia || ReferenceEquals(c, _cuenta)).OrderBy(c => c.Numero))
        {
            var actual = ReferenceEquals(c, _cuenta);
            var b = new Button
            {
                Content = $"{c.Titulo}  ·  {Formato.Dinero(c.Total)}",
                Style = (Style)FindResource(actual ? "BotonAzul" : typeof(Button)),
                Focusable = false,
                FontSize = 16,
                MinHeight = 44,
                Padding = new Thickness(14, 6, 14, 6),
                Margin = new Thickness(0, 0, 8, 0),
                ToolTip = actual ? "Esta es la cuenta que está viendo" : "Toque para seguir con esta cuenta"
            };
            System.Windows.Automation.AutomationProperties.SetName(b, c.Titulo);
            var cuenta = c;
            b.Click += (_, _) =>
            {
                if (!ReferenceEquals(cuenta, _cuenta))
                {
                    Activar(cuenta);
                    Estado($"Siguiendo con {cuenta.Titulo}.");
                }
            };
            panelCuentas.Children.Add(b);
        }
        lblEnEspera.Text = CuentasEnEspera switch
        {
            0 => "",
            1 => "⏸ 1 cuenta en espera",
            var n => $"⏸ {n} cuentas en espera"
        };
    }

    private async void NuevaCuenta_Click(object sender, RoutedEventArgs e) => await Ejecutar(NuevaCuentaAsync);

    // ===================================================================== Cobrar

    private async Task CobrarAsync()
    {
        if (_turno is null)
            return;
        if (_lineas.Count == 0)
        {
            Estado("No hay productos para cobrar. Pase primero los productos por el lector.", error: true);
            return;
        }

        var total = _lineas.Sum(l => l.Importe);
        var pagos = CobroWindow.Cobrar(this, total, _cliente);
        if (pagos is null)
            return;

        var venta = new VentaRequest(_turno.Id,
            _lineas.Select(l => new VentaItemRequest(l.Producto.Id, l.Cantidad, l.PrecioManual, l.Autorizacion)).ToList(), pagos, _cliente?.Id);
        var ticket = await Sesion.Api.PostAsync<TicketDto>("api/ventas", venta);
        _ultimoTicket = ticket!;
        // La cuenta cobrada se cierra y se pasa a una vacía (las cuentas en espera se quedan como están)
        CerrarCuentaActual();

        lblCambio.Text = Formato.Dinero(ticket!.Cambio);
        lblUltimoFolio.Text = $"Ticket {ticket.Folio}  ·  Total {Formato.Dinero(ticket.Total)}";
        panelCambio.Visibility = Visibility.Visible;
        var enEspera = CuentasEnEspera > 0 ? $"\nHay {CuentasEnEspera} cuenta(s) en espera: tóquela arriba cuando regrese el cliente." : "";
        Estado((ticket.SaldoCliente is { } debe
            ? $"✔ Venta a crédito para {ticket.Cliente}. Ahora debe {Formato.Dinero(debe)} en total."
            : "✔ Venta cobrada. Lista para el siguiente cliente.") + enEspera);

        ImprimirSeguro(() =>
        {
            if (pagos.Any(p => p.FormaPago == FormasPago.Efectivo))
                Sesion.AbrirCajon();
            Sesion.Imprimir(TicketEscPos.Venta(_cfg.Impresora, ticket));
        });
    }

    /// <summary>Historial de tickets de esta caja (hoy), con el último cobrado ya elegido para reimprimirlo con Enter.</summary>
    private Task TicketsAsync()
    {
        Historiales.Tickets(this, _cfg.CajaId, _ultimoTicket?.Folio);
        return Task.CompletedTask;
    }

    private async Task CancelarVentaAsync()
    {
        var r = CampoDialog.MostrarCon(this, "Cancelar una venta ya cobrada", null, "Continuar",
            new Campo("Número de ticket", _ultimoTicket?.Folio ?? "", Ayuda: "Aparece arriba en el ticket (ejemplo: C1-000015)."),
            new Campo("¿Por qué se cancela?", Ayuda: "Ejemplo: el cliente ya no lo quiso, se cobró mal…"));
        if (r is null)
            return;

        var ticket = await Sesion.Api.GetAsync<TicketDto>($"api/ventas/folio/{Uri.EscapeDataString(r[0])}");
        if (ticket is null)
        {
            Mensajes.Error(this, $"No existe el ticket {r[0]}. Revise el número.");
            return;
        }
        if (!Mensajes.Confirmar(this, $"¿Cancelar la venta {ticket.Folio} de {Formato.Dinero(ticket.Total)}?\n\nTendrá que regresarle {Formato.Dinero(ticket.Total)} al cliente.",
                "Sí, cancelar la venta", "No"))
            return;

        // Quien tiene el permiso especial (o el encargado) no necesita la clave del encargado
        AutorizacionDto? aut = null;
        if (!Sesion.Puede(Permisos.CancelarVenta)
            && (aut = await PedirAutorizacionAsync(Permisos.CancelarVenta, $"Cancelar la venta {ticket.Folio} de {Formato.Dinero(ticket.Total)}.")) is null)
            return;
        await Sesion.Api.PostAsync($"api/ventas/{ticket.VentaId}/cancelar", new CancelarVentaRequest(r[1], aut?.Token));
        var cancelado = await Sesion.Api.GetAsync<TicketDto>($"api/ventas/{ticket.VentaId}/ticket");
        ImprimirSeguro(() => Sesion.Imprimir(TicketEscPos.Venta(_cfg.Impresora, cancelado!)));
        Mensajes.Exito(this, $"Venta cancelada.\n\nRegrésele {Formato.Dinero(ticket.Total)} al cliente.");
    }

    // ===================================================================== Clientes

    private Task ElegirClienteAsync()
    {
        if (ElegirClienteWindow.Elegir(this, "¿A qué cliente se le vende?") is { } c)
        {
            MostrarCliente(c);
            Estado($"Cliente: {c.Nombre}");
        }
        return Task.CompletedTask;
    }

    private void QuitarCliente_Click(object sender, RoutedEventArgs e)
    {
        MostrarCliente(null);
        Estado("Venta a público en general.");
        EnfocarCodigo();
    }

    /// <summary>Muestra (o quita) el cliente y recalcula precios con su precio especial.</summary>
    private void MostrarCliente(Cliente? c)
    {
        _cliente = c;
        panelCliente.Visibility = c is null ? Visibility.Collapsed : Visibility.Visible;
        if (c is not null)
        {
            lblClienteNombre.Text = $"👤 {c.Nombre}";
            var partes = new List<string>();
            if (c.Saldo > 0) partes.Add($"Debe {Formato.Dinero(c.Saldo)}");
            partes.Add(c.LimiteCredito > 0 ? $"Crédito disponible {Formato.Dinero(c.CreditoDisponible)}" : "Sin crédito");
            if (c.PrecioEspecialTexto.Length > 0) partes.Add($"🏷 {c.PrecioEspecialTexto}");
            lblClienteDetalle.Text = string.Join("  ·  ", partes);
        }
        ActualizarTotales();
    }

    private async Task RecibirAbonoAsync()
    {
        if (!CajaLista())
            return;
        if (ElegirClienteWindow.Elegir(this, "¿Qué cliente va a abonar?", soloConDeuda: true) is not { } c)
            return;

        string[] formas = [.. FormasPago.ParaAbonos.Select(FormasPago.Texto)];
        var r = CampoDialog.MostrarCon(this, $"Abono de {c.Nombre}", $"Debe: {Formato.Dinero(c.Saldo)}", "Recibir abono",
            new Campo("¿Cuánto abona?", c.Saldo.ToString("0.00"), Numero: true, Minimo: 0.001m, Ayuda: "Ya viene puesto todo lo que debe; cámbielo si abona menos."),
            new Campo("¿Cómo paga?", formas[0], Opciones: formas),
            new Campo("Número de autorización (opcional, si es tarjeta)"));
        if (r is null || !Formato.TryDecimal(r[0], out var monto))
            return;
        var indice = Array.IndexOf(formas, r[1]);
        var forma = FormasPago.ParaAbonos[indice >= 0 ? indice : 0];

        var comprobante = await Sesion.Api.PostAsync<ComprobanteAbono>($"api/clientes/{c.Id}/abonos",
            new AbonoRequest(_turno!.Id, monto, forma, r[2].Length > 0 ? r[2] : null));
        ImprimirSeguro(() =>
        {
            if (forma == FormasPago.Efectivo)
                Sesion.AbrirCajon();
            Sesion.Imprimir(TicketEscPos.Abono(_cfg.Impresora, comprobante!));
        });
        Mensajes.Exito(this, comprobante!.SaldoNuevo == 0
            ? $"Abono de {Formato.Dinero(monto)} recibido.\n\n¡{c.Nombre} ya no debe nada!"
            : $"Abono de {Formato.Dinero(monto)} recibido.\n\n{c.Nombre} ahora debe {Formato.Dinero(comprobante.SaldoNuevo)}.");
    }

    private async void RecibirAbono_Click(object sender, RoutedEventArgs e) => await Ejecutar(RecibirAbonoAsync);

    // ===================================================================== Dinero de la caja

    private async Task SacarDineroAsync()
    {
        if (!CajaLista())
            return;
        var r = CampoDialog.MostrarCon(this, "Sacar dinero de la caja", null, "Sacar dinero",
            new Campo("¿Cuánto dinero saca?", Numero: true, Minimo: 0.001m),
            new Campo("¿Para qué?", Ayuda: "Ejemplo: pago al proveedor de refrescos, compra de bolsas…"));
        if (r is null || !Formato.TryDecimal(r[0], out var monto))
            return;
        // Sin el permiso especial, el encargado autoriza con su clave de autorización
        AutorizacionDto? aut = null;
        if (!Sesion.Puede(Permisos.SacarDinero)
            && (aut = await PedirAutorizacionAsync(Permisos.SacarDinero, $"Sacar {Formato.Dinero(monto)} de la caja para: {r[1]}.")) is null)
            return;
        await Sesion.Api.PostAsync($"api/turnos/{_turno!.Id}/retiros", new RetiroRequest(monto, r[1], aut?.Token));
        ImprimirSeguro(Sesion.AbrirCajon);
        Estado(aut is null
            ? $"✔ Se registró que sacó {Formato.Dinero(monto)} de la caja."
            : $"✔ Se registró que sacó {Formato.Dinero(monto)} de la caja (autorizó {aut.Nombre}).");
    }

    private async Task ComoVoyAsync()
    {
        if (!CajaLista())
            return;
        var corte = await Sesion.Api.GetAsync<CorteDto>($"api/turnos/{_turno!.Id}/corte");
        if (Mensajes.Confirmar(this, ResumenCorte(corte!), "Imprimirlo", "Cerrar", icono: "📊"))
            ImprimirSeguro(() => Sesion.Imprimir(TicketEscPos.Corte(_cfg.Impresora, corte!)));
    }

    private async Task CerrarCajaAsync()
    {
        if (!CajaLista())
            return;
        var sinCobrar = _cuentas.Count(c => c.Lineas.Count > 0);
        if (sinCobrar > 0)
        {
            Mensajes.Info(this, sinCobrar == 1
                ? "Hay una cuenta sin cobrar. Cóbrela o bórrela antes de cerrar la caja."
                : $"Hay {sinCobrar} cuentas sin cobrar (arriba de la lista). Cóbrelas o bórrelas antes de cerrar la caja.");
            return;
        }
        if (!Mensajes.Confirmar(this, "¿Cerrar la caja?\n\nHágalo al terminar su turno o al final del día. Después de cerrarla ya no podrá vender hasta volver a abrirla.",
                "Sí, cerrar caja", "No"))
            return;

        var contado = CampoDialog.PedirDecimal(this, "Cerrar la caja", "¿Cuánto dinero en efectivo hay en la caja?", "",
            "Cuente todos los billetes y monedas del cajón y escriba el total.");
        if (contado is null)
            return;

        var corte = await Sesion.Api.PostAsync<CorteDto>($"api/turnos/{_turno!.Id}/cerrar", new CerrarTurnoRequest(contado.Value));
        _turno = null;
        ImprimirSeguro(() => Sesion.Imprimir(TicketEscPos.Corte(_cfg.Impresora, corte!)));
        Mensajes.Info(this, ResumenCorte(corte!));
        Close();
    }

    private static string ResumenCorte(CorteDto c)
    {
        var texto =
            $"Ventas cobradas: {c.NumVentas}\n" +
            (c.NumCanceladas > 0 ? $"Ventas canceladas: {c.NumCanceladas}\n" : "") +
            $"\nCobrado en efectivo: {Formato.Dinero(c.VentasEfectivo)}\n" +
            $"Cobrado con tarjeta: {Formato.Dinero(c.VentasTarjeta)}\n" +
            (c.VentasTransferencia > 0 ? $"Cobrado por transferencia: {Formato.Dinero(c.VentasTransferencia)}\n" : "") +
            (c.VentasCredito > 0 ? $"Vendido a crédito: {Formato.Dinero(c.VentasCredito)}\n" : "") +
            $"TOTAL VENDIDO: {Formato.Dinero(c.TotalVentas)}\n" +
            (c.AbonosEfectivo + c.AbonosOtros > 0
                ? $"\nAbonos de clientes: {Formato.Dinero(c.AbonosEfectivo + c.AbonosOtros)} (en efectivo: {Formato.Dinero(c.AbonosEfectivo)})\n"
                : "") +
            $"\nDinero con que empezó: {Formato.Dinero(c.FondoInicial)}\n" +
            (c.Retiros > 0 ? $"Dinero que se sacó: {Formato.Dinero(c.Retiros)}\n" : "") +
            $"Debe haber en la caja: {Formato.Dinero(c.EfectivoEsperado)}";

        if (c.EfectivoDeclarado is { } contado)
        {
            texto += $"\nUsted contó: {Formato.Dinero(contado)}\n\n" + c.Diferencia switch
            {
                0 => "✔ ¡Cuadra perfecto!",
                > 0 => $"Sobran {Formato.Dinero(c.Diferencia!.Value)}",
                _ => $"⚠ Faltan {Formato.Dinero(-c.Diferencia!.Value)}"
            };
        }
        return texto;
    }

    // ===================================================================== Utilidades

    private async Task Ejecutar(Func<Task> accion)
    {
        if (_ocupado)
            return;
        _ocupado = true;
        try
        {
            await accion();
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
        }
        finally
        {
            _ocupado = false;
            EnfocarCodigo();
        }
    }

    /// <summary>Si falla la impresora, la venta ya quedó guardada: solo se avisa.</summary>
    private void ImprimirSeguro(Action imprimir)
    {
        try
        {
            imprimir();
        }
        catch (Exception)
        {
            Estado("⚠ La venta sí se guardó, pero no se pudo imprimir. Revise que la impresora esté prendida y con papel, y use \"Tickets (F5)\" para imprimirlo otra vez.", error: true);
        }
    }

    private void ActualizarTotales()
    {
        // Promociones con la misma calculadora que el servidor → el total que se ve es el que se cobra
        var precios = CalculadoraPrecios.AplicarPromociones(_lineas.Select(l => (l.Producto, l.Cantidad, l.PrecioManual)).ToList(), DateTime.Today, _cliente);
        for (var i = 0; i < _lineas.Count; i++)
            _lineas[i].AplicarPrecio(precios[i]);

        var ahorro = _lineas.Sum(l => Math.Max(0, l.Descuento));
        lblAhorro.Text = $"🏷 El cliente ahorra {Formato.Dinero(ahorro)}";
        lblAhorro.Visibility = ahorro > 0 ? Visibility.Visible : Visibility.Collapsed;

        lblTotal.Text = Formato.Dinero(_lineas.Sum(l => l.Importe));
        var piezas = _lineas.Sum(l => l.Producto.PorPeso ? 1 : l.Cantidad);
        lblArticulos.Text = $"{piezas:0} artículo{(piezas == 1 ? "" : "s")}";
        panelVacio.Visibility = _lineas.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        ActualizarPestanas();
    }

    private void Estado(string texto, bool error = false)
    {
        lblEstado.Text = texto;
        panelEstado.Visibility = Visibility.Visible;
        panelEstado.Background = new SolidColorBrush(error ? Color.FromRgb(0xFD, 0xEC, 0xEA) : Color.FromRgb(0xEE, 0xF2, 0xF7));
        lblEstado.Foreground = (Brush)FindResource(error ? "Peligro" : "TextoSuave");
        if (error)
            System.Media.SystemSounds.Hand.Play();
    }

    private void EnfocarCodigo() => Dispatcher.BeginInvoke(() => txtCodigo.Focus(), DispatcherPriority.Input);

    private void AlCerrar(object? sender, CancelEventArgs e)
    {
        var sinCobrar = _cuentas.Count(c => c.Lineas.Count > 0);
        if (sinCobrar == 0 || Sesion.SesionCortada)
            return;
        var aviso = sinCobrar == 1 ? "Hay una cuenta sin cobrar." : $"Hay {sinCobrar} cuentas sin cobrar.";
        if (!Mensajes.Confirmar(this, $"{aviso}\n¿Salir y borrarlas?", "Sí, salir", "No"))
            e.Cancel = true;
    }
}
