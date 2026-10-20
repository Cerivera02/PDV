using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using PDV.Caja.Servicios;
using PDV.Core;

namespace PDV.Caja.Ventanas;

/// <summary>Productos y mercancía (Encargado y Capturista).</summary>
public partial class ProductosWindow : Window
{
    private static readonly CompareInfo Comparador = Formato.Mx.CompareInfo;
    private const string Todos = "Todos los departamentos";
    private const string SinDepartamento = "(sin departamento)";
    private List<Producto> _productos = [];
    private List<Departamento> _departamentos = [];

    public ProductosWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) => await Ejecutar(RecargarAsync);
    }

    private async Task RecargarAsync()
    {
        // Solo productos vigentes: lo que se quitó ya no aparece.
        _productos = await Sesion.Api.GetAsync<List<Producto>>("api/productos") ?? [];
        _departamentos = await Sesion.Api.GetAsync<List<Departamento>>("api/departamentos") ?? [];

        var elegido = cmbDepartamento.SelectedItem as string ?? Todos;
        var opciones = new[] { Todos }.Concat(_departamentos.Select(d => d.Nombre)).Append(SinDepartamento).ToList();
        cmbDepartamento.ItemsSource = opciones;
        cmbDepartamento.SelectedItem = opciones.Contains(elegido) ? elegido : Todos;

        Filtrar();
        txtBuscar.Focus();
    }

    private void Filtrar()
    {
        if (cmbDepartamento is null || btnPocos is null)
            return; // la ventana todavía se está construyendo
        var texto = txtBuscar.Text.Trim();
        lblPista.Visibility = texto.Length == 0 ? Visibility.Visible : Visibility.Collapsed;

        var q = _productos.AsEnumerable();
        if (texto.Length > 0)
            q = q.Where(p => p.CodigoBarras == texto || p.CodigoInterno == texto
                || Comparador.IndexOf(p.Nombre, texto, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
        q = (cmbDepartamento.SelectedItem as string) switch
        {
            null or Todos => q,
            SinDepartamento => q.Where(p => p.DepartamentoId is null),
            var nombre => q.Where(p => p.DepartamentoNombre == nombre)
        };
        if (btnPocos.IsChecked == true)
            q = q.Where(p => p.BajoMinimo);

        var lista = q.ToList();
        grid.ItemsSource = lista;
        if (lista.Count > 0 && grid.SelectedIndex < 0)
            grid.SelectedIndex = 0;

        var pocos = _productos.Count(p => p.BajoMinimo);
        btnPocos.Content = pocos > 0 ? $"⚠ Se están acabando ({pocos})" : "⚠ Se están acabando";
        lblConteo.Text = $"Mostrando {lista.Count} de {_productos.Count} productos.   Doble clic en un producto para cambiar sus datos.";

        lblVacio.Text = _productos.Count == 0
            ? "Todavía no hay productos.\nPresione \"➕ Nuevo producto\" para dar de alta el primero."
            : texto.Length > 0 && texto.All(char.IsAsciiDigit)
                ? $"No hay ningún producto con el código {texto}.\nPresione Enter para darlo de alta."
                : "No se encontró ningún producto.";
        lblVacio.Visibility = lista.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>El producto elegido; si no hay, avisa qué hacer.</summary>
    private Producto? Elegido()
    {
        if (grid.SelectedItem is Producto p)
            return p;
        Mensajes.Info(this, "Primero toque un producto de la lista.");
        return null;
    }

    private void Seleccionar(int productoId)
    {
        grid.SelectedItem = (grid.ItemsSource as List<Producto>)?.FirstOrDefault(x => x.Id == productoId);
        if (grid.SelectedItem is not null)
            grid.ScrollIntoView(grid.SelectedItem);
    }

    // ------------------------------------------------------------------ Producto

    private async Task NuevoAsync(string? codigo = null)
    {
        var departamento = _departamentos.FirstOrDefault(d => d.Nombre == cmbDepartamento.SelectedItem as string);
        var nuevo = new Producto { CodigoBarras = codigo ?? "", DepartamentoId = departamento?.Id };
        if (await ProductoWindow.EditarAsync(this, nuevo) is { } guardado)
        {
            txtBuscar.Clear();
            await RecargarAsync();
            Seleccionar(guardado.Id);
        }
    }

    private async Task EditarAsync()
    {
        if (Elegido() is not { } p)
            return;
        if (await ProductoWindow.EditarAsync(this, p) is { } guardado)
        {
            await RecargarAsync();
            Seleccionar(guardado.Id);
        }
    }

    private async Task QuitarAsync()
    {
        if (Elegido() is not { } p)
            return;
        if (!Mensajes.Confirmar(this, $"¿Quitar \"{p.Nombre}\"?\n\nYa no aparecerá en la lista ni se podrá vender. Las ventas anteriores no se borran.",
                "Sí, quitar", "No"))
            return;
        p.Activo = false;
        await Sesion.Api.PutAsync<Producto>($"api/productos/{p.Id}", p);
        await RecargarAsync();
        Mensajes.Exito(this, $"Se quitó \"{p.Nombre}\".\n\nSi lo vuelve a dar de alta con el mismo código, se recupera con su historial.");
    }

    // ------------------------------------------------------------------ Mercancía

    private async Task LlegoMercanciaAsync()
    {
        if (Elegido() is not { } p)
            return;
        var r = CampoDialog.MostrarCon(this, "📦 Llegó mercancía", $"Producto: {p.Nombre}\nAhora hay: {p.HayTexto}", "Guardar",
            new Campo(p.PorPeso ? "¿Cuántos kilos llegaron?" : "¿Cuántas piezas llegaron?", Numero: true, Minimo: 0.001m),
            new Campo("¿Cuánto le costó cada una? (opcional)", Ayuda: "Si lo escribe, se actualiza el costo del producto.", Numero: true, Opcional: true),
            new Campo("Nota (opcional)", Ayuda: "Ejemplo: número de factura o nombre del proveedor."));
        if (r is null || !Formato.TryDecimal(r[0], out var cantidad))
            return;
        decimal? costo = Formato.TryDecimal(r[1], out var c) && c > 0 ? c : null;
        await RegistrarAsync(p, TiposMovimiento.Entrada, cantidad, costo, r[2]);
    }

    private async Task SeEchoAPerderAsync()
    {
        if (Elegido() is not { } p)
            return;
        var r = CampoDialog.MostrarCon(this, "🥀 Se echó a perder o se rompió", $"Producto: {p.Nombre}\nAhora hay: {p.HayTexto}", "Guardar",
            new Campo(p.PorPeso ? "¿Cuántos kilos?" : "¿Cuántas piezas?", Numero: true, Minimo: 0.001m),
            new Campo("¿Qué pasó? (opcional)", Ayuda: "Ejemplo: caducó, se rompió, se lo comió el ratón…"));
        if (r is null || !Formato.TryDecimal(r[0], out var cantidad))
            return;
        await RegistrarAsync(p, TiposMovimiento.Merma, cantidad, null, r[1]);
    }

    private async Task ContarAsync()
    {
        if (Elegido() is not { } p)
            return;
        var r = CampoDialog.MostrarCon(this, "🔢 Contar lo que hay",
            $"Producto: {p.Nombre}\nEl sistema dice que hay: {p.HayTexto}\n\nCuente lo que hay de verdad en la tienda y escríbalo. El sistema se corrige solo.", "Guardar",
            new Campo(p.PorPeso ? "¿Cuántos kilos hay de verdad?" : "¿Cuántas piezas hay de verdad?",
                Ayuda: "Si no hay ninguno, escriba 0.", Numero: true));
        if (r is null || !Formato.TryDecimal(r[0], out var cantidad))
            return;
        await RegistrarAsync(p, TiposMovimiento.Ajuste, cantidad, null, null);
    }

    private async Task RegistrarAsync(Producto p, string tipo, decimal cantidad, decimal? costo, string? nota)
    {
        await Sesion.Api.PostAsync("api/inventario/movimientos",
            new MovimientoInventarioRequest(p.Id, tipo, cantidad, costo, string.IsNullOrWhiteSpace(nota) ? null : nota));
        await RecargarAsync();
        Seleccionar(p.Id);
        var actualizado = _productos.FirstOrDefault(x => x.Id == p.Id);
        Mensajes.Exito(this, $"Listo.\n\nAhora hay {actualizado?.HayTexto ?? "?"} de \"{p.Nombre}\".");
    }

    private void Historial()
    {
        if (Elegido() is not { } p)
            return;
        new TablaWindow($"Historial: {p.Nombre}", "Todo lo que ha entrado y salido de este producto, lo más reciente arriba.",
            [
                new Columna("Fecha", nameof(MovimientoInventario.FechaLocal), "{0:dd/MM/yyyy  h:mm tt}", 200),
                new Columna("Qué pasó", nameof(MovimientoInventario.TipoTexto), Ancho: 230),
                new Columna("Cantidad", nameof(MovimientoInventario.Cantidad), "{0:+0.###;-0.###;0}", 110),
                new Columna("Quedaron", nameof(MovimientoInventario.ExistenciaResultante), "{0:0.###}", 110),
                new Columna("Quién", nameof(MovimientoInventario.Usuario), Ancho: 160),
                new Columna("Detalle", nameof(MovimientoInventario.Detalle))
            ],
            async () => (IEnumerable)(await Sesion.Api.GetAsync<List<MovimientoInventario>>($"api/inventario/kardex/{p.Id}") ?? []))
        { Owner = this }.ShowDialog();
    }

    /// <summary>Historial de precios: cada cambio del precio de venta o del costo.</summary>
    private void Precios()
    {
        if (Elegido() is not { } p)
            return;
        new TablaWindow($"Precios anteriores: {p.Nombre}",
            $"Precio de hoy: {p.PrecioTexto}   ·   Costo: {Formato.Dinero(p.PrecioCosto)}.   Cada vez que cambió el precio o el costo, lo más reciente arriba.",
            [
                new Columna("Fecha", nameof(CambioPrecio.FechaLocal), "{0:dd/MM/yyyy  h:mm tt}", 200),
                new Columna("Qué pasó", nameof(CambioPrecio.OrigenTexto), Ancho: 220),
                new Columna("Precio de venta", nameof(CambioPrecio.VentaTexto)),
                new Columna("Costo", nameof(CambioPrecio.CostoTexto), Ancho: 290),
                new Columna("Quién", nameof(CambioPrecio.Usuario), Ancho: 150)
            ],
            async () => (IEnumerable)(await Sesion.Api.GetAsync<List<CambioPrecio>>($"api/productos/{p.Id}/precios") ?? []))
        { Owner = this }.ShowDialog();
    }

    // ------------------------------------------------------------------ Ofertas

    private async Task OfertasAsync()
    {
        if (Elegido() is not { } p)
            return;
        var unidad = p.PorPeso ? "kilos" : "piezas";
        var porUnidad = p.PorPeso ? "por kilo" : "por pieza";
        var hoy = DateTime.Today.ToString("yyyy-MM-dd");

        var acciones = new List<AccionTabla>
        {
            new("➕  Precio de oferta por fechas", async _ =>
            {
                var r = CampoDialog.MostrarCon(this, "Precio de oferta", $"{p.Nombre} · precio normal: {p.PrecioTexto}", "Guardar",
                    new Campo($"Precio de oferta ({porUnidad})", Numero: true, Minimo: 0.001m),
                    new Campo("Desde el día", hoy, Fecha: true),
                    new Campo("Hasta el día (incluido)", Fecha: true, Ayuda: "Al terminar ese día, regresa solo a su precio normal."));
                if (r is null) return false;
                await Sesion.Api.PostAsync($"api/productos/{p.Id}/ofertas",
                    new OfertaRequest(TiposOferta.Precio, decimal.Parse(r[0]), null, null, DateTime.Parse(r[1]), DateTime.Parse(r[2])));
                return true;
            }, RequiereSeleccion: false, Estilo: "BotonVerde")
        };

        // El 2x1 / 3x2 solo tiene sentido en productos por pieza
        if (!p.PorPeso)
        {
            acciones.Add(new("➕  2x1 / 3x2", async _ =>
            {
                var r = CampoDialog.MostrarCon(this, "Promoción 2x1 / 3x2", p.Nombre, "Guardar",
                    new Campo("¿Cuántas lleva?", "3", Opciones: ["2", "3", "4", "5", "6"], Editable: true),
                    new Campo("¿Cuántas paga?", "2", Opciones: ["1", "2", "3", "4", "5"], Editable: true,
                        Ayuda: "Ejemplo 3x2: lleva 3 y paga 2.  2x1: lleva 2 y paga 1."),
                    new Campo("Desde el día (opcional)", Fecha: true, Opcional: true, Ayuda: "Vacío = desde hoy."),
                    new Campo("Hasta el día (opcional)", Fecha: true, Opcional: true, Ayuda: "Vacío = hasta que la quite."));
                if (r is null) return false;
                if (!int.TryParse(r[0], out var lleva) || !int.TryParse(r[1], out var paga))
                {
                    Mensajes.Error(this, "Escriba con números cuántas lleva y cuántas paga.");
                    return false;
                }
                await Sesion.Api.PostAsync($"api/productos/{p.Id}/ofertas",
                    new OfertaRequest(TiposOferta.NxM, null, lleva, paga, Fecha(r[2]), Fecha(r[3])));
                return true;
            }, RequiereSeleccion: false, Estilo: "BotonVerde"));
        }

        acciones.Add(new("➕  Precio de mayoreo", async _ =>
        {
            var r = CampoDialog.MostrarCon(this, "Precio de mayoreo", $"{p.Nombre} · precio normal: {p.PrecioTexto}", "Guardar",
                new Campo($"A partir de cuántas {unidad}", Numero: true, Minimo: 0.001m, Ayuda: $"Ejemplo: 12 → de 12 {unidad} en adelante."),
                new Campo($"Precio de mayoreo ({porUnidad})", Numero: true, Minimo: 0.001m));
            if (r is null) return false;
            await Sesion.Api.PostAsync($"api/productos/{p.Id}/ofertas",
                new OfertaRequest(TiposOferta.Mayoreo, decimal.Parse(r[1]), decimal.Parse(r[0]), null, null, null));
            return true;
        }, RequiereSeleccion: false, Estilo: "BotonVerde"));

        acciones.Add(new("🗑  Quitar", async sel =>
        {
            var o = (Oferta)sel!;
            if (!Mensajes.Confirmar(this, $"¿Quitar la promoción \"{o.Descripcion}\" de {p.Nombre}?", "Sí, quitar", "No"))
                return false;
            await Sesion.Api.DeleteAsync($"api/ofertas/{o.Id}");
            return true;
        }, Estilo: "BotonRojo"));

        new TablaWindow($"Ofertas: {p.Nombre}",
            $"Precio normal: {p.PrecioTexto}. Si hay varias promociones, al cobrar se aplica solo la que más le conviene al cliente (no se juntan).",
            [
                new Columna("Promoción", nameof(Oferta.Descripcion)),
                new Columna("Cuándo", nameof(Oferta.VigenciaTexto), Ancho: 280),
                new Columna("Estado", nameof(Oferta.EstadoTexto), Ancho: 200)
            ],
            async () => (IEnumerable)(await Sesion.Api.GetAsync<List<Oferta>>($"api/productos/{p.Id}/ofertas") ?? []),
            [.. acciones])
        { Owner = this }.ShowDialog();

        await RecargarAsync();
        Seleccionar(p.Id);
    }

    private static DateTime? Fecha(string texto) => DateTime.TryParse(texto, out var f) ? f : null;

    // ------------------------------------------------------------------ Departamentos

    private async Task DepartamentosAsync()
    {
        new TablaWindow("Departamentos", "Sirven para agrupar los productos. Ejemplo: Abarrotes, Lácteos, Limpieza, Carnes frías.",
            [new Columna("Nombre del departamento", nameof(Departamento.Nombre))],
            async () => (IEnumerable)(await Sesion.Api.GetAsync<List<Departamento>>("api/departamentos") ?? []),
            new AccionTabla("➕  Nuevo departamento", async _ =>
            {
                var r = CampoDialog.MostrarCon(this, "Nuevo departamento", null, "Guardar",
                    new Campo("Nombre", Ayuda: "Ejemplo: Abarrotes"));
                if (r is null || r[0].Length == 0) return false;
                await Sesion.Api.PostAsync("api/departamentos", new DepartamentoRequest(r[0]));
                return true;
            }, RequiereSeleccion: false, Estilo: "BotonVerde"),
            new AccionTabla("✏  Cambiar nombre", async sel =>
            {
                var d = (Departamento)sel!;
                var r = CampoDialog.MostrarCon(this, "Cambiar nombre", null, "Guardar", new Campo("Nombre", d.Nombre));
                if (r is null || r[0].Length == 0) return false;
                await Sesion.Api.PutAsync($"api/departamentos/{d.Id}", new DepartamentoRequest(r[0]));
                return true;
            }),
            new AccionTabla("🗑  Quitar", async sel =>
            {
                var d = (Departamento)sel!;
                var cuantos = _productos.Count(p => p.DepartamentoId == d.Id);
                var aviso = cuantos == 0
                    ? $"¿Quitar el departamento \"{d.Nombre}\"?"
                    : $"¿Quitar el departamento \"{d.Nombre}\"?\n\nSus {cuantos} productos NO se borran: solo quedan sin departamento.";
                if (!Mensajes.Confirmar(this, aviso, "Sí, quitar", "No")) return false;
                await Sesion.Api.DeleteAsync($"api/departamentos/{d.Id}");
                return true;
            }, Estilo: "BotonRojo"))
        { Owner = this }.ShowDialog();

        await RecargarAsync();
    }

    // ------------------------------------------------------------------ Eventos

    private void Buscar_TextChanged(object sender, TextChangedEventArgs e) => Filtrar();
    private void Filtro_Changed(object sender, RoutedEventArgs e) => Filtrar();

    /// <summary>Enter en la búsqueda: si pasó un código que no existe, ofrece darlo de alta.</summary>
    private async void Buscar_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
            return;
        e.Handled = true;
        var texto = txtBuscar.Text.Trim();
        if (grid.Items.Count > 0)
        {
            await Ejecutar(EditarAsync);
        }
        else if (texto.Length > 0 && texto.All(char.IsAsciiDigit)
                 && Mensajes.Confirmar(this, $"No existe ningún producto con el código {texto}.\n\n¿Quiere darlo de alta ahora?", "Sí, darlo de alta", "No"))
        {
            await Ejecutar(() => NuevoAsync(texto));
        }
    }

    private async void Nuevo_Click(object sender, RoutedEventArgs e) => await Ejecutar(() => NuevoAsync());
    private async void Editar_Click(object sender, RoutedEventArgs e) => await Ejecutar(EditarAsync);
    private async void Quitar_Click(object sender, RoutedEventArgs e) => await Ejecutar(QuitarAsync);
    private async void Ofertas_Click(object sender, RoutedEventArgs e) => await Ejecutar(OfertasAsync);
    private async void LlegoMercancia_Click(object sender, RoutedEventArgs e) => await Ejecutar(LlegoMercanciaAsync);
    private async void SeEchoAPerder_Click(object sender, RoutedEventArgs e) => await Ejecutar(SeEchoAPerderAsync);
    private async void Contar_Click(object sender, RoutedEventArgs e) => await Ejecutar(ContarAsync);
    private void Historial_Click(object sender, RoutedEventArgs e) => Historial();
    private void Precios_Click(object sender, RoutedEventArgs e) => Precios();
    private async void Departamentos_Click(object sender, RoutedEventArgs e) => await Ejecutar(DepartamentosAsync);

    private void Ventana_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        // Flechas desde la búsqueda para moverse en la lista
        if (txtBuscar.IsKeyboardFocused && e.Key is Key.Down or Key.Up && grid.Items.Count > 0)
        {
            e.Handled = true;
            grid.SelectedIndex = Math.Clamp(grid.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, grid.Items.Count - 1);
            grid.ScrollIntoView(grid.SelectedItem);
        }
    }

    private async Task Ejecutar(Func<Task> accion)
    {
        try
        {
            await accion();
        }
        catch (ApiException ex)
        {
            Mensajes.Error(this, ex.Message);
        }
    }
}
