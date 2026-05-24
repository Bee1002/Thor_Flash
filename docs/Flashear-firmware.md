# Flashear firmware (flujo Thor en Thor_Flash)

Basado en el protocolo Odin estándar — misma lógica de PIT + `.tar` que un flasheo por lotes.

## Orden recomendado

1. Modo **Download** → **Conectar USB** → **Iniciar Odin** (`Sesión Odin activa`).
2. **Flash firmware**:
   - Ruta a **carpeta** con `AP_*.tar.md5`, `BL_*.tar.md5`, etc., **o** un solo `.tar` (botón **.tar…**).
   - **Escanear** → tabla con imágenes que coinciden con el PIT.
   - Marca particiones → **Flashear selección**.
3. Si el escaneo no encuentra nada, el **log** lista:
   - archivos dentro de cada `.tar`;
   - nombres que el **PIT del teléfono** espera.
4. **Archivo suelto**: una imagen `.img` / `.lz4` y partición en el desplegable (se rellena al iniciar Odin).

## Opciones Odin (pestaña Flash)

- **EFS Clear** — equivalente a borrar EFS en Odin oficial.
- **Bootloader Update** — actualización de bootloader.
- **Reset flash count** — contador de flashes (activado por defecto).

## Error «Bulk read failed: Timeout» al Iniciar Odin

Significa que el teléfono **no respondió LOKE** tras enviar `ODIN`. Suele pasar si:

1. Ya usaste **Iniciar Odin** o flasheaste antes **sin** reiniciar el teléfono en Download.
2. WinUSB está en la **interfaz equivocada** (debe ser CDC Data **0x0A**, no módem 0x02).

**Solución:** **Desconectar** en la app → apagar/reiniciar modo Download → **Conectar** → **Iniciar Odin** (solo una vez; espera hasta 30 s).

La build **v3** del handler hace flush USB, ZLP correcto en WinUSB y reintento automático del handshake.

## Tras flashear o «Fin sesión»

Reinicia el teléfono en **modo Download** y vuelve a conectar. No reutilices la misma sesión USB (aviso del README de Thor).

## Si solo flasheaste `bootloader`

Correcto para probar. Un firmware completo suele requerir varios `.tar` (AP, BL, CP, CSC) escaneados y marcados en la tabla.
