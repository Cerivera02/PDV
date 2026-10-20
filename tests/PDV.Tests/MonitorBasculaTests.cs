using PDV.Core;
using PDV.Hardware;

namespace PDV.Tests;

public class MonitorBasculaTests
{
    /// <summary>Báscula de prueba que devuelve lo que se le indique, o falla.</summary>
    private sealed class BasculaFalsa : IBascula
    {
        public decimal Peso { get; set; } = 1.25m;
        public bool Falla { get; set; }
        public int Lecturas;

        public Task<LecturaPeso> LeerPesoAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref Lecturas);
            if (Falla)
                throw new TimeoutException("La báscula no respondió en COM3.");
            return Task.FromResult(new LecturaPeso(Peso, true, $"{Peso}"));
        }

        public void Dispose() { }
    }

    [Fact]
    public async Task Lee_una_y_otra_vez_y_avisa_cada_lectura()
    {
        var bascula = new BasculaFalsa();
        await using var monitor = new MonitorBascula(bascula, TimeSpan.FromMilliseconds(20));
        var lecturas = 0;
        monitor.Lectura += _ => Interlocked.Increment(ref lecturas);
        monitor.Iniciar();
        await Task.Delay(300);
        Assert.True(lecturas >= 3, $"solo {lecturas} lecturas");
        Assert.Equal(1.25m, monitor.Ultima!.Kilogramos);
        Assert.Equal(1.25m, monitor.PesoEstableReciente(TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Avisa_cuando_falla_y_sigue_intentando()
    {
        var bascula = new BasculaFalsa { Falla = true };
        await using var monitor = new MonitorBascula(bascula, TimeSpan.FromMilliseconds(20));
        string? error = null;
        monitor.Falla += m => error = m;
        monitor.Iniciar();
        await Task.Delay(150);
        Assert.Contains("no respondió", error);
        bascula.Falla = false;   // se reconecta
        await Task.Delay(150);
        Assert.NotNull(monitor.Ultima);
    }

    [Fact]
    public async Task Al_cerrarse_deja_de_leer()
    {
        var bascula = new BasculaFalsa();
        var monitor = new MonitorBascula(bascula, TimeSpan.FromMilliseconds(10));
        monitor.Iniciar();
        await Task.Delay(100);
        await monitor.DisposeAsync();
        var despues = bascula.Lecturas;
        await Task.Delay(100);
        Assert.Equal(despues, bascula.Lecturas);
    }

    [Fact]
    public void Peso_cero_o_viejo_no_cuenta_como_estable()
    {
        var monitor = new MonitorBascula(new BasculaFalsa(), TimeSpan.FromSeconds(1));
        Assert.Null(monitor.PesoEstableReciente(TimeSpan.FromSeconds(1)));   // sin lecturas todavía
    }

    [Fact]
    public async Task Simulada_se_queda_estable_entre_cambios()
    {
        var s = new BasculaSimulada();
        await s.LeerPesoAsync();
        await Task.Delay(600);   // pasa el medio segundo en que "se mueve"
        var a = await s.LeerPesoAsync();
        var b = await s.LeerPesoAsync();
        Assert.True(a.Estable);
        Assert.Equal(a.Kilogramos, b.Kilogramos);
    }
}
