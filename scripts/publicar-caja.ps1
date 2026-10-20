# Genera la carpeta de la app de caja lista para copiar a cada computadora (no requiere instalar .NET).
#   powershell -ExecutionPolicy Bypass -File scripts\publicar-caja.ps1
param([string]$Destino = ".\publicado\Caja")
$ErrorActionPreference = 'Stop'
dotnet publish src\PDV.Caja -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o $Destino
Write-Host "Listo: copie la carpeta $Destino a cada caja y ejecute PDV.Caja.exe." -ForegroundColor Green
Write-Host "La primera vez use 'Configurar caja' para indicar la IP del servidor, el número de caja, impresora y báscula." -ForegroundColor Yellow
