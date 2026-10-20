using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Caja.Servicios;

/// <summary>Configuración local de cada caja: caja.json junto al ejecutable.</summary>
public sealed class ConfigCaja
{
    public string ServidorUrl { get; set; } = "http://localhost:5080/";
    public int CajaId { get; set; } = 1;
    /// <summary>La pantalla de venta ocupa todo el monitor (sin barra de título ni barra de tareas).</summary>
    public bool PantallaCompleta { get; set; } = true;
    public ConfigImpresora Impresora { get; set; } = new();
    public ConfigBascula Bascula { get; set; } = new();
    public ConfigEtiquetaBascula EtiquetaBascula { get; set; } = new();

    private static readonly JsonSerializerOptions Opciones = new()
    {
        WriteIndented = true,
        // Deja legibles los <, + y acentos para que el archivo se pueda editar a mano.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() }
    };

    public static string Ruta => Path.Combine(AppContext.BaseDirectory, "caja.json");

    public static ConfigCaja Cargar()
    {
        if (!File.Exists(Ruta))
        {
            var nueva = new ConfigCaja();
            nueva.Guardar();
            return nueva;
        }
        return JsonSerializer.Deserialize<ConfigCaja>(File.ReadAllText(Ruta), Opciones) ?? new ConfigCaja();
    }

    public void Guardar() => File.WriteAllText(Ruta, JsonSerializer.Serialize(this, Opciones));
}
