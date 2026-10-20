using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PDV.Caja.Servicios;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>Buscar y elegir un cliente (para la venta o para recibir un abono).</summary>
public sealed class ElegirClienteWindow : Window
{
    private readonly TextBox _texto = new() { FontSize = 24 };
    private readonly DataGrid _grid = new() { Margin = new Thickness(16, 0, 16, 0), FontSize = 19, RowHeight = 46 };
    private readonly TextBlock _vacio = new() { FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false, TextAlignment = TextAlignment.Center };
    private readonly bool _soloConDeuda;
    private List<Cliente> _clientes = [];
    private Cliente? _elegido;

    private ElegirClienteWindow(string titulo, bool soloConDeuda)
    {
        Title = titulo;
        Width = 1000;
        Height = 640;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 17;
        Background = (Brush)Application.Current.FindResource("Fondo");
        _vacio.Foreground = (Brush)Application.Current.FindResource("TextoSuave");
        _soloConDeuda = soloConDeuda;

        _grid.Columns.Add(new DataGridTextColumn { Header = "Cliente", Binding = new Binding(nameof(Cliente.Nombre)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Teléfono", Binding = new Binding(nameof(Cliente.Telefono)), Width = 170 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Debe", Binding = new Binding(nameof(Cliente.SaldoTexto)), Width = 130 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Crédito disponible", Binding = new Binding(nameof(Cliente.CreditoTexto)), Width = 210 });

        var arriba = new StackPanel { Margin = new Thickness(16, 16, 16, 12) };
        arriba.Children.Add(new TextBlock { Text = titulo, FontSize = 26, FontWeight = FontWeights.SemiBold, Foreground = (Brush)Application.Current.FindResource("Primario") });
        arriba.Children.Add(new TextBlock { Text = "Escriba el nombre o el teléfono:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 6) });
        arriba.Children.Add(_texto);

        var abajo = new DockPanel { Margin = new Thickness(11, 10, 11, 12) };
        var elegir = new Button { Content = "✔  Elegir   (Enter)", Style = (Style)Application.Current.FindResource("BotonVerde") };
        elegir.Click += (_, _) => Elegir();
        var regresar = new Button { Content = "Regresar   (Esc)" };
        regresar.Click += (_, _) => Close();
        DockPanel.SetDock(elegir, Dock.Right);
        abajo.Children.Add(elegir);
        abajo.Children.Add(regresar);
        abajo.Children.Add(new TextBlock());

        var centro = new Grid();
        centro.Children.Add(_grid);
        centro.Children.Add(_vacio);
        var dock = new DockPanel();
        DockPanel.SetDock(arriba, Dock.Top);
        DockPanel.SetDock(abajo, Dock.Bottom);
        dock.Children.Add(arriba);
        dock.Children.Add(abajo);
        dock.Children.Add(centro);
        Content = dock;

        _texto.TextChanged += (_, _) => Filtrar();
        _texto.PreviewKeyDown += (_, e) =>
        {
            if (e.Key is Key.Down or Key.Up && _grid.Items.Count > 0)
            {
                _grid.SelectedIndex = Math.Clamp(_grid.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _grid.Items.Count - 1);
                _grid.ScrollIntoView(_grid.SelectedItem);
                e.Handled = true;
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { Elegir(); e.Handled = true; }
            else if (e.Key == Key.Escape) Close();
        };
        _grid.MouseDoubleClick += (_, _) => Elegir();
        Loaded += async (_, _) =>
        {
            _texto.Focus();
            try
            {
                _clientes = await Sesion.Api.GetAsync<List<Cliente>>("api/clientes") ?? [];
                Filtrar();
            }
            catch (ApiException ex)
            {
                Mensajes.Error(this, ex.Message);
                Close();
            }
        };
    }

    /// <param name="soloConDeuda">Solo clientes que deben algo (para recibir abonos).</param>
    public static Cliente? Elegir(Window owner, string titulo, bool soloConDeuda = false)
    {
        var w = new ElegirClienteWindow(titulo, soloConDeuda) { Owner = owner };
        w.ShowDialog();
        return w._elegido;
    }

    private void Filtrar()
    {
        var t = _texto.Text.Trim();
        var lista = _clientes
            .Where(c => !_soloConDeuda || c.Saldo > 0)
            .Where(c => t.Length == 0 || TablaWindow.Contiene(c.Nombre, t) || (c.Telefono?.Contains(t) ?? false))
            .ToList();
        _grid.ItemsSource = lista;
        if (lista.Count > 0)
            _grid.SelectedIndex = 0;
        _vacio.Text = _clientes.Count == 0
            ? "Todavía no hay clientes registrados.\nEl encargado los da de alta en Menú → Clientes."
            : _soloConDeuda && !_clientes.Any(c => c.Saldo > 0) ? "Ningún cliente debe nada." : "No se encontró ningún cliente.";
        _vacio.Visibility = lista.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Elegir()
    {
        _elegido = _grid.SelectedItem as Cliente;
        if (_elegido is not null)
            Close();
    }
}
