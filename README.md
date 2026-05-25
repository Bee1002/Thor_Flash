# Thor_Flash

**Herramienta de flasheo Odin para Samsung en Windows con interfaz WPF.**

Motor de protocolo completo (LOKE, flash, PIT, reinicio) en `Protocol.Thor.Library` (`Thor_Flash.Core`), con capa USB nativa para Windows.

| Componente | Implementación |
|------------|----------------|
| Protocolo Odin (LOKE, flash, PIT, reinicio) | `Protocol.Thor.Library` en `Thor_Flash.Core` |
| USB | **Windows** (`Platform/Windows.cs`, LibUsbDotNet + WinUSB/Zadig) |
| Interfaz | **WPF** `Thor_Flash` |

## Requisitos

- Windows 10/11, .NET 8
- Teléfono Samsung en **modo Download** (VID `04E8`)
- Driver **WinUSB** en la interfaz CDC **0x0A** ([guía](docs/Instalar-WinUSB-Samsung.md))

## Compilar y ejecutar

```bash
dotnet build Thor_Flash.sln -c Release
```

Ejecutable: `Thor_Flash\bin\Release\net8.0-windows\Thor_Flash.exe`

## Flujo de sesión (WPF)

1. Conectar el teléfono en Download → **conexión automática** (USB, Odin, lectura PIT) → **Ready**.
2. **Flash firmware**: carpeta o `.tar` → Escanear → Flashear selección.
3. Log a pantalla completa durante el flash; **Nuevo flash** vuelve a la selección.
4. Opcional: **Opciones de conexión** (expandir arriba) para Conectar / Iniciar Odin / Desconectar manualmente.

Ver [Flashear firmware](docs/Flashear-firmware.md).

## Comandos Thor CLI → pantallas WPF

| Comando | En Thor_Flash |
|---------|---------------|
| `connect` / `begin` | Automático al detectar Download; manual en **Opciones de conexión** |
| `end` / `disconnect` | **Fin sesión** / **Desconectar** (Opciones de conexión) |
| `flashTar` | Flash firmware → Escanear + Flashear selección |
| `dumpPit` | PIT → Volcar PIT del dispositivo |
| `printPit` | Ver PIT (dispositivo / archivo) |
| `flashPit` | PIT → Flashear PIT desde archivo |
| `factoryReset` | No disponible en la interfaz WPF |
| `erasePartition` | No disponible en la interfaz WPF |
| `setRegion` | No disponible en la interfaz WPF |
| `flashFile` | No disponible en la interfaz WPF |
| `options efsclear` | Flash firmware → casilla **EFS Clear** |
| `options blupdate` | **Automático** al flashear `.tar` con BL |
| `options resetfc` | **Automático** al final del lote de flash |
| `reboot` | Reinicio (+ **Reinicio automático** tras flash) |
| `write` / `read` raw USB | No disponible en la interfaz WPF |

## Estructura del repositorio

```
Thor_Flash.sln
├── Thor_Flash.Core/             # Motor: Protocol.Thor.Library + USB Windows
│   ├── Communication/           # IHandler, USB, dispositivos
│   ├── Protocols/               # LOKE/Odin (handshake, flash, PIT, reinicio)
│   ├── Platform/                # WinUSB (LibUsbDotNet, ZLP, interfaces CDC 0x0A)
│   ├── PIT/                     # Tabla de particiones
│   ├── OdinOperations.cs        # Flash .tar, progreso, escaneo firmware
│   └── OdinSession.cs           # Sesión USB + Odin (API de alto nivel)
├── Thor_Flash/                  # App WPF (solo UI; referencia al Core)
├── docs/                        # WinUSB, flashear firmware
└── drivers/                     # INF ejemplo Samsung 04E8:685D
```

## Licencia

**MPL-2.0** — ver [LICENSE](LICENSE).
