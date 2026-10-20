using System.IO.Ports;
using System.Printing;
using System.Windows;
using PDV.Caja.Servicios;
using PDV.Core;
using PDV.Hardware;

namespace PDV.Caja.Ventanas;

public partial class LoginWindow : Window
{
    private bool _entro;

    public LoginWindow()
    {
        InitializeComponent();
        MostrarCaja();
        Loaded += (_, _) => txtUsuario.Focus();
        Closed += (_, _) =>
        {
            if (!_entro)
                Application.Current.Shutdown();
        };
    }

    private string? _miIp;

    /// <summary>"Caja 2 · Esta computadora: 192.168.1.37" (la IP tal como la ve el servidor; sirve para registrar la caja).</summary>
    private async void MostrarCaja()
    {
        lblCaja.Text = $"Caja {Sesion.Config.CajaId}";
        try
        {
            _miIp = (await Sesion.Api.GetAsync<System.Text.Json.JsonElement>("api/mi-ip")).GetProperty("ip").GetString();
            lblCaja.Text = $"Caja {Sesion.Config.CajaId}  ·  Esta computadora: {_miIp}";
        }
        catch
        {
            // Sin servidor: el error real se verá al intentar entrar.
        }
    }

    private void MostrarError(string texto)
    {
        lblError.Text = texto;
        panelError.Visibility = texto.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    // Aviso de mayúsculas: la causa más común de "contraseña incorrecta".
    private void ActualizarAvisoMayusculas() =>
        lblMayusculas.Visibility = System.Windows.Input.Keyboard.IsKeyToggled(System.Windows.Input.Key.CapsLock)
            ? Visibility.Visible : Visibility.Collapsed;

    private void Password_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e) =>
        Dispatcher.BeginInvoke(ActualizarAvisoMayusculas);

    private void Password_GotKeyboardFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e) =>
        ActualizarAvisoMayusculas();

    private async void Entrar_Click(object sender, RoutedEventArgs e)
    {
        MostrarError("");
        if (txtUsuario.Text.Trim().Length == 0)
        {
            MostrarError("Escriba su usuario.");
            txtUsuario.Focus();
            return;
        }
        btnEntrar.IsEnabled = false;
        btnEntrar.Content = "Entrando…";
        try
        {
            var usuario = await Sesion.Api.PostAsync<LoginResponse>("api/auth/login",
                new LoginRequest(txtUsuario.Text, txtPassword.Password, Sesion.Config.CajaId));
            await Sesion.IniciarAsync(usuario!);

            Window principal = usuario!.Rol switch
            {
                Roles.Administrador => new MenuAdminWindow(),
                Roles.Capturista => new ProductosWindow(),
                _ => new VentaWindow()
            };
            _entro = true;
            App.AbrirPrincipal(principal);
            Close();
        }
        catch (ApiException ex)
        {
            MostrarError(ex.Message);
            txtPassword.Clear();
            txtPassword.Focus();
        }
        finally
        {
            btnEntrar.IsEnabled = true;
            btnEntrar.Content = "Entrar";
        }
    }

    /// <summary>Configuración local de esta caja (caja.json). Los parámetros finos de la báscula se editan en el archivo.</summary>
    private void Configurar_Click(object sender, RoutedEventArgs e)
    {
        var cfg = Sesion.Config;
        var impresoras = new List<string> { "(ninguna)" };
        try
        {
            using var servidor = new LocalPrintServer();
            impresoras.AddRange(servidor.GetPrintQueues([EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections])
                .Select(q => q.FullName));
        }
        catch { /* sin servicio de impresión: solo "(ninguna)" */ }

        var puertos = SerialPort.GetPortNames().Order().ToList();
        if (!puertos.Contains(cfg.Bascula.Puerto))
            puertos.Add(cfg.Bascula.Puerto);

        string[] anchos = ["Rollo chico (58 mm)", "Rollo grande (80 mm)"];
        var r = CampoDialog.MostrarCon(this, "Configuración de esta caja",
            "Normalmente solo se hace una vez, al instalar la caja. Si no está seguro, pregunte al encargado.",
            "Guardar",
            new Campo("Dirección del servidor", cfg.ServidorUrl, Ayuda: "En la caja principal: http://localhost:5080/   ·   En las demás: http://IP-de-la-principal:5080/"),
            new Campo("Número de esta caja", cfg.CajaId.ToString(),
                Ayuda: "La caja principal es la 1. En el servidor, esta caja debe estar registrada con la IP de esta computadora"
                       + (_miIp is null ? "." : $": {_miIp}")),
            new Campo("Impresora de tickets", string.IsNullOrEmpty(cfg.Impresora.Nombre) ? "(ninguna)" : cfg.Impresora.Nombre, Opciones: impresoras),
            new Campo("Tamaño del papel", cfg.Impresora.Ancho <= 32 ? anchos[0] : anchos[1], Opciones: anchos),
            new Campo("¿Abrir el cajón del dinero al cobrar?", cfg.Impresora.AbrirCajon ? "Sí" : "No", Opciones: ["Sí", "No"]),
            new Campo("Báscula (marca)", cfg.Bascula.Perfil, Opciones: PerfilesBascula.Nombres.ToList(), Ayuda: "\"Ninguna\" si no tiene báscula conectada a la computadora."),
            new Campo("Puerto de la báscula", cfg.Bascula.Puerto, Opciones: puertos, Editable: true, Ayuda: "Normalmente COM1, COM3…"),
            new Campo("Pantalla de venta en pantalla completa", cfg.PantallaCompleta ? "Sí" : "No", Opciones: ["Sí", "No"],
                Ayuda: "Sí = ocupa todo el monitor, sin barra de tareas (recomendado para cajas)."));
        if (r is null)
            return;

        if (!Uri.TryCreate(r[0], UriKind.Absolute, out _) || !int.TryParse(r[1], out var cajaId))
        {
            Mensajes.Error(this, "Revise la dirección del servidor (ejemplo: http://192.168.1.10:5080/) y el número de caja.");
            return;
        }

        cfg.ServidorUrl = r[0];
        cfg.CajaId = cajaId;
        cfg.Impresora.Nombre = r[2] == "(ninguna)" ? "" : r[2];
        cfg.Impresora.Ancho = r[3] == anchos[0] ? 32 : 48;
        cfg.Impresora.AbrirCajon = r[4] == "Sí";
        cfg.Bascula.Perfil = r[5];
        cfg.Bascula.Puerto = r[6];
        cfg.PantallaCompleta = r[7] == "Sí";
        cfg.Guardar();
        Sesion.Configurar(cfg);
        MostrarCaja();
        Mensajes.Exito(this, "Configuración guardada.");
    }
}
