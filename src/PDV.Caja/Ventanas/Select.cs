using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using PDV.Caja.Servicios;

namespace PDV.Caja.Ventanas;

/// <summary>
/// Opciones extra para las listas para elegir (ComboBox) con el estilo de App.xaml, parecido a react-select:
/// <list type="bullet">
/// <item><c>v:Select.Pista="Elija…"</c>: texto gris cuando no hay nada elegido.</item>
/// <item><c>v:Select.Buscable="True"</c>: se puede escribir para filtrar las opciones (sin acentos ni mayúsculas).</item>
/// <item><c>v:Select.Limpiable="True"</c>: una ✕ para dejarlo sin elegir.</item>
/// </list>
/// </summary>
public static class Select
{
    // ------------------------------------------------------------------ Pista

    public static readonly DependencyProperty PistaProperty = DependencyProperty.RegisterAttached(
        "Pista", typeof(string), typeof(Select), new PropertyMetadata(""));

    public static string GetPista(DependencyObject d) => (string)d.GetValue(PistaProperty);
    public static void SetPista(DependencyObject d, string valor) => d.SetValue(PistaProperty, valor);

    // ------------------------------------------------------------------ Limpiable

    public static readonly DependencyProperty LimpiableProperty = DependencyProperty.RegisterAttached(
        "Limpiable", typeof(bool), typeof(Select), new PropertyMetadata(false));

    public static bool GetLimpiable(DependencyObject d) => (bool)d.GetValue(LimpiableProperty);
    public static void SetLimpiable(DependencyObject d, bool valor) => d.SetValue(LimpiableProperty, valor);

    static Select()
    {
        // La ✕ del estilo (PART_Limpiar) avisa con un clic que sube hasta el ComboBox
        EventManager.RegisterClassHandler(typeof(ComboBox), ButtonBase.ClickEvent, new RoutedEventHandler((s, e) =>
        {
            if (s is ComboBox cb && e.OriginalSource is FrameworkElement { Name: "PART_Limpiar" })
            {
                e.Handled = true;
                cb.SelectedItem = null;
                if (cb.IsEditable)
                    cb.Text = "";
                cb.Focus();
            }
        }));
        // Mientras se filtra, la lista suelta y recupera la opción elegida: la pantalla no debe enterarse
        // (por ejemplo, Productos volvería a llenar su lista y la lista desplegable se cerraría).
        EventManager.RegisterClassHandler(typeof(ComboBox), Selector.SelectionChangedEvent, new SelectionChangedEventHandler((s, e) =>
        {
            if (s is ComboBox cb && Estados.TryGetValue(cb, out var estado) && (estado.Ignorar || estado.Filtrando))
                e.Handled = true;
        }));
    }

    /// <summary>Registra los manejadores de las listas (llamar una vez al iniciar la app).</summary>
    public static void Registrar() => RuntimeHelpers.RunClassConstructor(typeof(Select).TypeHandle);

    // ------------------------------------------------------------------ Buscable

    public static readonly DependencyProperty BuscableProperty = DependencyProperty.RegisterAttached(
        "Buscable", typeof(bool), typeof(Select), new PropertyMetadata(false, AlCambiarBuscable));

    public static bool GetBuscable(DependencyObject d) => (bool)d.GetValue(BuscableProperty);
    public static void SetBuscable(DependencyObject d, bool valor) => d.SetValue(BuscableProperty, valor);

    /// <summary>Estado del filtro de cada lista buscable.</summary>
    private sealed class Estado
    {
        public TextBox? Texto;
        public bool Ignorar;
        public bool Filtrando;
        /// <summary>Se movió con las flechas desde la última vez que escribió.</summary>
        public bool Navego;
        public object? Anterior;
    }

    private static readonly ConditionalWeakTable<ComboBox, Estado> Estados = new();

    private static void AlCambiarBuscable(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ComboBox cb || e.NewValue is not true)
            return;
        cb.IsEditable = true;
        cb.IsTextSearchEnabled = false;
        cb.StaysOpenOnEdit = true;
        Estados.GetOrCreateValue(cb);

