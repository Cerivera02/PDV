using System.Collections;
using System.Media;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PDV.Caja.Servicios;

namespace PDV.Caja.Ventanas;

/// <summary>Definición de un campo para <see cref="CampoDialog"/>.</summary>
/// <param name="Ayuda">Texto chico debajo del campo que explica qué escribir.</param>
/// <param name="Opciones">Si se indica, el campo es una lista para elegir.</param>
/// <param name="Editable">Lista que además permite escribir un valor libre.</param>
/// <param name="Numero">Solo acepta números; no deja continuar hasta que el número sea válido.</param>
/// <param name="Opcional">Con <paramref name="Numero"/>: se puede dejar vacío.</param>
/// <param name="Minimo">Con <paramref name="Numero"/>: 0 = acepta cero; 0.001 = tiene que ser mayor a cero.</param>
/// <param name="Fecha">Calendario para elegir el día; el valor se devuelve como "yyyy-MM-dd" (vacío si no se eligió).</param>
public sealed record Campo(string Etiqueta, string Valor = "", bool Password = false,
    IReadOnlyList<string>? Opciones = null, bool Editable = false, string? Ayuda = null,
    bool Numero = false, bool Opcional = false, decimal Minimo = 0, bool Fecha = false);

/// <summary>Formulario sencillo: etiqueta arriba, campo grande, ayuda abajo. Enter = aceptar, Esc = cancelar.</summary>
public sealed class CampoDialog : Window
{
    private readonly List<Func<string>> _lectores = [];
    // Revisiones de los campos numéricos: devuelven false (y muestran el aviso) si el número no sirve.
    private readonly List<Func<bool>> _validaciones = [];

