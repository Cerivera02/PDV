-- =====================================================================
-- 005 - Ofertas: precio por fechas, NxM (2x1, 3x2…) y mayoreo
-- Se aplica UNA promoción por producto: la que más le conviene al cliente
-- (ver PDV.Core/Ofertas.cs → CalculadoraPrecios).
-- =====================================================================

CREATE TABLE IF NOT EXISTS ofertas (
    id           SERIAL PRIMARY KEY,
    producto_id  INTEGER       NOT NULL REFERENCES productos(id) ON DELETE CASCADE,
    tipo         VARCHAR(10)   NOT NULL CHECK (tipo IN ('PRECIO', 'NXM', 'MAYOREO')),
    precio       NUMERIC(12,2),          -- PRECIO y MAYOREO
    cantidad     NUMERIC(12,3),          -- NXM: lleva · MAYOREO: a partir de
    paga         INTEGER,                -- NXM
    desde        DATE,                   -- null = desde ya
    hasta        DATE,                   -- null = sin fin
    creada_en    TIMESTAMPTZ   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_ofertas_producto ON ofertas (producto_id);

-- Qué promoción se aplicó en cada renglón vendido (queda aunque después se quite la oferta)
ALTER TABLE venta_detalle ADD COLUMN IF NOT EXISTS precio_lista NUMERIC(12,2);
ALTER TABLE venta_detalle ADD COLUMN IF NOT EXISTS descuento    NUMERIC(12,2) NOT NULL DEFAULT 0;
ALTER TABLE venta_detalle ADD COLUMN IF NOT EXISTS promocion    VARCHAR(60);
