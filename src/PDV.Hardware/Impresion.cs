using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using PDV.Core;

namespace PDV.Hardware;

public sealed class ConfigImpresora
{
    /// <summary>Nombre de la impresora en Windows ("Panel de control › Impresoras"). Vacío = no imprimir.</summary>
    public string Nombre { get; set; } = "";
    /// <summary>Caracteres por renglón: 32 (58 mm) o 48 (80 mm).</summary>
    public int Ancho { get; set; } = 48;
    /// <summary>Página de códigos de .NET usada para acentos y ñ (850 es la más compatible).</summary>
    public int CodePage { get; set; } = 850;
    /// <summary>Tabla ESC/POS que corresponde a CodePage (ESC t n). Para 850 normalmente es 2.</summary>
    public byte TablaEscPos { get; set; } = 2;
    public bool AbrirCajon { get; set; } = true;
    public bool Cortar { get; set; } = true;
}

/// <summary>Envía bytes crudos (ESC/POS) a una impresora instalada en Windows, sin pasar por el driver gráfico.</summary>
public static class ImpresoraRaw
{
    public static void Enviar(string impresora, byte[] datos)
    {
        if (!OpenPrinter(impresora, out var h, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"No se pudo abrir la impresora \"{impresora}\".");
        try
        {
            var doc = new DOCINFOW { pDocName = "Ticket PDV", pDataType = "RAW" };
            if (StartDocPrinter(h, 1, ref doc) == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                StartPagePrinter(h);
                if (!WritePrinter(h, datos, datos.Length, out var escritos) || escritos != datos.Length)
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Error al enviar datos a la impresora.");
                EndPagePrinter(h);
            }
            finally
            {
                EndDocPrinter(h);
            }
        }
        finally
        {
            ClosePrinter(h);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFOW
    {
        public string pDocName;
        public string? pOutputFile;
        public string pDataType;
    }

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "OpenPrinterW")]
    private static extern bool OpenPrinter(string pPrinterName, out IntPtr phPrinter, IntPtr pDefault);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool ClosePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "StartDocPrinterW")]
    private static extern int StartDocPrinter(IntPtr hPrinter, int level, ref DOCINFOW pDocInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndDocPrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool StartPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool EndPagePrinter(IntPtr hPrinter);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool WritePrinter(IntPtr hPrinter, byte[] pBytes, int dwCount, out int dwWritten);
}

