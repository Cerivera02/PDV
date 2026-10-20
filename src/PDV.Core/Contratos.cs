namespace PDV.Core;

// DTOs compartidos entre el servidor (PDV.Server) y las cajas (PDV.Caja).

/// <param name="CajaId">Caja desde la que se entra (caja.json). El servidor revisa que esté desbloqueada y que la IP coincida.</param>
public sealed record LoginRequest(string Usuario, string Password, int? CajaId = null);
/// <param name="Permisos">Permisos especiales de la persona (el encargado los tiene todos).</param>
/// <param name="TieneClaveAutorizacion">false = el encargado todavía no crea su clave de autorización (se le pide al entrar).</param>
public sealed record LoginResponse(string Token, int UsuarioId, string Nombre, string Rol, string[]? Permisos = null,
    bool TieneClaveAutorizacion = false);

/// <param name="Cajas">Cajas en las que puede trabajar. Vacío = todas.</param>
/// <param name="Permisos">Permisos especiales (ver <see cref="Core.Permisos"/>).</param>
public sealed record UsuarioDto(int Id, string Login, string Nombre, string Rol, bool Activo, int[] Cajas, string[]? Permisos = null,
    bool TieneClaveAutorizacion = false);
/// <param name="ClaveAutorizacion">Obligatoria si es encargado o lleva permisos especiales.</param>
public sealed record CrearUsuarioRequest(string Login, string Nombre, string Password, string Rol,
    string[]? Permisos = null, string? ClaveAutorizacion = null);
/// <param name="ClaveAutorizacion">Obligatoria si se le dan permisos y todavía no tiene clave.</param>
public sealed record PermisosRequest(string[] Permisos, string? ClaveAutorizacion = null);
public sealed record ClaveAutorizacionRequest(string Clave);

public sealed record CrearCajaRequest(string Nombre, string Serie, string? Ip);
public sealed record ActualizarCajaRequest(string Nombre, string? Ip, bool Activa);

/// <summary>Código que manda el servidor cuando la caja está bloqueada o la IP no corresponde.</summary>
public static class CodigosError
{
    public const string CajaSinAcceso = "CAJA_SIN_ACCESO";
}

public sealed record DepartamentoRequest(string Nombre, bool Activo = true);

public sealed record MovimientoInventarioRequest(int ProductoId, string Tipo, decimal Cantidad, decimal? CostoUnitario, string? Nota);

public sealed record AbrirTurnoRequest(int CajaId, decimal FondoInicial);
/// <param name="Autorizacion">Firma del encargado (POST /api/autorizaciones) si quien saca el dinero no tiene el permiso.</param>
public sealed record RetiroRequest(decimal Monto, string Motivo, string? Autorizacion = null);
public sealed record CerrarTurnoRequest(decimal EfectivoDeclarado);

/// <param name="PrecioManual">Precio cambiado a mano solo para esta venta (null = el del sistema, con promociones).</param>
/// <param name="Autorizacion">Si quien cobra no tiene el permiso de cambiar precios: la autorización firmada del encargado.</param>
public sealed record VentaItemRequest(int ProductoId, decimal Cantidad, decimal? PrecioManual = null, string? Autorizacion = null);
public sealed record PagoRequest(string FormaPago, decimal Monto, string? Referencia = null);
/// <param name="ClienteId">Opcional. Obligatorio si algún pago es a crédito (CREDITO). Aplica el precio especial del cliente.</param>
public sealed record VentaRequest(long TurnoId, List<VentaItemRequest> Items, List<PagoRequest> Pagos, int? ClienteId = null);
/// <param name="Autorizacion">Firma del encargado (POST /api/autorizaciones) si quien cancela no tiene el permiso.</param>
public sealed record CancelarVentaRequest(string Motivo, string? Autorizacion = null);

/// <summary>El encargado (o alguien con el permiso) autoriza algo en la caja de otra persona.</summary>
/// <param name="Clave">La clave de autorización de quien autoriza (no su contraseña para entrar). Con ella el servidor sabe quién es.</param>
public sealed record AutorizacionRequest(string Clave, string Permiso);
/// <param name="Token">Autorización firmada por el servidor; vale unas horas y solo para ese permiso.</param>
public sealed record AutorizacionDto(string Token, string Nombre);

/// <summary>Ajustes de la pantalla de Vender que elige el encargado (iguales en todas las cajas).</summary>
public sealed class ConfigVentaDto
{
    /// <summary>Qué abre la flecha izquierda (la derecha abre lo otro): <see cref="AccionesFlecha"/>.</summary>
    public string FlechaIzquierda { get; set; } = AccionesFlecha.Cantidad;

    [System.Text.Json.Serialization.JsonIgnore]
    public string FlechaDerecha => FlechaIzquierda == AccionesFlecha.Precio ? AccionesFlecha.Cantidad : AccionesFlecha.Precio;
}

