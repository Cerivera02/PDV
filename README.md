# PDV – Punto de Venta multi-caja

Sistema de punto de venta para Windows con varias cajas, inventario (kardex), roles, báscula, lector de códigos de barras, tickets térmicos y cortes de caja.

## Arquitectura

```
        ┌──────────── CAJA MAESTRA (IP fija) ─────────────┐
        │  PostgreSQL  ←  PDV.Server (servicio Windows)    │
        │                     ↑ http://localhost:5080      │
        │               PDV.Caja (Caja 1)                  │
        └─────────────────────┬────────────────────────────┘
                              │ red local: HTTP + SignalR
              ┌───────────────┼───────────────┐
          PDV.Caja 2      PDV.Caja 3      PDV.Caja N
```

| Proyecto | Qué hace |
|---|---|
| `src/PDV.Core` | Modelos, DTOs, reglas compartidas, lector de etiquetas de báscula |
| `src/PDV.Data` | PostgreSQL con Dapper. El esquema está en `Sql/001_esquema.sql` y se aplica solo al arrancar |
| `src/PDV.Server` | API ASP.NET Core + JWT + SignalR. Corre como **servicio de Windows** |
| `src/PDV.Hardware` | Básculas por puerto serie (perfiles configurables), impresión ESC/POS RAW, cajón de dinero |
| `src/PDV.Caja` | App WPF de la caja: venta, cobro, cortes, productos, administración |
| `tests/PDV.Tests` | Pruebas unitarias (etiquetas de báscula, tickets) |

**Reglas importantes que ya aplica el sistema**
- El servidor toma los precios de la BD, nunca del cliente.
- Cada venta se guarda en una sola transacción: folio, detalle, pagos, existencias y kardex.
- Los folios son consecutivos por caja (`C1-000001`) y no se duplican aunque varias cajas vendan al mismo tiempo.
- Dinero en `NUMERIC(12,2)` y cantidades en `NUMERIC(12,3)`. No se usa `float`.
- Los códigos de barras son `VARCHAR(50)`, así se conservan los ceros a la izquierda. Un producto puede tener códigos alternos.
- La existencia solo cambia con movimientos: entrada, venta, ajuste, merma o cancelación. Todo queda en el kardex.
- Solo puede haber un turno abierto por caja.
- Para cancelar una venta se necesita el usuario y la contraseña de un administrador.

## Roles

| Rol | Al iniciar sesión | Puede |
|---|---|---|
| **Administrador** | Menú de administración | Todo: vender, productos, inventario, usuarios, cajas, datos del negocio, autorizar cancelaciones |
| **Capturista** | Productos e inventario | Alta y edición de productos, entradas, mermas, ajustes, kardex |
| **Cajero** | Pantalla de venta | Vender, cobrar, retiros, reimprimir, corte X, cerrar su turno |

## Instalación en la caja maestra

1. **PostgreSQL 17 o 18**: instálelo desde https://www.postgresql.org/download/windows/ y cree el usuario y la base:
   ```sql
   CREATE USER pdv WITH PASSWORD 'una-contraseña-segura';
   CREATE DATABASE pdv OWNER pdv;
   ```
2. Escriba esa contraseña en `src/PDV.Server/appsettings.json` → `ConnectionStrings:Pdv`.
3. En PowerShell **como Administrador**, desde la raíz del proyecto:
   ```powershell
   powershell -ExecutionPolicy Bypass -File scripts\instalar-servidor.ps1
   ```
   Esto publica el servidor en `C:\PDV\Servidor`, lo registra como servicio con arranque automático y abre el puerto 5080 en el firewall.
4. Al primer arranque se crean las tablas y el usuario **`admin` / `admin`**. **Cambie esa contraseña de inmediato** en *Usuarios → Cambiar contraseña*.
5. Asigne una **IP fija** a la caja maestra (por ejemplo `192.168.1.10`).
6. Programe `scripts\respaldo.ps1` en el Programador de tareas para que corra todos los días.

## Instalación en cada caja

```powershell
powershell -ExecutionPolicy Bypass -File scripts\publicar-caja.ps1
```
Copie `publicado\Caja` a la computadora y ejecute `PDV.Caja.exe`. En la pantalla de inicio, use **Configurar caja** para indicar:
- Servidor: `http://192.168.1.10:5080/` (en la caja maestra, `http://localhost:5080/`)
- Número de caja: el que aparece en *Administración → Cajas* (la maestra es la 1)
- Impresora de tickets, ancho (32 = 58 mm, 48 = 80 mm) y si se abre el cajón
- Báscula y puerto COM

La configuración se guarda en `caja.json`, junto al ejecutable.

## Báscula

Cada marca usa su propio protocolo, así que el sistema lo maneja por perfiles:

| Perfil | Parámetros |
|---|---|
| `Torrey`, `Rhino` | 9600 8N1, comando `P` |
| `CAS` | 9600 8N1, transmisión continua |
| `Toledo` | 9600 7E1, comando `W` |
| `Simulada` | Pesos aleatorios para pruebas sin hardware |
| `Personalizado` | Todo se ajusta en `caja.json` |

Con el perfil **Personalizado** se ajustan en `caja.json`: `Baudios`, `Paridad`, `BitsDatos`, `BitsParada`, `Comando` (admite `\r`, `\x05`…), `Patron` (regex con el grupo `peso`), `Unidad` (`kg`, `g`, `lb`) y `TimeoutMs`.
Use **Administración → Probar báscula** para ver lo que manda la báscula y ajustar el patrón.

> Los valores de los perfiles son los de fábrica más comunes. Verifíquelos con el manual de cada báscula.

**Etiquetas de báscula** (EAN-13 que empieza con `2`): el sistema lee el PLU y el peso (o el importe) del código. El formato se configura en `caja.json → EtiquetaBascula`. El PLU se registra en el producto, en el campo *PLU báscula*.

## Pantalla de venta (atajos)

| Tecla | Acción |
|---|---|
| Enter | Agregar el código escaneado o escrito (`3*código` agrega 3 piezas) |
| F2 | Volver a pesar la línea seleccionada |
| F3 | Buscar producto por nombre (no distingue acentos) |
| F4 | Cambiar la cantidad |
| F6 | Quitar la línea |
| F5 | Reimprimir ticket |
| F7 | Retiro de efectivo |
| F8 | Corte X (parcial, no cierra el turno) |
| F9 | Cancelar venta (requiere administrador) |
| F10 | Cerrar turno: corte Z |
| F12 | Cobrar (efectivo, tarjeta o mixto) |
| Esc | Descartar la venta |

## Desarrollo

```powershell
dotnet build PDV.sln
dotnet test tests\PDV.Tests
dotnet run --project src\PDV.Server     # API en http://localhost:5080
dotnet run --project src\PDV.Caja       # app de caja
```

## Pendientes sugeridos

- **Modo sin conexión** en las cajas secundarias: cola local en SQLite que se sincroniza cuando vuelve la red.
- **Reportes**: ventas por día, producto, cajero, utilidad y productos bajo mínimo exportables a Excel.
- **Devoluciones** parciales después del corte.
- **Descuentos** y promociones con autorización.
- **CFDI 4.0** (facturación) mediante un PAC, si el negocio lo requiere.
- **HTTPS** en la red local y proteger *Configurar caja* con usuario administrador.
- **Proveedores** y órdenes de compra.
# PDV
