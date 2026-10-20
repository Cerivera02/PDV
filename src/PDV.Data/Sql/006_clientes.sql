-- =====================================================================
-- 006 - Clientes: crédito (cargos y abonos), precio especial y datos fiscales
-- =====================================================================

CREATE TABLE IF NOT EXISTS clientes (
    id                    SERIAL PRIMARY KEY,
    nombre                VARCHAR(150)  NOT NULL,
    telefono              VARCHAR(30),
    direccion             VARCHAR(250),
    -- Crédito
    limite_credito        NUMERIC(12,2) NOT NULL DEFAULT 0 CHECK (limite_credito >= 0),
    saldo                 NUMERIC(12,2) NOT NULL DEFAULT 0,      -- lo que debe
    -- Precio especial (no se acumula con promociones)
    descuento_porcentaje  NUMERIC(5,2)  NOT NULL DEFAULT 0 CHECK (descuento_porcentaje BETWEEN 0 AND 90),
    precio_mayoreo        BOOLEAN       NOT NULL DEFAULT FALSE,
    -- Datos para factura
    rfc                   VARCHAR(13),
    razon_social          VARCHAR(250),
    codigo_postal_fiscal  VARCHAR(5),
    regimen_fiscal        VARCHAR(3),
    uso_cfdi              VARCHAR(4),
    correo                VARCHAR(150),
    activo                BOOLEAN       NOT NULL DEFAULT TRUE,
    creado_en             TIMESTAMPTZ   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_clientes_nombre_trgm ON clientes USING gin (nombre gin_trgm_ops);

-- Estado de cuenta: cada venta a crédito, abono o cancelación
CREATE TABLE IF NOT EXISTS movimientos_cliente (
    id                BIGSERIAL PRIMARY KEY,
    cliente_id        INTEGER       NOT NULL REFERENCES clientes(id),
    tipo              VARCHAR(12)   NOT NULL CHECK (tipo IN ('CARGO', 'ABONO', 'CANCELACION')),
    monto             NUMERIC(12,2) NOT NULL,          -- + aumenta lo que debe, − lo disminuye
    saldo_resultante  NUMERIC(12,2) NOT NULL,
    venta_id          BIGINT        REFERENCES ventas(id),
    turno_id          BIGINT        REFERENCES turnos(id),   -- los abonos cuentan en el corte de ese turno
    forma_pago        VARCHAR(20),
    referencia        VARCHAR(50),
    nota              VARCHAR(250),
    usuario_id        INTEGER       NOT NULL REFERENCES usuarios(id),
    fecha             TIMESTAMPTZ   NOT NULL DEFAULT now()
);
CREATE INDEX IF NOT EXISTS ix_movimientos_cliente ON movimientos_cliente (cliente_id, fecha DESC);
CREATE INDEX IF NOT EXISTS ix_movimientos_cliente_turno ON movimientos_cliente (turno_id) WHERE turno_id IS NOT NULL;

ALTER TABLE ventas ADD COLUMN IF NOT EXISTS cliente_id INTEGER REFERENCES clientes(id);
CREATE INDEX IF NOT EXISTS ix_ventas_cliente ON ventas (cliente_id) WHERE cliente_id IS NOT NULL;
