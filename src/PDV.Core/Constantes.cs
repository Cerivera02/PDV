namespace PDV.Core;

public static class Roles
{
    public const string Administrador = "Administrador";
    public const string Capturista = "Capturista";
    public const string Cajero = "Cajero";

    public static readonly string[] Todos = [Administrador, Capturista, Cajero];

    /// <summary>Explicación del rol en palabras simples, para mostrar en pantalla.</summary>
    public static string Descripcion(string rol) => rol switch
    {
        Administrador => "Encargado (puede hacer todo)",
        Capturista => "Capturista (productos y mercancía)",
        Cajero => "Cajero (solo cobra)",
        _ => rol
    };
}

/// <summary>
/// Permisos especiales que el encargado le puede dar a una persona (además de lo que permite su rol).
/// El encargado (Administrador) siempre los tiene todos.
/// </summary>
public static class Permisos
{
    /// <summary>Cambiar el precio de un artículo en una venta sin pedir la clave del encargado.</summary>
    public const string CambiarPrecio = "CAMBIAR_PRECIO";
    /// <summary>Cancelar ventas sin pedir la clave del encargado.</summary>
    public const string CancelarVenta = "CANCELAR_VENTA";
    /// <summary>Sacar dinero de la caja sin pedir la clave del encargado.</summary>
    public const string SacarDinero = "SACAR_DINERO";

    public static readonly string[] Todos = [CambiarPrecio, CancelarVenta, SacarDinero];

    public static string Descripcion(string permiso) => permiso switch
    {
        CambiarPrecio => "Cambiar el precio de un artículo en la venta",
        CancelarVenta => "Cancelar ventas ya cobradas",
        SacarDinero => "Sacar dinero de la caja",
        _ => permiso
    };

    /// <summary>"Cambiar precios", "Cancelar ventas" (corto, para la lista de personas).</summary>
    public static string Corto(string permiso) => permiso switch
    {
        CambiarPrecio => "Cambiar precios",
        CancelarVenta => "Cancelar ventas",
        SacarDinero => "Sacar dinero",
        _ => permiso
    };

    public static bool Tiene(string rol, IEnumerable<string>? permisos, string permiso) =>
        rol == Roles.Administrador || (permisos?.Contains(permiso) ?? false);

    /// <summary>Quien puede autorizar (el encargado o alguien con permisos especiales) necesita una clave de autorización.</summary>
    public static bool NecesitaClave(string rol, IEnumerable<string>? permisos) =>
        rol == Roles.Administrador || (permisos?.Any() ?? false);

    public const int LargoMinimoClave = 4;
}

/// <summary>Qué hacen las flechas izquierda y derecha en la pantalla de Vender (lo elige el encargado).</summary>
public static class AccionesFlecha
{
    public const string Cantidad = "CANTIDAD";
    public const string Precio = "PRECIO";
}

public static class TiposMovimiento
{
    public const string Entrada = "ENTRADA";
    public const string Venta = "VENTA";
    public const string Ajuste = "AJUSTE";
    public const string Merma = "MERMA";
    public const string Cancelacion = "CANCELACION";

    /// <summary>Tipos que se capturan manualmente (VENTA y CANCELACION los genera el sistema).</summary>
    public static readonly string[] Manuales = [Entrada, Ajuste, Merma];

    /// <summary>Nombre del movimiento en palabras simples, para mostrar en pantalla.</summary>
    public static string Descripcion(string tipo) => tipo switch
    {
        Entrada => "Llegó mercancía",
        Venta => "Se vendió",
        Ajuste => "Se contó",
        Merma => "Se echó a perder",
        Cancelacion => "Venta cancelada (regresó)",
        _ => tipo
    };
}

public static class FormasPago
{
    public const string Efectivo = "EFECTIVO";
    public const string Tarjeta = "TARJETA";
    public const string Transferencia = "TRANSFERENCIA";
    /// <summary>Crédito: se suma a lo que debe el cliente.</summary>
    public const string Credito = "CREDITO";

    public static readonly string[] Todas = [Efectivo, Tarjeta, Transferencia, Credito];

    /// <summary>Formas con las que un cliente puede abonar (no se abona "a crédito").</summary>
    public static readonly string[] ParaAbonos = [Efectivo, Tarjeta, Transferencia];

    public static string Texto(string forma) => forma switch
    {
        Efectivo => "Efectivo",
        Tarjeta => "Tarjeta",
        Transferencia => "Transferencia",
        Credito => "Crédito",
        _ => forma
    };
}

public static class EstadosTurno
{
    public const string Abierto = "ABIERTO";
    public const string Cerrado = "CERRADO";
}

public static class EstadosVenta
{
    public const string Pagada = "PAGADA";
    public const string Cancelada = "CANCELADA";
}

public static class Dinero
{
    /// <summary>Redondeo comercial a 2 decimales (nunca usar float/double para dinero).</summary>
    public static decimal Redondear(decimal valor) => Math.Round(valor, 2, MidpointRounding.AwayFromZero);

    /// <summary>Redondeo de cantidades (piezas o kilos) a 3 decimales.</summary>
    public static decimal RedondearCantidad(decimal valor) => Math.Round(valor, 3, MidpointRounding.AwayFromZero);
}

/// <summary>Error de validación o de regla de negocio; el servidor lo devuelve como HTTP 400.</summary>
public sealed class ReglaNegocioException(string mensaje) : Exception(mensaje);
