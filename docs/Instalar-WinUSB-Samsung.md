# Instalar WinUSB para Samsung en modo Download (Odin)

Tu log muestra **VID=04E8 PID=685D** — es el modo Download/Odin correcto.  
La app **ya funciona**; Windows solo bloquea el acceso con el driver de Samsung.

> La plantilla INF de Microsoft que viste (`VID_0547`, "Fx2 Learning Kit") es **otro dispositivo**.  
> No la uses para el Samsung.

---

## Método recomendado: Zadig (5 minutos)

### 1. Preparación

- Teléfono en **modo Download** (pantalla de advertencia).
- Cierra **Odin**, **Smart Switch**, **Android File Transfer**, etc.
- Descarga [Zadig](https://zadig.akeo.ie/) y ejecútalo **como administrador**.

### 2. Opciones en Zadig

1. Menú **Options** → activa **List All Devices**.
2. En el desplegable superior, busca entradas como:
   - `SAMSUNG Mobile USB CDC Composite Device`
   - `USB Composite Device`
   - A veces aparece **`(Interface 0)`**, **`(Interface 1)`**, **`(Interface 2)`** — son distintas.

### 3. Elegir la interfaz correcta

En la esquina superior derecha de Zadig debe poner:

- **USB ID: 04E8 685D** (coincide con tu log)

Prueba **cada interfaz** del mismo dispositivo hasta ver en el cuadro de detalle algo parecido a:

- **Class: CDC Data** o clase **0x0A**
- Endpoints **Bulk** (no solo Interrupt)

**No instales WinUSB en la interfaz de módem (clase 0x02 / Communications).**  
Esa es la que suele tener driver `SAMSUNG Mobile USB Modem`.

### 4. Instalar driver

1. Campo destino: **WinUSB** (si falla, prueba **libusbK**).
2. Clic en **Replace Driver** / **Reinstall Driver**.
3. Espera a que termine sin error.

### 5. Después de Zadig

1. **Desenchufa** el USB.
2. Vuelve a entrar en **modo Download**.
3. Abre Thor_Flash → **Actualizar** → **Conectar USB**.

Si el log dice `Apertura: OK` o conecta sin error de driver, ya está.

---

## Método B: Administrador de dispositivos

1. `Win + X` → **Administrador de dispositivos**.
2. Busca **SAMSUNG** o **Dispositivos USB** con el teléfono en Download.
3. Expande **SAMSUNG Mobile USB CDC Composite Device**.
4. En cada hijo (módem, puerto serie, etc.):
   - Clic derecho → **Desinstalar dispositivo**.
   - Marca **Eliminar el controlador** si aparece.
5. Desenchufa, reconecta en Download.
6. Si Windows vuelve a instalar el driver Samsung, usa **Zadig** (método A).

---

## INF manual (avanzado)

En la carpeta `drivers/Samsung-Odin-685D/` hay un `.inf` de ejemplo para **04E8:685D**.  
Windows puede exigir firma de controlador; por eso **Zadig suele ser más fácil**.

Solo úsalo si sabes instalar drivers sin firmar o en entorno de pruebas.

---

## Comprobar en Thor_Flash

Al iniciar debe salir en el log: `USB handler v5 (flash NAND 10 min, PIT progreso, ZLP 1024B).`

Tras Zadig, **Diagnóstico** debería mostrar:

```text
→ Apertura: OK
→ Interfaz Odin candidata: #X (IN 0x8X, OUT 0x0X)
```

Luego **Conectar USB** → **Iniciar Odin**.

---

## Problemas frecuentes

| Síntoma | Qué hacer |
|--------|-----------|
| Sigue "driver bloquea" | Zadig en **otra** interfaz (0, 1 o 2) del mismo 04E8:685D |
| Zadig no lista el teléfono | Cable/puerto USB distinto; modo Download de nuevo |
| Conecta pero no Odin | Reinicia Download; no reutilices sesión USB sin reiniciar |
| WinUSB falla en Zadig | Prueba **libusbK** como destino |

---

## Referencias

- [DeviceHunt: 04E8:685D Download mode](https://devicehunt.com/view/type/usb/vendor/04E8/device/685D)
- [Zadig (libwdi)](https://github.com/pbatard/libwdi/wiki/Zadig)
- Protocolo Odin / LOKE: documentación en `docs/`
