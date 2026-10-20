using System.Text;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Tests;

/// <summary>Precio cambiado a mano en una venta y permisos especiales.</summary>
public class PrecioManualTests
{
    private static readonly DateTime Hoy = new(2026, 10, 19);

    private static Producto Coca() => new()
    {
        Id = 1, Nombre = "Coca 600", PrecioVenta = 18.50m,
        Ofertas = [new Oferta { Tipo = TiposOferta.NxM, Cantidad = 3, Paga = 2 }]
    };

    private static Producto Jamon() => new() { Id = 2, Nombre = "Jamón", PrecioVenta = 150m, PorPeso = true };

    [Fact]
    public void Precio_bajado_a_mano_reemplaza_la_promocion()
    {
        var r = CalculadoraPrecios.AplicarPromociones([(Coca(), 3m, (decimal?)15m)], Hoy);
        Assert.Equal(45m, r[0].Importe);          // 3 × 15, sin el 3x2
        Assert.Equal(15m, r[0].PrecioUnitario);
        Assert.Equal(10.50m, r[0].Descuento);      // 55.50 − 45
        Assert.Equal(TiposOferta.Manual, r[0].TipoOferta);
        Assert.Equal("Precio cambiado", r[0].Promocion);
    }

    [Fact]
    public void Precio_subido_a_mano_da_descuento_negativo()
    {
        var r = CalculadoraPrecios.AplicarPromociones([(Jamon(), 0.5m, (decimal?)170m)], Hoy);
        Assert.Equal(85m, r[0].Importe);
        Assert.Equal(-10m, r[0].Descuento);
    }

    [Fact]
    public void Los_demas_renglones_siguen_con_su_promocion()
    {
        // Un renglón de Coca con precio cambiado y otro de 3 Cocas normal: el 3x2 solo cuenta las normales
        var r = CalculadoraPrecios.AplicarPromociones(
            [(Coca(), 1m, (decimal?)10m), (Coca(), 3m, (decimal?)null), (Jamon(), 1m, (decimal?)null)], Hoy);
        Assert.Equal(10m, r[0].Importe);
        Assert.Equal(37m, r[1].Importe);           // 3x2
        Assert.Equal("3x2", r[1].Promocion);
        Assert.Equal(150m, r[2].Importe);
    }

    [Fact]
    public void Sin_precios_manuales_da_lo_mismo_que_antes()
    {
        var conManual = CalculadoraPrecios.AplicarPromociones([(Coca(), 3m, (decimal?)null), (Jamon(), 0.75m, (decimal?)null)], Hoy);
        var antes = CalculadoraPrecios.AplicarPromociones([(Coca(), 3m), (Jamon(), 0.75m)], Hoy);
        Assert.Equal(antes, conManual);
    }

    [Fact]
    public void En_el_ticket_el_precio_subido_no_resta_al_ahorro()
    {
        var t = new TicketDto
        {
            Lineas =
            [
                new() { Descripcion = "Coca", Unidad = "PZA", Cantidad = 3, PrecioUnitario = 15, Importe = 45, Descuento = 10.50m, Promocion = "Precio cambiado" },
                new() { Descripcion = "Jamón", Unidad = "KG", Cantidad = 0.5m, PrecioUnitario = 170, Importe = 85, Descuento = -10, Promocion = "Precio cambiado" }
            ]
        };
        Assert.Equal(10.50m, t.Ahorro);

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var texto = TicketEscPos.VentaTexto(new ConfigImpresora { Ancho = 32, CodePage = 850 }, t);
        Assert.Contains("* Precio cambiado", texto);  // el que bajó muestra el precio normal y el descuento
        Assert.Contains("0.500 KG x $170.00", texto);  // el que subió muestra el precio cobrado
    }

    [Theory]
    [InlineData(Roles.Administrador, new string[0], true)]
    [InlineData(Roles.Cajero, new[] { Permisos.CambiarPrecio }, true)]
    [InlineData(Roles.Cajero, new[] { Permisos.CancelarVenta }, false)]
    [InlineData(Roles.Cajero, new string[0], false)]
    public void Quien_puede_cambiar_precios(string rol, string[] permisos, bool puede) =>
        Assert.Equal(puede, Permisos.Tiene(rol, permisos, Permisos.CambiarPrecio));

    [Theory]
    [InlineData(Roles.Administrador, new string[0], true)]
    [InlineData(Roles.Cajero, new[] { Permisos.SacarDinero }, true)]
    [InlineData(Roles.Cajero, new[] { Permisos.CambiarPrecio, Permisos.CancelarVenta }, false)]
    public void Quien_puede_sacar_dinero(string rol, string[] permisos, bool puede) =>
        Assert.Equal(puede, Permisos.Tiene(rol, permisos, Permisos.SacarDinero));

    [Theory]
    [InlineData(Roles.Administrador, new string[0], true)]
    [InlineData(Roles.Cajero, new[] { Permisos.SacarDinero }, true)]
    [InlineData(Roles.Cajero, new string[0], false)]
    public void Quien_necesita_clave_de_autorizacion(string rol, string[] permisos, bool necesita) =>
        Assert.Equal(necesita, Permisos.NecesitaClave(rol, permisos));

    [Fact]
    public void La_lista_de_personas_avisa_si_falta_la_clave()
    {
        static PDV.Caja.Ventanas.FilaPersona Fila(string rol, string[] permisos, bool clave) =>
            new(1, "Ana", "ana", "", rol, [], "Todas", permisos, clave);
        Assert.Equal("Todo", Fila(Roles.Administrador, [], true).PermisosTexto);
        Assert.Contains("falta su clave", Fila(Roles.Administrador, [], false).PermisosTexto);
        Assert.Equal("Sacar dinero  ⚠ falta su clave", Fila(Roles.Cajero, [Permisos.SacarDinero], false).PermisosTexto);
        Assert.Equal("—", Fila(Roles.Cajero, [], false).PermisosTexto);
    }

    [Fact]
    public void Todos_los_permisos_tienen_nombre_para_la_pantalla()
    {
        Assert.Contains(Permisos.SacarDinero, Permisos.Todos);
        foreach (var p in Permisos.Todos)
        {
            Assert.NotEqual(p, Permisos.Descripcion(p));
            Assert.NotEqual(p, Permisos.Corto(p));
        }
    }

    [Fact]
    public void Flecha_derecha_es_lo_contrario_de_la_izquierda()
    {
        Assert.Equal(AccionesFlecha.Precio, new ConfigVentaDto().FlechaDerecha);
        Assert.Equal(AccionesFlecha.Cantidad, new ConfigVentaDto { FlechaIzquierda = AccionesFlecha.Precio }.FlechaDerecha);
    }
}
