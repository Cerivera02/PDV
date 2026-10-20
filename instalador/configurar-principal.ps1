# Prepara la COMPUTADORA PRINCIPAL después de copiar los archivos (lo corre el instalador, como Administrador):
#   1. Base de datos propia (PostgreSQL incluido en $Raiz\PostgreSQL, datos en $Raiz\Datos, puerto $PuertoBD,
#      solo escucha en esta computadora). La primera vez la crea con una contraseña al azar.
#   2. Servicio "PDV Base de datos" y servicio "PDV Servidor" (arrancan solos con Windows).
#   3. Puerto del servidor abierto en el firewall para las demás cajas.
# Si ya existe (actualización), no toca los datos: solo vuelve a registrar y arrancar los servicios.
# Bitácora: $Raiz\instalacion.log
#
# -SinServicios: para probarlo sin ser Administrador (arranca PostgreSQL con pg_ctl, sin servicios ni firewall).
param(
    [string]$Raiz = "C:\PDV",
    [int]$PuertoBD = 5480,
    [int]$PuertoServidor = 5080,
    [string]$ServicioBD = "PDV Base de datos",
    [string]$ServicioServidor = "PDV Servidor",
    [switch]$SinServicios
)
$ErrorActionPreference = 'Stop'
$pg = Join-Path $Raiz "PostgreSQL\bin"
$datos = Join-Path $Raiz "Datos"
$servidor = Join-Path $Raiz "Servidor"
$config = Join-Path $servidor "appsettings.Production.json"
$respaldos = Join-Path $Raiz "Respaldos"
Start-Transcript -Path (Join-Path $Raiz "instalacion.log") -Append | Out-Null

