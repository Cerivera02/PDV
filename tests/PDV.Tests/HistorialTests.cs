using System.Text;
using PDV.Caja.Ventanas;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Tests;

/// <summary>Vista previa de tickets y cortes, historial de precios y filtros del historial.</summary>
public class HistorialTests
{
    private static readonly ConfigImpresora Cfg = new() { Ancho = 32, CodePage = 850, TablaEscPos = 2 };

    static HistorialTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    private static TicketDto Ticket(string estado = EstadosVenta.Pagada) => new()
    {
        Folio = "C1-000007",
        Fecha = new DateTime(2026, 10, 19, 18, 30, 0, DateTimeKind.Utc),
        Caja = "Caja maestra",
        Cajero = "Ana",
        Estado = estado,
        Subtotal = 37m,
        Total = 37m,
        Pagado = 50m,
        Cambio = 13m,
        Negocio = new NegocioDto { Nombre = "ABARROTES LA ÑORA", MensajeTicket = "Gracias" },
        Lineas = [new() { Descripcion = "Coca 600ml", Unidad = "PZA", Cantidad = 2, PrecioUnitario = 18.50m, Importe = 37m }],
        Pagos = [new() { FormaPago = FormasPago.Efectivo, Monto = 37m }]
    };

    private static CorteDto Corte() => new()
    {
        TurnoId = 3, Caja = "Caja maestra", Cajero = "Ana", Estado = EstadosTurno.Cerrado,
        AbiertoEn = new DateTime(2026, 10, 19, 14, 0, 0, DateTimeKind.Utc),
        CerradoEn = new DateTime(2026, 10, 20, 2, 0, 0, DateTimeKind.Utc),
        NumVentas = 2, FondoInicial = 500, VentasEfectivo = 300, VentasTarjeta = 100, Retiros = 50, EfectivoDeclarado = 740,
        Negocio = new NegocioDto { Nombre = "ABARROTES" },
        DetalleRetiros = [new() { Fecha = new DateTime(2026, 10, 19, 20, 15, 0, DateTimeKind.Utc), Monto = 50, Motivo = "Bolsas", Usuario = "Ana", Autorizo = "Don Pepe" }]
    };

    // ------------------------------------------------------------------ Vista previa en pantalla

    [Fact]
    public void Vista_previa_del_ticket_es_lo_mismo_que_se_imprime_sin_comandos()
    {
        var texto = TicketEscPos.VentaTexto(Cfg, Ticket());
        var impreso = Encoding.GetEncoding(850).GetString(TicketEscPos.Venta(Cfg, Ticket()));

        Assert.DoesNotContain('\x1B', texto);
        Assert.DoesNotContain('\x1D', texto);
        foreach (var dato in new[] { "C1-000007", "Coca 600ml", "$37.00", "CAMBIO", "$13.00", "Gracias" })
        {
            Assert.Contains(dato, texto);
            Assert.Contains(dato, impreso);
        }
    }

    [Fact]
    public void Vista_previa_centra_el_encabezado_y_respeta_el_ancho()
    {
        var renglones = TicketEscPos.VentaTexto(Cfg, Ticket()).Split('\n');
        Assert.All(renglones, r => Assert.True(r.Length <= Cfg.Ancho, $"Renglón demasiado largo: \"{r}\""));
        // "ABARROTES LA ÑORA" (17 letras) centrado en 32 → empieza con espacios
        var nombre = renglones.First(r => r.Contains("ÑORA"));
        Assert.StartsWith("       ", nombre);
        // El TOTAL va en una sola línea con el ancho completo (en papel es letra doble)
        Assert.Contains(renglones, r => r.StartsWith("TOTAL") && r.EndsWith("$37.00") && r.Length == Cfg.Ancho);
    }

    [Fact]
    public void Vista_previa_de_venta_cancelada_lo_dice()
    {
        Assert.Contains("VENTA CANCELADA", TicketEscPos.VentaTexto(Cfg, Ticket(EstadosVenta.Cancelada)));
        Assert.DoesNotContain("VENTA CANCELADA", TicketEscPos.VentaTexto(Cfg, Ticket()));
    }

