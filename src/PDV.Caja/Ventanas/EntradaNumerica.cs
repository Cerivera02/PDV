using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PDV.Caja.Ventanas;

/// <summary>
/// Hace que una caja de texto solo acepte números con punto decimal (hasta 3 decimales).
/// Las letras, espacios y comas no se escriben. Al pegar "$1,000.00" queda "1000.00".
/// Uso en XAML: <c>v:EntradaNumerica.SoloNumeros="True"</c>; en código: <c>EntradaNumerica.SetSoloNumeros(tb, true)</c>.
/// </summary>
public static partial class EntradaNumerica
{
    public static readonly DependencyProperty SoloNumerosProperty = DependencyProperty.RegisterAttached(
        "SoloNumeros", typeof(bool), typeof(EntradaNumerica), new PropertyMetadata(false, AlCambiar));

    public static bool GetSoloNumeros(DependencyObject o) => (bool)o.GetValue(SoloNumerosProperty);
    public static void SetSoloNumeros(DependencyObject o, bool valor) => o.SetValue(SoloNumerosProperty, valor);

    // Sin coma decimal a propósito: en México "1,000" es mil, no uno.
    [GeneratedRegex(@"^\d{0,9}(\.\d{0,3})?$")]
    private static partial Regex NumeroParcial();

    private static void AlCambiar(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox tb || e.NewValue is not true)
            return;
        tb.PreviewTextInput += (_, a) => a.Handled = !NumeroParcial().IsMatch(TextoResultante(tb, a.Text));
        DataObject.AddPastingHandler(tb, AlPegar);
        InputMethod.SetIsInputMethodEnabled(tb, false);
    }

    /// <summary>Cómo quedaría el texto si se acepta lo que se está escribiendo.</summary>
    private static string TextoResultante(TextBox tb, string nuevo) =>
        tb.Text.Remove(tb.SelectionStart, tb.SelectionLength).Insert(tb.SelectionStart, nuevo);

    private static void AlPegar(object sender, DataObjectPastingEventArgs e)
    {
        var tb = (TextBox)sender;
        e.CancelCommand();
        if (e.DataObject.GetData(DataFormats.UnicodeText) is not string pegado)
            return;
        var limpio = new string(pegado.Where(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        var resultado = TextoResultante(tb, limpio);
        if (!NumeroParcial().IsMatch(resultado))
            return;
        var posicion = tb.SelectionStart + limpio.Length;
        tb.Text = resultado;
        tb.CaretIndex = posicion;
    }
}
