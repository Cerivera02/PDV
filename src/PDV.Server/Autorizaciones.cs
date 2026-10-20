using System.Security.Cryptography;
using System.Text;

namespace PDV.Server;

/// <summary>
/// Autorización del encargado para algo puntual en la caja de otra persona (por ejemplo, cambiar un precio).
/// El encargado teclea su clave de autorización; el servidor responde una "firma" que la caja
/// manda junto con la operación. Así la clave no se guarda en la caja y la firma no se puede inventar.
/// Formato: usuarioId.permiso.expiraUnix.firmaHmac
/// </summary>
public sealed class Autorizaciones(byte[] clave)
{
    /// <summary>Una cuenta puede quedarse en espera un buen rato: la autorización dura un turno.</summary>
    public static readonly TimeSpan Vigencia = TimeSpan.FromHours(12);

    public string Emitir(int usuarioId, string permiso)
    {
        var expira = DateTimeOffset.UtcNow.Add(Vigencia).ToUnixTimeSeconds();
        var datos = $"{usuarioId}.{permiso}.{expira}";
        return $"{datos}.{Firma(datos)}";
    }

    /// <returns>Quién autorizó, o null si la firma no es válida, ya venció o es de otro permiso.</returns>
    public int? Validar(string? token, string permiso)
    {
        var partes = token?.Split('.');
        if (partes is not { Length: 4 })
            return null;
        var datos = string.Join('.', partes[..3]);
        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Firma(datos)), Encoding.ASCII.GetBytes(partes[3])))
            return null;
        if (partes[1] != permiso || !long.TryParse(partes[2], out var expira) || DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expira)
            return null;
        return int.TryParse(partes[0], out var id) ? id : null;
    }

    // ---- Intentos fallidos: la clave de autorización es corta y no lleva usuario, así que se limita cuántas
    //      veces seguidas se puede equivocar una caja (la sesión que pide la autorización).
    public const int IntentosPermitidos = 5;
    public static readonly TimeSpan Espera = TimeSpan.FromMinutes(2);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, (int Fallos, DateTimeOffset Hasta)> _fallos = new();

    /// <summary>Cuánto falta para poder volver a intentar (null = puede intentar ya).</summary>
    public TimeSpan? Bloqueada(int quienPide, DateTimeOffset? ahora = null)
    {
        var t = ahora ?? DateTimeOffset.UtcNow;
        return _fallos.TryGetValue(quienPide, out var f) && f.Fallos >= IntentosPermitidos && f.Hasta > t ? f.Hasta - t : null;
    }

    public void Fallo(int quienPide, DateTimeOffset? ahora = null)
    {
        var t = ahora ?? DateTimeOffset.UtcNow;
        _fallos.AddOrUpdate(quienPide, (1, t + Espera),
            (_, f) => f.Hasta <= t ? (1, t + Espera) : (f.Fallos + 1, t + Espera));
    }

    public void Acierto(int quienPide) => _fallos.TryRemove(quienPide, out _);

    private string Firma(string datos) =>
        Convert.ToBase64String(HMACSHA256.HashData(clave, Encoding.UTF8.GetBytes(datos))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
