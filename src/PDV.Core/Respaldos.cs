using System.Globalization;
using System.Text.Json.Serialization;

namespace PDV.Core;

/// <summary>Una copia de la base de datos guardada por el servidor.</summary>
public sealed class RespaldoDto
{
    private static readonly CultureInfo Mx = CultureInfo.GetCultureInfo("es-MX");

    /// <summary>Nombre del archivo (pdv_20261019_213005.backup). Es lo que se manda para restaurar.</summary>
    public string Nombre { get; set; } = "";
    public DateTimeOffset Fecha { get; set; }
    /// <summary>Por qué se hizo: "Se cerró el programa de la Caja 1", "Hecho a mano por…", "Automático antes de restaurar…".</summary>
    public string Motivo { get; set; } = "";
    public long Bytes { get; set; }

    [JsonIgnore] public DateTime FechaLocal => Fecha.LocalDateTime;
    [JsonIgnore] public string TamanoTexto => Tamano(Bytes);

    /// <summary>"hace 5 minutos", "hace 3 horas", "hace 2 días".</summary>
    [JsonIgnore] public string HaceTexto => Hace(DateTimeOffset.Now - Fecha);

    public static string Tamano(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{(bytes / 1024.0).ToString("0", Mx)} KB",
        < 1024L * 1024 * 1024 => $"{(bytes / 1024.0 / 1024).ToString("0.0", Mx)} MB",
        _ => $"{(bytes / 1024.0 / 1024 / 1024).ToString("0.0", Mx)} GB"
    };

    public static string Hace(TimeSpan t) => t.TotalMinutes switch
    {
        < 1 => "hace un momento",
        < 2 => "hace 1 minuto",
        < 60 => $"hace {(int)t.TotalMinutes} minutos",
        < 120 => "hace 1 hora",
        < 60 * 24 => $"hace {(int)t.TotalHours} horas",
        < 60 * 48 => "ayer",
        _ => $"hace {(int)t.TotalDays} días"
    };
}

/// <summary>Lista de respaldos y la carpeta donde están (en la computadora del servidor).</summary>
public sealed class ListaRespaldos
{
    public string Carpeta { get; set; } = "";
    public List<RespaldoDto> Respaldos { get; set; } = [];
}

/// <summary>La caja avisa que se está cerrando el programa: el servidor hace un respaldo.</summary>
public sealed record AvisoCierreRequest(int CajaId);

/// <summary>Restaurar pide la clave de autorización de un encargado.</summary>
public sealed record RestaurarRespaldoRequest(string Nombre, string ClaveAutorizacion);

/// <summary>Qué respaldos viejos se borran.</summary>
public static class RetencionRespaldos
{
    /// <summary>
    /// Se conservan todos los de los últimos <paramref name="dias"/> días, pero nunca más de <paramref name="maximo"/>,
    /// y siempre los <paramref name="minimo"/> más recientes (aunque sean viejos: si la tienda cerró un mes, no se queda sin respaldos).
    /// </summary>
    /// <returns>Los nombres que se pueden borrar.</returns>
    public static List<string> ParaBorrar(IEnumerable<(string Nombre, DateTimeOffset Fecha)> respaldos, DateTimeOffset ahora,
        int dias = 30, int minimo = 10, int maximo = 300)
    {
        var ordenados = respaldos.OrderByDescending(r => r.Fecha).ToList();
        var limite = ahora.AddDays(-dias);
        return ordenados
            .Select((r, i) => (r.Nombre, r.Fecha, Posicion: i))
            .Where(r => r.Posicion >= minimo && (r.Fecha < limite || r.Posicion >= maximo))
            .Select(r => r.Nombre)
            .ToList();
    }
}
