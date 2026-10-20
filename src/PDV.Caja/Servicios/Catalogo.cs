using System.Globalization;
using PDV.Core;

namespace PDV.Caja.Servicios;

/// <summary>
/// Catálogo de productos en memoria: la búsqueda por código de barras es instantánea y no depende de la red.
/// Se mantiene al día con los avisos de SignalR del servidor.
/// </summary>
public sealed class Catalogo
{
    private static readonly CompareInfo Comparador = CultureInfo.GetCultureInfo("es-MX").CompareInfo;
    private readonly Lock _candado = new();
    private Dictionary<int, Producto> _porId = [];
    private Dictionary<string, Producto> _porCodigo = [];
    private Dictionary<string, Producto> _porPlu = [];

    public async Task CargarAsync(ApiClient api)
    {
        var productos = await api.GetAsync<List<Producto>>("api/productos") ?? [];
        lock (_candado)
        {
            _porId = productos.ToDictionary(p => p.Id);
            ReconstruirIndices();
        }
    }

    public void Actualizar(Producto p)
    {
        lock (_candado)
        {
            if (p.Activo)
                _porId[p.Id] = p;
            else
                _porId.Remove(p.Id);
            ReconstruirIndices();
        }
    }

    public Producto? PorCodigo(string codigo)
    {
        lock (_candado)
            return _porCodigo.GetValueOrDefault(codigo.Trim());
    }

    public Producto? PorPlu(string plu)
    {
        lock (_candado)
            return _porPlu.GetValueOrDefault(EtiquetaBascula.NormalizarPlu(plu));
    }

    /// <summary>Búsqueda por nombre sin distinguir mayúsculas ni acentos ("jamon" encuentra "Jamón").</summary>
    public List<Producto> Buscar(string texto, int maximo = 200)
    {
        lock (_candado)
        {
            var q = _porId.Values.AsEnumerable();
            if (!string.IsNullOrWhiteSpace(texto))
            {
                var t = texto.Trim();
                q = q.Where(p => p.CodigoBarras == t
                    || Comparador.IndexOf(p.Nombre, t, CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0);
            }
            return q.OrderBy(p => p.Nombre).Take(maximo).ToList();
        }
    }

    private void ReconstruirIndices()
    {
        _porCodigo = new Dictionary<string, Producto>(_porId.Count, StringComparer.Ordinal);
        _porPlu = new Dictionary<string, Producto>(StringComparer.Ordinal);
        foreach (var p in _porId.Values)
        {
            _porCodigo[p.CodigoBarras] = p;
            if (!string.IsNullOrEmpty(p.CodigoInterno))
                _porPlu[EtiquetaBascula.NormalizarPlu(p.CodigoInterno)] = p;
        }
    }
}
