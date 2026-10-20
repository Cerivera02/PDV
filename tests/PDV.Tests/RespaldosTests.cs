using PDV.Core;

namespace PDV.Tests;

public class RespaldosTests
{
    private static readonly DateTimeOffset Ahora = new(2026, 10, 19, 21, 0, 0, TimeSpan.FromHours(-7));

    private static List<(string, DateTimeOffset)> UnoPorDia(int dias) =>
        Enumerable.Range(0, dias).Select(d => ($"r{d}", Ahora.AddDays(-d))).ToList();

    [Fact]
    public void Conserva_los_ultimos_30_dias()
    {
        var borrar = RetencionRespaldos.ParaBorrar(UnoPorDia(40), Ahora, dias: 30, minimo: 10);
        // r0..r30 tienen 30 días o menos; r31..r39 son más viejos
        Assert.Equal(Enumerable.Range(31, 9).Select(d => $"r{d}"), borrar);
    }

    [Fact]
    public void Siempre_conserva_los_mas_recientes_aunque_sean_viejos()
    {
        // La tienda estuvo cerrada: solo hay respaldos de hace meses
        var viejos = Enumerable.Range(0, 15).Select(i => ($"v{i}", Ahora.AddDays(-100 - i))).ToList();
        var borrar = RetencionRespaldos.ParaBorrar(viejos, Ahora, dias: 30, minimo: 10);
        Assert.Equal(5, borrar.Count);
        Assert.Equal(["v10", "v11", "v12", "v13", "v14"], borrar);
    }

    [Fact]
    public void No_pasa_del_maximo_de_archivos()
    {
        // Muchos cierres en un día: aunque sean recientes, no más de 300
        var muchos = Enumerable.Range(0, 320).Select(i => ($"m{i}", Ahora.AddMinutes(-i))).ToList();
        var borrar = RetencionRespaldos.ParaBorrar(muchos, Ahora, maximo: 300);
        Assert.Equal(20, borrar.Count);
        Assert.Contains("m319", borrar);
        Assert.DoesNotContain("m0", borrar);
    }

    [Fact]
    public void Sin_respaldos_no_borra_nada() =>
        Assert.Empty(RetencionRespaldos.ParaBorrar([], Ahora));

    [Theory]
    [InlineData(500, "500 bytes")]
    [InlineData(2048, "2 KB")]
    [InlineData(5 * 1024 * 1024 + 300 * 1024, "5.3 MB")]
    public void Tamano_en_palabras(long bytes, string esperado) => Assert.Equal(esperado, RespaldoDto.Tamano(bytes));

    [Theory]
    [InlineData(0.5, "hace un momento")]
    [InlineData(5, "hace 5 minutos")]
    [InlineData(90, "hace 1 hora")]
    [InlineData(60 * 5, "hace 5 horas")]
    [InlineData(60 * 30, "ayer")]
    [InlineData(60 * 24 * 4, "hace 4 días")]
    public void Hace_cuanto(double minutos, string esperado) => Assert.Equal(esperado, RespaldoDto.Hace(TimeSpan.FromMinutes(minutos)));
}
