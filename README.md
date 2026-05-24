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

## Flujo de sesión

1. `connect` → **Conectar USB**
2. `begin` → **Iniciar Odin**
3. Comandos Odin (tablas abajo)
4. `end` → **Fin sesión**
5. `disconnect` → **Desconectar** — tras flash o fin de sesión, **reinicia el teléfono en Download**

## Comandos → pantallas WPF

| Comando | En Thor_Flash |
|---------|---------------|
| `connect` | Dispositivo → Conectar USB |
| `begin` | Iniciar Odin |
| `end` | Fin sesión |
| `disconnect` | Desconectar |
| `flashTar` | Flash firmware → Escanear + Flashear selección |
| `flashFile` | Archivo suelto |
| `dumpPit` | PIT → Volcar PIT del dispositivo |
| `printPit` | Ver PIT (dispositivo / archivo) |
| `flashPit` | PIT → Flashear PIT desde archivo |
| `factoryReset` | PIT → Borrar userdata |
| `erasePartition` | Avanzado → Borrar partición |
| `setRegion` | Avanzado → Código de región |
| `options efsclear` | Flash firmware → casilla **EFS Clear** |
| `options blupdate/resetfc` | Automático en el motor al flashear `.tar` (BL en lote → bootloader update; reset flash count al final) |
| `reboot` / reinicio Odin | Reinicio (+ **Reinicio automático** tras flash) |
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
│   ├── OdinSession.cs           # Sesión USB + Odin (API de alto nivel)
│   └── SerialFlashOperations.cs # Reservado: flash por COM (no usado en Windows USB)
├── Thor_Flash/                  # App WPF (solo UI; referencia al Core)
├── docs/                        # WinUSB, flashear firmware
└── drivers/                     # INF ejemplo Samsung 04E8:685D
```

## Licencia

**MPL-2.0** — ver [LICENSE](LICENSE).
