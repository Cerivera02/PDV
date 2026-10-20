using System.Windows;
using System.Windows.Controls;
using PDV.Caja.Servicios;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>Alta o cambio de un cliente (solo el encargado).</summary>
public partial class ClienteWindow : Window
{
    private readonly Cliente _original;
    private Cliente? _guardado;

    private ClienteWindow(Cliente c)
    {
        InitializeComponent();
        _original = c;
        lblTitulo.Text = c.Id == 0 ? "Nuevo cliente" : "Cambiar datos del cliente";
        Title = lblTitulo.Text;

        txtNombre.Text = c.Nombre;
        txtTelefono.Text = c.Telefono ?? "";
        txtDireccion.Text = c.Direccion ?? "";

        (c.LimiteCredito > 0 ? rbConCredito : rbSinCredito).IsChecked = true;
        txtLimite.Text = c.LimiteCredito > 0 ? c.LimiteCredito.ToString("0.##") : "";
        lblDebe.Text = c.Saldo > 0 ? $"Ahora debe {Formato.Dinero(c.Saldo)}." : "";

        (c.DescuentoPorcentaje > 0 ? rbDescuento : c.PrecioMayoreo ? rbMayoreo : rbPrecioNormal).IsChecked = true;
        txtDescuento.Text = c.DescuentoPorcentaje > 0 ? c.DescuentoPorcentaje.ToString("0.##") : "";

        cmbRegimen.ItemsSource = CatalogosSat.Regimenes;
        cmbUso.ItemsSource = CatalogosSat.UsosCfdi;
        txtRfc.Text = c.Rfc ?? "";
        txtRazonSocial.Text = c.RazonSocial ?? "";
        txtCp.Text = c.CodigoPostalFiscal ?? "";
        cmbRegimen.SelectedItem = c.RegimenFiscal is null ? null : CatalogosSat.Opcion(CatalogosSat.Regimenes, c.RegimenFiscal);
        cmbUso.SelectedItem = CatalogosSat.Opcion(CatalogosSat.UsosCfdi, c.UsoCfdi ?? "G03");
        txtCorreo.Text = c.Correo ?? "";
        expFactura.IsExpanded = c.TieneDatosFactura;

        Loaded += (_, _) => txtNombre.Focus();
    }

    /// <summary>Abre el formulario; devuelve el cliente guardado o null si se canceló.</summary>
    public static Cliente? Editar(Window owner, Cliente cliente)
    {
        var w = new ClienteWindow(cliente) { Owner = owner };
        return w.ShowDialog() == true ? w._guardado : null;
    }

    private void Credito_Checked(object sender, RoutedEventArgs e)
    {
        if (panelLimite is not null)
            panelLimite.Visibility = rbConCredito.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Precio_Checked(object sender, RoutedEventArgs e)
    {
        if (txtDescuento is not null)
            txtDescuento.IsEnabled = rbDescuento.IsChecked == true;
    }

    private void Error(string texto, Control? campo = null)
    {
        lblError.Text = texto;
        panelError.Visibility = Visibility.Visible;
        campo?.Focus();
    }

    private async void Guardar_Click(object sender, RoutedEventArgs e)
    {
        panelError.Visibility = Visibility.Collapsed;
        if (txtNombre.Text.Trim().Length == 0) { Error("Falta el nombre del cliente.", txtNombre); return; }

        decimal limite = 0, descuento = 0;
        if (rbConCredito.IsChecked == true && (!Formato.TryDecimal(txtLimite.Text, out limite) || limite <= 0))
        {
            Error("Escriba el límite de crédito. Ejemplo: 1000", txtLimite);
            return;
        }
        if (rbConCredito.IsChecked == true && limite < _original.Saldo)
        {
            Error($"El límite no puede ser menor a lo que ya debe ({Formato.Dinero(_original.Saldo)}).", txtLimite);
            return;
        }
        if (rbConCredito.IsChecked != true && _original.Saldo > 0)
        {
            Error($"Todavía debe {Formato.Dinero(_original.Saldo)}. Para quitarle el crédito, primero tiene que liquidar.");
            return;
        }
        if (rbDescuento.IsChecked == true && (!Formato.TryDecimal(txtDescuento.Text, out descuento) || descuento is <= 0 or > 90))
        {
            Error("Escriba el descuento entre 1 y 90. Ejemplo: 10 (para 10%).", txtDescuento);
            return;
        }

        var c = new Cliente
        {
            Id = _original.Id,
            Nombre = txtNombre.Text.Trim(),
            Telefono = txtTelefono.Text,
            Direccion = txtDireccion.Text,
            LimiteCredito = rbConCredito.IsChecked == true ? limite : 0,
            DescuentoPorcentaje = rbDescuento.IsChecked == true ? descuento : 0,
            PrecioMayoreo = rbMayoreo.IsChecked == true,
            Rfc = txtRfc.Text,
            RazonSocial = txtRazonSocial.Text,
            CodigoPostalFiscal = txtCp.Text,
            RegimenFiscal = CatalogosSat.Clave(cmbRegimen.SelectedItem as string),
            UsoCfdi = txtRfc.Text.Trim().Length > 0 ? CatalogosSat.Clave(cmbUso.SelectedItem as string) : null,
            Correo = txtCorreo.Text
        };

        btnGuardar.IsEnabled = false;
        try
        {
            _guardado = c.Id == 0
                ? await Sesion.Api.PostAsync<Cliente>("api/clientes", c)
                : await Sesion.Api.PutAsync<Cliente>($"api/clientes/{c.Id}", c);
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            // Los errores de los datos de factura se ven mejor con la sección abierta
            if (ex.Message.Contains("RFC") || ex.Message.Contains("factur") || ex.Message.Contains("postal") || ex.Message.Contains("correo"))
                expFactura.IsExpanded = true;
            Error(ex.Message);
        }
        finally
        {
            btnGuardar.IsEnabled = true;
        }
    }
}