/// <summary>
/// Genera tickets en ESC/POS (compatible con Epson y la mayoría de térmicas genéricas).
/// Los métodos "…Texto" arman exactamente el mismo ticket como texto plano, para verlo en pantalla.
/// </summary>
public sealed class TicketEscPos
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");
    private readonly ConfigImpresora _cfg;
    private readonly Encoding _encoding;
    private readonly List<byte> _bytes = [];
    // Modo texto (vista previa): sin comandos ESC/POS; el centrado se hace con espacios.
    private readonly StringBuilder? _texto;
    private bool _centrado;

    static TicketEscPos() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public TicketEscPos(ConfigImpresora cfg) : this(cfg, soloTexto: false) { }

    private TicketEscPos(ConfigImpresora cfg, bool soloTexto)
    {
        _cfg = cfg;
        _encoding = Encoding.GetEncoding(cfg.CodePage);
        _texto = soloTexto ? new StringBuilder() : null;
    }

    /// <summary>Pulso al cajón de dinero conectado a la impresora (pin 2).</summary>
    public static readonly byte[] AbrirCajon = [0x1B, 0x70, 0x00, 0x19, 0xFA];

    public static byte[] Venta(ConfigImpresora cfg, TicketDto t, bool reimpresion = false) =>
        ArmarVenta(new TicketEscPos(cfg), t, reimpresion).Finalizar();

    /// <summary>El ticket de venta como texto, renglón por renglón (para la vista previa en pantalla).</summary>
    public static string VentaTexto(ConfigImpresora cfg, TicketDto t, bool reimpresion = false) =>
        ArmarVenta(new TicketEscPos(cfg, soloTexto: true), t, reimpresion).TextoFinal();

    private static TicketEscPos ArmarVenta(TicketEscPos p, TicketDto t, bool reimpresion)
    {
        p.Iniciar();
        p.Encabezado(t.Negocio);
        if (reimpresion)
            p.Centro().Texto("*** REIMPRESION ***");
        if (t.Estado == EstadosVenta.Cancelada)
            p.Centro().Negrita(true).Texto("*** VENTA CANCELADA ***").Negrita(false);

        p.Izquierda()
            .Texto($"Folio: {t.Folio}")
            .Texto($"Fecha: {t.Fecha.ToLocalTime():dd/MM/yyyy HH:mm}")
            .Texto($"Caja: {t.Caja}  Cajero: {t.Cajero}");
        if (!string.IsNullOrWhiteSpace(t.Cliente))
            p.Texto($"Cliente: {t.Cliente}");
        p.Separador();

        foreach (var l in t.Lineas)
        {
            p.Texto(l.Descripcion);
            var cantidad = l.Unidad == "PZA" ? l.Cantidad.ToString("0.###", Mx) : $"{l.Cantidad.ToString("0.000", Mx)} {l.Unidad}";
            if (l.Descuento > 0)
            {
                // Se muestra el precio normal y debajo el ahorro de la promoción
                var normal = l.Importe + l.Descuento;
                p.Columnas($"  {cantidad} x {(normal / l.Cantidad).ToString("C2", Mx)}", normal.ToString("C2", Mx));
                p.Columnas($"  * {l.Promocion}", "-" + l.Descuento.ToString("C2", Mx));
            }
            else
            {
                p.Columnas($"  {cantidad} x {l.PrecioUnitario.ToString("C2", Mx)}", l.Importe.ToString("C2", Mx));
            }
        }

        p.Separador()
            .Columnas("Subtotal", t.Subtotal.ToString("C2", Mx))
            .Columnas("IVA", t.Impuestos.ToString("C2", Mx))
            .Negrita(true).Doble(true).Columnas("TOTAL", t.Total.ToString("C2", Mx), dobleAncho: true).Doble(false).Negrita(false);

        foreach (var pago in t.Pagos)
        {
            var monto = pago.FormaPago == FormasPago.Efectivo ? pago.Monto + t.Cambio : pago.Monto;
            p.Columnas(FormasPago.Texto(pago.FormaPago).ToUpperInvariant(), monto.ToString("C2", Mx));
        }
        if (t.Cambio > 0)
            p.Columnas("CAMBIO", t.Cambio.ToString("C2", Mx));
        if (t.SaldoCliente is { } saldo)
            p.Negrita(true).Columnas("DEBE EN TOTAL", saldo.ToString("C2", Mx)).Negrita(false);

        p.Texto($"Articulos: {t.Lineas.Sum(l => l.Unidad == "PZA" ? l.Cantidad : 1):0}");
        if (t.Ahorro > 0)
            p.Avanzar(1).Centro().Negrita(true).Texto($"USTED AHORRÓ: {t.Ahorro.ToString("C2", Mx)}").Negrita(false).Izquierda();
        p.Avanzar(1);
        if (!string.IsNullOrWhiteSpace(t.Negocio.MensajeTicket))
            p.Centro().Texto(t.Negocio.MensajeTicket);
        return p;
    }

    public static byte[] Corte(ConfigImpresora cfg, CorteDto c, bool reimpresion = false) =>
        ArmarCorte(new TicketEscPos(cfg), c, reimpresion).Finalizar();

    /// <summary>El corte como texto, renglón por renglón (para verlo en pantalla).</summary>
    public static string CorteTexto(ConfigImpresora cfg, CorteDto c, bool reimpresion = false) =>
        ArmarCorte(new TicketEscPos(cfg, soloTexto: true), c, reimpresion).TextoFinal();

    private static TicketEscPos ArmarCorte(TicketEscPos p, CorteDto c, bool reimpresion)
    {
        p.Iniciar();
        p.Encabezado(c.Negocio);
        p.Centro().Negrita(true).Texto(c.Estado == EstadosTurno.Cerrado ? "CORTE DE CAJA (Z)" : "CORTE PARCIAL (X)").Negrita(false);
        if (reimpresion)
            p.Texto("*** REIMPRESION ***");
        p.Izquierda()
            .Texto($"Caja: {c.Caja}")
            .Texto($"Cajero: {c.Cajero}")
            .Texto($"Apertura: {c.AbiertoEn.ToLocalTime():dd/MM/yyyy HH:mm}")
            .Texto($"Cierre:   {(c.CerradoEn?.ToLocalTime().ToString("dd/MM/yyyy HH:mm") ?? "-")}")
            .Separador()
            .Columnas("Ventas", c.NumVentas.ToString())
            .Columnas("Canceladas", c.NumCanceladas.ToString())
            .Columnas("Efectivo", c.VentasEfectivo.ToString("C2", Mx))
            .Columnas("Tarjeta", c.VentasTarjeta.ToString("C2", Mx))
            .Columnas("Transferencia", c.VentasTransferencia.ToString("C2", Mx))
            .Columnas("A crédito", c.VentasCredito.ToString("C2", Mx))
            .Negrita(true).Columnas("TOTAL VENTAS", c.TotalVentas.ToString("C2", Mx)).Negrita(false);
        if (c.AbonosEfectivo + c.AbonosOtros > 0)
        {
            p.Columnas("Abonos en efectivo", c.AbonosEfectivo.ToString("C2", Mx))
             .Columnas("Abonos tarjeta/transf.", c.AbonosOtros.ToString("C2", Mx));
        }
        p.Separador()
            .Columnas("Fondo inicial", c.FondoInicial.ToString("C2", Mx))
            .Columnas("(+) Efectivo", c.VentasEfectivo.ToString("C2", Mx))
            .Columnas("(+) Abonos efectivo", c.AbonosEfectivo.ToString("C2", Mx))
            .Columnas("(-) Retiros", c.Retiros.ToString("C2", Mx))
            .Negrita(true).Columnas("Efectivo esperado", c.EfectivoEsperado.ToString("C2", Mx)).Negrita(false);

        if (c.EfectivoDeclarado is { } declarado)
        {
            p.Columnas("Efectivo contado", declarado.ToString("C2", Mx))
             .Negrita(true).Columnas("Diferencia", c.Diferencia!.Value.ToString("C2", Mx)).Negrita(false);
        }
        if (c.DetalleRetiros.Count > 0)
        {
            p.Separador().Texto("Dinero que se sacó:");
            foreach (var r in c.DetalleRetiros)
            {
                p.Columnas($" {r.Fecha.ToLocalTime():HH:mm} {r.Motivo}", r.Monto.ToString("C2", Mx));
                if (r.Autorizo is { Length: > 0 } a)
                    p.Texto($"   Autorizó: {a}");
            }
        }
        p.Avanzar(3).Centro().Texto("______________________").Texto("Firma cajero");
        return p;
    }

    /// <summary>Comprobante que se le entrega al cliente cuando abona a lo que debe.</summary>
    public static byte[] Abono(ConfigImpresora cfg, ComprobanteAbono a)
    {
        var p = new TicketEscPos(cfg).Iniciar();
        p.Encabezado(a.Negocio);
        p.Centro().Negrita(true).Texto("COMPROBANTE DE ABONO").Negrita(false)
            .Izquierda()
            .Texto($"Fecha: {a.Fecha:dd/MM/yyyy HH:mm}")
            .Texto($"Caja: {a.Caja}  Cajero: {a.Cajero}")
            .Texto($"Cliente: {a.Cliente}")
            .Separador()
            .Columnas("Debía", a.SaldoAnterior.ToString("C2", Mx))
            .Columnas($"Abonó ({FormasPago.Texto(a.FormaPago).ToLowerInvariant()})", "-" + a.Monto.ToString("C2", Mx))
            .Negrita(true).Columnas("DEBE AHORA", a.SaldoNuevo.ToString("C2", Mx)).Negrita(false);
        if (a.SaldoNuevo == 0)
            p.Avanzar(1).Centro().Negrita(true).Texto("¡CUENTA LIQUIDADA!").Negrita(false);
        p.Avanzar(3).Centro().Texto("______________________").Texto("Firma de quien recibe");
        return p.Finalizar();
    }

    // ------------------------------------------------------------------ primitivas ESC/POS

    private TicketEscPos Iniciar()
    {
        Cmd(0x1B, 0x40);                    // ESC @  reset
        Cmd(0x1B, 0x74, _cfg.TablaEscPos);  // ESC t  página de códigos
        return this;
    }

    private void Encabezado(NegocioDto n)
    {
        Centro().Negrita(true).Doble(true).Texto(n.Nombre, dobleAncho: true).Doble(false).Negrita(false);
        if (!string.IsNullOrWhiteSpace(n.Rfc)) Texto($"RFC: {n.Rfc}");
        if (!string.IsNullOrWhiteSpace(n.Direccion)) Texto(n.Direccion);
        if (!string.IsNullOrWhiteSpace(n.Telefono)) Texto($"Tel. {n.Telefono}");
        Avanzar(1);
    }

    private TicketEscPos Centro() { _centrado = true; Cmd(0x1B, 0x61, 1); return this; }
    private TicketEscPos Izquierda() { _centrado = false; Cmd(0x1B, 0x61, 0); return this; }
    private TicketEscPos Negrita(bool on) { Cmd(0x1B, 0x45, (byte)(on ? 1 : 0)); return this; }
    private TicketEscPos Doble(bool on) { Cmd(0x1D, 0x21, (byte)(on ? 0x11 : 0x00)); return this; }

    private TicketEscPos Avanzar(byte lineas)
    {
        _texto?.Append('\n', lineas);
        Cmd(0x1B, 0x64, lineas);
        return this;
    }

    /// <summary>En pantalla no hay letra doble: lo que va en doble ancho se muestra normal, con el ancho completo.</summary>
    private int Ancho(bool dobleAncho) => dobleAncho && _texto is null ? _cfg.Ancho / 2 : _cfg.Ancho;

    private TicketEscPos Texto(string texto, bool dobleAncho = false)
    {
        var ancho = Ancho(dobleAncho);
        foreach (var renglon in Partir(texto, ancho))
            Renglon(_centrado && _texto is not null ? renglon.PadLeft((ancho + renglon.Length) / 2) : renglon);
        return this;
    }

    /// <summary>Texto a la izquierda y valor alineado a la derecha.</summary>
    private TicketEscPos Columnas(string izquierda, string derecha, bool dobleAncho = false)
    {
        var ancho = Ancho(dobleAncho);
        var espacio = ancho - derecha.Length - 1;
        if (izquierda.Length > espacio)
            izquierda = izquierda[..Math.Max(0, espacio)];
        Renglon(izquierda.PadRight(espacio) + " " + derecha);
        return this;
    }

    private void Renglon(string renglon)
    {
        if (_texto is not null)
            _texto.Append(renglon.TrimEnd()).Append('\n');
        else
            _bytes.AddRange(_encoding.GetBytes(renglon + "\n"));
    }

    private TicketEscPos Separador() => Texto(new string('-', _cfg.Ancho));

    private byte[] Finalizar()
    {
        Avanzar(4);
        if (_cfg.Cortar)
            Cmd(0x1D, 0x56, 0x42, 0x00);    // GS V B  corte parcial
        return [.. _bytes];
    }

    private string TextoFinal() => _texto!.ToString().TrimEnd('\n');

    private void Cmd(params byte[] b)
    {
        if (_texto is null)
            _bytes.AddRange(b);
    }

    /// <summary>Parte el texto en renglones respetando palabras (corta una palabra solo si no cabe sola).</summary>
    private static IEnumerable<string> Partir(string texto, int ancho)
    {
        var renglon = new StringBuilder();
        foreach (var palabra in texto.Split(' '))
        {
            var p = palabra;
            if (renglon.Length > 0 && renglon.Length + 1 + p.Length > ancho)
            {
                yield return renglon.ToString();
                renglon.Clear();
            }
            while (p.Length > ancho)
            {
                yield return p[..ancho];
                p = p[ancho..];
            }
            if (renglon.Length > 0)
                renglon.Append(' ');
            renglon.Append(p);
        }
        yield return renglon.ToString();
    }
}
