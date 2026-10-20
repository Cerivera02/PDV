namespace PDV.Tests;

// La escala se prueba con la fórmula (la ventana completa se prueba con capturas).
public class PantallaCompletaTests
{
    [Theory]
    [InlineData(1920, 1080, 1.0)]     // monitor para el que se diseñó
    [InlineData(1366, 768, 0.7111)]   // laptop / caja común
    [InlineData(1280, 1024, 0.6667)]  // monitor cuadrado: manda el ancho
    [InlineData(1024, 768, 0.6)]      // muy chico: no baja de 0.6
    [InlineData(2560, 1440, 1.3333)]  // monitor grande: todo crece
    [InlineData(3840, 2160, 1.6)]     // 4K sin escala de Windows: no pasa de 1.6
    public void Escala_segun_el_monitor(double ancho, double alto, double esperada)
    {
        Assert.Equal(esperada, PDV.Caja.Ventanas.PantallaCompleta.CalcularEscala(ancho, alto), 3);
    }
}
