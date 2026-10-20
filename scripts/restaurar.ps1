# Restaurar un respaldo de la base de datos del PDV A MANO (emergencias).
# Úselo solo si el programa no abre (por ejemplo, la base está dañada y el servidor no arranca).
# Si el programa sí abre, es más fácil: Menú del encargado → Respaldos → "Regresar a este respaldo".
#
# Ejecutar en la computadora principal, en PowerShell como administrador:
#   powershell -ExecutionPolicy Bypass -File C:\PDV\restaurar.ps1 -Archivo "C:\ProgramData\PDV\Respaldos\pdv_20261019_213005.backup"
#
# Igual que el servidor: todo en UNA transacción. Si algo falla, la base queda como estaba.
param(
    [Parameter(Mandatory)] [string]$Archivo,
    [string]$PgBin = "C:\Program Files\PostgreSQL\18\bin",
    [string]$Servidor = "localhost",
    [int]$Puerto = 5432,
    [string]$Base = "pdv",
    [string]$Usuario = "pdv",
    [string]$Servicio = "PDV Servidor"
)
$ErrorActionPreference = 'Stop'
if (-not (Test-Path $Archivo)) { throw "No existe el archivo $Archivo" }

if (-not $env:PGPASSWORD) {
    $clave = Read-Host "Contraseña del usuario '$Usuario' de PostgreSQL" -AsSecureString
    $env:PGPASSWORD = [Runtime.InteropServices.Marshal]::PtrToStringAuto([Runtime.InteropServices.Marshal]::SecureStringToBSTR($clave))
}

# 1) Detener el servidor del PDV (si está como servicio) para que nadie use la base
$svc = Get-Service $Servicio -ErrorAction SilentlyContinue
if ($svc -and $svc.Status -eq 'Running') { Write-Host "Deteniendo '$Servicio'..."; Stop-Service $Servicio }

$tmp = Join-Path $env:TEMP ("pdv_restaurar_" + [Guid]::NewGuid().ToString('N'))
try {
    # 2) El respaldo como script SQL (no toca la base; si el archivo está dañado, falla aquí)
    & "$PgBin\pg_restore.exe" --no-owner --no-acl --file "$tmp.sql" $Archivo
    if ($LASTEXITCODE -ne 0) { throw "El archivo no es un respaldo válido (pg_restore terminó con código $LASTEXITCODE)" }

    # 3) Borrar lo de la app y cargar el respaldo, en una sola transacción
    @'
DO $$
DECLARE o record;
BEGIN
    FOR o IN SELECT viewname AS n FROM pg_views WHERE schemaname = 'public' LOOP
        EXECUTE format('DROP VIEW IF EXISTS public.%I CASCADE', o.n);
    END LOOP;
    FOR o IN SELECT tablename AS n FROM pg_tables WHERE schemaname = 'public' LOOP
        EXECUTE format('DROP TABLE IF EXISTS public.%I CASCADE', o.n);
    END LOOP;
    FOR o IN SELECT sequencename AS n FROM pg_sequences WHERE schemaname = 'public' LOOP
        EXECUTE format('DROP SEQUENCE IF EXISTS public.%I CASCADE', o.n);
    END LOOP;
    FOR o IN SELECT p.oid::regprocedure AS f FROM pg_proc p JOIN pg_namespace s ON s.oid = p.pronamespace
             WHERE s.nspname = 'public'
               AND NOT EXISTS (SELECT 1 FROM pg_depend d WHERE d.objid = p.oid AND d.deptype = 'e') LOOP
        EXECUTE format('DROP FUNCTION IF EXISTS %s CASCADE', o.f);
    END LOOP;
END $$;
'@ | Set-Content "$tmp-borrar.sql" -Encoding utf8

    $env:PGOPTIONS = '-c client_min_messages=warning'   # sin los avisos de "eliminando además…"
    & "$PgBin\psql.exe" --no-psqlrc --quiet --single-transaction --set ON_ERROR_STOP=1 `
        --host $Servidor --port $Puerto --username $Usuario --dbname $Base `
        --file "$tmp-borrar.sql" --file "$tmp.sql" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "No se pudo restaurar (psql terminó con código $LASTEXITCODE). La base de datos NO se cambió." }
    Write-Host "Listo: la base de datos quedó como en el respaldo $Archivo" -ForegroundColor Green
}
finally {
    Remove-Item "$tmp.sql", "$tmp-borrar.sql" -ErrorAction SilentlyContinue
    $env:PGPASSWORD = $null
    $env:PGOPTIONS = $null
    # 4) Volver a arrancar el servidor (al arrancar aplica las migraciones que falten)
    if ($svc) { Write-Host "Arrancando '$Servicio'..."; Start-Service $Servicio }
}
