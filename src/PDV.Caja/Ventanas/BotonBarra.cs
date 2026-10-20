using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PDV.Caja.Ventanas;

/// <summary>
/// Botón de la barra de herramientas: ícono grande arriba, nombre y tecla abajo.
/// Uso: <c>&lt;v:BotonBarra Icono="🔍" Texto="Buscar" Tecla="F3" Click="…"/&gt;</c>.
/// No se queda con el foco, para que el lector de códigos siga escribiendo en la caja de código.
/// </summary>
public sealed class BotonBarra : Button
{
    public string Icono { get; set; } = "";
    public string Texto { get; set; } = "";
    /// <summary>Tecla de atajo (se muestra debajo y se usa en Tag para ejecutar la misma acción).</summary>
    public string? Tecla { get; set; }
    /// <summary>Rojo para acciones que borran.</summary>
    public bool Peligro { get; set; }

    public BotonBarra()
    {
        Focusable = false;
        Initialized += (_, _) => Armar();
    }

    private TextBlock? _lblTecla;

    /// <summary>Cambia la tecla que se ve debajo del nombre (por ejemplo, las flechas que configura el encargado).</summary>
    public void MostrarTecla(string tecla)
    {
        if (_lblTecla is not null)
            _lblTecla.Text = tecla;
        ToolTip = $"{Texto}  ({tecla})";
    }

    private void Armar()
    {
        var colorTexto = Peligro ? (Brush)FindResource("Peligro") : (Brush)FindResource("Primario");
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        panel.Children.Add(new TextBlock { Text = Icono, FontSize = 26, HorizontalAlignment = HorizontalAlignment.Center, Foreground = colorTexto });
        panel.Children.Add(new TextBlock { Text = Texto, FontSize = 14, FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextAlignment = TextAlignment.Center, Foreground = colorTexto, Margin = new Thickness(0, 2, 0, 0) });
        // Renglón de la tecla (vacío si no tiene) para que todos los botones queden alineados
        _lblTecla = new TextBlock
        {
            Text = string.IsNullOrEmpty(Tecla) ? " " : Tecla == "Escape" ? "Esc" : Tecla,
            FontSize = 12,
            HorizontalAlignment = HorizontalAlignment.Center,
            Foreground = (Brush)FindResource("TextoSuave")
        };
        panel.Children.Add(_lblTecla);
        Content = panel;

        Tag ??= Tecla;
        ToolTip ??= string.IsNullOrEmpty(Tecla) ? Texto : $"{Texto}  ({(Tecla == "Escape" ? "Esc" : Tecla)})";
        AutomationProperties.SetName(this, Texto); // lectores de pantalla y pruebas automáticas
    }
}
