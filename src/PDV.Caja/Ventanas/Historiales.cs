using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using PDV.Caja.Servicios;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Caja.Ventanas;

/// <summary>Historial de tickets y de cortes de caja (usan <see cref="HistorialWindow"/>).</summary>
public static class Historiales
{
    // ------------------------------------------------------------------ Tickets

    /// <summary>
    /// Tickets vendidos, con el ticket completo a la derecha y "Imprimir de nuevo".
    /// El encargado puede ver todas las cajas; los demás solo ven la suya (lo revisa el servidor).
    /// </summary>
    /// <param name="cajaId">Caja que se ve al abrir (null = todas).</param>
    /// <param name="folio">Ticket que se elige al abrir (por ejemplo, el último que se cobró).</param>
    /// <param name="corte">Si se indica, solo los tickets de ese corte (sin filtro de días).</param>
    public static void Tickets(Window owner, int? cajaId = null, string? folio = null, CorteDto? corte = null)
    {
        var cache = new Dictionary<long, TicketDto>();
        async Task<TicketDto?> Ticket(object renglon)
        {
            var v = (VentaResumen)renglon;
            if (!cache.TryGetValue(v.VentaId, out var t))
            {
                t = await Sesion.Api.GetAsync<TicketDto>($"api/ventas/{v.VentaId}/ticket");
                if (t is not null)
                    cache[v.VentaId] = t;
            }
            return t;
        }

        var titulo = corte is null
            ? "🧾 Tickets"
            : $"🧾 Tickets del corte: {corte.Caja} · {corte.Cajero} · {corte.AbiertoLocal:dd/MM/yyyy}";
        var explicacion = corte is null
            ? "Toque un ticket para verlo completo a la derecha. Enter o \"Imprimir de nuevo\" lo vuelve a imprimir."
            : "Todas las ventas de ese turno. Toque un ticket para verlo completo.";

        HistorialWindow? w = null;
        w = new HistorialWindow(titulo, explicacion,
            [
                new Columna("Ticket", nameof(VentaResumen.Folio), Ancho: 115),
                new Columna("Fecha y hora", nameof(VentaResumen.FechaLocal), "{0:dd/MM/yy  h:mm tt}", 175),
                new Columna("Cliente", nameof(VentaResumen.ClienteTexto)),
                new Columna("Pagó con", nameof(VentaResumen.FormasPagoTexto), Ancho: 175),
                new Columna("Total", nameof(VentaResumen.Total), "{0:C2}", 115),
                new Columna("", nameof(VentaResumen.EstadoTexto), Ancho: 125)
            ],
            async f => corte is null
                ? await Sesion.Api.GetAsync<List<VentaResumen>>($"api/ventas?{f.Query}") ?? []
                : (IEnumerable)(await Sesion.Api.GetAsync<List<VentaResumen>>($"api/ventas?turnoId={corte.TurnoId}") ?? []),
            async renglon => await Ticket(renglon) is { } t
                ? TicketEscPos.VentaTexto(Sesion.Config.Impresora, t)
                : "No se encontró el ticket.",
            new AccionHistorial("🖨  Imprimir de nuevo", async renglon =>
            {
                if (await Ticket(renglon) is { } t)
                    Imprimir(w!, TicketEscPos.Venta(Sesion.Config.Impresora, t, reimpresion: true), $"✔ Se imprimió de nuevo el ticket {t.Folio}.");
                return false;
            }, "BotonAzul"))
        {
            Owner = owner,
            ConFechas = corte is null,
            ElegirCaja = corte is null && Sesion.EsAdmin,
            CajaInicial = cajaId,
            PistaBusqueda = "🔍  Buscar: número de ticket, cliente, cajero o importe",
            TextoVacio = "No hay tickets en esos días.",
            SeleccionInicial = folio is null ? null : r => ((VentaResumen)r).Folio == folio,
            ColorRenglon = r => ((VentaResumen)r).Cancelada ? (Brush)Application.Current.FindResource("Peligro") : null,
            FiltroBusqueda = (r, texto) =>
            {
                var v = (VentaResumen)r;
                return TablaWindow.Contiene(v.Folio, texto) || TablaWindow.Contiene(v.Cliente, texto)
                    || TablaWindow.Contiene(v.Cajero, texto) || TablaWindow.Contiene(v.Caja, texto)
                    || (Formato.TryDecimal(texto, out var importe) && v.Total == importe);
            },
            Resumen = renglones =>
            {
                var ventas = renglones.Cast<VentaResumen>().ToList();
                var cobradas = ventas.Where(v => !v.Cancelada).ToList();
                var canceladas = ventas.Count - cobradas.Count;
                return $"{Cuantos(cobradas.Count, "ticket cobrado", "tickets cobrados")} · Total {Formato.Dinero(cobradas.Sum(v => v.Total))}"
                       + (canceladas > 0 ? $"   ·   {Cuantos(canceladas, "cancelado", "cancelados")}" : "");
            }
        };
        w.ShowDialog();
    }

    // ------------------------------------------------------------------ Cortes

