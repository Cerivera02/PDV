using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PDV.Caja.Servicios;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>Días y caja elegidos en <see cref="HistorialWindow"/>.</summary>
/// <param name="CajaId">null = todas las cajas.</param>
public sealed record FiltroHistorial(DateTime DesdeDia, DateTime HastaDia, int? CajaId)
{
    /// <summary>"desde=…&amp;hasta=…[&amp;cajaId=…]": de la medianoche del primer día a la del día siguiente al último (hora local).</summary>
    public string Query
    {
        get
        {
            var q = $"desde={Fecha(DesdeDia)}&hasta={Fecha(HastaDia.AddDays(1))}";
            return CajaId is { } c ? q + $"&cajaId={c}" : q;
        }
    }

    private static string Fecha(DateTime dia) =>
        Uri.EscapeDataString(new DateTimeOffset(DateTime.SpecifyKind(dia.Date, DateTimeKind.Local)).ToString("o"));
}

/// <summary>Botón de <see cref="HistorialWindow"/>. Recibe el renglón elegido; devuelve true si hay que recargar la lista.</summary>
public sealed record AccionHistorial(string Texto, Func<object, Task<bool>> Ejecutar, string? Estilo = null);

/// <summary>
/// Historial con filtros de días (y de caja para el encargado), buscador, lista a la izquierda
/// y a la derecha el renglón elegido tal como sale impreso (ticket o corte).
/// Enter = primer botón (normalmente "Imprimir"), ↑↓ = moverse, Esc = cerrar.
/// </summary>
public sealed class HistorialWindow : Window
{
    private sealed record CajaOpcion(int? Id, string Nombre)
    {
        public override string ToString() => Nombre;
    }

    private readonly Func<FiltroHistorial, Task<IEnumerable>> _cargar;
    private readonly Func<object, Task<string>> _vistaPrevia;
    private readonly AccionHistorial[] _acciones;
    private readonly DataGrid _grid;
    private readonly TextBlock _vista;
    private readonly TextBlock _vacio;
    private readonly TextBlock _resumen;
    private readonly TextBlock _aviso;
    private readonly TextBox _busqueda;
    private readonly TextBlock _pista;
    private readonly DatePicker _desde;
    private readonly DatePicker _hasta;
    private readonly ComboBox _cajas;
    private readonly Panel _filtros;
    private readonly Panel _panelCaja;
    private readonly Dictionary<object, string> _vistas = new(ReferenceEqualityComparer.Instance);
    private List<object> _todos = [];
    private bool _sinEventos;
    private bool _primeraCarga = true;
    private int _version;

