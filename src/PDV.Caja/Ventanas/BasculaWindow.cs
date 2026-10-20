using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PDV.Caja.Servicios;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Caja.Ventanas;

/// <summary>
/// Muestra el peso de la báscula en vivo, en grande. Sirve para "solo pesar" (sin producto)
/// o, si se le pasa un producto por kilo, para usar ese peso en la venta.
/// </summary>
public sealed class BasculaWindow : Window
{
    private readonly MonitorBascula _monitor;
    private readonly Producto? _producto;
    private readonly TextBlock _peso = new() { Text = "—", FontSize = 120, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _estado = new() { FontSize = 22, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 4, 0, 0), TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center };
    private readonly TextBlock _importe = new() { FontSize = 26, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Button? _usar;
    private decimal? _pesoEstable;
    private decimal? _resultado;

    /// <param name="soloOfrecer">true = modo "solo pesar": Enter cierra, y el producto (si hay) se ofrece en un botón aparte
    /// para no cambiarle el peso por accidente.</param>
    private BasculaWindow(MonitorBascula monitor, Producto? producto, bool soloOfrecer)
    {
        _monitor = monitor;
        _producto = producto;
        var modoProducto = producto is not null && !soloOfrecer;
        Title = "Báscula";
        Width = 620;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 18;
        Background = Brushes.White;

        var panel = new StackPanel { Margin = new Thickness(28, 20, 28, 20) };
        panel.Children.Add(new TextBlock
        {
            Text = modoProducto ? $"⚖  Pesar: {producto!.Nombre}" : "⚖  Báscula",
            FontSize = 24, FontWeight = FontWeights.SemiBold, Foreground = (Brush)FindResource("Primario"), TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = modoProducto
                ? $"Precio: {producto!.PrecioTexto}. Cuando el peso esté estable, presione \"Usar este peso\"."
                : "Ponga lo que quiera pesar sobre la báscula.",
            Foreground = (Brush)FindResource("TextoSuave"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 10)
        });

        var pantalla = new Border { Background = (Brush)FindResource("Fondo"), CornerRadius = new CornerRadius(14), Padding = new Thickness(20, 6, 20, 16) };
        var centro = new StackPanel();
        centro.Children.Add(_peso);
        centro.Children.Add(_estado);
        if (modoProducto)
            centro.Children.Add(_importe);
        pantalla.Child = centro;
        panel.Children.Add(pantalla);

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        if (producto is not null)
        {
            // Modo producto: botón verde con Enter. Solo pesar: botón aparte, sin Enter.
            _usar = new Button
            {
                Content = modoProducto ? "✔  Usar este peso   (Enter)" : $"Poner este peso a {producto.Nombre}",
                IsDefault = modoProducto,
                IsEnabled = false,
                Style = (Style)FindResource(modoProducto ? "BotonVerde" : typeof(Button))
            };
            _usar.Click += (_, _) =>
            {
                _resultado = _pesoEstable;
                DialogResult = true;
            };
        }
        var cerrar = new Button { Content = modoProducto ? "Regresar   (Esc)" : "Cerrar   (Enter)", IsCancel = true, IsDefault = !modoProducto };
        if (!modoProducto && _usar is not null)
        {
            botones.Children.Add(_usar);
            botones.Children.Add(cerrar);
        }
        else
        {
            botones.Children.Add(cerrar);
            if (_usar is not null)
                botones.Children.Add(_usar);
        }
        panel.Children.Add(botones);
        Content = panel;

        // Mostrar ya la última lectura (si hay) y luego cada lectura nueva
        if (monitor.Ultima is { } ultima)
            Mostrar(ultima);
        else
            MostrarEstado("Leyendo la báscula…", "TextoSuave");
        monitor.Lectura += AlLeer;
        monitor.Falla += AlFallar;
        Closed += (_, _) =>
        {
            monitor.Lectura -= AlLeer;
            monitor.Falla -= AlFallar;
        };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F2) { Close(); e.Handled = true; } };
    }

    /// <summary>Abre la ventana. Con producto por kilo, devuelve el peso elegido (o null si regresó).</summary>
    /// <param name="soloOfrecer">Modo "solo pesar" (F2): el producto se ofrece en un botón aparte, sin Enter.</param>
    public static decimal? Mostrar(Window owner, MonitorBascula monitor, Producto? producto = null, bool soloOfrecer = false)
    {
        var w = new BasculaWindow(monitor, producto, soloOfrecer) { Owner = owner };
        w.ShowDialog();
        return w._resultado;
    }

    private void AlLeer(LecturaPeso l) => Dispatcher.BeginInvoke(() => Mostrar(l));
    private void AlFallar(string error) => Dispatcher.BeginInvoke(() =>
    {
        _peso.Text = "—";
        _pesoEstable = null;
        if (_usar is not null) _usar.IsEnabled = false;
        MostrarEstado($"⚠ No se pudo leer la báscula. Revise que esté prendida y conectada.", "Peligro");
    });

    private void Mostrar(LecturaPeso l)
    {
        _peso.Text = $"{l.Kilogramos:0.000} kg";
        _pesoEstable = l.Estable && l.Kilogramos > 0 ? l.Kilogramos : null;
        if (_usar is not null)
            _usar.IsEnabled = _pesoEstable is not null;

        if (l.Kilogramos <= 0)
            MostrarEstado("Ponga el producto en la báscula", "TextoSuave");
        else if (!l.Estable)
            MostrarEstado("⏳ Se está moviendo… espere", "Aviso");
        else
            MostrarEstado("✔ Peso estable", "Exito");

        if (_producto is not null)
            _importe.Text = l.Kilogramos > 0 ? $"Importe: {Formato.Dinero(Dinero.Redondear(l.Kilogramos * _producto.PrecioVenta))}" : "";    }

    private void MostrarEstado(string texto, string color)
    {
        _estado.Text = texto;
        _estado.Foreground = (Brush)FindResource(color);
        _peso.Foreground = (Brush)FindResource(color == "Exito" ? "Primario" : color == "Peligro" ? "Peligro" : "TextoSuave");
    }
}
