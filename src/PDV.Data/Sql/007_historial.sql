-- =====================================================================
-- 007 - Historial de precios por producto + índices para consultar
--       tickets y cortes de caja anteriores
-- =====================================================================

-- Cada cambio de precio de venta o de costo queda guardado (no se borra nunca).
CREATE TABLE IF NOT EXISTS producto_precios (
    id                     BIGSERIAL PRIMARY KEY,
    producto_id            INTEGER       NOT NULL REFERENCES productos(id),
    precio_venta           NUMERIC(12,2) NOT NULL,
    precio_costo           NUMERIC(12,2) NOT NULL,
    precio_venta_anterior  NUMERIC(12,2),            -- NULL = alta o precio inicial
    precio_costo_anterior  NUMERIC(12,2),
    origen                 VARCHAR(12)   NOT NULL,   -- INICIAL, ALTA, CAMBIO, MERCANCIA
    usuario_id             INTEGER       REFERENCES usuarios(id),
    fecha                  TIMESTAMPTZ   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_producto_precios ON producto_precios (producto_id, fecha DESC);

-- Productos que ya existían antes del historial: su precio actual queda como punto de partida.
INSERT INTO producto_precios (producto_id, precio_venta, precio_costo, origen, fecha)
SELECT p.id, p.precio_venta, p.precio_costo, 'INICIAL', p.creado_en
FROM productos p
WHERE NOT EXISTS (SELECT 1 FROM producto_precios h WHERE h.producto_id = p.id);

-- El trigger registra el cambio venga de donde venga (pantalla de productos, llegada de mercancía o SQL directo).
-- Quién y desde dónde: los repositorios lo indican con set_config('pdv.usuario_id' / 'pdv.origen', …, true).
CREATE OR REPLACE FUNCTION fn_historial_precios() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF TG_OP = 'INSERT'
       OR NEW.precio_venta IS DISTINCT FROM OLD.precio_venta
       OR NEW.precio_costo IS DISTINCT FROM OLD.precio_costo THEN
        INSERT INTO producto_precios (producto_id, precio_venta, precio_costo,
                                      precio_venta_anterior, precio_costo_anterior, origen, usuario_id)
        VALUES (NEW.id, NEW.precio_venta, NEW.precio_costo,
                CASE WHEN TG_OP = 'UPDATE' THEN OLD.precio_venta END,
                CASE WHEN TG_OP = 'UPDATE' THEN OLD.precio_costo END,
                COALESCE(NULLIF(current_setting('pdv.origen', true), ''),
                         CASE WHEN TG_OP = 'INSERT' THEN 'ALTA' ELSE 'CAMBIO' END),
                NULLIF(current_setting('pdv.usuario_id', true), '')::int);
    END IF;
    RETURN NEW;
END
$$;

DROP TRIGGER IF EXISTS tr_historial_precios ON productos;
CREATE TRIGGER tr_historial_precios
    AFTER INSERT OR UPDATE OF precio_venta, precio_costo ON productos
    FOR EACH ROW EXECUTE FUNCTION fn_historial_precios();

-- Consultas de tickets por fecha/caja y de cortes por fecha
CREATE INDEX IF NOT EXISTS ix_ventas_caja_fecha ON ventas (caja_id, fecha DESC);
CREATE INDEX IF NOT EXISTS ix_turnos_abierto_en ON turnos (abierto_en DESC);
CREATE INDEX IF NOT EXISTS ix_retiros_turno ON retiros (turno_id);
