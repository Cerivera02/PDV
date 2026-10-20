namespace PDV.Core;

public sealed class Usuario
{
    public int Id { get; set; }
    public string Login { get; set; } = "";
    public string Nombre { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public string Rol { get; set; } = "";
    public bool Activo { get; set; }
    /// <summary>Permisos especiales (ver <see cref="Core.Permisos"/>).</summary>
    public string[] Permisos { get; set; } = [];
    /// <summary>Hash de la clave de autorización (null = no tiene; ver <see cref="Core.Permisos.NecesitaClave"/>).</summary>
    public string? ClaveAutorizacionHash { get; set; }
}

public sealed class CajaInfo
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public string Serie { get; set; } = "";
    public int UltimoFolio { get; set; }
    public bool EsMaestra { get; set; }
    /// <summary>false = caja bloqueada: nadie puede entrar ni cobrar en ella.</summary>
    public bool Activa { get; set; }
    /// <summary>IP fija de la computadora de esta caja. La principal no usa IP: solo funciona en el servidor.</summary>
    public string? Ip { get; set; }

    // ---- Textos para mostrar en pantalla
    [System.Text.Json.Serialization.JsonIgnore]
    public string IpTexto => EsMaestra ? "Esta computadora (principal)" : string.IsNullOrEmpty(Ip) ? "⚠ Sin IP registrada" : Ip;

    [System.Text.Json.Serialization.JsonIgnore]
    public string EstadoTexto => Activa ? "✔ Puede usarse" : "⛔ Bloqueada";
}

public sealed class Departamento
{
    public int Id { get; set; }
    public string Nombre { get; set; } = "";
    public bool Activo { get; set; } = true;
}

public sealed class Producto
{
    public int Id { get; set; }
    /// <summary>Código de barras principal. Se guarda como VARCHAR para conservar ceros a la izquierda.</summary>
    public string CodigoBarras { get; set; } = "";
    /// <summary>PLU / código interno usado por las etiquetas de báscula.</summary>
    public string? CodigoInterno { get; set; }
    public string Nombre { get; set; } = "";
    public int? DepartamentoId { get; set; }
    /// <summary>Solo lectura: lo llena el servidor con un JOIN para mostrarlo en pantalla.</summary>
    public string? DepartamentoNombre { get; set; }
    public string Unidad { get; set; } = "PZA";
    public bool PorPeso { get; set; }
    public decimal PrecioCosto { get; set; }
    /// <summary>Precio al público con impuestos incluidos.</summary>
    public decimal PrecioVenta { get; set; }
    /// <summary>Tasa de IVA en fracción (0.16 = 16%).</summary>
    public decimal TasaIva { get; set; }
    public decimal Existencia { get; set; }
    public decimal StockMinimo { get; set; }
    public bool Activo { get; set; } = true;

    /// <summary>Promociones que aplican hoy o más adelante (las terminadas no se envían).</summary>
    public List<Oferta> Ofertas { get; set; } = [];

    /// <summary>"🏷 3x2 · Oferta $15.00" con las promociones que aplican hoy; vacío si no hay.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string OfertaTexto => string.Join(" · ", Ofertas.Where(o => o.VigenteEn(DateTime.Today)).Select(o => "🏷 " + o.Descripcion));

    /// <summary>Solo avisa si se capturó un mínimo ("Avisarme cuando queden…").</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool BajoMinimo => Activo && StockMinimo > 0 && Existencia <= StockMinimo;

    // ---- Textos para mostrar en pantalla
    private static readonly System.Globalization.CultureInfo Mx = System.Globalization.CultureInfo.GetCultureInfo("es-MX");

    [System.Text.Json.Serialization.JsonIgnore]
    public string PrecioTexto => PorPeso ? $"{PrecioVenta.ToString("C2", Mx)} el kilo" : PrecioVenta.ToString("C2", Mx);

    [System.Text.Json.Serialization.JsonIgnore]
    public string HayTexto =>
        (PorPeso ? $"{Existencia.ToString("0.###", Mx)} kg" : $"{Existencia.ToString("0.###", Mx)} {(Existencia == 1 ? "pieza" : "piezas")}")
        + (BajoMinimo ? "   ⚠ quedan pocos" : "");
}

public sealed class Turno
{
    public long Id { get; set; }
    public int CajaId { get; set; }
    public int UsuarioId { get; set; }
    public string UsuarioNombre { get; set; } = "";
    public DateTime AbiertoEn { get; set; }
    public DateTime? CerradoEn { get; set; }
    public decimal FondoInicial { get; set; }
    public decimal? EfectivoDeclarado { get; set; }
    public string Estado { get; set; } = EstadosTurno.Abierto;
}

public sealed class MovimientoInventario
{
    public long Id { get; set; }
    /// <summary>En UTC (así la guarda PostgreSQL). Para mostrar usar <see cref="FechaLocal"/>.</summary>
    public DateTime Fecha { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public DateTime FechaLocal => Fecha.ToLocalTime();
    public string Tipo { get; set; } = "";
    public decimal Cantidad { get; set; }
    public decimal ExistenciaResultante { get; set; }
    public decimal? CostoUnitario { get; set; }
    public string? Referencia { get; set; }
    public string? Usuario { get; set; }
    public string? Nota { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public string TipoTexto => TiposMovimiento.Descripcion(Tipo);

    /// <summary>Folio de venta o nota capturada, lo que haya.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Detalle => string.Join(" · ", new[] { Referencia, Nota }.Where(s => !string.IsNullOrWhiteSpace(s)));
}
