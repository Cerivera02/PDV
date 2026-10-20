using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>
/// "Permisos especiales de…": casillas con lo que la persona puede hacer sin pedir la clave del encargado.
/// Devuelve los permisos marcados ([] = ninguno) o null si se canceló.
/// </summary>
public sealed class ElegirPermisosWindow : Window
{
    private readonly List<(CheckBox Casilla, string Permiso)> _casillas = [];
    private string[]? _resultado;

    private ElegirPermisosWindow(string persona, string[] actuales, string? explicacion)
    {
        Title = "Permisos especiales";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontSize = 18;
        Background = (Brush)Application.Current.FindResource("Fondo");

        var panel = new StackPanel { Margin = new Thickness(24, 20, 24, 20) };
        panel.Children.Add(new TextBlock { Text = $"Permisos especiales de {persona}", FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock
        {
            Text = explicacion ?? "Marque lo que puede hacer sin pedir la clave del encargado. Lo que no marque, lo puede hacer solo si el encargado teclea su clave de autorización.",
            TextWrapping = TextWrapping.Wrap, FontSize = 15, Margin = new Thickness(0, 6, 0, 14),
            Foreground = (Brush)Application.Current.FindResource("TextoSuave")
        });

        foreach (var p in Permisos.Todos)
        {
            var cb = new CheckBox
            {
                Content = Permisos.Descripcion(p), IsChecked = actuales.Contains(p),
                Margin = new Thickness(0, 6, 0, 6), VerticalContentAlignment = VerticalAlignment.Center
            };
            System.Windows.Automation.AutomationProperties.SetName(cb, Permisos.Descripcion(p));
            _casillas.Add((cb, p));
            panel.Children.Add(cb);
        }

        var botones = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 20, 0, 0) };
        var guardar = new Button { Content = "Guardar", IsDefault = true, Style = (Style)Application.Current.FindResource("BotonVerde") };
        guardar.Click += (_, _) =>
        {
            _resultado = _casillas.Where(x => x.Casilla.IsChecked == true).Select(x => x.Permiso).ToArray();
            DialogResult = true;
        };
        botones.Children.Add(new Button { Content = "Cancelar", IsCancel = true });
        botones.Children.Add(guardar);
        panel.Children.Add(botones);
        Content = panel;
    }

    public static string[]? Elegir(Window owner, string persona, string[] actuales, string? explicacion = null)
    {
        var w = new ElegirPermisosWindow(persona, actuales, explicacion) { Owner = owner };
        return w.ShowDialog() == true ? w._resultado : null;
    }
}