    private CampoDialog(string titulo, string? explicacion, string textoAceptar, Campo[] campos)
    {
        Title = titulo;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 17;
        Background = (Brush)Application.Current.FindResource("Fondo");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = titulo, FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 8), TextWrapping = TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(explicacion))
            panel.Children.Add(new TextBlock { Text = explicacion, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8), Foreground = (Brush)Application.Current.FindResource("TextoSuave") });

        Control? primero = null;
        foreach (var c in campos)
        {
            panel.Children.Add(new TextBlock { Text = c.Etiqueta, Margin = new Thickness(0, 12, 0, 4), FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });

            Control control;
            if (c.Password)
            {
                var pb = new PasswordBox { Password = c.Valor };
                _lectores.Add(() => pb.Password);
                control = pb;
            }
            else if (c.Fecha)
            {
                var dp = new DatePicker
                {
                    SelectedDate = DateTime.TryParse(c.Valor, out var f) ? f : null,
                    FontSize = 18,
                    Padding = new Thickness(6),
                    SelectedDateFormat = DatePickerFormat.Long
                };
                _lectores.Add(() => dp.SelectedDate?.ToString("yyyy-MM-dd") ?? "");
                control = dp;
            }
            else if (c.Opciones is not null)
            {
                var cb = new ComboBox { ItemsSource = c.Opciones, IsEditable = c.Editable, Text = c.Valor };
                if (!c.Editable && c.Opciones.Count > 6)
                    Select.SetBuscable(cb, true);
                cb.SelectedItem = c.Opciones.FirstOrDefault(o => o == c.Valor);
                _lectores.Add(() => cb.Text);
                control = cb;
            }
            else
            {
                var tb = new TextBox { Text = c.Valor };
                tb.GotKeyboardFocus += (_, _) => tb.SelectAll();
                _lectores.Add(() => tb.Text);
                control = tb;
            }
            panel.Children.Add(control);
            primero ??= control;

            if (!string.IsNullOrWhiteSpace(c.Ayuda))
                panel.Children.Add(new TextBlock { Text = c.Ayuda, FontSize = 14, Margin = new Thickness(2, 4, 0, 0), TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("TextoSuave") });

            if (c.Numero && control is TextBox numero)
                AgregarValidacionNumerica(panel, numero, c);
            else if (c.Fecha && !c.Opcional && control is DatePicker fecha)
                AgregarAviso(panel, fecha, () => fecha.SelectedDate is null ? "Elija el día en el calendario." : null);
        }

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var aceptar = new Button { Content = textoAceptar, IsDefault = true, Style = (Style)Application.Current.FindResource("BotonVerde") };
        // No deja pasar hasta que todos los números estén bien (el primer campo con error recibe el foco).
        aceptar.Click += (_, _) =>
        {
            if (_validaciones.All(v => v()))
                DialogResult = true;
        };
        botones.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        botones.Children.Add(aceptar);
        panel.Children.Add(botones);

        Content = panel;
        Loaded += (_, _) => primero?.Focus();
    }

    /// <summary>Solo números al escribir; al aceptar, aviso rojo debajo del campo si está vacío o es menor al mínimo.</summary>
    private void AgregarValidacionNumerica(Panel panel, TextBox tb, Campo c)
    {
        EntradaNumerica.SetSoloNumeros(tb, true);
        AgregarAviso(panel, tb, () =>
        {
            var texto = tb.Text.Trim();
            if (texto.Length == 0)
                return c.Opcional ? null : "Falta este dato. Escríbalo con números, por ejemplo: 500";
            if (!Formato.TryDecimal(texto, out var valor))
                return "Ese número no se entiende. Escriba solo números, por ejemplo: 500 o 150.50";
            if (valor < c.Minimo)
                return c.Minimo > 0 ? "Debe ser mayor a 0." : "No puede ser negativo.";
            return null;
        });
    }

    /// <summary>Aviso rojo debajo de un campo; <paramref name="revisar"/> devuelve el error o null si está bien.</summary>
    private void AgregarAviso(Panel panel, Control campo, Func<string?> revisar)
    {
        var aviso = new TextBlock
        {
            FontSize = 15,
            Margin = new Thickness(2, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.FindResource("Peligro"),
            Visibility = Visibility.Collapsed
        };
        panel.Children.Add(aviso);
        if (campo is TextBox tb)
            tb.TextChanged += (_, _) => aviso.Visibility = Visibility.Collapsed;
        if (campo is DatePicker dp)
            dp.SelectedDateChanged += (_, _) => aviso.Visibility = Visibility.Collapsed;

        _validaciones.Add(() =>
        {
            if (revisar() is not { } error)
                return true;
            aviso.Text = "⚠ " + error;
            aviso.Visibility = Visibility.Visible;
            System.Media.SystemSounds.Hand.Play();
            campo.Focus();
            return false;
        });
    }

    /// <summary>Muestra el formulario; devuelve los valores en el mismo orden, o null si se canceló.</summary>
    public static string[]? Mostrar(Window? owner, string titulo, params Campo[] campos) =>
        MostrarCon(owner, titulo, null, "Aceptar", campos);

    public static string[]? MostrarCon(Window? owner, string titulo, string? explicacion, string textoAceptar, params Campo[] campos)
    {
        var dlg = new CampoDialog(titulo, explicacion, textoAceptar, campos) { Owner = Mensajes.Dueno(owner) };
        return dlg.ShowDialog() == true ? dlg._lectores.Select(l => l().Trim()).ToArray() : null;
    }

    /// <summary>Pide un número. Solo se pueden escribir números y no deja continuar hasta que sea válido.</summary>
    /// <returns>El número, o null si presionó Cancelar.</returns>
    public static decimal? PedirDecimal(Window? owner, string titulo, string etiqueta, string valor = "", string? ayuda = null, decimal minimo = 0)
    {
        var r = Mostrar(owner, titulo, new Campo(etiqueta, valor, Ayuda: ayuda, Numero: true, Minimo: minimo));
        return r is not null && Formato.TryDecimal(r[0], out var d) ? d : null;
    }
}

/// <summary>Columna de <see cref="TablaWindow"/>. Ancho 0 = ocupa el espacio que sobre.</summary>
public sealed record Columna(string Titulo, string Propiedad, string? Formato = null, double Ancho = 0);

/// <summary>Botón de <see cref="TablaWindow"/>. La acción devuelve true cuando hay que recargar la lista.</summary>
public sealed record AccionTabla(string Texto, Func<object?, Task<bool>> Ejecutar, bool RequiereSeleccion = true, string? Estilo = null);

