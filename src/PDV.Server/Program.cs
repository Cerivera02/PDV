using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.IdentityModel.Tokens;
using PDV.Data;
using PDV.Server;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    // Como servicio de Windows el directorio actual es System32; usar la carpeta del ejecutable.
    ContentRootPath = WindowsServiceHelpers.IsWindowsService() ? AppContext.BaseDirectory : default
});

builder.Services.AddWindowsService(o => o.ServiceName = "PDV Servidor");

// ---- Datos
var cadena = builder.Configuration.GetConnectionString("Pdv")
    ?? throw new InvalidOperationException("Falta ConnectionStrings:Pdv en appsettings.json");
builder.Services.AddSingleton(new Db(cadena));
builder.Services.AddSingleton<UsuarioRepositorio>();
builder.Services.AddSingleton<ProductoRepositorio>();
builder.Services.AddSingleton<DepartamentoRepositorio>();
builder.Services.AddSingleton<OfertaRepositorio>();
builder.Services.AddSingleton<ClienteRepositorio>();
builder.Services.AddSingleton<InventarioRepositorio>();
builder.Services.AddSingleton<CajaRepositorio>();
builder.Services.AddSingleton<ConfiguracionRepositorio>();
builder.Services.AddSingleton<TurnoRepositorio>();
builder.Services.AddSingleton<VentaRepositorio>();
builder.Services.AddSingleton<AccesoCajas>();
// Respaldos: se hacen en segundo plano cuando una caja avisa que se cierra el programa
builder.Services.AddSingleton<Respaldos>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<Respaldos>());

// ---- Autenticación JWT
var claveJwt = ClaveJwt.Obtener(builder.Configuration, builder.Environment.ContentRootPath);
builder.Services.AddSingleton(new Autorizaciones(claveJwt));
builder.Services.AddSingleton(new GeneradorToken(claveJwt, TimeSpan.FromHours(builder.Configuration.GetValue("Jwt:HorasVigencia", 14))));
builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = false,
            ValidateAudience = false,
            IssuerSigningKey = new SymmetricSecurityKey(claveJwt),
            NameClaimType = ClaimTypes.Name,
            RoleClaimType = ClaimTypes.Role,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
        // SignalR (WebSockets) manda el token en la query string
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var token = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(token) && ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = token;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorizationBuilder()
    .AddPolicy(Politicas.Admin, p => p.RequireRole(PDV.Core.Roles.Administrador))
    .AddPolicy(Politicas.Inventario, p => p.RequireRole(PDV.Core.Roles.Administrador, PDV.Core.Roles.Capturista))
    .AddPolicy(Politicas.Venta, p => p.RequireRole(PDV.Core.Roles.Administrador, PDV.Core.Roles.Cajero));

builder.Services.AddSignalR();

var app = builder.Build();

// ---- Esquema de BD + usuario admin inicial
if (await EsquemaInicializador.AplicarAsync(app.Services.GetRequiredService<Db>()))
    app.Logger.LogWarning("Se creó el usuario '{Usuario}' con contraseña '{Password}'. CÁMBIELA de inmediato.",
        EsquemaInicializador.AdminLogin, EsquemaInicializador.AdminPasswordInicial);

app.UseMiddleware<ManejoErrores>();
app.UseMiddleware<EnMantenimiento>();      // restaurando un respaldo → 503
app.UseAuthentication();
app.UseMiddleware<ControlAccesoCajas>();   // caja bloqueada / IP / persona sin acceso → 403
app.UseAuthorization();

app.MapGet("/", () => Results.Ok(new { servicio = "PDV Servidor", hora = DateTime.Now }));
app.MapHub<PdvHub>("/hubs/pdv");
app.MapPdvEndpoints();

app.Run();