    /// <summary>Cortes de caja (cada vez que se abrió y se cerró una caja), con el corte completo a la derecha.</summary>
    public static void Cortes(Window owner)
    {
        var cache = new Dictionary<long, CorteDto>();
        async Task<CorteDto?> Corte(object renglon)
        {
            var c = (CorteDto)renglon;
            if (!cache.TryGetValue(c.TurnoId, out var completo))
            {
                completo = await Sesion.Api.GetAsync<CorteDto>($"api/cortes/{c.TurnoId}");
                // Uno abierto sigue cambiando: no se guarda
                if (completo is not null && !completo.Abierto)
                    cache[c.TurnoId] = completo;
            }
            return completo;
        }

        HistorialWindow? w = null;
        w = new HistorialWindow("📊 Cortes de caja",
            "Cada vez que una caja se abre y se cierra queda su corte. Toque uno para verlo completo a la derecha.",
            [
                new Columna("Caja", nameof(CorteDto.Caja), Ancho: 130),
                new Columna("Cajero", nameof(CorteDto.Cajero)),
                new Columna("Abrió", nameof(CorteDto.AbiertoLocal), "{0:dd/MM  h:mm tt}", 150),
                new Columna("Cerró", nameof(CorteDto.CerradoLocal), "{0:dd/MM  h:mm tt}", 150),
                new Columna("Vendido", nameof(CorteDto.TotalVentas), "{0:C2}", 120),
                new Columna("¿Cuadró?", nameof(CorteDto.DiferenciaTexto), Ancho: 165)
            ],
            async f => await Sesion.Api.GetAsync<List<CorteDto>>($"api/cortes?{f.Query}") ?? [],
            async renglon => await Corte(renglon) is { } c
                ? TicketEscPos.CorteTexto(Sesion.Config.Impresora, c)
                : "No se encontró el corte.",
            new AccionHistorial("🖨  Imprimir", async renglon =>
            {
                if (await Corte(renglon) is { } c)
                    Imprimir(w!, TicketEscPos.Corte(Sesion.Config.Impresora, c, reimpresion: true), $"✔ Se imprimió el corte de {c.Caja} ({c.Cajero}).");
                return false;
            }, "BotonAzul"),
            new AccionHistorial("🧾  Ver sus tickets", renglon =>
            {
                Tickets(w!, corte: (CorteDto)renglon);
                return Task.FromResult(false);
            }))
        {
            Owner = owner,
            DiasAlAbrir = 6,
            ElegirCaja = true,
            PistaBusqueda = "🔍  Buscar: caja o cajero",
            TextoVacio = "No hay cortes en esos días.",
            ColorRenglon = r => ((CorteDto)r).Diferencia < 0 ? (Brush)Application.Current.FindResource("Peligro") : null,
            FiltroBusqueda = (r, texto) =>
            {
                var c = (CorteDto)r;
                return TablaWindow.Contiene(c.Caja, texto) || TablaWindow.Contiene(c.Cajero, texto);
            },
            Resumen = renglones =>
            {
                var cortes = renglones.Cast<CorteDto>().ToList();
                var faltantes = cortes.Where(c => c.Diferencia < 0).Sum(c => -c.Diferencia!.Value);
                var sobrantes = cortes.Where(c => c.Diferencia > 0).Sum(c => c.Diferencia!.Value);
                var texto = $"{Cuantos(cortes.Count, "corte", "cortes")} · Vendido {Formato.Dinero(cortes.Sum(c => c.TotalVentas))} " +
                            $"(efectivo {Formato.Dinero(cortes.Sum(c => c.VentasEfectivo))}, tarjeta {Formato.Dinero(cortes.Sum(c => c.VentasTarjeta))}" +
                            (cortes.Sum(c => c.VentasCredito) is > 0 and var credito ? $", crédito {Formato.Dinero(credito)}" : "") + ")";
                if (faltantes > 0) texto += $"   ·   Faltó {Formato.Dinero(faltantes)}";
                if (sobrantes > 0) texto += $"   ·   Sobró {Formato.Dinero(sobrantes)}";
                return texto;
            }
        };
        w.ShowDialog();
    }

    // ------------------------------------------------------------------ Utilidades

    private static string Cuantos(int n, string singular, string plural) =>
        $"{n.ToString("N0", CultureInfo.GetCultureInfo("es-MX"))} {(n == 1 ? singular : plural)}";

    /// <summary>Imprime y avisa abajo en la ventana; si no hay impresora o falla, lo explica.</summary>
    private static void Imprimir(HistorialWindow w, byte[] datos, string listo)
    {
        if (!Sesion.HayImpresora)
        {
            Mensajes.Info(w, "Esta caja no tiene impresora configurada.\n\nPara configurarla: Salir → en la pantalla de entrada, \"⚙ Configuración\" → \"Impresora de tickets\".");
            return;
        }
        try
        {
            Sesion.Imprimir(datos);
            w.Avisar(listo);
        }
        catch (Exception)
        {
            w.Avisar("⚠ No se pudo imprimir. Revise que la impresora esté prendida y con papel.", error: true);
        }
    }
}
