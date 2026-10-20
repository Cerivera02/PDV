-- =====================================================================
-- 002 - Departamentos de productos (Abarrotes, Lácteos, Limpieza…)
-- Idempotente. En bases creadas con la versión anterior de 001 había una
-- tabla "categorias" sin uso: se renombra para conservarla.
-- =====================================================================

DO $$
BEGIN
    IF to_regclass('public.categorias') IS NOT NULL AND to_regclass('public.departamentos') IS NULL THEN
        ALTER TABLE categorias RENAME TO departamentos;
    END IF;

    IF EXISTS (SELECT 1 FROM information_schema.columns
               WHERE table_schema = 'public' AND table_name = 'productos' AND column_name = 'categoria_id') THEN
        ALTER TABLE productos RENAME COLUMN categoria_id TO departamento_id;
    END IF;
END $$;

CREATE TABLE IF NOT EXISTS departamentos (
    id      SERIAL PRIMARY KEY,
    nombre  VARCHAR(80) NOT NULL UNIQUE
);
ALTER TABLE departamentos ADD COLUMN IF NOT EXISTS activo BOOLEAN NOT NULL DEFAULT TRUE;

ALTER TABLE productos ADD COLUMN IF NOT EXISTS departamento_id INTEGER REFERENCES departamentos(id);
CREATE INDEX IF NOT EXISTS ix_productos_departamento ON productos (departamento_id);
