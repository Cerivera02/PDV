using PDV.Core;

namespace PDV.Tests;

public class EtiquetaBasculaTests
{
    private readonly ConfigEtiquetaBascula _cfg = new();

    [Fact]
    public void Lee_plu_y_peso_de_etiqueta_valida()
    {
        // 2 0 00123 00750 2  → PLU 123, 0.750 kg
        Assert.True(EtiquetaBascula.TryParse("2000123007502", _cfg, out var plu, out var valor));
        Assert.Equal("123", plu);
        Assert.Equal(0.750m, valor);
    }

    [Fact]
    public void Rechaza_digito_verificador_incorrecto()
    {
        Assert.False(EtiquetaBascula.TryParse("2000123007509", _cfg, out _, out _));
    }

    [Theory]
    [InlineData("7501234567890")] // código comercial normal (no empieza con 2)
    [InlineData("200012300750")]  // 12 dígitos
    [InlineData("20001230075AB")]
    public void Ignora_codigos_que_no_son_etiqueta(string codigo)
    {
        Assert.False(EtiquetaBascula.TryParse(codigo, _cfg, out _, out _));
    }

    [Fact]
    public void Etiqueta_con_importe_en_centavos()
    {
        var cfg = new ConfigEtiquetaBascula { ValorEsPeso = false, DecimalesValor = 2 };
        // valor 01250 → $12.50 ; verificador calculado para 200012301250
        var codigo = ConDigito("200012301250");
        Assert.True(EtiquetaBascula.TryParse(codigo, cfg, out _, out var importe));
        Assert.Equal(12.50m, importe);
    }

    [Theory]
    [InlineData("00123", "123")]
    [InlineData("123", "123")]
    [InlineData("0000", "0")]
    public void Normaliza_plu(string entrada, string esperado) => Assert.Equal(esperado, EtiquetaBascula.NormalizarPlu(entrada));

    private static string ConDigito(string doce)
    {
        var suma = 0;
        for (var i = 0; i < 12; i++)
            suma += (doce[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return doce + (10 - suma % 10) % 10;
    }
}
