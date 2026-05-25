# Flashear firmware (flujo Thor en Thor_Flash)

Basado en el protocolo Odin estándar — misma lógica de PIT + `.tar` que un flasheo por lotes.

## Orden recomendado

1. Modo **Download** → conexión **automática** (USB + Odin + PIT). La barra inferior pasa a **Ready**.
   - Soporte manual opcional: expandir **Opciones de conexión** (Conectar / Iniciar Odin).
2. **Flash firmware** (pestaña principal):
   - Ruta a **carpeta** con `AP_*.tar.md5`, `BL_*.tar.md5`, etc., **o** un solo `.tar` / `.tar.md5` (botón **.tar…**).
   - **Escanear** → tabla BL / AP / CP / CSC según el PIT del teléfono.
   - Marca particiones → **Flashear selección** → vista de **log** a pantalla completa.
3. Tras el flash, revisa el log y pulsa **Nuevo flash** para volver a la selección de particiones.

## Opciones Odin (pestaña Flash)

- **EFS Clear** — equivalente a borrar EFS en Odin oficial.
- **Reinicio automático** — reinicia por protocolo al terminar el flash.

**Automático en el motor** (no hay casillas en la UI):

- **Bootloader Update** — se activa al flashear un `.tar` que incluye BL.
- **Reset flash count** — se envía al final del lote de flash.

## Error «Bulk read failed: Timeout» al iniciar Odin

Significa que el teléfono **no respondió LOKE** tras enviar `ODIN`. Suele pasar si:

1. Ya flasheaste o iniciaste Odin **sin** reiniciar el teléfono en Download.
2. WinUSB está en la **interfaz equivocada** (debe ser CDC Data **0x0A**, no módem 0x02).

**Solución:** **Desconectar** en la app → reiniciar modo Download → esperar **Ready** (o Conectar + Iniciar Odin manualmente una vez).

## Tras flashear

Con **Reinicio automático** activo, el teléfono sale de Download por protocolo. Para otro flash, vuelve a modo Download y espera **Ready** de nuevo.

## Si solo flasheaste `bootloader`

Correcto para probar. Un firmware completo suele requerir varios `.tar` (AP, BL, CP, CSC) escaneados y marcados en la tabla.
