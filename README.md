# FanMaxxxing 🌀❄

**Enfriador post-vicio para Windows 10/11.** Al terminar de jugar pone los ventiladores de CPU y GPU al 100 % unos minutos para sacar el calor residual. Si quieres, después apaga el PC. Viene con memes, partículas, un ventilador con cara y rangos de pingüino.

![Enfriando](docs/enfriando.png)

## Instalación

Copia **`dist\FanMaxxxing-1.0.0-Setup.msi`** al PC (por USB, red o la nube) y ábrelo con doble clic. El asistente está en español y no hace falta instalar .NET ni nada más. Hace esto:

- Instala en `C:\Program Files\FanMaxxxing`.
- **Menú Inicio** › FanMaxxxing: *FanMaxxxing*, *Enfriar y apagar*, *Enfriar ahora*, *FanMaxxxing (modo simulación)* y *Léeme*.
- **Escritorio**: *FanMaxxxing* y *Enfriar y apagar (FanMaxxxing)*.
- Aparece en **Configuración › Aplicaciones**, desde donde se desinstala. Al desinstalar se cierra la app si estaba abierta (devolviendo antes los ventiladores a la normalidad) y se borran sus tareas programadas. Tus ajustes y estadísticas se quedan en `%LOCALAPPDATA%\FanMaxxxing`.
- Para actualizar, instala un MSI más nuevo encima: sustituye al anterior.

> El instalador no está firmado digitalmente, así que Windows puede decir «Editor desconocido» o, si lo descargas de internet, mostrar SmartScreen («Más información» → «Ejecutar de todas formas»).

Instalación silenciosa, sin accesos en el escritorio:

```powershell
msiexec /i FanMaxxxing-1.0.0-Setup.msi /qb DESKTOPSHORTCUTS=0
```

También se puede usar sin instalar: `publish\FanMaxxxing.exe` es portable.

## Cómo se usa

1. Abre **FanMaxxxing** (o *Enfriar y apagar*, que empieza directamente). Pedirá permisos de administrador, igual que FanControl. Para que no pregunte cada vez, activa en Ajustes «Abrir sin que Windows pregunte permisos».
2. Elige la **duración** (3, 4, 5, 7 o 10 min) y qué hacer **al terminar**: nada, apagar, hibernar o suspender.
3. Pulsa **MAXXEAR**. Ventiladores al 100 %, el PC no se suspende mientras tanto y al acabar todo vuelve a automático.
4. Si elegiste apagar, sale una cuenta atrás de 30 s (configurable) con un botón **«¡QUIETO! Sigo despierto»** por si te arrepientes.

> **Primera vez en tu PC gaming:** abre ⚙ Ajustes y pulsa **«🧪 Probar 10 segundos»**. Si oyes el despegue, todo está bien configurado.

## Cómo controla los ventiladores

### Motor FanControl (recomendado, por defecto si lo tienes)

