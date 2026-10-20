using PDV.Core;

namespace PDV.Hardware;

/// <summary>
/// Lee la báscula una y otra vez en segundo plano y avisa cada lectura (para mostrar el peso en vivo).
/// Los eventos llegan en un hilo de fondo: la pantalla debe pasarlos a su hilo (Dispatcher).
/// </summary>
public sealed class MonitorBascula(IBascula bascula, TimeSpan intervalo) : IAsyncDisposable
{
    private CancellationTokenSource? _cts;
    private Task? _ciclo;

    /// <summary>Cada lectura correcta.</summary>
    public event Action<LecturaPeso>? Lectura;
    /// <summary>Cuando no se pudo leer (báscula apagada, desconectada…).</summary>
    public event Action<string>? Falla;

    /// <summary>Última lectura correcta y cuándo llegó (UTC).</summary>
    public LecturaPeso? Ultima { get; private set; }
    public DateTime UltimaEn { get; private set; }

    /// <summary>Última lectura si es estable, mayor a cero y de hace menos de <paramref name="antiguedad"/>.</summary>
    public decimal? PesoEstableReciente(TimeSpan antiguedad) =>
        Ultima is { Estable: true, Kilogramos: > 0 } l && DateTime.UtcNow - UltimaEn <= antiguedad ? l.Kilogramos : null;

    public void Iniciar()
    {
        if (_ciclo is not null)
            return;
        _cts = new CancellationTokenSource();
        _ciclo = Task.Run(() => CicloAsync(_cts.Token));
    }

    private async Task CicloAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var l = await bascula.LeerPesoAsync(ct);
                Ultima = l;
                UltimaEn = DateTime.UtcNow;
                Lectura?.Invoke(l);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception e)
            {
                Falla?.Invoke(e.Message);
            }

            try
            {
                await Task.Delay(intervalo, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_cts is null)
            return;
        await _cts.CancelAsync();
        try
        {
            if (_ciclo is not null)
                await _ciclo;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}
