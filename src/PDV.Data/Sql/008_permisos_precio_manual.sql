-- =====================================================================
-- 008 - Permisos especiales por persona, precio cambiado a mano en una venta
--       y qué hacen las flechas en la pantalla de Vender
-- =====================================================================

-- Permisos especiales (CAMBIAR_PRECIO, CANCELAR_VENTA…). El encargado los tiene todos sin necesidad de marcarlos.
ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS permisos TEXT[] NOT NULL DEFAULT '{}';

-- Quién hizo o autorizó el cambio de precio de ese renglón (NULL = precio normal del sistema)
ALTER TABLE venta_detalle ADD COLUMN IF NOT EXISTS precio_cambiado_por INTEGER REFERENCES usuarios(id);

-- Flecha izquierda: CANTIDAD o PRECIO (la derecha hace lo otro)
INSERT INTO configuracion (clave, valor) VALUES ('flecha_izquierda', 'CANTIDAD') ON CONFLICT DO NOTHING;