Usa el [FanControl de Rem0o](https://github.com/Rem0o/FanControl.Releases) que ya tienes configurado, a través de su línea de comandos oficial (`FanControl.exe -c config.json`, que cambia la config en caliente):

1. Lee **tu config normal** de la carpeta `Configurations` de FanControl. En modo «Automática» usa la que FanControl guardó por última vez, o sea, la que tienes activa.
2. Genera una copia, `FanMaxxxing_MAX.json`, con todos los ventiladores en **manual al 100 %**. El resto de tu config no se toca.
3. Carga esa copia, espera el tiempo elegido y **vuelve a cargar tu config normal**.
4. Si FanControl estaba cerrado al empezar, lo vuelve a cerrar al terminar (opcional).

En Ajustes puedes:
- Desmarcar ventiladores que no quieras al máximo (por ejemplo, una bomba de agua).
- Usar una config MAX hecha a mano en FanControl en vez de la generada.

### Motor directo (experimental)

Para quien no tenga FanControl. Controla los ventiladores de la placa base y de la GPU con [LibreHardwareMonitor](https://github.com/LibreHardwareMonitor/LibreHardwareMonitor), la misma librería que usa FanControl por dentro. Necesita el driver **PawnIO**, que instala FanControl o se descarga de pawnio.eu. No funciona con FanControl abierto, para que no se peleen por los ventiladores.

### Simulación

Temperaturas de mentira para ver la app sin tocar hardware: `FanMaxxxing.exe --sim`. En simulación **nunca** se apaga el PC de verdad.

## Fiabilidad

- **Siempre vuelve a automático**: la restauración está en un `finally`, así que pasa aunque canceles, haya un error o cierres la app.
- **Si la app muere a mitad** (cuelgue, matar el proceso, apagón), deja un marcador en disco. Al volver a abrirla, devuelve FanControl a tu config normal.
- **Si Windows se apaga a mitad de un enfriamiento**, restaura tu config antes de cerrarse.
- **Sin fugas de memoria**:
  - Un único bucle de animación, que se detiene cuando la ventana está oculta o minimizada.
  - Partículas en un array de tamaño fijo y pinceles congelados.
  - Historial de LibreHardwareMonitor desactivado (por defecto guarda 24 h por sensor).
  - Timers parados y eventos desenganchados al salir.
  - Probado con 28 enfriamientos seguidos: memoria gestionada plana (~3,8 MB), y handles y objetos GDI/USER estables.
- Animaciones limitadas a 40 fps (24 en modo reducido) para no calentar la GPU que queremos enfriar.
- Log con rotación (máx. 1 MB) en `%LOCALAPPDATA%\FanMaxxxing\log.txt`.

## Extras

- **Parada inteligente**: si CPU y GPU bajan de 45 °C (configurable) durante 20 s seguidos, termina antes.
- **Apagar la pantalla** mientras enfría.
- **Bandeja del sistema**: clic derecho → «Enfriar ahora» o «Enfriar y apagar».
- **Iniciar con Windows** y **abrir sin UAC**: crean tareas programadas elevadas, como hace FanControl.
- **Accesos directos en el escritorio** «Enfriar y apagar» y «Enfriar ahora»: un doble clic antes de irte a dormir y listo.
- **Rangos**: Tostadora → Microondas → Ventilador de pie → Aire acondicionado → Nevera industrial → Pingüino emperador → Cero absoluto.

## Línea de comandos

```
FanMaxxxing.exe --start --minutes 5 --then shutdown
```

| Argumento | Qué hace |
|---|---|
| `--start` | Empieza a enfriar nada más abrir |
| `--minutes N` | Duración (1-30) |
| `--then shutdown\|hibernate\|sleep\|nothing` | Qué hacer al terminar |
| `--tray` | Arranca minimizado en la bandeja |
| `--sim` | Modo simulación |
| `--no-elevate` | No pedir permisos de administrador |

Si la app ya está abierta, la nueva instancia le pasa la orden y se cierra.

## Compilar

Requiere el SDK de .NET 8. WiX 5 (licencia MS-RL) se descarga solo de NuGet. Un único comando publica la app y genera el instalador en `dist\`:

```powershell
powershell -ExecutionPolicy Bypass -File .\build-installer.ps1
```

Para una versión nueva, sube `<Version>` en `src\FanMaxxxing\FanMaxxxing.csproj`: el MSI toma la versión del exe.

En compilación Debug hay opciones extra para pruebas: `--debug-seconds N`, `--debug-loop` y `--snap ruta.png`.

## Problemas conocidos

- **«No aparece ningún ventilador»**: tu versión de FanControl guarda la config con otro formato. Crea en FanControl una config con todo en manual al 100 %, guárdala y elígela en Ajustes → «Usar mi propia config MAX».
- **No se apaga**: algún programa con cambios sin guardar está bloqueando el apagado. Activa «Forzar el cierre de programas» en Ajustes (esos cambios se perderán).
- **CPU «--»**: la temperatura de la CPU necesita permisos de administrador y el driver PawnIO. Sin ella, el enfriamiento funciona igual por tiempo.

## Estructura

```
src/FanMaxxxing/
  Core/   lógica: motores (FanControl, directo, simulación), enfriamiento, sensores, apagado, ajustes, log
  UI/     ventilador animado, partículas, texto de meme, tarjetas, bandeja
  MainWindow / SettingsWindow / App / Program (Main propio)
installer/                proyecto WiX del MSI (Package.wxs, textos en español, licencia, imágenes)
build-installer.ps1       publica la app y genera el MSI
publish/FanMaxxxing.exe   ejecutable portable (autocontenido, ~65 MB)
dist/                     instalador MSI listo para copiar
```

FanControl es software de Rémi Mercier (código cerrado, uso personal). FanMaxxxing solo usa su línea de comandos pública. LibreHardwareMonitor tiene licencia MPL-2.0.