    [Fact]
    public void Corte_muestra_lo_que_se_saco_y_la_marca_de_reimpresion()
    {
        var texto = TicketEscPos.CorteTexto(Cfg, Corte(), reimpresion: true);
        Assert.Contains("Autorizó: Don Pepe", texto);
        Assert.Contains("CORTE DE CAJA (Z)", texto);
        Assert.Contains("REIMPRESION", texto);
        Assert.Contains("Dinero que se sacó", texto);
        Assert.Contains("Bolsas", texto);
        Assert.Contains("$750.00", texto); // esperado: 500 + 300 − 50
        Assert.DoesNotContain("REIMPRESION", TicketEscPos.CorteTexto(Cfg, Corte()));

        var impreso = TicketEscPos.Corte(Cfg, Corte());
        Assert.Equal(new byte[] { 0x1B, 0x40 }, impreso[..2]);
    }

    // ------------------------------------------------------------------ Corte: textos de la lista

    [Theory]
    [InlineData(750, "✔ Cuadra")]
    [InlineData(770, "Sobran $20.00")]
    [InlineData(735, "⚠ Faltan $15.00")]
    public void Corte_dice_si_cuadro(double contado, string esperado)
    {
        var c = Corte();
        c.EfectivoDeclarado = (decimal)contado;
        Assert.Equal(esperado, c.DiferenciaTexto);
    }

    [Fact]
    public void Corte_abierto_dice_que_sigue_abierta()
    {
        var c = Corte();
        c.Estado = EstadosTurno.Abierto;
        c.CerradoEn = null;
        c.EfectivoDeclarado = null;
        Assert.Equal("🟢 Sigue abierta", c.DiferenciaTexto);
    }

    // ------------------------------------------------------------------ Historial de precios

    [Fact]
    public void Cambio_de_precio_muestra_antes_ahora_y_porcentaje()
    {
        Assert.Equal("$20.00 → $22.00  (+10%)", CambioPrecio.Texto(20m, 22m));
        Assert.Equal("$20.00 → $15.00  (-25%)", CambioPrecio.Texto(20m, 15m));
        Assert.Equal("$3.00 → $3.10  (+3.3%)", CambioPrecio.Texto(3m, 3.10m));
    }

    [Fact]
    public void Sin_precio_anterior_o_sin_cambio_solo_muestra_el_precio()
    {
        Assert.Equal("$22.00", CambioPrecio.Texto(null, 22m));
        Assert.Equal("$22.00", CambioPrecio.Texto(22m, 22m));
        Assert.Equal("$0.00 → $5.00", CambioPrecio.Texto(0m, 5m));
    }

    // ------------------------------------------------------------------ Historial de tickets

    [Fact]
    public void Resumen_de_venta_explica_pago_y_cliente()
    {
        var v = new VentaResumen { FormasPago = "EFECTIVO,TARJETA", Estado = EstadosVenta.Cancelada };
        Assert.Equal("Efectivo + Tarjeta", v.FormasPagoTexto);
        Assert.Equal("Público en general", v.ClienteTexto);
        Assert.Equal("✖ Cancelada", v.EstadoTexto);
    }

    [Fact]
    public void Filtro_de_dias_va_de_medianoche_a_medianoche_del_dia_siguiente()
    {
        var f = new FiltroHistorial(new DateTime(2026, 10, 13), new DateTime(2026, 10, 19), 2);
        var partes = f.Query.Split('&').Select(p => p.Split('=')).ToDictionary(p => p[0], p => Uri.UnescapeDataString(p[1]));

        var desde = DateTimeOffset.Parse(partes["desde"]);
        var hasta = DateTimeOffset.Parse(partes["hasta"]);
        Assert.Equal(new DateTime(2026, 10, 13), desde.LocalDateTime);
        Assert.Equal(new DateTime(2026, 10, 20), hasta.LocalDateTime);
        Assert.Equal("2", partes["cajaId"]);
        Assert.DoesNotContain("cajaId", new FiltroHistorial(DateTime.Today, DateTime.Today, null).Query);
    }
}
