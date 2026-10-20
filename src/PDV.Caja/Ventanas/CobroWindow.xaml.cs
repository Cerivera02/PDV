using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PDV.Caja.Servicios;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>
/// Cobro sencillo: Efectivo (se teclea con cuánto pagan; vacío = exacto), Tarjeta (todo el total; "parte y parte" opcional)
/// o Crédito (solo si el cliente elegido tiene crédito; puede pagar una parte en efectivo).
/// </summary>
public partial class CobroWindow : Window
{
    private enum Modo { Efectivo, Tarjeta, Credito }

    private readonly decimal _total;
    private readonly Cliente? _cliente;
    private Modo _modo;
    private List<PagoRequest>? _pagos;

    private CobroWindow(decimal total, Cliente? cliente)
    {
        InitializeComponent();
        _total = total;
        _cliente = cliente;
        lblTotal.Text = Formato.Dinero(total);
        lblInstruccionTarjeta.Text = $"Cobre {Formato.Dinero(total)} en la terminal de tarjeta. Cuando salga aprobado, presione \"Cobrar\".";

        if (cliente is not null)
        {
            lblCliente.Text = $"👤 {cliente.Nombre}";
            lblCliente.Visibility = Visibility.Visible;
        }
        // Crédito solo para clientes con crédito
        if (cliente is { LimiteCredito: > 0 })
        {
            btnCredito.Visibility = Visibility.Visible;
            gridModos.Columns = 3;
        }

        Loaded += (_, _) => txtEfectivo.Focus();
        CambiarModo(Modo.Efectivo);
    }

    /// <summary>Devuelve cómo pagó el cliente, o null si regresó a la venta.</summary>
    public static List<PagoRequest>? Cobrar(Window owner, decimal total, Cliente? cliente = null)
    {
        var w = new CobroWindow(total, cliente) { Owner = owner };
        return w.ShowDialog() == true ? w._pagos : null;
    }

    // ------------------------------------------------------------------ Modo

    private void ModoEfectivo_Click(object sender, RoutedEventArgs e) => CambiarModo(Modo.Efectivo);
    private void ModoTarjeta_Click(object sender, RoutedEventArgs e) => CambiarModo(Modo.Tarjeta);
    private void ModoCredito_Click(object sender, RoutedEventArgs e) => CambiarModo(Modo.Credito);

    private void CambiarModo(Modo modo)
    {
        _modo = modo;
        panelEfectivo.Visibility = modo == Modo.Efectivo ? Visibility.Visible : Visibility.Collapsed;
        panelTarjeta.Visibility = modo == Modo.Tarjeta ? Visibility.Visible : Visibility.Collapsed;
        panelCredito.Visibility = modo == Modo.Credito ? Visibility.Visible : Visibility.Collapsed;
        Marcar(btnEfectivo, modo == Modo.Efectivo);
        Marcar(btnTarjeta, modo == Modo.Tarjeta);
        Marcar(btnCredito, modo == Modo.Credito);

        if (modo == Modo.Credito && _cliente is not null)
        {
            lblInstruccionCredito.Text = _cliente.CreditoDisponible >= _total
                ? $"Venta a crédito para {_cliente.Nombre}. Crédito disponible: {Formato.Dinero(_cliente.CreditoDisponible)}."
                : $"⚠ A {_cliente.Nombre} solo le quedan {Formato.Dinero(_cliente.CreditoDisponible)} de crédito. Tiene que pagar la diferencia ahorita.";
            if (_cliente.CreditoDisponible < _total)
                chkAbonoAhora.IsChecked = true;
        }

        Control foco = modo switch
        {
            Modo.Tarjeta => chkMixto.IsChecked == true ? txtTarjeta : txtReferencia,
            Modo.Credito => chkAbonoAhora.IsChecked == true ? txtEfectivoCredito : btnCobrar,
            _ => txtEfectivo
        };
        foco.Focus();
        Recalcular();
    }

    private void Marcar(Button b, bool elegido)
    {
        b.Style = (Style)FindResource(elegido ? "BotonAzul" : typeof(Button));
        b.Focusable = false;
    }

    private void Mixto_Changed(object sender, RoutedEventArgs e)
    {
        var mixto = chkMixto.IsChecked == true;
        panelMixto.Visibility = mixto ? Visibility.Visible : Visibility.Collapsed;
        lblInstruccionTarjeta.Text = mixto
            ? "Escriba cuánto se cobra con tarjeta y cuánto le dan en efectivo."
            : $"Cobre {Formato.Dinero(_total)} en la terminal de tarjeta. Cuando salga aprobado, presione \"Cobrar\".";
        if (mixto)
            txtTarjeta.Focus();
        Recalcular();
    }

    private void AbonoAhora_Changed(object sender, RoutedEventArgs e)
    {
        panelAbonoAhora.Visibility = chkAbonoAhora.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (chkAbonoAhora.IsChecked == true)
            txtEfectivoCredito.Focus();
        Recalcular();
    }