public sealed class NegocioDto
{
    public string Nombre { get; set; } = "";
    public string Rfc { get; set; } = "";
    public string Direccion { get; set; } = "";
    public string Telefono { get; set; } = "";
    public string MensajeTicket { get; set; } = "";
}

public sealed class TicketLineaDto
{
    public string Descripcion { get; set; } = "";
    public string Unidad { get; set; } = "";
    public decimal Cantidad { get; set; }
    public decimal PrecioUnitario { get; set; }
    public decimal Importe { get; set; }
    /// <summary>Ahorro por promoción en este renglón (0 si no hubo).</summary>
    public decimal Descuento { get; set; }
    /// <summary>Promoción aplicada, ej. "3x2" u "Oferta $15.00".</summary>
    public string? Promocion { get; set; }
}

public sealed class TicketPagoDto
{
    public string FormaPago { get; set; } = "";
    public decimal Monto { get; set; }
    public string? Referencia { get; set; }
}

public sealed class TicketDto
{
    public long VentaId { get; set; }
    public string Folio { get; set; } = "";
    public DateTime Fecha { get; set; }
    public string Caja { get; set; } = "";
    public string Cajero { get; set; } = "";
    public string Estado { get; set; } = "";
    public decimal Subtotal { get; set; }
    public decimal Impuestos { get; set; }
    public decimal Total { get; set; }
    public decimal Pagado { get; set; }
    public decimal Cambio { get; set; }
    public NegocioDto Negocio { get; set; } = new();
    /// <summary>Cliente de la venta (null = público en general).</summary>
    public string? Cliente { get; set; }
    /// <summary>Lo que debe el cliente después de esta venta (solo si la venta fue a crédito).</summary>
    public decimal? SaldoCliente { get; set; }
    public List<TicketLineaDto> Lineas { get; set; } = [];
    public List<TicketPagoDto> Pagos { get; set; } = [];

    /// <summary>Total ahorrado por promociones (un precio cambiado hacia arriba no resta).</summary>
    public decimal Ahorro => Lineas.Sum(l => Math.Max(0, l.Descuento));
}

public sealed class CorteDto
{
    public long TurnoId { get; set; }
    public string Caja { get; set; } = "";
    public string Cajero { get; set; } = "";
    public string Estado { get; set; } = "";
    public DateTime AbiertoEn { get; set; }
    public DateTime? CerradoEn { get; set; }
    public int NumVentas { get; set; }
    public int NumCanceladas { get; set; }
    public decimal FondoInicial { get; set; }
    public decimal VentasEfectivo { get; set; }
    public decimal VentasTarjeta { get; set; }
    public decimal VentasTransferencia { get; set; }
    /// <summary>Vendido a crédito: no entra dinero a la caja.</summary>
    public decimal VentasCredito { get; set; }
    /// <summary>Abonos de clientes recibidos en efectivo (sí entran al cajón).</summary>
    public decimal AbonosEfectivo { get; set; }
    /// <summary>Abonos de clientes con tarjeta o transferencia.</summary>
    public decimal AbonosOtros { get; set; }
    public decimal Retiros { get; set; }
    public decimal TotalVentas => VentasEfectivo + VentasTarjeta + VentasTransferencia + VentasCredito;
    public decimal EfectivoEsperado => FondoInicial + VentasEfectivo + AbonosEfectivo - Retiros;
    public decimal? EfectivoDeclarado { get; set; }
    public decimal? Diferencia => EfectivoDeclarado - EfectivoEsperado;
    public NegocioDto Negocio { get; set; } = new();
    public int CajaId { get; set; }
    /// <summary>Cada vez que se sacó dinero (solo en el corte completo; en la lista de cortes va vacío).</summary>
    public List<RetiroDto> DetalleRetiros { get; set; } = [];

    // ---- Textos para la lista de cortes
    [System.Text.Json.Serialization.JsonIgnore] public DateTime AbiertoLocal => AbiertoEn.ToLocalTime();
    [System.Text.Json.Serialization.JsonIgnore] public DateTime? CerradoLocal => CerradoEn?.ToLocalTime();
    [System.Text.Json.Serialization.JsonIgnore] public bool Abierto => Estado == EstadosTurno.Abierto;

    /// <summary>"✔ Cuadra", "Sobran $20.00", "⚠ Faltan $15.00" o "🟢 Sigue abierta".</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string DiferenciaTexto => Diferencia switch
    {
        null => Abierto ? "🟢 Sigue abierta" : "",
        0 => "✔ Cuadra",
        > 0 => $"Sobran {Diferencia.Value.ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("es-MX"))}",
        _ => $"⚠ Faltan {(-Diferencia.Value).ToString("C2", System.Globalization.CultureInfo.GetCultureInfo("es-MX"))}"
    };
}
