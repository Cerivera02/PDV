using System.Globalization;
using System.IO.Ports;
using System.Text;
using System.Text.RegularExpressions;
using PDV.Core;

namespace PDV.Hardware;

/// <summary>
/// Configuración de la báscula de cada caja (caja.json). Con Perfil distinto de "Personalizado"
/// los parámetros de comunicación se toman del perfil; solo se respeta el Puerto.
/// </summary>
public sealed class ConfigBascula
{
    /// <summary>Ninguna, Simulada, Torrey, Rhino, CAS, Toledo o Personalizado.</summary>
    public string Perfil { get; set; } = "Ninguna";
    public string Puerto { get; set; } = "COM3";
    public int Baudios { get; set; } = 9600;
    public Parity Paridad { get; set; } = Parity.None;
    public int BitsDatos { get; set; } = 8;
    public StopBits BitsParada { get; set; } = StopBits.One;
    /// <summary>Texto que se envía para solicitar el peso (admite escapes: "\r", "\x05"). Vacío = la báscula transmite sola.</summary>
    public string Comando { get; set; } = "P";
    /// <summary>Expresión regular con un grupo llamado "peso".</summary>
    public string Patron { get; set; } = @"(?<peso>[-+]?\d+(?:[.,]\d+)?)";
    /// <summary>Unidad que envía la báscula: kg, g o lb. Siempre se convierte a kg.</summary>
    public string Unidad { get; set; } = "kg";
    public int TimeoutMs { get; set; } = 800;
}

public static class PerfilesBascula
{
    public const string Ninguna = "Ninguna";
    public const string Simulada = "Simulada";
    public const string Personalizado = "Personalizado";

    // Valores típicos de fábrica. Verifíquelos contra el manual de la báscula y, si no coinciden,
    // use el perfil "Personalizado" y la opción "Probar báscula" del menú de administrador.
    private static readonly Dictionary<string, ConfigBascula> Perfiles = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Torrey"] = new() { Baudios = 9600, Paridad = Parity.None, BitsDatos = 8, BitsParada = StopBits.One, Comando = "P" },
        ["Rhino"] = new() { Baudios = 9600, Paridad = Parity.None, BitsDatos = 8, BitsParada = StopBits.One, Comando = "P" },
        ["CAS"] = new() { Baudios = 9600, Paridad = Parity.None, BitsDatos = 8, BitsParada = StopBits.One, Comando = "" },
        ["Toledo"] = new() { Baudios = 9600, Paridad = Parity.Even, BitsDatos = 7, BitsParada = StopBits.One, Comando = "W" },
    };

    public static IEnumerable<string> Nombres => [Ninguna, Simulada, .. Perfiles.Keys, Personalizado];

    public static IBascula? Crear(ConfigBascula cfg)
    {
        if (cfg.Perfil.Equals(Ninguna, StringComparison.OrdinalIgnoreCase))
            return null;
        if (cfg.Perfil.Equals(Simulada, StringComparison.OrdinalIgnoreCase))
            return new BasculaSimulada();
        if (cfg.Perfil.Equals(Personalizado, StringComparison.OrdinalIgnoreCase))
            return new BasculaSerial(cfg);
        if (!Perfiles.TryGetValue(cfg.Perfil, out var perfil))
            throw new ArgumentException($"Perfil de báscula desconocido: {cfg.Perfil}");

        return new BasculaSerial(new ConfigBascula
        {
            Perfil = cfg.Perfil,
            Puerto = cfg.Puerto,
            Baudios = perfil.Baudios,
            Paridad = perfil.Paridad,
            BitsDatos = perfil.BitsDatos,
            BitsParada = perfil.BitsParada,
            Comando = perfil.Comando,
            Patron = perfil.Patron,
            Unidad = perfil.Unidad,
            TimeoutMs = cfg.TimeoutMs
        });
    }
}

/// <summary>
/// Báscula por puerto serie (RS-232 o USB con adaptador/driver COM virtual).
/// Funciona en modo "solicitud" (envía Comando y espera respuesta) o "continuo" (Comando vacío).
/// </summary>
public sealed class BasculaSerial : IBascula
{
    private static readonly char[] Terminadores = ['\r', '\n', '\x03'];
    private readonly ConfigBascula _cfg;
    private readonly SerialPort _puerto;
    private readonly Regex _patron;
    private readonly Lock _candado = new();

