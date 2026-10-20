using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>
/// "¿En qué cajas puede trabajar…?": en todas, o solo en las que se marquen.
/// Devuelve [] para "todas" o los números de caja elegidos; null si se canceló.
/// </summary>
public sealed class ElegirCajasWindow : Window
{
    private readonly RadioButton _todas = new() { Content = "En todas las cajas", GroupName = "cajas" };
    private readonly RadioButton _algunas = new() { Content = "Solo en estas:", GroupName = "cajas" };
    private readonly List<(CheckBox Casilla, int Id)> _casillas = [];
    private readonly TextBlock _error;
    private int[]? _resultado;

    private ElegirCajasWindow(string persona, IReadOnlyList<CajaInfo> cajas, int[] actuales)
    {
        Title = "Elegir cajas";
        Width = 500;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 18;
        Background = (Brush)Application.Current.FindResource("Fondo");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = $"¿En qué cajas puede trabajar {persona}?", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        panel.Children.Add(_todas);
        panel.Children.Add(_algunas);

        var lista = new StackPanel { Margin = new Thickness(34, 4, 0, 0) };
        foreach (var c in cajas)
        {
            var texto = c.Activa ? c.Nombre : $"{c.Nombre}  (bloqueada)";
            var cb = new CheckBox { Content = texto, IsChecked = actuales.Contains(c.Id), Margin = new Thickness(0, 6, 0, 6), VerticalContentAlignment = VerticalAlignment.Center };
            _casillas.Add((cb, c.Id));
            lista.Children.Add(cb);
        }
        panel.Children.Add(lista);

        _error = new TextBlock { Foreground = (Brush)Application.Current.FindResource("Peligro"), Margin = new Thickness(0, 10, 0, 0), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        panel.Children.Add(_error);

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var guardar = new Button { Content = "Guardar", IsDefault = true, Style = (Style)Application.Current.FindResource("BotonVerde") };
        guardar.Click += (_, _) => Guardar();
        botones.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        botones.Children.Add(guardar);
        panel.Children.Add(botones);
        Content = panel;

        // Las casillas solo se pueden marcar si eligió "Solo en estas"
        _todas.Checked += (_, _) => lista.IsEnabled = false;
        _algunas.Checked += (_, _) => lista.IsEnabled = true;
        (actuales.Length == 0 ? _todas : _algunas).IsChecked = true;
        lista.IsEnabled = actuales.Length > 0;
    }

    public static int[]? Elegir(Window owner, string persona, IReadOnlyList<CajaInfo> cajas, int[] actuales)
    {
        var w = new ElegirCajasWindow(persona, cajas, actuales) { Owner = owner };
        return w.ShowDialog() == true ? w._resultado : null;
    }

    private void Guardar()
    {
        if (_todas.IsChecked == true)
        {
            _resultado = [];
        }
        else
        {
            _resultado = _casillas.Where(x => x.Casilla.IsChecked == true).Select(x => x.Id).ToArray();
            if (_resultado.Length == 0)
            {
                _error.Text = "Marque al menos una caja, o elija \"En todas las cajas\".";
                _error.Visibility = Visibility.Visible;
                return;
            }
        }
        DialogResult = true;
    }
}
