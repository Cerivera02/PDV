-- =====================================================================
-- 010 - Clave de autorización
--       Aparte de su contraseña para entrar, quien tiene permisos especiales (y el encargado) tiene una
--       clave que con solo teclearla autoriza cosas en la caja de otra persona. Cada clave es de una
--       sola persona (el servidor no deja repetirlas).
-- =====================================================================

ALTER TABLE usuarios ADD COLUMN IF NOT EXISTS clave_autorizacion_hash TEXT;