    public BasculaSerial(ConfigBascula cfg)
    {
        _cfg = cfg;
        _patron = new Regex(cfg.Patron, RegexOptions.Compiled);
        _puerto = new SerialPort(cfg.Puerto, cfg.Baudios, cfg.Paridad, cfg.BitsDatos, cfg.BitsParada)
        {
            Encoding = Encoding.Latin1,
            ReadTimeout = cfg.TimeoutMs,
            WriteTimeout = cfg.TimeoutMs
        };
    }

    public Task<LecturaPeso> LeerPesoAsync(CancellationToken ct = default) => Task.Run(() =>
    {
        lock (_candado)
        {
            if (!_puerto.IsOpen)
                _puerto.Open();

            // Dos lecturas consecutivas iguales = peso estable.
            var (primera, _) = LeerUna(ct);
            var (segunda, crudo) = LeerUna(ct);
            return new LecturaPeso(segunda, Math.Abs(primera - segunda) < 0.0005m, crudo);
        }
    }, ct);

    private (decimal Kg, string Crudo) LeerUna(CancellationToken ct)
    {
        _puerto.DiscardInBuffer();
        if (!string.IsNullOrEmpty(_cfg.Comando))
            _puerto.Write(Regex.Unescape(_cfg.Comando));

        var buffer = new StringBuilder();
        var limite = DateTime.UtcNow.AddMilliseconds(_cfg.TimeoutMs);
        while (DateTime.UtcNow < limite)
        {
            ct.ThrowIfCancellationRequested();
            if (_puerto.BytesToRead == 0)
            {
                Thread.Sleep(10);
                continue;
            }

            buffer.Append(_puerto.ReadExisting());
            // Solo se consideran tramas completas (seguidas de un terminador).
            var tramas = buffer.ToString().Split(Terminadores);
            for (var i = tramas.Length - 2; i >= 0; i--)
            {
                if (TryInterpretar(tramas[i], out var kg))
                    return (kg, buffer.ToString());
            }
        }

        // Algunas básculas no mandan terminador: intentar con todo lo recibido.
        var crudo = buffer.ToString();
        if (TryInterpretar(crudo, out var peso))
            return (peso, crudo);
        throw new TimeoutException(crudo.Length == 0
            ? $"La báscula no respondió en {_cfg.Puerto}."
            : $"Respuesta no reconocida de la báscula: \"{Visible(crudo)}\"");
    }

    private bool TryInterpretar(string trama, out decimal kg)
    {
        kg = 0;
        var m = _patron.Matches(trama).LastOrDefault();
        if (m is null || !m.Groups["peso"].Success)
            return false;
        if (!decimal.TryParse(m.Groups["peso"].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var valor))
            return false;

        kg = Dinero.RedondearCantidad(_cfg.Unidad.ToLowerInvariant() switch
        {
            "g" => valor / 1000m,
            "lb" => valor * 0.45359237m,
            _ => valor
        });
        return true;
    }

    /// <summary>Muestra caracteres de control para diagnóstico ("\x02 1.250kg\r").</summary>
    public static string Visible(string s) =>
        string.Concat(s.Select(c => char.IsControl(c) ? $"\\x{(int)c:X2}" : c.ToString()));

    public void Dispose() => _puerto.Dispose();
}

/// <summary>
/// Báscula falsa para desarrollo sin hardware. Se comporta como una real: cada 6 segundos "ponen"
/// otra cosa encima; durante medio segundo el peso se mueve y luego se queda estable.
/// </summary>
public sealed class BasculaSimulada : IBascula
{
    private static readonly TimeSpan CadaCuanto = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan SeMueve = TimeSpan.FromMilliseconds(500);
    private readonly Lock _candado = new();
    private decimal _peso;
    private DateTime _cambio = DateTime.MinValue;

    public Task<LecturaPeso> LeerPesoAsync(CancellationToken ct = default)
    {
        lock (_candado)
        {
            var ahora = DateTime.UtcNow;
            if (ahora - _cambio > CadaCuanto)
            {
                _peso = Math.Round(Random.Shared.Next(100, 2500) / 1000m, 3);
                _cambio = ahora;
            }
            var estable = ahora - _cambio > SeMueve;
            var kg = estable ? _peso : Math.Round(_peso + (decimal)(Random.Shared.NextDouble() - 0.5) * 0.05m, 3);
            return Task.FromResult(new LecturaPeso(kg, estable, $"SIMULADA {kg:0.000}kg"));
        }
    }

    public void Dispose() { }
}
