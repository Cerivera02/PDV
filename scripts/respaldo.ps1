# Respaldo manual o programado de la base de datos del PDV.
# OJO: el servidor ya hace un respaldo cada vez que se cierra el programa en una caja
# (carpeta C:\ProgramData\PDV\Respaldos). Este script es un extra opcional (por ejemplo, a otro disco).
# Programarlo en el "Programador de tareas" de Windows de la caja maestra, por ejemplo cada día a las 23:00:
#   powershell -ExecutionPolicy Bypass -File C:\PDV\respaldo.ps1
# La contraseña se toma de %APPDATA%\postgresql\pgpass.conf (formato: localhost:5432:pdv:pdv:CONTRASEÑA)
param(
    [string]$Carpeta = "D:\Respaldos\PDV",
    [string]$PgBin = "C:\Program Files\PostgreSQL\17\bin",
    [int]$DiasConservar = 30
)
$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force $Carpeta | Out-Null
$archivo = Join-Path $Carpeta ("pdv_{0:yyyyMMdd_HHmm}.backup" -f (Get-Date))

& "$PgBin\pg_dump.exe" -h localhost -U pdv -d pdv -F c -f $archivo
if ($LASTEXITCODE -ne 0) { throw "pg_dump falló con código $LASTEXITCODE" }

# Borrar respaldos viejos
Get-ChildItem $Carpeta -Filter 'pdv_*.backup' |
    Where-Object LastWriteTime -lt (Get-Date).AddDays(-$DiasConservar) |
    Remove-Item

Write-Host "Respaldo creado: $archivo"
# Restaurar: pg_restore -h localhost -U pdv -d pdv --clean "archivo.backup"
