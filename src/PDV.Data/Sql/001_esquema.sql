-- =====================================================================
-- PDV - Esquema PostgreSQL
-- Idempotente: el servidor lo ejecuta en cada arranque.
-- Dinero: NUMERIC(12,2)   Cantidades (piezas/kilos): NUMERIC(12,3)
-- =====================================================================

CREATE EXTENSION IF NOT EXISTS pg_trgm;
CREATE EXTENSION IF NOT EXISTS unaccent;   -- buscar "jamon" encuentra "Jamón"

-- ---------------------------------------------------------------- Seguridad
CREATE TABLE IF NOT EXISTS roles (
    id      SMALLINT PRIMARY KEY,
    nombre  VARCHAR(30) NOT NULL UNIQUE
);
INSERT INTO roles (id, nombre) VALUES
    (1, 'Administrador'), (2, 'Capturista'), (3, 'Cajero')
ON CONFLICT DO NOTHING;

CREATE TABLE IF NOT EXISTS usuarios (
    id             SERIAL PRIMARY KEY,
    login          VARCHAR(50)  NOT NULL,
    nombre         VARCHAR(120) NOT NULL,
    password_hash  VARCHAR(100) NOT NULL,
    rol_id         SMALLINT     NOT NULL REFERENCES roles(id),
    activo         BOOLEAN      NOT NULL DEFAULT TRUE,
    creado_en      TIMESTAMPTZ  NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_usuarios_login ON usuarios (lower(login));

-- ---------------------------------------------------------------- Cajas
CREATE TABLE IF NOT EXISTS cajas (
    id            SERIAL PRIMARY KEY,
    nombre        VARCHAR(50) NOT NULL,
    serie         VARCHAR(10) NOT NULL UNIQUE,   -- prefijo del folio: C1-000001
    ultimo_folio  INTEGER     NOT NULL DEFAULT 0,
    es_maestra    BOOLEAN     NOT NULL DEFAULT FALSE,
    activa        BOOLEAN     NOT NULL DEFAULT TRUE
);
INSERT INTO cajas (id, nombre, serie, es_maestra) VALUES (1, 'Caja maestra', 'C1', TRUE)
ON CONFLICT DO NOTHING;
SELECT setval(pg_get_serial_sequence('cajas', 'id'), GREATEST((SELECT MAX(id) FROM cajas), 1));

-- ---------------------------------------------------------------- Catálogo
-- (los departamentos se agregan en 002_departamentos.sql)
CREATE TABLE IF NOT EXISTS productos (
    id              SERIAL PRIMARY KEY,
    codigo_barras   VARCHAR(50)   NOT NULL UNIQUE,
    codigo_interno  VARCHAR(20)   UNIQUE,              -- PLU para etiquetas de báscula
    nombre          VARCHAR(150)  NOT NULL,
    unidad          VARCHAR(10)   NOT NULL DEFAULT 'PZA',
    por_peso        BOOLEAN       NOT NULL DEFAULT FALSE,
    precio_costo    NUMERIC(12,2) NOT NULL DEFAULT 0,
    precio_venta    NUMERIC(12,2) NOT NULL DEFAULT 0,  -- con IVA incluido
    tasa_iva        NUMERIC(5,4)  NOT NULL DEFAULT 0,  -- 0.16 = 16%
    existencia      NUMERIC(12,3) NOT NULL DEFAULT 0,
    stock_minimo    NUMERIC(12,3) NOT NULL DEFAULT 0,
    activo          BOOLEAN       NOT NULL DEFAULT TRUE,
    creado_en       TIMESTAMPTZ   NOT NULL DEFAULT now(),
    actualizado_en  TIMESTAMPTZ   NOT NULL DEFAULT now(),
    CONSTRAINT ck_productos_precios CHECK (precio_costo >= 0 AND precio_venta >= 0)
);
CREATE INDEX IF NOT EXISTS ix_productos_nombre_trgm ON productos USING gin (nombre gin_trgm_ops);

-- Códigos de barras alternos (un producto con varias presentaciones/proveedores)
CREATE TABLE IF NOT EXISTS producto_codigos (
    codigo_barras  VARCHAR(50) PRIMARY KEY,
    producto_id    INTEGER     NOT NULL REFERENCES productos(id) ON DELETE CASCADE
);

-- ---------------------------------------------------------------- Inventario (kardex)
CREATE TABLE IF NOT EXISTS movimientos_inventario (
    id                     BIGSERIAL PRIMARY KEY,
    producto_id            INTEGER       NOT NULL REFERENCES productos(id),
    tipo                   VARCHAR(20)   NOT NULL,  -- ENTRADA, VENTA, AJUSTE, MERMA, CANCELACION
    cantidad               NUMERIC(12,3) NOT NULL,  -- con signo: + entra, - sale
    existencia_resultante  NUMERIC(12,3) NOT NULL,
    costo_unitario         NUMERIC(12,2),
    referencia             VARCHAR(50),             -- folio de venta, factura de proveedor…
    nota                   VARCHAR(250),
    usuario_id             INTEGER       NOT NULL REFERENCES usuarios(id),
    caja_id                INTEGER       REFERENCES cajas(id),
    fecha                  TIMESTAMPTZ   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_movimientos_producto_fecha ON movimientos_inventario (producto_id, fecha DESC);

-- ---------------------------------------------------------------- Turnos y cortes
CREATE TABLE IF NOT EXISTS turnos (
    id                  BIGSERIAL PRIMARY KEY,
    caja_id             INTEGER       NOT NULL REFERENCES cajas(id),
    usuario_id          INTEGER       NOT NULL REFERENCES usuarios(id),
    abierto_en          TIMESTAMPTZ   NOT NULL DEFAULT now(),
    cerrado_en          TIMESTAMPTZ,
    fondo_inicial       NUMERIC(12,2) NOT NULL DEFAULT 0,
    efectivo_declarado  NUMERIC(12,2),
    estado              VARCHAR(10)   NOT NULL DEFAULT 'ABIERTO'
);
-- Solo un turno abierto por caja
CREATE UNIQUE INDEX IF NOT EXISTS ux_turnos_abierto_por_caja ON turnos (caja_id) WHERE estado = 'ABIERTO';

CREATE TABLE IF NOT EXISTS retiros (
    id          BIGSERIAL PRIMARY KEY,
    turno_id    BIGINT        NOT NULL REFERENCES turnos(id),
    usuario_id  INTEGER       NOT NULL REFERENCES usuarios(id),
    monto       NUMERIC(12,2) NOT NULL CHECK (monto > 0),
    motivo      VARCHAR(150)  NOT NULL,
    fecha       TIMESTAMPTZ   NOT NULL DEFAULT now()
);

-- ---------------------------------------------------------------- Ventas
CREATE TABLE IF NOT EXISTS ventas (
    id                  BIGSERIAL PRIMARY KEY,
    folio               VARCHAR(20)   NOT NULL UNIQUE,
    caja_id             INTEGER       NOT NULL REFERENCES cajas(id),
    turno_id            BIGINT        NOT NULL REFERENCES turnos(id),
    usuario_id          INTEGER       NOT NULL REFERENCES usuarios(id),
    fecha               TIMESTAMPTZ   NOT NULL DEFAULT now(),
    subtotal            NUMERIC(12,2) NOT NULL,
    impuestos           NUMERIC(12,2) NOT NULL,
    total               NUMERIC(12,2) NOT NULL,
    pagado              NUMERIC(12,2) NOT NULL,
    cambio              NUMERIC(12,2) NOT NULL DEFAULT 0,
    estado              VARCHAR(10)   NOT NULL DEFAULT 'PAGADA',
    cancelada_por       INTEGER       REFERENCES usuarios(id),
    cancelada_en        TIMESTAMPTZ,
    motivo_cancelacion  VARCHAR(250)
);
CREATE INDEX IF NOT EXISTS ix_ventas_fecha ON ventas (fecha);
CREATE INDEX IF NOT EXISTS ix_ventas_turno ON ventas (turno_id);

CREATE TABLE IF NOT EXISTS venta_detalle (
    id               BIGSERIAL PRIMARY KEY,
    venta_id         BIGINT        NOT NULL REFERENCES ventas(id),
    producto_id      INTEGER       NOT NULL REFERENCES productos(id),
    descripcion      VARCHAR(150)  NOT NULL,
    unidad           VARCHAR(10)   NOT NULL,
    cantidad         NUMERIC(12,3) NOT NULL,
    precio_unitario  NUMERIC(12,2) NOT NULL,
    importe          NUMERIC(12,2) NOT NULL,
    tasa_iva         NUMERIC(5,4)  NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_venta_detalle_venta ON venta_detalle (venta_id);

CREATE TABLE IF NOT EXISTS venta_pagos (
    id          BIGSERIAL PRIMARY KEY,
    venta_id    BIGINT        NOT NULL REFERENCES ventas(id),
    forma_pago  VARCHAR(20)   NOT NULL,
    monto       NUMERIC(12,2) NOT NULL,   -- importe aplicado (el efectivo ya sin cambio)
    referencia  VARCHAR(50)
);
CREATE INDEX IF NOT EXISTS ix_venta_pagos_venta ON venta_pagos (venta_id);

-- ---------------------------------------------------------------- Configuración general
CREATE TABLE IF NOT EXISTS configuracion (
    clave  VARCHAR(50) PRIMARY KEY,
    valor  TEXT NOT NULL
);
INSERT INTO configuracion (clave, valor) VALUES
    ('negocio_nombre', 'MI NEGOCIO'),
    ('negocio_rfc', ''),
    ('negocio_direccion', ''),
    ('negocio_telefono', ''),
    ('ticket_mensaje', '¡Gracias por su compra!')
ON CONFLICT DO NOTHING;