/// <summary>Lista sencilla con botones grandes arriba (personas, departamentos, historial…).</summary>
public sealed class TablaWindow : Window
{
    private readonly DataGrid _grid;
    private readonly TextBlock _vacio;
    private readonly Func<Task<IEnumerable>> _cargar;

    public TablaWindow(string titulo, string? explicacion, Columna[] columnas, Func<Task<IEnumerable>> cargar, params AccionTabla[] acciones)
    {
        Title = titulo;
        Width = 1120;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FontSize = 17;
        Background = (Brush)Application.Current.FindResource("Fondo");
        _cargar = cargar;

        _grid = new DataGrid { Margin = new Thickness(16, 8, 16, 16) };
        foreach (var c in columnas)
        {
            _grid.Columns.Add(new DataGridTextColumn
            {
                Header = c.Titulo,
                Binding = new Binding(c.Propiedad) { StringFormat = c.Formato },
                Width = c.Ancho > 0 ? new DataGridLength(c.Ancho) : new DataGridLength(1, DataGridLengthUnitType.Star)
            });
        }

        var encabezado = new StackPanel { Margin = new Thickness(16, 16, 16, 0) };
        encabezado.Children.Add(new TextBlock { Text = titulo, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.FindResource("Primario") });
        if (!string.IsNullOrWhiteSpace(explicacion))
            encabezado.Children.Add(new TextBlock { Text = explicacion, TextWrapping = TextWrapping.Wrap, Foreground = (Brush)Application.Current.FindResource("TextoSuave"), Margin = new Thickness(0, 4, 0, 0) });

        var barra = new WrapPanel { Margin = new Thickness(11, 8, 11, 0) };
        foreach (var a in acciones)
        {
            var b = new Button { Content = a.Texto };
            if (a.Estilo is not null)
                b.Style = (Style)Application.Current.FindResource(a.Estilo);
            b.Click += async (_, _) =>
            {
                if (a.RequiereSeleccion && _grid.SelectedItem is null)
                {
                    Mensajes.Info(this, "Primero toque un renglón de la lista.");
                    return;
                }
                try
                {
                    if (await a.Ejecutar(_grid.SelectedItem))
                        await RecargarAsync();
                }
                catch (ApiException ex)
                {
                    Mensajes.Error(this, ex.Message);
                }
            };
            barra.Children.Add(b);
        }
        var cerrar = new Button { Content = "Cerrar" };
        cerrar.Click += (_, _) => Close();
        barra.Children.Add(cerrar);

        _vacio = new TextBlock
        {
            Text = "Todavía no hay nada aquí.",
            FontSize = 20,
            Foreground = (Brush)Application.Current.FindResource("TextoSuave"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false
        };

        // Buscador (solo se ve si se indica FiltroBusqueda)
        _busqueda = new TextBox { FontSize = 20, Margin = new Thickness(16, 10, 16, 0), Visibility = Visibility.Collapsed };
        _busqueda.TextChanged += (_, _) => Filtrar();
        _busqueda.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up && _grid.Items.Count > 0)
            {
                _grid.SelectedIndex = Math.Clamp(_grid.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _grid.Items.Count - 1);
                _grid.ScrollIntoView(_grid.SelectedItem);
                e.Handled = true;
            }
        };

        var dock = new DockPanel();
        DockPanel.SetDock(encabezado, Dock.Top);
        DockPanel.SetDock(_busqueda, Dock.Top);
        DockPanel.SetDock(barra, Dock.Top);
        dock.Children.Add(encabezado);
        dock.Children.Add(_busqueda);
        dock.Children.Add(barra);
        var centro = new Grid();
        centro.Children.Add(_grid);
        centro.Children.Add(_vacio);
        dock.Children.Add(centro);
        Content = dock;

        Loaded += async (_, _) =>
        {
            if (FiltroBusqueda is not null)
            {
                _busqueda.Visibility = Visibility.Visible;
                _busqueda.Focus();
            }
            try { await RecargarAsync(); }
            catch (ApiException ex) { Mensajes.Error(this, ex.Message); }
        };
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private readonly TextBox _busqueda;
    private List<object> _todos = [];

