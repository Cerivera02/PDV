using PDV.Core;

namespace PDV.Tests;

public class CalculadoraPreciosTests
{
    private static readonly DateTime Hoy = new(2026, 11, 10);

    private static Producto Coca(params Oferta[] ofertas) => new()
    {
        Id = 1, Nombre = "Coca-Cola 600 ml", PrecioVenta = 18.50m, Ofertas = [.. ofertas]
    };

    private static Producto Jamon(params Oferta[] ofertas) => new()
    {
        Id = 2, Nombre = "Jamón", PrecioVenta = 149.90m, PorPeso = true, Ofertas = [.. ofertas]
    };

    private static Oferta PrecioOferta(decimal precio, DateTime? desde = null, DateTime? hasta = null) =>
        new() { Tipo = TiposOferta.Precio, Precio = precio, Desde = desde, Hasta = hasta };

    private static Oferta TresPorDos() => new() { Tipo = TiposOferta.NxM, Cantidad = 3, Paga = 2 };

    private static Oferta Mayoreo(decimal desde, decimal precio) => new() { Tipo = TiposOferta.Mayoreo, Cantidad = desde, Precio = precio };

    [Fact]
    public void Sin_ofertas_cobra_precio_normal()
    {
        var r = CalculadoraPrecios.Calcular(Coca(), 3, Hoy);
        Assert.Equal(55.50m, r.Importe);
        Assert.Equal(0, r.Descuento);
        Assert.Null(r.Promocion);
    }

    [Theory]
    [InlineData("2026-11-01", 45.00)] // primer día: aplica
    [InlineData("2026-11-15", 45.00)] // último día: aplica
    [InlineData("2026-10-31", 55.50)] // antes: no aplica
    [InlineData("2026-11-16", 55.50)] // después: no aplica
    public void Precio_de_oferta_respeta_las_fechas(string dia, decimal esperado)
    {
        var p = Coca(PrecioOferta(15m, new DateTime(2026, 11, 1), new DateTime(2026, 11, 15)));
        Assert.Equal(esperado, CalculadoraPrecios.Calcular(p, 3, DateTime.Parse(dia)).Importe);
    }

    [Theory]
    [InlineData(2, 37.00)]  // no completa el 3x2
    [InlineData(3, 37.00)]  // paga 2
    [InlineData(4, 55.50)]  // paga 3
    [InlineData(6, 74.00)]  // paga 4
    [InlineData(7, 92.50)]  // paga 5
    public void Tres_por_dos(int piezas, decimal esperado)
    {
        var r = CalculadoraPrecios.Calcular(Coca(TresPorDos()), piezas, Hoy);
        Assert.Equal(esperado, r.Importe);
        if (piezas >= 3)
            Assert.Equal("3x2", r.Promocion);
    }

    [Fact]
    public void Mayoreo_aplica_a_partir_de_la_cantidad()
    {
        var p = Coca(Mayoreo(12, 16m));
        Assert.Equal(203.50m, CalculadoraPrecios.Calcular(p, 11, Hoy).Importe); // 11 × 18.50
        Assert.Equal(192.00m, CalculadoraPrecios.Calcular(p, 12, Hoy).Importe); // 12 × 16
    }

    [Fact]
    public void Se_aplica_solo_la_promocion_que_mas_conviene()
    {
        // Oferta $15 (3 = $45) contra 3x2 (3 = $37): gana el 3x2, y NO se juntan
        var p = Coca(PrecioOferta(15m), TresPorDos());
        var r = CalculadoraPrecios.Calcular(p, 3, Hoy);
        Assert.Equal(37.00m, r.Importe);
        Assert.Equal("3x2", r.Promocion);

        // Con 1 pieza el 3x2 no aplica: gana la oferta de $15
        Assert.Equal(15.00m, CalculadoraPrecios.Calcular(p, 1, Hoy).Importe);
    }

    [Fact]
    public void El_NxM_no_aplica_a_productos_por_kilo()
    {
        var p = Jamon(new Oferta { Tipo = TiposOferta.NxM, Cantidad = 2, Paga = 1 });
        Assert.Equal(449.70m, CalculadoraPrecios.Calcular(p, 3, Hoy).Importe);
    }

    [Fact]
    public void Mayoreo_por_kilo_suma_todos_los_renglones_del_producto()
    {
        // Dos pesadas de 3 kg = 6 kg → aplica mayoreo desde 5 kg a $120
        var p = Jamon(Mayoreo(5, 120m));
        var r = CalculadoraPrecios.AplicarPromociones([(p, 3m), (p, 3m)], Hoy);
        Assert.All(r, x => Assert.Equal(360.00m, x.Importe));
        Assert.All(r, x => Assert.Equal(89.70m, x.Descuento));
    }

    [Fact]
    public void Tres_por_dos_en_varios_renglones_nunca_deja_importes_negativos()
    {
        var p = Coca(TresPorDos());
        var r = CalculadoraPrecios.AplicarPromociones([(p, 1m), (p, 1m), (p, 1m)], Hoy);
        Assert.Equal(37.00m, r.Sum(x => x.Importe));
        Assert.All(r, x => Assert.True(x.Importe >= 0));
        Assert.Equal(18.50m, r.Sum(x => x.Descuento));
    }

    [Fact]
    public void Otros_productos_de_la_venta_no_se_afectan()
    {
        var coca = Coca(TresPorDos());
        var jamon = Jamon();
        var r = CalculadoraPrecios.AplicarPromociones([(coca, 3m), (jamon, 0.5m)], Hoy);
        Assert.Equal(37.00m, r[0].Importe);
        Assert.Equal(74.95m, r[1].Importe);
        Assert.Null(r[1].Promocion);
    }
}
