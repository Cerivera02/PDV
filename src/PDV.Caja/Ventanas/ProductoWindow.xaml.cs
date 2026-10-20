using System.Windows;
using System.Windows.Controls;
using PDV.Caja.Servicios;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>Alta o cambio de un producto, con lo mínimo a la vista y lo demás en "Más opciones".</summary>
public partial class ProductoWindow : Window
{
    private const string SinDepartamento = "(ninguno)";
    private readonly Producto _original;
    private List<Departamento> _departamentos;
    private Producto? _guardado;

    private ProductoWindow(Producto original, List<Departamento> departamentos)
    {
        InitializeComponent();
        _original = original;
        _departamentos = departamentos;
        var esNuevo = original.Id == 0;

        lblTitulo.Text = esNuevo ? "Nuevo producto" : "Cambiar datos del producto";
        Title = lblTitulo.Text;
        txtCodigo.Text = original.CodigoBarras;
        txtNombre.Text = original.Nombre;
        (original.PorPeso ? rbKilo : rbPieza).IsChecked = true;
        txtPrecio.Text = esNuevo ? "" : original.PrecioVenta.ToString("0.00");
        txtCosto.Text = original.PrecioCosto > 0 ? original.PrecioCosto.ToString("0.00") : "";
        (original.TasaIva switch { >= 0.15m => rbIva16, >= 0.07m => rbIva8, _ => rbIva0 }).IsChecked = true;
        txtMinimo.Text = original.StockMinimo > 0 ? original.StockMinimo.ToString("0.###") : "";
        txtPlu.Text = original.CodigoInterno ?? "";
        LlenarDepartamentos(original.DepartamentoId);

        // Si ya tiene datos "avanzados", mostrar la sección abierta para que se vean
        expMas.IsExpanded = !esNuevo && (original.StockMinimo > 0 || !string.IsNullOrEmpty(original.CodigoInterno));

        Loaded += (_, _) => (txtCodigo.Text.Length == 0 ? txtCodigo : txtNombre.Text.Length == 0 ? txtNombre : (Control)txtPrecio).Focus();
    }

    /// <summary>Abre el formulario; devuelve el producto guardado o null si se canceló.</summary>
    public static async Task<Producto?> EditarAsync(Window owner, Producto producto)
    {
        var departamentos = await Sesion.Api.GetAsync<List<Departamento>>("api/departamentos") ?? [];
        var w = new ProductoWindow(producto, departamentos) { Owner = owner };
        return w.ShowDialog() == true ? w._guardado : null;
    }

    private void LlenarDepartamentos(int? elegido)
    {
        cmbDepartamento.ItemsSource = new[] { SinDepartamento }.Concat(_departamentos.Select(d => d.Nombre)).ToList();
        cmbDepartamento.SelectedItem = _departamentos.FirstOrDefault(d => d.Id == elegido)?.Nombre ?? SinDepartamento;
    }

    private void Venta_Checked(object sender, RoutedEventArgs e)
    {
        if (lblPrecio is not null)
            lblPrecio.Text = rbKilo.IsChecked == true ? "Precio por kilo" : "Precio por pieza";
    }

    private async void NuevoDepartamento_Click(object sender, RoutedEventArgs e)
    {
        var r = CampoDialog.MostrarCon(this, "Nuevo departamento", null, "Guardar", new Campo("Nombre", Ayuda: "Ejemplo: Abarrotes"));
        if (r is null || r[0].Length == 0)
            return;
        try
        {
            var nuevo = await Sesion.Api.PostAsync<Departamento>("api/departamentos", new DepartamentoRequest(r[0]));
            _departamentos = [.. _departamentos.Append(nuevo!).OrderBy(d => d.Nombre)];
            LlenarDepartamentos(nuevo!.Id);
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
        }
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

        if (txtCodigo.Text.Trim().Length == 0) { Error("Falta el código de barras.", txtCodigo); return; }
        if (txtNombre.Text.Trim().Length == 0) { Error("Falta el nombre del producto.", txtNombre); return; }
        if (!Formato.TryDecimal(txtPrecio.Text, out var precio) || precio <= 0) { Error("Escriba el precio de venta, solo con números. Ejemplo: 18.50", txtPrecio); return; }

        decimal costo = 0, minimo = 0;
        if (txtCosto.Text.Trim().Length > 0 && !Formato.TryDecimal(txtCosto.Text, out costo))
        {
            expMas.IsExpanded = true;
            Error("El costo debe ser un número. Ejemplo: 12.50 (o déjelo vacío).", txtCosto);
            return;
        }
        if (txtMinimo.Text.Trim().Length > 0 && !Formato.TryDecimal(txtMinimo.Text, out minimo))
        {
            expMas.IsExpanded = true;
            Error("\"Avisarme cuando queden\" debe ser un número. Ejemplo: 5 (o déjelo vacío).", txtMinimo);
            return;
        }
        if (costo > precio && !Mensajes.Confirmar(this,
                $"Ojo: lo vende en {Formato.Dinero(precio)} pero le cuesta {Formato.Dinero(costo)}. Estaría perdiendo dinero.\n\n¿Guardar de todos modos?",
                "Sí, guardar", "No, corregir"))
            return;

        var porKilo = rbKilo.IsChecked == true;
        var p = new Producto
        {
            Id = _original.Id,
            CodigoBarras = txtCodigo.Text.Trim(),
            Nombre = txtNombre.Text.Trim(),
            DepartamentoId = _departamentos.FirstOrDefault(d => d.Nombre == cmbDepartamento.SelectedItem as string)?.Id,
            PorPeso = porKilo,
            // Se respeta una unidad especial capturada antes (LT, CAJA…) si sigue siendo por pieza
            Unidad = porKilo ? "KG" : _original.Unidad is "KG" or "" ? "PZA" : _original.Unidad,
            PrecioVenta = precio,
            PrecioCosto = costo,
            TasaIva = rbIva16.IsChecked == true ? 0.16m : rbIva8.IsChecked == true ? 0.08m : 0m,
            StockMinimo = minimo,
            CodigoInterno = txtPlu.Text.Trim(),
            Existencia = _original.Existencia,
            Activo = true
        };

        btnGuardar.IsEnabled = false;
        try
        {
            _guardado = p.Id == 0
                ? await Sesion.Api.PostAsync<Producto>("api/productos", p)
                : await Sesion.Api.PutAsync<Producto>($"api/productos/{p.Id}", p);
            DialogResult = true;
        }
        catch (ApiException ex)
        {
            Error(ex.Message);
        }
        finally
        {
            btnGuardar.IsEnabled = true;
        }
    }
}
