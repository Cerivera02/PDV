using System.Globalization;
using System.Text.Json.Serialization;

namespace PDV.Core;

/// <summary>Renglón del historial de tickets (sin el detalle; el ticket completo se pide aparte).</summary>
public sealed class VentaResumen
{
    public long VentaId { get; set; }
    public string Folio { get; set; } = "";
    /// <summary>En UTC (así la guarda PostgreSQL). Para mostrar usar <see cref="FechaLocal"/>.</summary>
    public DateTime Fecha { get; set; }
    public int CajaId { get; set; }
    public string Caja { get; set; } = "";
    public string Cajero { get; set; } = "";
    public string? Cliente { get; set; }
    public decimal Total { get; set; }
    public string Estado { get; set; } = "";
    /// <summary>Formas de pago separadas por coma, ej. "EFECTIVO,TARJETA".</summary>
    public string? FormasPago { get; set; }
    /// <summary>Número de renglones (productos distintos) del ticket.</summary>
    public int Renglones { get; set; }

    [JsonIgnore] public DateTime FechaLocal => Fecha.ToLocalTime();
    [JsonIgnore] public bool Cancelada => Estado == EstadosVenta.Cancelada;
    [JsonIgnore] public string EstadoTexto => Cancelada ? "✖ Cancelada" : "✔ Cobrada";
    [JsonIgnore] public string ClienteTexto => string.IsNullOrWhiteSpace(Cliente) ? "Público en general" : Cliente;
    [JsonIgnore]
    public string FormasPagoTexto => string.IsNullOrWhiteSpace(FormasPago)
        ? ""
        : string.Join(" + ", FormasPago.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(PDV.Core.FormasPago.Texto));
}

/// <summary>Dinero que se sacó de la caja durante un turno.</summary>
public sealed class RetiroDto
{
    public DateTime Fecha { get; set; }
    public decimal Monto { get; set; }
    public string Motivo { get; set; } = "";
    public string Usuario { get; set; } = "";
    /// <summary>Quién lo autorizó, si no fue la misma persona que lo sacó (vacío si lo sacó con su propio permiso).</summary>
    public string? Autorizo { get; set; }
}

public static class OrigenesPrecio
{
    /// <summary>El precio que tenía el producto cuando se empezó a guardar el historial.</summary>
    public const string Inicial = "INICIAL";
    public const string Alta = "ALTA";
    public const string Cambio = "CAMBIO";
    /// <summary>El costo cambió al registrar "Llegó mercancía".</summary>
    public const string Mercancia = "MERCANCIA";

    public static string Descripcion(string origen) => origen switch
    {
        Inicial => "Precio que ya tenía",
        Alta => "Se dio de alta",
        Cambio => "Cambio de precio",
        Mercancia => "Llegó mercancía (costo)",
        _ => origen
    };
}

/// <summary>Un cambio en el precio de venta o en el costo de un producto.</summary>
public sealed class CambioPrecio
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    public long Id { get; set; }
    public DateTime Fecha { get; set; }
    public decimal PrecioVenta { get; set; }
    public decimal PrecioCosto { get; set; }
    public decimal? PrecioVentaAnterior { get; set; }
    public decimal? PrecioCostoAnterior { get; set; }
    public string Origen { get; set; } = "";
    public string? Usuario { get; set; }

    [JsonIgnore] public DateTime FechaLocal => Fecha.ToLocalTime();
    [JsonIgnore] public string OrigenTexto => OrigenesPrecio.Descripcion(Origen);
    [JsonIgnore] public string VentaTexto => Texto(PrecioVentaAnterior, PrecioVenta);
    [JsonIgnore] public string CostoTexto => Texto(PrecioCostoAnterior, PrecioCosto);

    /// <summary>"$20.00 → $22.00  (+10%)", o solo "$22.00" si no cambió o no había precio anterior.</summary>
    public static string Texto(decimal? antes, decimal ahora)
    {
        if (antes is not { } a || a == ahora)
            return ahora.ToString("C2", Mx);
        var texto = $"{a.ToString("C2", Mx)} → {ahora.ToString("C2", Mx)}";
        if (a > 0)
        {
            var porcentaje = Math.Round((ahora - a) / a * 100, 1, MidpointRounding.AwayFromZero);
            texto += $"  ({(porcentaje > 0 ? "+" : "")}{porcentaje.ToString("0.#", Mx)}%)";
        }
        return texto;
    }
}
