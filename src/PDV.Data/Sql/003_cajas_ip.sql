-- =====================================================================
-- 003 - Acceso por caja: IP fija y bloqueo
-- Cada caja secundaria solo se puede usar desde su IP registrada.
-- La caja principal (es_maestra) solo desde la propia computadora del servidor.
-- cajas.activa ya existía: FALSE = caja bloqueada.
-- =====================================================================

ALTER TABLE cajas ADD COLUMN IF NOT EXISTS ip VARCHAR(45);

-- Dos cajas no pueden tener la misma IP
CREATE UNIQUE INDEX IF NOT EXISTS ux_cajas_ip ON cajas (ip) WHERE ip IS NOT NULL;

-- La caja principal nunca se bloquea (si no, nadie podría desbloquearla)
UPDATE cajas SET activa = TRUE WHERE es_maestra AND NOT activa;