    /// <summary>Si se indica, aparece un buscador arriba que filtra la lista con esta función (renglón, texto).</summary>
    public Func<object, string, bool>? FiltroBusqueda { get; init; }

    private async Task RecargarAsync()
    {
        _todos = (await _cargar()).Cast<object>().ToList();
        Filtrar();
    }

    private void Filtrar()
    {
        var texto = _busqueda.Text.Trim();
        var datos = FiltroBusqueda is null || texto.Length == 0 ? _todos : _todos.Where(x => FiltroBusqueda(x, texto)).ToList();
        _grid.ItemsSource = datos;
        _vacio.Text = _todos.Count == 0 ? "Todavía no hay nada aquí." : "No se encontró nada con esa búsqueda.";
        _vacio.Visibility = datos.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Compara sin distinguir mayúsculas ni acentos ("jose" encuentra "José").</summary>
    public static bool Contiene(string? texto, string buscado) =>
        texto is not null && Formato.Mx.CompareInfo.IndexOf(texto, buscado,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
}

/// <summary>Mensajes grandes y claros (en lugar de las ventanitas de Windows).</summary>
public static class Mensajes
{
    /// <param name="forzar">Mostrar aunque la sesión se esté cortando (solo lo usa App).</param>
    public static void Error(Window? owner, string mensaje, bool forzar = false)
    {
        // Si el servidor cortó el acceso, App ya muestra el único mensaje necesario.
        if (Sesion.SesionCortada && !forzar)
            return;
        SystemSounds.Hand.Play();
        Mostrar(owner, "⚠", "Peligro", mensaje, "Entendido", null);
    }

    public static void Info(Window? owner, string mensaje) => Mostrar(owner, "ℹ", "Primario", mensaje, "Aceptar", null);

    public static void Exito(Window? owner, string mensaje) => Mostrar(owner, "✔", "Exito", mensaje, "Aceptar", null);

    /// <param name="si">Texto del botón que confirma, por ejemplo "Sí, quitar".</param>
    public static bool Confirmar(Window? owner, string mensaje, string si = "Sí", string no = "No", string icono = "?") =>
        Mostrar(owner, icono, "Primario", mensaje, si, no);

    /// <summary>Solo se puede usar como dueña una ventana ya visible.</summary>
    internal static Window? Dueno(Window? owner) => owner is { IsLoaded: true, IsVisible: true } ? owner : null;

    private static bool Mostrar(Window? owner, string icono, string color, string mensaje, string si, string? no)
    {
        var dueno = Dueno(owner);
        var w = new Window
        {
            Title = "PDV",
            Width = 540,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = dueno is null ? WindowStartupLocation.CenterScreen : WindowStartupLocation.CenterOwner,
            Owner = dueno,
            ShowInTaskbar = dueno is null,
            FontSize = 19,
            Background = Brushes.White
        };

        var grid = new Grid { Margin = new Thickness(24) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var ico = new TextBlock { Text = icono, FontSize = 44, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 0, 20, 0), VerticalAlignment = VerticalAlignment.Top, Foreground = (Brush)Application.Current.FindResource(color) };
        var texto = new TextBlock { Text = mensaje, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(texto, 1);
        grid.Children.Add(ico);
        grid.Children.Add(texto);

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        if (no is not null)
            botones.Children.Add(new Button { Content = no, IsCancel = true });
        var ok = new Button { Content = si, IsDefault = true, Style = (Style)Application.Current.FindResource(no is null ? "BotonAzul" : "BotonVerde") };
        ok.Click += (_, _) => w.DialogResult = true;
        if (no is null)
            ok.IsCancel = true; // Esc también cierra un aviso
        botones.Children.Add(ok);
        Grid.SetRow(botones, 1);
        Grid.SetColumnSpan(botones, 2);
        grid.Children.Add(botones);

        w.Content = grid;
        w.Loaded += (_, _) => ok.Focus();
        return w.ShowDialog() == true;
    }
}
