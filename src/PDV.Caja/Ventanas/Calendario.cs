using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using HandyControl.Tools;

namespace PDV.Caja.Ventanas;

/// <summary>
/// Calendarios (DatePicker) con el estilo de la librería HandyControl, sin que la librería cambie el resto de la app.
/// <para>
/// HandyControl trae en un solo archivo (Theme.xaml) sus pinceles y los estilos de TODOS los controles. Si se agrega
/// completo, cambia botones, listas, tablas… Por eso se carga aparte y a la app solo pasan:
/// los colores (SkinDefault.xaml), los recursos con nombre (pinceles, íconos, estilos con clave, que solo se usan
/// si alguien los pide) y el estilo del DatePicker. Los estilos por tipo (los que se aplican solos) se quedan fuera.
/// </para>
/// </summary>
public static class Calendario
{
    private const string Piel = "pack://application:,,,/HandyControl;component/Themes/SkinDefault.xaml";
    private const string Tema = "pack://application:,,,/HandyControl;component/Themes/Theme.xaml";

    public static void Aplicar(Application app)
    {
        // Idioma del calendario (si no, sale en chino: es el idioma por omisión de la librería)
        ConfigHelper.Instance.SetLang(Servicios.Formato.Mx.Name);

        var tema = new ResourceDictionary { Source = new Uri(Tema) };
        var recursos = new ResourceDictionary();
        recursos.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri(Piel) });
        CopiarConNombre(tema, recursos);
        // Antes que los recursos propios de la app: si una clave se repite, gana la de la app
        app.Resources.MergedDictionaries.Add(recursos);

        // Colores de HandyControl con el azul de la app
        var primario = ((SolidColorBrush)app.Resources["Primario"]).Color;
        var oscuro = Color.FromRgb(0x16, 0x3A, 0x5C);
        var claro = ((SolidColorBrush)app.Resources["PrimarioClaro"]).Color;
        app.Resources["PrimaryColor"] = primario;
        app.Resources["DarkPrimaryColor"] = oscuro;
        app.Resources["LightPrimaryColor"] = claro;
        app.Resources["PrimaryBrush"] = new SolidColorBrush(primario);
        app.Resources["DarkPrimaryBrush"] = new SolidColorBrush(oscuro);
        app.Resources["LightPrimaryBrush"] = new SolidColorBrush(claro);
        // Su borde gris (muy tenue) con el de las cajas de texto y las listas de la app
        app.Resources["BorderBrush"] = app.Resources["Borde"];

        if ((tema["DatePickerExtend"] ?? tema[typeof(DatePicker)]) is Style estilo)
        {
            // Mismo alto, letra y borde que las cajas de texto y las listas de la app
            var propio = new Style(typeof(DatePicker), estilo);
            propio.Setters.Add(new Setter(Control.FontSizeProperty, 17.0));
            propio.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 46.0));
            propio.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.White));
            propio.Setters.Add(new Setter(Control.BorderBrushProperty, app.Resources["Borde"]));
            propio.Setters.Add(new Setter(HandyControl.Controls.BorderElement.CornerRadiusProperty, new CornerRadius(8)));
            app.Resources[typeof(DatePicker)] = propio;
        }
    }

    /// <summary>
    /// Copia los recursos con clave de texto. Los que se aplican solos no se copian:
    /// estilos por tipo (clave = Type) y plantillas por tipo de dato (DataTemplateKey).
    /// </summary>
    private static void CopiarConNombre(ResourceDictionary origen, ResourceDictionary destino)
    {
        foreach (var anidado in origen.MergedDictionaries)
            CopiarConNombre(anidado, destino);
        foreach (DictionaryEntry e in origen)
        {
            if (e.Key is not (Type or TemplateKey))
                destino[e.Key] = e.Value;
        }
    }
}