    /// <param name="cargar">Trae los renglones para los días y la caja elegidos.</param>
    /// <param name="vistaPrevia">Texto del renglón tal como se imprime.</param>
    public HistorialWindow(string titulo, string? explicacion, Columna[] columnas,
        Func<FiltroHistorial, Task<IEnumerable>> cargar, Func<object, Task<string>> vistaPrevia, params AccionHistorial[] acciones)
    {
        Title = titulo;
        var area = SystemParameters.WorkArea;
        Width = Math.Min(1500, area.Width - 40);
        Height = Math.Min(860, area.Height - 30);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = 17;
        Background = (Brush)FindResource("Fondo");
        _cargar = cargar;
        _vistaPrevia = vistaPrevia;
        _acciones = acciones;

        // ---- Encabezado
        var encabezado = new StackPanel { Margin = new Thickness(16, 14, 16, 0) };
        encabezado.Children.Add(new TextBlock { Text = titulo, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Primario") });
        if (!string.IsNullOrWhiteSpace(explicacion))
            encabezado.Children.Add(new TextBlock { Text = explicacion, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("TextoSuave"), Margin = new Thickness(0, 4, 0, 0) });

        // ---- Filtros: días y caja
        _desde = NuevoCalendario();
        _hasta = NuevoCalendario();
        _desde.SelectedDateChanged += async (_, _) => await AlCambiarFiltroAsync();
        _hasta.SelectedDateChanged += async (_, _) => await AlCambiarFiltroAsync();
        _cajas = new ComboBox { Width = 220, VerticalAlignment = VerticalAlignment.Center };
        _cajas.SelectionChanged += async (_, _) => await AlCambiarFiltroAsync();

        var dias = new WrapPanel { VerticalAlignment = VerticalAlignment.Center };
        dias.Children.Add(BotonDias("Hoy", DateTime.Today, DateTime.Today));
        dias.Children.Add(BotonDias("Ayer", DateTime.Today.AddDays(-1), DateTime.Today.AddDays(-1)));
        dias.Children.Add(BotonDias("Últimos 7 días", DateTime.Today.AddDays(-6), DateTime.Today));
        dias.Children.Add(BotonDias("Este mes", new DateTime(DateTime.Today.Year, DateTime.Today.Month, 1), DateTime.Today));
        dias.Children.Add(Etiqueta("Del día"));
        dias.Children.Add(_desde);
        dias.Children.Add(Etiqueta("al"));
        dias.Children.Add(_hasta);
        _filtros = dias;

        _panelCaja = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };
        _panelCaja.Children.Add(Etiqueta("Caja:"));
        _panelCaja.Children.Add(_cajas);

        var filtros = new WrapPanel { Margin = new Thickness(11, 10, 11, 0) };
        filtros.Children.Add(dias);
        filtros.Children.Add(_panelCaja);

        // ---- Buscador (con pista encima mientras está vacío)
        // Los Name sirven de AutomationId (lectores de pantalla y pruebas automáticas)
        _busqueda = new TextBox { FontSize = 20, Name = "txtBuscar" };
        _pista = new TextBlock
        {
            FontSize = 18, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("TextoSuave"), IsHitTestVisible = false
        };
        _busqueda.TextChanged += (_, _) =>
        {
            _pista.Visibility = _busqueda.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Filtrar();
        };
        var buscador = new Grid { Margin = new Thickness(16, 10, 16, 0) };
        buscador.Children.Add(_busqueda);
        buscador.Children.Add(_pista);

        // ---- Lista
        _grid = new DataGrid
        {
            IsReadOnly = true, AutoGenerateColumns = false, SelectionMode = DataGridSelectionMode.Single,
            CanUserAddRows = false, Margin = new Thickness(0, 0, 12, 0)
        };
        foreach (var c in columnas)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = c.Titulo,
                Binding = new Binding(c.Propiedad) { StringFormat = c.Formato, ConverterCulture = Formato.Mx },
                Width = c.Ancho > 0 ? new DataGridLength(c.Ancho) : new DataGridLength(1, DataGridLengthUnitType.Star)
            });
        }
        _grid.SelectionChanged += async (_, _) => await MostrarVistaAsync();
        _grid.MouseDoubleClick += async (_, _) => await EjecutarAsync(_acciones.FirstOrDefault());
        _grid.LoadingRow += (_, e) =>
        {
            if (ColorRenglon?.Invoke(e.Row.Item) is { } color)
                e.Row.Foreground = color;
            else
                e.Row.ClearValue(ForegroundProperty);
        };
        _vacio = new TextBlock
        {
            FontSize = 20, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(20),
            Foreground = (Brush)FindResource("TextoSuave"), HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false
        };
        var lista = new Grid();
        lista.Children.Add(_grid);
        lista.Children.Add(_vacio);

        // ---- Vista previa (como sale en el papel)
        _vista = new TextBlock
        {
            Name = "txtVista", FontFamily = new FontFamily("Consolas"), FontSize = 15, Foreground = Brushes.Black,
            Text = "Toque un renglón de la lista para verlo aquí."
        };
        var papel = new Border
        {
            Background = Brushes.White, BorderBrush = (Brush)FindResource("Borde"), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6), Padding = new Thickness(16, 12, 16, 12), Width = 450,
            Child = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _vista }
        };

        var centro = new Grid { Margin = new Thickness(16, 10, 16, 0) };
        centro.ColumnDefinitions.Add(new ColumnDefinition());
        centro.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        centro.Children.Add(lista);
        Grid.SetColumn(papel, 1);
        centro.Children.Add(papel);

        // ---- Abajo: resumen a la izquierda, botones a la derecha
        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        foreach (var a in acciones)
        {
            var b = new Button { Content = a.Texto };
            if (a.Estilo is not null)
                b.Style = (Style)FindResource(a.Estilo);
            b.Click += async (_, _) => await EjecutarAsync(a);
            botones.Children.Add(b);
        }
        var cerrar = new Button { Content = "Cerrar  (Esc)" };
        cerrar.Click += (_, _) => Close();
        botones.Children.Add(cerrar);

        _resumen = new TextBlock { Name = "lblResumen", FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        _aviso = new TextBlock { Name = "lblAviso", FontSize = 15, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)FindResource("Exito") };
        var textos = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(5, 0, 12, 0) };
        textos.Children.Add(_resumen);
        textos.Children.Add(_aviso);

        var pie = new DockPanel { Margin = new Thickness(11, 8, 11, 12) };
        DockPanel.SetDock(botones, Dock.Right);
        pie.Children.Add(botones);
        pie.Children.Add(textos);

        var dock = new DockPanel();
        foreach (var arriba in new UIElement[] { encabezado, filtros, buscador })
        {
            DockPanel.SetDock(arriba, Dock.Top);
            dock.Children.Add(arriba);
        }
        DockPanel.SetDock(pie, Dock.Bottom);
        dock.Children.Add(pie);
        dock.Children.Add(centro);
        Content = dock;

        Loaded += async (_, _) => await IniciarAsync();
        PreviewKeyDown += async (_, e) => await TeclaAsync(e);
    }

    // ------------------------------------------------------------------ Opciones (se ponen al crear la ventana)

    /// <summary>false = sin filtro de días (por ejemplo, los tickets de un solo corte).</summary>
    public bool ConFechas { get; init; } = true;
    /// <summary>Días hacia atrás que se ven al abrir (0 = solo hoy).</summary>
    public int DiasAlAbrir { get; init; }
    /// <summary>Muestra la lista de cajas para elegir (solo el encargado).</summary>
    public bool ElegirCaja { get; init; }
    /// <summary>Caja elegida al abrir (null = todas).</summary>
    public int? CajaInicial { get; init; }
    public string PistaBusqueda { get; init; } = "🔍  Buscar";
    /// <summary>Filtra la lista con lo que se escribe en el buscador (renglón, texto).</summary>
    public Func<object, string, bool>? FiltroBusqueda { get; init; }
    /// <summary>Texto de abajo con los totales de lo que se está viendo.</summary>
    public Func<IReadOnlyList<object>, string>? Resumen { get; init; }
    /// <summary>Renglón que se elige al abrir (si no, el primero).</summary>
    public Func<object, bool>? SeleccionInicial { get; init; }
    /// <summary>Color de letra de un renglón (por ejemplo, rojo si está cancelado); null = normal.</summary>
    public Func<object, Brush?>? ColorRenglon { get; init; }
    public string TextoVacio { get; init; } = "No hay nada en esos días.";

    /// <summary>Mensaje chico abajo (por ejemplo "Se imprimió el ticket…").</summary>
    public void Avisar(string texto, bool error = false)
    {
        _aviso.Text = texto;
        _aviso.Foreground = (Brush)FindResource(error ? "Peligro" : "Exito");
    }

    // ------------------------------------------------------------------ Carga

    private async Task IniciarAsync()
    {
        _pista.Text = PistaBusqueda;
        _sinEventos = true;
        _filtros.Visibility = ConFechas ? Visibility.Visible : Visibility.Collapsed;
        _desde.SelectedDate = DateTime.Today.AddDays(-DiasAlAbrir);
        if (ElegirCaja)
        {
            try
            {
                var cajas = await Sesion.Api.GetAsync<List<CajaInfo>>("api/cajas") ?? [];
                var opciones = new List<CajaOpcion> { new(null, "Todas las cajas") };
                opciones.AddRange(cajas.Select(c => new CajaOpcion(c.Id, c.Nombre)));
                _cajas.ItemsSource = opciones;
                _cajas.SelectedItem = opciones.FirstOrDefault(o => o.Id == CajaInicial) ?? opciones[0];
                _panelCaja.Visibility = Visibility.Visible;
            }
            catch (ApiException ex)
            {
                Mensajes.Error(this, ex.Message);
            }
        }
        _sinEventos = false;
        _busqueda.Focus();
        await RecargarAsync();
    }

    private Button BotonDias(string texto, DateTime desde, DateTime hasta)
    {
        var b = new Button { Content = texto };
        b.Click += async (_, _) =>
        {
            _sinEventos = true;
            _desde.SelectedDate = desde;
            _hasta.SelectedDate = hasta;
            _sinEventos = false;
            await RecargarAsync();
        };
        return b;
    }

    /// <summary>Calendario con la fecha completa a la vista (el estilo viene de <see cref="Calendario"/>).</summary>
    private static DatePicker NuevoCalendario()
    {
        var dp = new DatePicker
        {
            SelectedDate = DateTime.Today, SelectedDateFormat = DatePickerFormat.Short,
            Width = 190, VerticalAlignment = VerticalAlignment.Center
        };
        return dp;
    }

    private static TextBlock Etiqueta(string texto) =>
        new() { Text = texto, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 6, 0) };

    private async Task AlCambiarFiltroAsync()
    {
        if (!_sinEventos && IsLoaded)
            await RecargarAsync();
    }

    /// <summary>Vuelve a pedir la lista con los filtros actuales.</summary>
    public async Task RecargarAsync()
    {
        var desde = _desde.SelectedDate ?? DateTime.Today;
        var hasta = _hasta.SelectedDate ?? DateTime.Today;
        if (ConFechas && hasta < desde)
        {
            _todos = [];
            Filtrar("El segundo día tiene que ser igual o después del primero.");
            return;
        }

        var filtro = new FiltroHistorial(desde, hasta, (_cajas.SelectedItem as CajaOpcion)?.Id ?? (ElegirCaja ? null : CajaInicial));
        _vacio.Text = "Cargando…";
        _vacio.Visibility = Visibility.Visible;
        try
        {
            _todos = (await _cargar(filtro)).Cast<object>().ToList();
        }
        catch (ApiException ex)
        {
            _todos = [];
            Filtrar("⚠ " + ex.Message);
            return;
        }
        _vistas.Clear();
        Filtrar();
    }

    private void Filtrar(string? mensajeVacio = null)
    {
        var texto = _busqueda.Text.Trim();
        var datos = FiltroBusqueda is null || texto.Length == 0 ? _todos : _todos.Where(x => FiltroBusqueda(x, texto)).ToList();
        _grid.ItemsSource = datos;

        if (datos.Count > 0)
        {
            var inicial = _primeraCarga && SeleccionInicial is not null ? datos.FirstOrDefault(SeleccionInicial) : null;
            _grid.SelectedItem = inicial ?? datos[0];
            _grid.ScrollIntoView(_grid.SelectedItem);
        }
        else
        {
            _vista.Text = "";
        }
        _primeraCarga = false;

        _vacio.Text = mensajeVacio ?? (_todos.Count == 0 ? TextoVacio : "No se encontró nada con esa búsqueda.");
        _vacio.Visibility = datos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        _resumen.Text = Resumen?.Invoke(datos) ?? "";
    }

    private async Task MostrarVistaAsync()
    {
        var elegido = _grid.SelectedItem;
        var version = ++_version;
        if (elegido is null)
            return;
        if (!_vistas.TryGetValue(elegido, out var texto))
        {
            _vista.Text = "Cargando…";
            try
            {
                texto = await _vistaPrevia(elegido);
                _vistas[elegido] = texto;
            }
            catch (ApiException ex)
            {
                texto = "⚠ " + ex.Message;
            }
        }
        // Si mientras cargaba se eligió otro renglón, no se encima
        if (version == _version)
            _vista.Text = texto;
    }

    // ------------------------------------------------------------------ Botones y teclas

    private async Task EjecutarAsync(AccionHistorial? accion)
    {
        if (accion is null)
            return;
        if (_grid.SelectedItem is not { } elegido)
        {
            Mensajes.Info(this, "Primero toque un renglón de la lista.");
            return;
        }
        try
        {
            if (await accion.Ejecutar(elegido))
                await RecargarAsync();
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
        }
    }

    private async Task TeclaAsync(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                Close();
                break;
            // Flechas desde el buscador: moverse en la lista sin soltar el buscador
            case Key.Down or Key.Up when _busqueda.IsKeyboardFocused && _grid.Items.Count > 0:
                e.Handled = true;
                _grid.SelectedIndex = Math.Clamp(_grid.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _grid.Items.Count - 1);
                _grid.ScrollIntoView(_grid.SelectedItem);
                break;
            // Enter en el buscador o en la lista = primer botón (en los calendarios, Enter es del calendario)
            case Key.Enter when _busqueda.IsKeyboardFocused || _grid.IsKeyboardFocusWithin:
                e.Handled = true;
                await EjecutarAsync(_acciones.FirstOrDefault());
                break;
        }
    }
}