function Paso($texto) { Write-Host "[$(Get-Date -Format 'HH:mm:ss')] $texto" }
function Correr($exe, [string[]]$argumentos) {
    & (Join-Path $pg $exe) @argumentos
    if ($LASTEXITCODE -ne 0) { throw "$exe terminó con error $LASTEXITCODE" }
}
# Solo Administradores y el sistema; opcionalmente otros con su permiso (SID o nombre, icacls).
# En un ARCHIVO no van (OI)(CI): icacls lo acepta pero deja el archivo sin permisos para nadie.
function Restringir($ruta, [string[]]$otros = @()) {
    $herencia = if (Test-Path $ruta -PathType Container) { '(OI)(CI)' } else { '' }
    $permisos = @("*S-1-5-32-544:$($herencia)F", "*S-1-5-18:$($herencia)F") + $otros
    & icacls $ruta /inheritance:r /grant:r @permisos /Q | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No se pudieron poner los permisos de $ruta" }
}
function ClaveAlAzar {
    $letras = 'abcdefghijkmnopqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789'.ToCharArray()
    $bytes = New-Object byte[] 32
    [Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
    -join ($bytes | ForEach-Object { $letras[$_ % $letras.Length] })
}
function EsperarServicio($nombre) {
    $s = Get-Service $nombre
    if ($s.Status -ne 'Running') { Start-Service $nombre }
    (Get-Service $nombre).WaitForStatus('Running', [TimeSpan]::FromSeconds(60))
}

try {
    Paso "Instalando en $Raiz (base de datos en el puerto $PuertoBD, servidor en el $PuertoServidor)"
    New-Item -ItemType Directory -Force $respaldos | Out-Null

    # ---------------------------------------------------------------- 1. Base de datos
    $versionIncluida = [int](((& (Join-Path $pg "postgres.exe") --version) -replace '[^\d.]', '').Split('.')[0])
    if (Test-Path (Join-Path $datos "PG_VERSION")) {
        $versionDatos = [int](Get-Content (Join-Path $datos "PG_VERSION")).Trim()
        if ($versionDatos -ne $versionIncluida) {
            throw "Los datos son de PostgreSQL $versionDatos y este instalador trae PostgreSQL $versionIncluida. " +
                  "Hay que pasarlos con un respaldo (pg_dump) antes de actualizar; no se tocó nada."
        }
        if (-not (Test-Path $config)) { throw "Ya existe la base de datos pero falta $config (tiene la contraseña)." }
        # La versión 1.0.0 lo dejaba sin permisos para nadie: se arregla antes de leerlo
        if (-not $SinServicios) { Restringir $config }
        Paso "La base de datos ya existe: se conserva tal cual."
        $clave = ((Get-Content $config -Raw | ConvertFrom-Json).ConnectionStrings.Pdv -split ';' |
            Where-Object { $_ -like 'Password=*' }) -replace '^Password=', ''
    }
    else {
        Paso "Creando la base de datos en $datos"
        if ((Test-Path $datos) -and (Get-ChildItem $datos -Force | Select-Object -First 1)) {
            throw "La carpeta $datos no está vacía y no es una base de datos. Muévala o bórrela y vuelva a instalar."
        }
        New-Item -ItemType Directory -Force $datos | Out-Null
        if (-not $SinServicios) {
            # NetworkService corre la base; quien instala también (initdb se quita los permisos de Administrador)
            $yo = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
            Restringir $datos @('*S-1-5-20:(OI)(CI)F', "*$($yo):(OI)(CI)F")
        }
        $clave = ClaveAlAzar
        $archivoClave = Join-Path $env:TEMP "pdv-$([guid]::NewGuid()).txt"
        [IO.File]::WriteAllText($archivoClave, $clave)
        try {
            # Ordena como en español (ICU es-MX) aunque Windows esté en otro idioma
            Correr "initdb.exe" @('-D', $datos, '-U', 'pdv', "--pwfile=$archivoClave", '-E', 'UTF8',
                '--locale=C', '--locale-provider=icu', '--icu-locale=es-MX', '--auth=scram-sha-256')
        }
        finally { Remove-Item $archivoClave -Force -ErrorAction SilentlyContinue }
        Add-Content (Join-Path $datos "postgresql.conf") -Encoding ASCII @"

# ---- PDV
port = $PuertoBD
listen_addresses = 'localhost'
logging_collector = on
log_directory = 'log'
log_filename = 'postgresql-%a.log'
log_truncate_on_rotation = on
log_rotation_age = 1d
lc_messages = 'C'
"@
        # Configuración del servidor PDV para esta instalación (no la pisan las actualizaciones)
        $json = [ordered]@{
            ConnectionStrings = [ordered]@{ Pdv = "Host=localhost;Port=$PuertoBD;Database=pdv;Username=pdv;Password=$clave;Maximum Pool Size=50" }
            Kestrel           = @{ Endpoints = @{ Http = @{ Url = "http://*:$PuertoServidor" } } }
            Respaldos         = [ordered]@{ Carpeta = $respaldos; PgBin = $pg }
        } | ConvertTo-Json -Depth 5
        [IO.File]::WriteAllText($config, $json)
    }
    if (-not $SinServicios) {
        Restringir $servidor @('*S-1-5-11:(OI)(CI)RX')       # corre como sistema: que nadie más lo cambie
        Restringir $config               # tiene la contraseña: solo Administradores y el sistema
        Restringir (Join-Path $Raiz "PostgreSQL") @('*S-1-5-11:(OI)(CI)RX')
    }

    Paso "Arrancando la base de datos"
    if ($SinServicios) {
        & (Join-Path $pg "pg_ctl.exe") status -D $datos | Out-Null
        if ($LASTEXITCODE -ne 0) { Correr "pg_ctl.exe" @('start', '-D', $datos, '-w', '-l', (Join-Path $datos "arranque.log")) }
    }
    else {
        if (-not (Get-Service $ServicioBD -ErrorAction SilentlyContinue)) {
            Correr "pg_ctl.exe" @('register', '-N', $ServicioBD, '-U', 'NT AUTHORITY\NetworkService', '-D', $datos, '-S', 'auto', '-w')
            & sc.exe description "$ServicioBD" "Base de datos del punto de venta (PostgreSQL)." | Out-Null
            & sc.exe failure "$ServicioBD" reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
        }
        EsperarServicio $ServicioBD
    }
    $env:PGPASSWORD = $clave
    for ($i = 0; $i -lt 30; $i++) {
        & (Join-Path $pg "pg_isready.exe") -h localhost -p $PuertoBD -q
        if ($LASTEXITCODE -eq 0) { break }
        Start-Sleep 1
    }
    $existe = & (Join-Path $pg "psql.exe") -h localhost -p $PuertoBD -U pdv -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = 'pdv'"
    if ($LASTEXITCODE -ne 0) { throw "No se pudo conectar a la base de datos en el puerto $PuertoBD." }
    if ($existe -ne '1') {
        Correr "createdb.exe" @('-h', 'localhost', '-p', $PuertoBD, '-U', 'pdv', '-O', 'pdv', 'pdv')
        Paso "Base de datos 'pdv' creada (las tablas las crea el servidor al arrancar)."
    }

    if ($SinServicios) { Paso "Listo (sin servicios)."; exit 0 }

    # ---------------------------------------------------------------- 2. Servidor PDV
    Paso "Registrando el servicio '$ServicioServidor'"
    $exe = Join-Path $servidor "PDV.Server.exe"
    if (-not (Get-Service $ServicioServidor -ErrorAction SilentlyContinue)) {
        New-Service -Name $ServicioServidor -BinaryPathName "`"$exe`"" -StartupType Automatic `
            -Description 'Servidor del punto de venta (ventas, inventario, cortes). Lo usan todas las cajas.' | Out-Null
        & sc.exe failure "$ServicioServidor" reset= 86400 actions= restart/5000/restart/5000/restart/30000 | Out-Null
    }
    else {
        & sc.exe config "$ServicioServidor" binPath= "`"$exe`"" start= auto | Out-Null
    }
    # Que arranque después de la base de datos
    & sc.exe config "$ServicioServidor" depend= "$ServicioBD" | Out-Null

    # ---------------------------------------------------------------- 3. Firewall (las demás cajas)
    if (-not (Get-NetFirewallRule -DisplayName $ServicioServidor -ErrorAction SilentlyContinue)) {
        New-NetFirewallRule -DisplayName $ServicioServidor -Direction Inbound -Protocol TCP -LocalPort $PuertoServidor `
            -Profile Private, Domain -Action Allow | Out-Null
        Paso "Firewall: puerto $PuertoServidor abierto (redes privadas y de dominio)."
    }

    EsperarServicio $ServicioServidor
    Paso "Esperando a que el servidor responda (la primera vez crea las tablas)"
    for ($i = 0; $i -lt 90; $i++) {
        try {
            Invoke-RestMethod "http://localhost:$PuertoServidor/" -TimeoutSec 2 | Out-Null
            Paso "Listo: el servidor responde en http://localhost:$PuertoServidor/"
            exit 0
        }
        catch { Start-Sleep 1 }
    }
    throw "El servidor no respondió. Revise el Visor de eventos (Aplicación) y $Raiz\Datos\log."
}
catch {
    Write-Host "ERROR: $($_.Exception.Message)"
    exit 1
}
finally {
    Stop-Transcript | Out-Null
}
