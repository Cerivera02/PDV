using System.Globalization;

namespace PDV.Core;

public sealed record LecturaPeso(decimal Kilogramos, bool Estable, string Crudo);

/// <summary>Abstracción de cualquier báscula (serial, USB-COM, simulada…).</summary>
public interface IBascula : IDisposable
{
    Task<LecturaPeso> LeerPesoAsync(CancellationToken ct = default);
}

/// <summary>
/// Formato de las etiquetas que imprimen las básculas etiquetadoras (EAN-13 con prefijo 2).
/// Por defecto: 2 X PPPPP VVVVV C → prefijo "2", PLU en la posición 2 (5 dígitos),
/// valor en la posición 7 (5 dígitos) y dígito verificador al final.
/// </summary>
public sealed class ConfigEtiquetaBascula
{
    public bool Habilitada { get; set; } = true;
    public string Prefijo { get; set; } = "2";
    public int InicioPlu { get; set; } = 2;
    public int LongitudPlu { get; set; } = 5;
    public int InicioValor { get; set; } = 7;
    public int LongitudValor { get; set; } = 5;
    /// <summary>true = el valor es peso; false = el valor es importe en dinero.</summary>
    public bool ValorEsPeso { get; set; } = true;
    /// <summary>Decimales implícitos del valor (3 para kg con gramos, 2 para pesos con centavos).</summary>
    public int DecimalesValor { get; set; } = 3;
}

public static class EtiquetaBascula
{
    public static bool TryParse(string codigo, ConfigEtiquetaBascula cfg, out string plu, out decimal valor)
    {
        plu = "";
        valor = 0;
        if (!cfg.Habilitada || codigo.Length != 13 || !codigo.All(char.IsAsciiDigit) || !codigo.StartsWith(cfg.Prefijo))
            return false;
        if (!DigitoVerificadorEan13Valido(codigo))
            return false;
        if (cfg.InicioPlu + cfg.LongitudPlu > 12 || cfg.InicioValor + cfg.LongitudValor > 12)
            return false;

        plu = NormalizarPlu(codigo.Substring(cfg.InicioPlu, cfg.LongitudPlu));
        var bruto = long.Parse(codigo.AsSpan(cfg.InicioValor, cfg.LongitudValor), CultureInfo.InvariantCulture);
        valor = bruto / (decimal)Math.Pow(10, cfg.DecimalesValor);
        return valor > 0;
    }

    /// <summary>Quita ceros a la izquierda para comparar PLUs ("00123" == "123").</summary>
    public static string NormalizarPlu(string plu)
    {
        var limpio = plu.Trim().TrimStart('0');
        return limpio.Length == 0 ? "0" : limpio;
    }

    public static bool DigitoVerificadorEan13Valido(string codigo)
    {
        var suma = 0;
        for (var i = 0; i < 12; i++)
            suma += (codigo[i] - '0') * (i % 2 == 0 ? 1 : 3);
        return (10 - suma % 10) % 10 == codigo[12] - '0';
    }
}
