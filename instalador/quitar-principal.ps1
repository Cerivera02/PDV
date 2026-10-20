# Al desinstalar la computadora principal: detiene y quita los servicios y la regla del firewall.
# NO borra los datos ($Raiz\Datos), la configuración ni los respaldos: si se vuelve a instalar, todo sigue ahí.
param(
    [string]$Raiz = "C:\PDV",
    [string]$ServicioBD = "PDV Base de datos",
    [string]$ServicioServidor = "PDV Servidor"
)
$ErrorActionPreference = 'Continue'
Start-Transcript -Path (Join-Path $Raiz "instalacion.log") -Append | Out-Null
foreach ($s in $ServicioServidor, $ServicioBD) {
    if (Get-Service $s -ErrorAction SilentlyContinue) {
        Stop-Service $s -Force
        & sc.exe delete "$s" | Out-Null
        Write-Host "Servicio '$s' quitado."
    }
}
Get-NetFirewallRule -DisplayName $ServicioServidor -ErrorAction SilentlyContinue | Remove-NetFirewallRule
Write-Host "Los datos se quedan en $Raiz\Datos y los respaldos en $Raiz\Respaldos."
Stop-Transcript | Out-Null
