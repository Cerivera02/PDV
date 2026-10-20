using System.Globalization;
using System.Text.RegularExpressions;

namespace PDV.Core;

public sealed partial class Cliente
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string? Telefono { get; set; }
    public string? Direccion { get; set; }

    // ---- Crédito
    /// <summary>Límite de crédito: hasta cuánto puede deber. 0 = sin crédito.</summary>
    public decimal LimiteCredito { get; set; }
    /// <summary>Lo que debe ahora.</summary>
    public decimal Saldo { get; set; }

    // ---- Precio especial (no se acumula con promociones: se aplica lo que más le convenga)
    /// <summary>Descuento en todo, en porcentaje (10 = 10%).</summary>
    public decimal DescuentoPorcentaje { get; set; }
    /// <summary>Siempre precio de mayoreo (primer nivel de mayoreo del producto), sin importar la cantidad.</summary>
    public bool PrecioMayoreo { get; set; }

    // ---- Datos para factura (por ahora solo se guardan)
    public string? Rfc { get; set; }
    public string? RazonSocial { get; set; }
    public string? CodigoPostalFiscal { get; set; }
    public string? RegimenFiscal { get; set; }
    public string? UsoCfdi { get; set; }
    public string? Correo { get; set; }

    public bool Activo { get; set; } = true;

    // ---- Textos para mostrar en pantalla
    [System.Text.Json.Serialization.JsonIgnore]
    public decimal CreditoDisponible => Math.Max(0, LimiteCredito - Saldo);

    [System.Text.Json.Serialization.JsonIgnore]
    public string SaldoTexto => Saldo > 0 ? Saldo.ToString("C2", Mx) : "—";

    [System.Text.Json.Serialization.JsonIgnore]
    public string CreditoTexto => LimiteCredito > 0 ? $"{CreditoDisponible.ToString("C2", Mx)} de {LimiteCredito.ToString("C2", Mx)}" : "Sin crédito";

    [System.Text.Json.Serialization.JsonIgnore]
    public string PrecioEspecialTexto => (DescuentoPorcentaje, PrecioMayoreo) switch
    {
        (> 0, true) => $"{DescuentoPorcentaje:0.##}% o mayoreo",
        (> 0, false) => $"{DescuentoPorcentaje:0.##}% en todo",
        (_, true) => "Precio de mayoreo",
        _ => ""
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public bool TieneDatosFactura => !string.IsNullOrWhiteSpace(Rfc);

    /// <summary>RFC de persona moral (12) o física (13), con homoclave.</summary>
    [GeneratedRegex(@"^[A-ZÑ&]{3,4}\d{6}[A-Z\d]{3}$")]
    public static partial Regex FormatoRfc();
}

public sealed class MovimientoCliente
{
    public long Id { get; set; }
    /// <summary>En UTC (así la guarda PostgreSQL). Para mostrar usar <see cref="FechaLocal"/>.</summary>
    public DateTime Fecha { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime FechaLocal => Fecha.ToLocalTime();

    /// <summary>CARGO (compra a crédito), ABONO (pagó), CANCELACION (se canceló una compra a crédito).</summary>
    public string Tipo { get; set; } = "";
    /// <summary>Con signo: + aumenta lo que debe, − disminuye.</summary>
    public decimal Monto { get; set; }
    public decimal SaldoResultante { get; set; }
    public string? Folio { get; set; }
    public string? FormaPago { get; set; }
    public string? Usuario { get; set; }
    public string? Nota { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string TipoTexto => Tipo switch
    {
        TiposMovimientoCliente.Cargo => "Compra a crédito",
        TiposMovimientoCliente.Abono => $"Abonó ({FormaPago?.ToLowerInvariant()})",
        TiposMovimientoCliente.Cancelacion => "Se canceló una venta",
        _ => Tipo
    };

    [System.Text.Json.Serialization.JsonIgnore]
    public string Detalle => string.Join(" · ", new[] { Folio, Nota }.Where(s => !string.IsNullOrWhiteSpace(s)));
}

public static class TiposMovimientoCliente
{
    public const string Cargo = "CARGO";
    public const string Abono = "ABONO";
    public const string Cancelacion = "CANCELACION";
}

public sealed record AbonoRequest(long TurnoId, decimal Monto, string FormaPago, string? Referencia);

/// <summary>Comprobante que se imprime al recibir un abono.</summary>
public sealed class ComprobanteAbono
{
    public string Cliente { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string Caja { get; set; } = "";
    public string Cajero { get; set; } = "";
    public decimal SaldoAnterior { get; set; }
    public decimal Monto { get; set; }
    public string FormaPago { get; set; } = "";
    public decimal SaldoNuevo { get; set; }
    public NegocioDto Negocio { get; set; } = new();
}

/// <summary>Catálogos del SAT en palabras (para elegir en pantalla).</summary>
public static class CatalogosSat
{
    public static readonly IReadOnlyList<string> Regimenes =
    [
        "601 - General de Ley Personas Morales",
        "603 - Personas Morales con Fines no Lucrativos",
        "605 - Sueldos y Salarios",
        "606 - Arrendamiento",
        "612 - Personas Físicas con Actividades Empresariales y Profesionales",
        "616 - Sin obligaciones fiscales",
        "621 - Incorporación Fiscal",
        "625 - Actividades Empresariales por Plataformas Tecnológicas",
        "626 - Régimen Simplificado de Confianza (RESICO)"
    ];

    public static readonly IReadOnlyList<string> UsosCfdi =
    [
        "G01 - Adquisición de mercancías",
        "G03 - Gastos en general",
        "S01 - Sin efectos fiscales",
        "CP01 - Pagos"
    ];

    /// <summary>"612 - Personas Físicas…" → "612".</summary>
    public static string? Clave(string? opcion) =>
        string.IsNullOrWhiteSpace(opcion) ? null : opcion.Split(" - ")[0].Trim();

    /// <summary>"612" → "612 - Personas Físicas…" (para mostrar el valor guardado).</summary>
    public static string Opcion(IReadOnlyList<string> catalogo, string? clave) =>
        catalogo.FirstOrDefault(o => o.StartsWith(clave + " ")) ?? clave ?? "";
}
