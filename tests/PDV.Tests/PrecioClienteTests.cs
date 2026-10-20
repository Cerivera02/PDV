using PDV.Core;

namespace PDV.Tests;

public class PrecioClienteTests
{
    private static readonly DateTime Hoy = new(2026, 11, 10);

    private static Producto Coca(params Oferta[] ofertas) => new() { Id = 1, Nombre = "Coca", PrecioVenta = 18.50m, Ofertas = [.. ofertas] };
    private static Oferta Mayoreo(decimal desde, decimal precio) => new() { Tipo = TiposOferta.Mayoreo, Cantidad = desde, Precio = precio };
    private static Oferta TresPorDos() => new() { Tipo = TiposOferta.NxM, Cantidad = 3, Paga = 2 };

    [Fact]
    public void Descuento_porcentaje_en_todo()
    {
        var r = CalculadoraPrecios.Calcular(Coca(), 2, Hoy, new Cliente { DescuentoPorcentaje = 10 });
        Assert.Equal(16.65m, r.PrecioUnitario);   // 18.50 − 10% = 16.65
        Assert.Equal(33.30m, r.Importe);
        Assert.Equal("Cliente -10%", r.Promocion);
    }

    [Fact]
    public void Mayoreo_siempre_usa_el_primer_nivel_aunque_lleve_una_pieza()
    {
        var p = Coca(Mayoreo(24, 14.50m), Mayoreo(12, 16m));
        var r = CalculadoraPrecios.Calcular(p, 1, Hoy, new Cliente { PrecioMayoreo = true });
        Assert.Equal(16m, r.PrecioUnitario);      // primer nivel (desde 12), no el de 24
        Assert.Equal("Mayoreo (cliente)", r.Promocion);
    }

    [Fact]
    public void Mayoreo_siempre_sin_mayoreo_en_el_producto_no_cambia_nada()
    {
        var r = CalculadoraPrecios.Calcular(Coca(), 3, Hoy, new Cliente { PrecioMayoreo = true });
        Assert.Equal(55.50m, r.Importe);
        Assert.Null(r.Promocion);
    }

    [Fact]
    public void Precio_de_cliente_y_promocion_no_se_juntan_gana_la_mejor()
    {
        var cliente = new Cliente { DescuentoPorcentaje = 10 };
        // 3 Cocas: 3x2 = 37.00 contra 10% = 49.95 → gana el 3x2
        Assert.Equal(37.00m, CalculadoraPrecios.Calcular(Coca(TresPorDos()), 3, Hoy, cliente).Importe);
        // 2 Cocas: el 3x2 no aplica → gana el 10%
        Assert.Equal(33.30m, CalculadoraPrecios.Calcular(Coca(TresPorDos()), 2, Hoy, cliente).Importe);
    }

    [Fact]
    public void Sin_cliente_no_hay_precio_especial()
    {
        Assert.Equal(37.00m, CalculadoraPrecios.Calcular(Coca(), 2, Hoy).Importe);
    }
}
