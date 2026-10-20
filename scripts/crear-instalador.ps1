# Crea publicado\Instalar-PDV-<versión>.exe: un solo instalador con TODO (servidor, caja y PostgreSQL).
#   powershell -ExecutionPolicy Bypass -File scripts\crear-instalador.ps1 [-Version 1.0.0]
# Necesita: Inno Setup 6 (winget install JRSoftware.InnoSetup) y PostgreSQL 18 instalado en esta computadora
# (de ahí se toman sus programas; no se usa ni se toca esa base de datos).
param(
    [string]$Version = (Get-Date -Format 'yyyy.M.d'),
    [string]$PgOrigen = "C:\Program Files\PostgreSQL\18",
    [string]$Iscc = "",
    # Carpeta para los archivos intermedios de compilación (útil si bin\ está en uso por la app abierta)
    [string]$Artefactos = ""
)
$ErrorActionPreference = 'Stop'
$raiz = Split-Path $PSScriptRoot
$prep = Join-Path $raiz "publicado\instalador"

if (-not $Iscc) {
    $Iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
    if (-not $Iscc) { throw "No se encontró Inno Setup 6. Instálelo con: winget install JRSoftware.InnoSetup" }
}
if (-not (Test-Path "$PgOrigen\bin\postgres.exe")) { throw "No se encontró PostgreSQL en $PgOrigen (use -PgOrigen)." }

if (Test-Path $prep) { Remove-Item $prep -Recurse -Force }
$extra = if ($Artefactos) { @('--artifacts-path', $Artefactos) } else { @() }

Write-Host "1/4 Servidor" -ForegroundColor Cyan
dotnet publish "$raiz\src\PDV.Server" -c Release -r win-x64 --self-contained true -o "$prep\Servidor" @extra
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación del servidor" }
# Nada de configuración ni claves de esta computadora de desarrollo
Remove-Item "$prep\Servidor\jwt.key", "$prep\Servidor\appsettings.Development.json", "$prep\Servidor\appsettings.Production.json" -ErrorAction SilentlyContinue

Write-Host "2/4 Caja" -ForegroundColor Cyan
dotnet publish "$raiz\src\PDV.Caja" -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:PublishReadyToRun=true -o "$prep\Caja" @extra
if ($LASTEXITCODE -ne 0) { throw "Falló la publicación de la caja" }
Remove-Item "$prep\Caja\caja.json" -ErrorAction SilentlyContinue
Get-ChildItem $prep -Recurse -Filter *.pdb | Remove-Item

Write-Host "3/4 PostgreSQL (solo lo necesario)" -ForegroundColor Cyan
$pgDestino = "$prep\PostgreSQL"
New-Item -ItemType Directory -Force "$pgDestino\bin", "$pgDestino\lib" | Out-Null
# bin: todo menos StackBuilder (wx*) y archivos de desarrollo
Get-ChildItem "$PgOrigen\bin" -File | Where-Object { $_.Name -notmatch '^(wx|stackbuilder|ecpg|testplug)' -and $_.Extension -notin '.lib', '.a' } |
    Copy-Item -Destination "$pgDestino\bin"
# PostgreSQL necesita el runtime de Visual C++: se incluye junto a sus programas (en una PC nueva puede no estar)
foreach ($dll in 'vcruntime140.dll', 'vcruntime140_1.dll', 'msvcp140.dll') {
    Copy-Item "$env:SystemRoot\System32\$dll" "$pgDestino\bin"
}
Get-ChildItem "$PgOrigen\lib" -File -Filter *.dll | Copy-Item -Destination "$pgDestino\lib"
# share: sin las traducciones de mensajes (28 MB); los mensajes quedan en inglés
Copy-Item "$PgOrigen\share" "$pgDestino\share" -Recurse
Remove-Item "$pgDestino\share\locale" -Recurse -Force -ErrorAction SilentlyContinue
Copy-Item "$PgOrigen\server_license.txt" "$pgDestino\LICENCIA-PostgreSQL.txt" -ErrorAction SilentlyContinue

New-Item -ItemType Directory -Force "$prep\Instalador" | Out-Null
Copy-Item "$raiz\instalador\configurar-principal.ps1", "$raiz\instalador\quitar-principal.ps1" "$prep\Instalador"

Write-Host "4/4 Instalador" -ForegroundColor Cyan
& $Iscc "/DVersion=$Version" "/DOrigen=$prep" "/O$raiz\publicado" "$raiz\instalador\PDV.iss"
if ($LASTEXITCODE -ne 0) { throw "Falló Inno Setup" }
$exe = Get-Item "$raiz\publicado\Instalar-PDV-$Version.exe"
Write-Host ("Listo: {0} ({1:N0} MB)" -f $exe.FullName, ($exe.Length / 1MB)) -ForegroundColor Green
