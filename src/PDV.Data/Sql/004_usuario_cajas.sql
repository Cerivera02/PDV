-- =====================================================================
-- 004 - Personas asignadas a cajas
-- Si una persona NO tiene renglones aquí, puede trabajar en todas las cajas.
-- Si tiene, solo en esas. El Encargado (Administrador) siempre puede en todas.
-- =====================================================================

CREATE TABLE IF NOT EXISTS usuario_cajas (
    usuario_id  INTEGER NOT NULL REFERENCES usuarios(id) ON DELETE CASCADE,
    caja_id     INTEGER NOT NULL REFERENCES cajas(id) ON DELETE CASCADE,
    PRIMARY KEY (usuario_id, caja_id)
);
