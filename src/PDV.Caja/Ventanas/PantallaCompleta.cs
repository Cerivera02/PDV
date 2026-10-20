using System.Windows;
using System.Windows.Media;

namespace PDV.Caja.Ventanas;

/// <summary>
/// Pone una ventana en pantalla completa (sin barra de título, tapando la barra de tareas) y ajusta
/// todo su contenido al tamaño del monitor: la pantalla se diseñó para 1920×1080; en monitores más
/// chicos todo se reduce para que quepa y en más grandes crece. Se reajusta sola si cambia la resolución.
/// </summary>
public static class PantallaCompleta
{
    /// <summary>Tamaño para el que se diseñaron las pantallas (en unidades de WPF).</summary>
    public static readonly Size Diseno = new(1920, 1080);

    // Límites para que no quede ni diminuto ni gigante
    private const double EscalaMinima = 0.6;
    private const double EscalaMaxima = 1.6;

    /// <param name="completa">true = sin bordes y tapando la barra de tareas; false = ventana normal maximizada (para soporte o desarrollo).</param>
    public static void Aplicar(Window ventana, bool completa)
    {
        if (completa)
        {
            ventana.WindowStyle = WindowStyle.None;
            ventana.ResizeMode = ResizeMode.NoResize;
        }
        ventana.WindowState = WindowState.Maximized;

        // El tamaño real se conoce al mostrarse; también cambia si se cambia la resolución o el monitor.
        ventana.SizeChanged += (_, _) => Escalar(ventana);
        ventana.Loaded += (_, _) => Escalar(ventana);
    }

    /// <summary>Escala que le toca a un área de ese tamaño (la misma en ancho y alto, para no deformar).</summary>
    public static double CalcularEscala(double ancho, double alto) =>
        Math.Clamp(Math.Min(ancho / Diseno.Width, alto / Diseno.Height), EscalaMinima, EscalaMaxima);

    private static void Escalar(Window ventana)
    {
        if (ventana.Content is not FrameworkElement contenido || ventana.ActualWidth <= 0)
            return;
        // Área útil: sin bordes es toda la ventana; con bordes se descuenta la barra de título y el marco.
        var conBordes = ventana.WindowStyle != WindowStyle.None;
        var ancho = ventana.ActualWidth - (conBordes ? 16 : 0);
        var alto = ventana.ActualHeight - (conBordes ? SystemParameters.WindowCaptionHeight + 16 : 0);
        var escala = CalcularEscala(ancho, alto);
        if (contenido.LayoutTransform is ScaleTransform actual && Math.Abs(actual.ScaleX - escala) < 0.001)
            return;
        contenido.LayoutTransform = new ScaleTransform(escala, escala);
    }
}