        cb.Loaded += (_, _) => Conectar(cb);
        cb.DropDownClosed += (_, _) => Terminar(cb);
        cb.LostKeyboardFocus += (_, _) =>
        {
            if (!cb.IsKeyboardFocusWithin && !cb.IsDropDownOpen)
                Terminar(cb);
        };
        // Las listas pueden compartir la colección (ej. catálogos del SAT): nunca dejar un filtro puesto
        cb.Unloaded += (_, _) => QuitarFiltro(cb);
        if (cb.IsLoaded)
            Conectar(cb);
    }

    private static void Conectar(ComboBox cb)
    {
        var estado = Estados.GetOrCreateValue(cb);
        if (estado.Texto is not null || cb.Template.FindName("PART_EditableTextBox", cb) is not TextBox tb)
            return;
        estado.Texto = tb;
        tb.GotKeyboardFocus += (_, _) => tb.SelectAll(); // escribir reemplaza lo elegido
        tb.TextChanged += (_, _) => AlEscribir(cb, estado);
        // Las teclas se toman desde la ventana: el ComboBox procesa ↓ y Enter antes que sus propios manejadores
        if (Window.GetWindow(cb) is { } ventana)
            ventana.PreviewKeyDown += (_, e) =>
            {
                if (cb.IsKeyboardFocusWithin)
                    Tecla(cb, e);
            };
    }

    private static void AlEscribir(ComboBox cb, Estado estado)
    {
        var tb = estado.Texto!;
        if (estado.Ignorar)
            return;
        var texto = tb.Text;
        // El texto lo puso la lista al elegir una opción (clic o flechas): no es una búsqueda
        if (cb.SelectedItem is { } elegido && TextoDe(cb, elegido) == texto)
            return;
        // Quedó vacío sin estar buscando (por ejemplo, con la ✕): no hay que abrir la lista
        if (texto.Length == 0 && !estado.Filtrando)
            return;

        if (!estado.Filtrando)
        {
            estado.Filtrando = true;
            estado.Anterior = cb.SelectedItem;
        }
        estado.Navego = false;
        var caret = tb.CaretIndex;
        // Al filtrar, la lista puede soltar la opción elegida y borrar el texto: esos cambios no son búsquedas
        estado.Ignorar = true;
        try
        {
            cb.Items.Filter = texto.Trim().Length == 0 ? null : o => Contiene(TextoDe(cb, o), texto.Trim());
            if (!cb.IsDropDownOpen)
                cb.IsDropDownOpen = true;
            if (tb.Text != texto)
                tb.Text = texto; // se respeta lo que se escribió
        }
        finally
        {
            estado.Ignorar = false;
        }
        tb.CaretIndex = Math.Min(caret, tb.Text.Length);
    }

    /// <summary>
    /// Mientras busca: ↓↑ recorren las opciones que quedaron y Enter elige la marcada
    /// (o la primera, si no se movió con las flechas), como react-select.
    /// </summary>
    private static void Tecla(ComboBox cb, KeyEventArgs e)
    {
        if (!Estados.TryGetValue(cb, out var estado) || !estado.Filtrando || !cb.IsDropDownOpen)
            return;
        var cuantos = cb.Items.Count;
        switch (e.Key)
        {
            case Key.Down or Key.Up when cuantos > 0:
                e.Handled = true;
                var abajo = e.Key == Key.Down;
                var actual = cb.SelectedIndex;
                cb.SelectedIndex = !estado.Navego || actual < 0
                    ? (abajo ? 0 : cuantos - 1)
                    : Math.Clamp(actual + (abajo ? 1 : -1), 0, cuantos - 1);
                estado.Navego = true;
                if (cb.ItemContainerGenerator.ContainerFromIndex(cb.SelectedIndex) is FrameworkElement opcion)
                    opcion.BringIntoView();
                break;
            case Key.Enter or Key.Tab:
                if (!estado.Navego || cb.SelectedItem is null)
                {
                    if (cuantos == 0)
                        break; // nada coincide: al cerrar regresa a lo que tenía
                    cb.SelectedIndex = 0;
                }
                if (e.Key == Key.Enter)
                    e.Handled = true; // que no dispare el botón "Aceptar" de la ventana
                cb.IsDropDownOpen = false;
                break;
        }
    }

    /// <summary>Al cerrar la lista o salir: quita el filtro y deja en el texto la opción elegida (o la que había).</summary>
    private static void Terminar(ComboBox cb)
    {
        if (!Estados.TryGetValue(cb, out var estado) || estado.Texto is not { } tb)
            return;
        var escrito = tb.Text.Trim();
        var elegido = cb.SelectedItem;
        var filtraba = estado.Filtrando;
        var anterior = estado.Anterior;

        estado.Ignorar = true;
        try
        {
            QuitarFiltro(cb);
            if (filtraba && elegido is null)
            {
                // Si escribió una opción completa, se elige; si no, se queda lo que tenía antes (como react-select)
                elegido = cb.Items.Cast<object>().FirstOrDefault(o => string.Equals(TextoDe(cb, o), escrito, StringComparison.CurrentCultureIgnoreCase))
                          ?? anterior;
            }
            cb.SelectedItem = elegido;
            tb.Text = elegido is null ? "" : TextoDe(cb, elegido);
        }
        finally
        {
            estado.Ignorar = false;
            estado.Filtrando = false;
            estado.Anterior = null;
            estado.Navego = false;
        }

        // Mientras filtraba, la pantalla no vio los cambios de selección: se le avisa una sola vez del resultado
        if (filtraba && !Equals(elegido, anterior))
            cb.RaiseEvent(new SelectionChangedEventArgs(Selector.SelectionChangedEvent,
                anterior is null ? Array.Empty<object>() : [anterior], elegido is null ? Array.Empty<object>() : [elegido]));
    }

    private static void QuitarFiltro(ComboBox cb)
    {
        if (cb.Items.Filter is not null)
            cb.Items.Filter = null;
    }

    private static string TextoDe(ComboBox cb, object o)
    {
        if (!string.IsNullOrEmpty(cb.DisplayMemberPath) && o.GetType().GetProperty(cb.DisplayMemberPath) is { } p)
            return p.GetValue(o)?.ToString() ?? "";
        return o.ToString() ?? "";
    }

    /// <summary>"regimen" encuentra "Régimen"; "612" encuentra "612 - Personas Físicas…".</summary>
    public static bool Contiene(string texto, string buscado) =>
        Formato.Mx.CompareInfo.IndexOf(texto, buscado, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;
}
