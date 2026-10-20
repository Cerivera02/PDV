-- =====================================================================
-- 009 - Permiso especial para sacar dinero de la caja
--       (quien no lo tiene necesita la autorización del encargado)
-- =====================================================================

-- Quién autorizó que se sacara el dinero (el mismo que lo sacó si tenía el permiso; NULL en retiros anteriores)
ALTER TABLE retiros ADD COLUMN IF NOT EXISTS autorizado_por INTEGER REFERENCES usuarios(id);
