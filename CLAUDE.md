# PDV – instrucciones para Claude

## Documentación en Obsidian (obligatorio)
Toda la memoria del proyecto vive en el vault de Obsidian:
`C:\Users\carlos.rivera\OneDrive - Gobierno del Estado de Sonora\Documentos\Obsidian Vault\PDV\`

- Punto de entrada: `00 - PDV Inicio.md`
- **Al terminar cualquier cambio**, actualizar las notas afectadas:
  - Crear una nota de sesión en `Seguimiento/Sesiones/AAAA-MM-DD - <tema>.md` (qué se pidió, qué se hizo, problemas y soluciones, pruebas) y agregarla a la tabla de `Seguimiento/Bitácora.md`.
  - Una decisión de diseño nueva → nueva ADR en `Decisiones/` y su enlace en el índice.
  - Endpoints, tablas, `caja.json` o atajos nuevos o modificados → actualizar la nota de `Referencia/` correspondiente.
  - Marcar o agregar tareas en `Seguimiento/Pendientes.md`.
  - Actualizar `Operación/Pruebas.md` con las pruebas que se corrieron.
- Usar enlaces `[[...]]` entre notas y escribir en español.

## Comandos
```powershell
dotnet build PDV.sln
dotnet test tests\PDV.Tests
dotnet run --project src\PDV.Server
dotnet run --project src\PDV.Caja
```

## Reglas del dominio (no romper)
- Dinero `decimal` / `NUMERIC(12,2)`, nunca float. Cantidades `NUMERIC(12,3)`.
- Códigos de barras `VARCHAR(50)`.
- La existencia solo cambia mediante movimientos (kardex).
- El servidor toma los precios de la BD, nunca del cliente.
- Cambios de esquema: nuevo script idempotente `src/PDV.Data/Sql/00N_*.sql`.
- No usar la instancia local de PostgreSQL 18 del usuario para pruebas; usar un clúster temporal.
