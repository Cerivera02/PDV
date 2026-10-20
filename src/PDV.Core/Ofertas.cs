using System.Globalization;

namespace PDV.Core;

public static class TiposOferta
{
    /// <summary>Precio especial por fechas. Ej.: Coca-Cola a $15 del 1 al 15 de noviembre.</summary>
    public const string Precio = "PRECIO";
    /// <summary>Lleva N, paga M. Ej.: 3x2. Solo productos por pieza.</summary>
    public const string NxM = "NXM";
    /// <summary>Precio por cantidad. Ej.: de 12 piezas en adelante, $16 cada una.</summary>
    public const string Mayoreo = "MAYOREO";
    /// <summary>No se guarda en la BD: precio especial del cliente (descuento % o mayoreo siempre).</summary>
    public const string Cliente = "CLIENTE";
    /// <summary>No es una oferta guardada: el precio se cambió a mano en la venta.</summary>
    public const string Manual = "MANUAL";
    public const string TextoManual = "Precio cambiado";
}

/// <summary>Promoción de un producto. Los campos que se usan dependen del tipo.</summary>
public sealed class Oferta
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    public int Id { get; set; }
    public int ProductoId { get; set; }
    public string Tipo { get; set; } = "";
    /// <summary>PRECIO: precio de oferta. MAYOREO: precio por unidad desde <see cref="Cantidad"/>.</summary>
    public decimal? Precio { get; set; }
    /// <summary>NXM: cuántas lleva. MAYOREO: a partir de cuántas.</summary>
    public decimal? Cantidad { get; set; }
    /// <summary>NXM: cuántas paga.</summary>
    public int? Paga { get; set; }
    /// <summary>Primer día de la promoción (incluido). null = desde ya.</summary>
    public DateTime? Desde { get; set; }
    /// <summary>Último día de la promoción (incluido). null = sin fin.</summary>
    public DateTime? Hasta { get; set; }

    public bool VigenteEn(DateTime dia) =>
        (Desde is null || dia.Date >= Desde.Value.Date) && (Hasta is null || dia.Date <= Hasta.Value.Date);

    // ---- Textos para mostrar en pantalla y en el ticket
    [System.Text.Json.Serialization.JsonIgnore]
    public string Descripcion => Tipo switch
    {
        TiposOferta.Precio => $"Oferta {Precio?.ToString("C2", Mx)}",
        TiposOferta.NxM => $"{Cantidad:0}x{Paga}",
        TiposOferta.Mayoreo => $"Mayoreo: {Precio?.ToString("C2", Mx)} desde {Cantidad?.ToString("0.###", Mx)}",
        _ => Tipo
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string VigenciaTexto => (Desde, Hasta) switch
    {
        (null, null) => "Siempre",
        (null, { } h) => $"Hasta el {h:dd/MM/yyyy}",
        ({ } d, null) => $"Desde el {d:dd/MM/yyyy}",
        ({ } d, { } h) => $"Del {d:dd/MM/yyyy} al {h:dd/MM/yyyy}"
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string EstadoTexto =>
        VigenteEn(DateTime.Today) ? "✔ Aplica hoy" : Desde > DateTime.Today ? "Todavía no empieza" : "Ya terminó";
}

public sealed record OfertaRequest(string Tipo, decimal? Precio, decimal? Cantidad, int? Paga, DateTime? Desde, DateTime? Hasta);

/// <summary>Resultado de cobrar una cantidad de un producto: importe final, precio usado y ahorro.</summary>
public sealed record PrecioCalculado(decimal Importe, decimal PrecioUnitario, decimal Descuento, string? Promocion, string? TipoOferta);

/// <summary>
/// Calcula precios con promociones. La USAN IGUAL la caja (para mostrar) y el servidor (para cobrar),
/// así el total que ve el cajero es el mismo que se registra.
/// Regla: se aplica UNA sola promoción por producto, la que más le conviene al cliente (no se acumulan).
/// </summary>
public static class CalculadoraPrecios
{
    /// <summary>Importe de <paramref name="cantidad"/> unidades de un producto con la mejor promoción vigente.</summary>
    /// <param name="cliente">Si se indica, su precio especial compite con las promociones (gana lo que más le convenga).</param>
    public static PrecioCalculado Calcular(Producto p, decimal cantidad, DateTime dia, Cliente? cliente = null)
    {
        var normal = Dinero.Redondear(cantidad * p.PrecioVenta);
        var mejor = new PrecioCalculado(normal, p.PrecioVenta, 0, null, null);
        if (cantidad <= 0)
            return mejor;

        foreach (var o in p.Ofertas.Where(o => o.VigenteEn(dia)))
        {
            PrecioCalculado? opcion = o.Tipo switch
            {
                TiposOferta.Precio when o.Precio is { } precio
                    => PorPrecio(precio),
                TiposOferta.Mayoreo when o.Precio is { } precio && o.Cantidad is { } desde && cantidad >= desde
                    => PorPrecio(precio),
                TiposOferta.NxM when !p.PorPeso && o.Cantidad is { } lleva && o.Paga is { } paga && paga < lleva && cantidad >= lleva
                    => NxM(lleva, paga),
                _ => null
            };
            if (opcion is not null && opcion.Importe < mejor.Importe)
                mejor = opcion;

            PrecioCalculado PorPrecio(decimal precio)
            {
                var importe = Dinero.Redondear(cantidad * precio);
                return new PrecioCalculado(importe, precio, normal - importe, o.Descripcion, o.Tipo);
            }

            PrecioCalculado NxM(decimal lleva, int paga)
            {
                var gratis = decimal.Floor(cantidad / lleva) * (lleva - paga);
                var importe = Dinero.Redondear((cantidad - gratis) * p.PrecioVenta);
                return new PrecioCalculado(importe, p.PrecioVenta, normal - importe, o.Descripcion, o.Tipo);
            }
        }

        // ---- Precio especial del cliente (compite con las promociones; no se suma)
        if (cliente is not null)
        {
            if (cliente.DescuentoPorcentaje > 0)
            {
                var precio = Dinero.Redondear(p.PrecioVenta * (1 - cliente.DescuentoPorcentaje / 100m));
                Considerar(precio, $"Cliente -{cliente.DescuentoPorcentaje:0.##}%");
            }
            // "Siempre mayoreo": el primer nivel de mayoreo del producto, sin importar la cantidad
            if (cliente.PrecioMayoreo
                && p.Ofertas.Where(o => o.Tipo == TiposOferta.Mayoreo && o.VigenteEn(dia) && o.Precio is not null)
                            .OrderBy(o => o.Cantidad).FirstOrDefault() is { Precio: { } mayoreo })
            {
                Considerar(mayoreo, "Mayoreo (cliente)");
            }

            void Considerar(decimal precio, string texto)
            {
                var importe = Dinero.Redondear(cantidad * precio);
                if (importe < mejor.Importe)
                    mejor = new PrecioCalculado(importe, precio, normal - importe, texto, TiposOferta.Cliente);
            }
        }
        return mejor;
    }

    /// <summary>
    /// Aplica promociones a todos los renglones de una venta. Las cantidades del mismo producto se SUMAN
    /// para decidir (ej. 2 renglones de 3 kg cuentan como 6 kg para el mayoreo).
    /// </summary>
    /// <returns>Un resultado por renglón, en el mismo orden.</returns>
    /// <summary>
    /// Igual que la otra versión, pero los renglones con precio cambiado a mano usan ese precio (sin promociones).
    /// El "descuento" es la diferencia con el precio normal (negativo si se subió).
    /// </summary>
    public static List<PrecioCalculado> AplicarPromociones(IReadOnlyList<(Producto Producto, decimal Cantidad, decimal? PrecioManual)> renglones,
        DateTime dia, Cliente? cliente = null)
    {
        var normales = renglones.Select((r, i) => (r, i)).Where(x => x.r.PrecioManual is null).ToList();
        var conPromocion = AplicarPromociones(normales.Select(x => (x.r.Producto, x.r.Cantidad)).ToList(), dia, cliente);
        var resultado = new PrecioCalculado[renglones.Count];
        for (var k = 0; k < normales.Count; k++)
            resultado[normales[k].i] = conPromocion[k];
        for (var i = 0; i < renglones.Count; i++)
        {
            if (renglones[i].PrecioManual is not { } manual)
                continue;
            var importe = Dinero.Redondear(renglones[i].Cantidad * manual);
            var normal = Dinero.Redondear(renglones[i].Cantidad * renglones[i].Producto.PrecioVenta);
            resultado[i] = new PrecioCalculado(importe, manual, normal - importe, TiposOferta.TextoManual, TiposOferta.Manual);
        }
        return [.. resultado];
    }

    public static List<PrecioCalculado> AplicarPromociones(IReadOnlyList<(Producto Producto, decimal Cantidad)> renglones, DateTime dia, Cliente? cliente = null)
    {
        var resultado = renglones
            .Select(r => new PrecioCalculado(Dinero.Redondear(r.Cantidad * r.Producto.PrecioVenta), r.Producto.PrecioVenta, 0, null, null))
            .ToList();

        foreach (var grupo in renglones.Select((r, i) => (r.Producto, r.Cantidad, Indice: i)).GroupBy(x => x.Producto.Id))
        {
            var producto = grupo.First().Producto;
            var total = grupo.Sum(x => x.Cantidad);
            var calc = Calcular(producto, total, dia, cliente);
            if (calc.Promocion is null)
                continue;

            if (calc.TipoOferta is TiposOferta.Precio or TiposOferta.Mayoreo or TiposOferta.Cliente)
            {
                // Precio por unidad: se aplica a cada renglón
                foreach (var x in grupo)
                {
                    var importe = Dinero.Redondear(x.Cantidad * calc.PrecioUnitario);
                    var normal = Dinero.Redondear(x.Cantidad * producto.PrecioVenta);
                    resultado[x.Indice] = new PrecioCalculado(importe, calc.PrecioUnitario, normal - importe, calc.Promocion, calc.TipoOferta);
                }
            }
            else
            {
                // NxM: el descuento se descuenta desde el último renglón hacia atrás (nunca deja un importe negativo)
                var pendiente = calc.Descuento;
                foreach (var x in grupo.Reverse())
                {
                    if (pendiente <= 0)
                        break;
                    var normal = resultado[x.Indice].Importe;
                    var aplicado = Math.Min(pendiente, normal);
                    resultado[x.Indice] = new PrecioCalculado(normal - aplicado, producto.PrecioVenta, aplicado, calc.Promocion, calc.TipoOferta);
                    pendiente -= aplicado;
                }
            }
        }
        return resultado;
    }
}
