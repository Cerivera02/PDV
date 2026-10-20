using System.Globalization;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Threading;
using PDV.Caja.Servicios;
using PDV.Caja.Ventanas;

namespace PDV.Caja;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        Ventanas.Select.Registrar(); // la ✕ de las listas para elegir

        // Formatos de México ($, fechas dd/MM/yyyy) en toda la interfaz.
        CultureInfo.DefaultThreadCurrentCulture = Formato.Mx;
        CultureInfo.DefaultThreadCurrentUICulture = Formato.Mx;
        FrameworkElement.LanguageProperty.OverrideMetadata(typeof(FrameworkElement),
            new FrameworkPropertyMetadata(XmlLanguage.GetLanguage(Formato.Mx.IetfLanguageTag)));

        DispatcherUnhandledException += AlErrorNoControlado;

        // Logo en la barra de título de todas las ventanas (incluye diálogos creados en código)
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((s, _) =>
        {
            if (s is Window { Icon: null } w)
                w.Icon = (System.Windows.Media.ImageSource)Current.FindResource("Icono");
        }));

        Sesion.Configurar(ConfigCaja.Cargar());
        ApiClient.SinAccesoACaja += AlPerderAcceso;
        Sesion.BaseRestaurada += m => CortarSesion(m, error: false);
        MostrarLogin();
        // Calendarios de HandyControl (~0.4 s): se cargan ya con la pantalla de entrada a la vista, que no tiene calendarios
        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => Calendario.Aplicar(this));
    }

    public static void MostrarLogin()
    {
        Sesion.SesionCortada = false;
        new LoginWindow().Show();
    }

    /// <summary>
    /// El servidor bloqueó la caja (o la persona perdió acceso) durante la sesión:
    /// se avisa una sola vez, se cierran todas las ventanas y se vuelve a la pantalla de entrada.
    /// </summary>
    private static void AlPerderAcceso(string mensaje) => CortarSesion(mensaje, error: true);

    /// <summary>
    /// Cierra la sesión desde afuera (caja bloqueada, respaldo restaurado…): un solo aviso, se cierran
    /// todas las ventanas y se vuelve a la pantalla de entrada.
    /// </summary>
    public static void CortarSesion(string mensaje, bool error)
    {
        if (Sesion.Usuario is null || Sesion.SesionCortada)
            return; // en el login el mensaje ya se muestra en pantalla
        Sesion.SesionCortada = true;
        Current.Dispatcher.BeginInvoke(() =>
        {
            if (error)
                Mensajes.Error(null, mensaje, forzar: true);
            else
                Mensajes.Info(null, mensaje);
            // Primero las ventanas "hijas" (diálogos), al final la principal → su Closed regresa al login
            foreach (var w in Current.Windows.Cast<Window>().Reverse().ToList())
                w.Close();
        });
    }

    /// <summary>Abre la ventana principal del rol; al cerrarla se cierra la sesión y vuelve el login.</summary>
    public static void AbrirPrincipal(Window ventana)
    {
        ventana.Closed += async (_, _) =>
        {
            await Sesion.CerrarAsync();
            MostrarLogin();
        };
        ventana.Show();
    }

    /// <summary>Cada vez que se cierra el programa, el servidor guarda un respaldo de la base de datos.</summary>
    protected override void OnExit(ExitEventArgs e)
    {
        Sesion.AvisarCierre();
        base.OnExit(e);
    }

    /// <summary>Errores inesperados con todo el detalle, para soporte: %LOCALAPPDATA%\PDV\errores.log</summary>
    public static string RutaBitacoraErrores => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDV", "errores.log");

    private static void RegistrarError(Exception ex)
    {
        try
        {
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(RutaBitacoraErrores)!);
            System.IO.File.AppendAllText(RutaBitacoraErrores,
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · caja {Sesion.Config?.CajaId} · {Sesion.Usuario?.Nombre}{Environment.NewLine}{ex}{Environment.NewLine}");
        }
        catch
        {
            // Nunca romper la app por no poder escribir la bitácora.
        }
    }

    private static void AlErrorNoControlado(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (e.Exception is not ApiException)
            RegistrarError(e.Exception);
        Mensajes.Error(null, e.Exception is ApiException
            ? e.Exception.Message
            : $"Algo salió mal. Intente de nuevo; si sigue pasando, avise al encargado.\n\n(Detalle: {e.Exception.Message})");
        e.Handled = true;
    }
}
