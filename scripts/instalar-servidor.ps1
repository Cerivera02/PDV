# Instala PDV.Server como servicio de Windows en la CAJA MAESTRA.
# Ejecutar en PowerShell como Administrador desde la carpeta raíz del proyecto:
#   powershell -ExecutionPolicy Bypass -File scripts\instalar-servidor.ps1
param(
    [string]$Destino = "C:\PDV\Servidor",
    [int]$Puerto = 5080
)
$ErrorActionPreference = 'Stop'
$servicio = 'PDV Servidor'

if (Get-Service $servicio -ErrorAction SilentlyContinue) {
    Stop-Service $servicio
}

dotnet publish src\PDV.Server -c Release -r win-x64 --self-contained true -o $Destino

if (-not (Get-Service $servicio -ErrorAction SilentlyContinue)) {
    New-Service -Name $servicio -BinaryPathName "`"$Destino\PDV.Server.exe`"" -StartupType Automatic `
        -Description 'API del punto de venta (ventas, inventario, cortes).'
    # Reiniciar automáticamente si el servicio falla
    sc.exe failure "$servicio" reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
}

# Permitir que las demás cajas de la red se conecten
if (-not (Get-NetFirewallRule -DisplayName $servicio -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $servicio -Direction Inbound -Protocol TCP -LocalPort $Puerto -Profile Private, Domain -Action Allow | Out-Null
}

Start-Service $servicio
Write-Host "Servicio '$servicio' instalado en $Destino y escuchando en el puerto $Puerto." -ForegroundColor Green
Write-Host "Revise la cadena de conexión en $Destino\appsettings.json antes de usarlo." -ForegroundColor Yellow
