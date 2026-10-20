using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using PDV.Caja.Servicios;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>Buscar un producto por nombre (sin consultar al servidor: usa el catálogo en memoria).</summary>
public sealed class BuscarProductoWindow : Window
{
    private readonly TextBox _texto = new() { FontSize = 24 };
    private readonly DataGrid _grid = new() { Margin = new Thickness(16, 0, 16, 0), FontSize = 19, RowHeight = 46 };
    private readonly TextBlock _vacio = new()
    {
        Text = "No se encontró ningún producto con ese nombre.",
        FontSize = 20,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false
    };
    private Producto? _seleccionado;

    private BuscarProductoWindow(string texto)
    {
        Title = "Buscar producto";
        Width = 1000;
        Height = 660;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 17;
        Background = (Brush)Application.Current.FindResource("Fondo");
        _vacio.Foreground = (Brush)Application.Current.FindResource("TextoSuave");

        _grid.Columns.Add(new DataGridTextColumn { Header = "Producto", Binding = new Binding(nameof(Producto.Nombre)), Width = new DataGridLength(1, DataGridLengthUnitType.Star) });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Precio", Binding = new Binding(nameof(Producto.PrecioTexto)), Width = 190 });
        _grid.Columns.Add(new DataGridTextColumn { Header = "Departamento", Binding = new Binding(nameof(Producto.DepartamentoNombre)), Width = 190 });

        var arriba = new StackPanel { Margin = new Thickness(16, 16, 16, 12) };
        arriba.Children.Add(new TextBlock { Text = "Escriba parte del nombre del producto:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 6) });
        arriba.Children.Add(_texto);

        var abajo = new DockPanel { Margin = new Thickness(11, 10, 11, 12) };
        var elegir = new Button { Content = "✔  Agregar a la venta   (Enter)", Style = (Style)Application.Current.FindResource("BotonVerde") };
        elegir.Click += (_, _) => Elegir();
        var cancelar = new Button { Content = "Regresar   (Esc)" };
        cancelar.Click += (_, _) => Close();
        DockPanel.SetDock(elegir, Dock.Right);
        abajo.Children.Add(elegir);
        abajo.Children.Add(new TextBlock { Text = "Use las flechas ↑ ↓ para elegir, o toque el producto.", VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.FindResource("TextoSuave"), Margin = new Thickness(6, 0, 0, 0) });
        abajo.Children.Add(cancelar);

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

        _texto.Text = texto;
        _texto.TextChanged += (_, _) => Filtrar();
        _texto.PreviewKeyDown += (_, e) =>
        {
            // Flechas desde la caja de texto mueven la selección
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
        Loaded += (_, _) =>
        {
            Filtrar();
            _texto.Focus();
            _texto.CaretIndex = _texto.Text.Length;
        };
    }

    public static Producto? Seleccionar(Window owner, string texto)
    {
        var w = new BuscarProductoWindow(texto) { Owner = owner };
        w.ShowDialog();
        return w._seleccionado;
    }

    private void Filtrar()
    {
        var lista = Sesion.Catalogo.Buscar(_texto.Text);
        _grid.ItemsSource = lista;
        if (lista.Count > 0)
            _grid.SelectedIndex = 0;
        _vacio.Visibility = lista.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Elegir()
    {
        _seleccionado = _grid.SelectedItem as Producto;
        if (_seleccionado is not null)
            Close();
    }
}
