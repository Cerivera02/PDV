using System.Text;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Tests;

public class TicketEscPosTests
{
    private static readonly ConfigImpresora Cfg = new() { Ancho = 32, CodePage = 850, TablaEscPos = 2 };

    static TicketEscPosTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static TicketDto Ticket() => new()
    {
        Folio = "C1-000001",
        Fecha = new DateTime(2026, 10, 16, 18, 30, 0, DateTimeKind.Utc),
        Caja = "Caja maestra",
        Cajero = "Juan",
        Estado = EstadosVenta.Pagada,
        Subtotal = 160.27m,
        Impuestos = 7.66m,
        Total = 167.93m,
        Pagado = 200m,
        Cambio = 32.07m,
        Negocio = new NegocioDto { Nombre = "ABARROTES LA ÑORA", MensajeTicket = "Gracias" },
        Lineas =
        [
            new() { Descripcion = "Refresco 600ml", Unidad = "PZA", Cantidad = 3, PrecioUnitario = 18.50m, Importe = 55.50m },
            new() { Descripcion = "Jamón de pavo", Unidad = "KG", Cantidad = 0.75m, PrecioUnitario = 149.90m, Importe = 112.43m }
        ],
        Pagos = [new() { FormaPago = FormasPago.Efectivo, Monto = 167.93m }]
    };

    [Fact]
    public void Ticket_inicia_con_reset_y_termina_con_corte()
    {
        var bytes = TicketEscPos.Venta(Cfg, Ticket());
        Assert.Equal(new byte[] { 0x1B, 0x40 }, bytes[..2]);
        Assert.Equal(new byte[] { 0x1D, 0x56, 0x42, 0x00 }, bytes[^4..]);
    }

    [Fact]
    public void Ticket_contiene_datos_y_acentos_en_cp850()
    {
        var texto = Encoding.GetEncoding(850).GetString(TicketEscPos.Venta(Cfg, Ticket()));
        Assert.Contains("C1-000001", texto);
        Assert.Contains("Jamón de pavo", texto);
        Assert.Contains("ÑORA", texto);
        Assert.Contains("$167.93", texto);
        Assert.Contains("CAMBIO", texto);
        // El efectivo mostrado es lo recibido (aplicado + cambio)
        Assert.Contains("$200.00", texto);
    }

    [Fact]
    public void Ticket_muestra_la_promocion_y_el_ahorro()
    {
        var t = Ticket();
        t.Lineas[0] = new TicketLineaDto { Descripcion = "Refresco 600ml", Unidad = "PZA", Cantidad = 3, PrecioUnitario = 18.50m, Importe = 37.00m, Descuento = 18.50m, Promocion = "3x2" };
        var texto = Encoding.GetEncoding(850).GetString(TicketEscPos.Venta(Cfg, t));
        Assert.Contains("$55.50", texto);   // precio normal de las 3
        Assert.Contains("* 3x2", texto);
        Assert.Contains("-$18.50", texto);
        Assert.Contains("USTED AHORRÓ: $18.50", texto);
    }

    [Fact]
    public void Venta_a_credito_muestra_cliente_y_lo_que_debe()
    {
        var t = Ticket();
        t.Cliente = "Juan Pérez";
        t.SaldoCliente = 467.93m;
        t.Pagos = [new() { FormaPago = FormasPago.Credito, Monto = 167.93m }];
        t.Cambio = 0;
        var texto = Encoding.GetEncoding(850).GetString(TicketEscPos.Venta(Cfg, t));
        Assert.Contains("Cliente: Juan Pérez", texto);
        Assert.Contains("CRÉDITO", texto);
        Assert.Contains("DEBE EN TOTAL", texto);
        Assert.Contains("$467.93", texto);
    }

    [Fact]
    public void Comprobante_de_abono_y_cuenta_liquidada()
    {
        var a = new ComprobanteAbono { Cliente = "Juan", Caja = "Caja 1", Cajero = "María", SaldoAnterior = 200, Monto = 200, FormaPago = FormasPago.Efectivo, SaldoNuevo = 0 };
        var texto = Encoding.GetEncoding(850).GetString(TicketEscPos.Abono(Cfg, a));
        Assert.Contains("COMPROBANTE DE ABONO", texto);
        Assert.Contains("-$200.00", texto);
        Assert.Contains("LIQUIDADA", texto);
    }

    [Fact]
    public void Sin_promociones_no_dice_ahorro()
    {
        var texto = Encoding.GetEncoding(850).GetString(TicketEscPos.Venta(Cfg, Ticket()));
        Assert.DoesNotContain("AHORR", texto);
    }

    [Fact]
    public void Ningun_renglon_excede_el_ancho()
    {
        var texto = Encoding.GetEncoding(850).GetString(TicketEscPos.Venta(Cfg, Ticket()));
        foreach (var renglon in texto.Split('\n'))
        {
            var visible = new string(renglon.Where(c => c >= ' ').ToArray());
            Assert.True(visible.Length <= Cfg.Ancho + 4, $"Renglón demasiado largo: \"{visible}\"");
        }
    }
}