    // ------------------------------------------------------------------ Cálculo

    private void Montos_TextChanged(object sender, TextChangedEventArgs e) => Recalcular();

    /// <summary>Lo capturado según el modo. Valido = false si algún número no se entiende.</summary>
    private (decimal Efectivo, decimal Tarjeta, decimal Credito, bool Valido) Leer()
    {
        switch (_modo)
        {
            case Modo.Efectivo:
                return LeerMonto(txtEfectivo.Text, out var ef) ? (ef, 0, 0, true) : (0, 0, 0, false);
            case Modo.Tarjeta when chkMixto.IsChecked != true:
                return (0, _total, 0, true);
            case Modo.Tarjeta:
                var ok = LeerMonto(txtTarjeta.Text, out var t) & LeerMonto(txtEfectivoMixto.Text, out var e);
                return (e, t, 0, ok);
            default: // Crédito: lo que no pague ahorita en efectivo queda a deber
                if (chkAbonoAhora.IsChecked != true)
                    return (0, 0, _total, true);
                if (!LeerMonto(txtEfectivoCredito.Text, out var ahora))
                    return (0, 0, 0, false);
                return (Math.Min(ahora, _total), 0, Math.Max(0, _total - ahora), true);
        }
    }

    private static bool LeerMonto(string texto, out decimal monto)
    {
        monto = 0;
        return texto.Trim().Length == 0 || (Formato.TryDecimal(texto, out monto) && monto >= 0);
    }

    private void Recalcular()
    {
        if (lblResultado is null)
            return;
        var (efectivo, tarjeta, credito, valido) = Leer();

        if (!valido)
        {
            Resultado("Revise el número", "Solo números, ej. 200", "Peligro", "#FDECEA");
            return;
        }
        if (_modo == Modo.Credito)
        {
            if (_cliente is not null && credito > _cliente.CreditoDisponible)
                Resultado("No alcanza el crédito. Tiene que pagar ahorita al menos:", Formato.Dinero(_total - _cliente.CreditoDisponible), "Peligro", "#FDECEA");
            else
                Resultado("Queda debiendo de esta compra:", Formato.Dinero(credito), "Aviso", "#FFF4E5");
            return;
        }
        if (_modo == Modo.Efectivo && efectivo == 0)
        {
            Resultado("Escriba con cuánto le pagan", "Enter = pago exacto", "TextoSuave", "#EEF2F7");
            return;
        }

        var diferencia = efectivo + tarjeta - _total;
        if (diferencia >= 0)
            Resultado(diferencia > 0 ? "Entregue de cambio:" : "Pago exacto, sin cambio", Formato.Dinero(diferencia), "Exito", "#E7F5EC");
        else
            Resultado("Todavía falta:", Formato.Dinero(-diferencia), "Peligro", "#FDECEA");
    }

    private void Resultado(string titulo, string valor, string color, string fondo)
    {
        lblResultadoTitulo.Text = titulo;
        lblResultado.Text = valor;
        lblResultado.Foreground = (Brush)FindResource(color);
        panelResultado.Background = (Brush)new BrushConverter().ConvertFromString(fondo)!;
    }

    private void Aceptar_Click(object sender, RoutedEventArgs e)
    {
        var (efectivo, tarjeta, credito, valido) = Leer();
        if (!valido)
        {
            Mensajes.Error(this, "Hay un número que no se entiende. Escriba solo números, por ejemplo: 200");
            return;
        }

        // En efectivo, Enter sin escribir nada = pago exacto
        if (_modo == Modo.Efectivo && efectivo == 0)
            efectivo = _total;

        if (_modo == Modo.Credito && _cliente is not null && credito > _cliente.CreditoDisponible)
        {
            Mensajes.Error(this, $"No alcanza el crédito de {_cliente.Nombre}: le quedan {Formato.Dinero(_cliente.CreditoDisponible)}.\n\nTiene que pagar ahorita al menos {Formato.Dinero(_total - _cliente.CreditoDisponible)}.");
            return;
        }
        if (tarjeta > _total)
        {
            Mensajes.Error(this, "Con tarjeta no se puede cobrar más que el total.");
            return;
        }
        if (efectivo + tarjeta + credito < _total)
        {
            Mensajes.Error(this, $"Todavía falta {Formato.Dinero(_total - efectivo - tarjeta - credito)}.");
            return;
        }

        _pagos = [];
        if (efectivo > 0)
            _pagos.Add(new PagoRequest(FormasPago.Efectivo, efectivo));
        if (tarjeta > 0)
            _pagos.Add(new PagoRequest(FormasPago.Tarjeta, tarjeta, txtReferencia.Text.Trim()));
        if (credito > 0)
            _pagos.Add(new PagoRequest(FormasPago.Credito, credito));
        DialogResult = true;
    }
}
