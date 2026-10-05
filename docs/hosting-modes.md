# Modos de ejecución: consola y Windows Service

El mismo `smtp-mockup.exe` funciona como aplicación de consola y como Windows Service. No hay dos
binarios ni un flag que mantener sincronizado: el proceso decide al arrancar (D-10).

## Detección del modo

`Hosting:Mode` decide la política y el entorno decide el resultado:

| `Hosting:Mode` | Windows, arrancado por el SCM | Windows, desde una terminal | Linux / macOS |
| --- | --- | --- | --- |
| `Auto` (default) | Servicio | Consola | Consola |
| `Console` | Consola + aviso por stderr | Consola | Consola |
| `WindowsService` | Servicio | Servicio | Consola |

Tres casos que merecen explicación:

- **`Auto` fuera de Windows siempre es consola.** No hay SCM equivalente, así que "detectar" no
  puede significar otra cosa.
- **`WindowsService` fuera de Windows degrada a consola en vez de fallar.** Si alguien despliega el
  mismo paquete en un contenedor Linux para pruebas, arrancar es más útil que morir.
- **`Console` arrancado por el SCM es un error de configuración**, no un modo nuevo: el token del
  proceso no corresponde al de un usuario interactivo, así que el log iría a una stdout que no
  existe. El proceso avisa por stderr y sigue en consola.

La lógica está en `HostingModeResolver` (`src/SmtpMockup.Core/Hosting/`) como **función pura** que
recibe el SO y la detección del SCM como parámetros, de modo que los seis casos de la tabla se
testean sin una máquina Windows ni un servicio instalado.

La primera línea del arranque dice siempre el modo efectivo, el modo configurado y si el SCM
arrancó el proceso. Sin eso, un servicio que "no dice nada" es imposible de diagnosticar:

```
info: SmtpMockup.Startup[0] Running as Console (configured Hosting:Mode=Auto, started by SCM=False);
      executable directory=/opt/smtp-mockup/; content root=/opt/smtp-mockup/
```

## Logging por modo

| Modo | Destinos |
| --- | --- |
| Consola | `Console` (una línea, timestamp UTC) |
| Servicio | Archivo diario + Event Log de Application |

Los providers por defecto de `WebApplicationBuilder` se limpian siempre: en servicio, `Debug` y
`EventSource` activados sólo añadirían ruido y una segunda fuente en el Event Log.

El Event Log lleva un filtro de nivel aparte porque tiene cuota y la poda solo; el archivo recibe
todo lo que llegue al nivel por defecto (`Logging:LogLevel:Default`), que es el destino barato para
bajarlo a `Debug`.

Claves de `Hosting:*`:

| Clave | Default | Para qué |
| --- | --- | --- |
| `Mode` | `Auto` | Política de detección (tabla de arriba) |
| `LogDirectory` | `logs` | Carpeta de los archivos. Relativa ⇒ bajo el ejecutable |
| `LogFileName` | `smtp-mockup-{Date}.log` | `{Date}` ⇒ `yyyy-MM-dd`, un archivo por día |
| `LogRetentionDays` | `7` | Días que se conservan; `0` desactiva la poda |
| `EventLogSource` | `smtp-mockup` | Origen registrado en Application |
| `EventLogLevel` | `Information` | Nivel mínimo enviado al Event Log |

Dos reglas que no son negociables en modo servicio:

- **El log en archivo no se puede desactivar.** Con `LogDirectory` o `LogFileName` vacíos el proceso
  muere al arrancar con un mensaje que nombra la clave. El validador de opciones rechaza el caso
  explícito (`Mode=WindowsService`); como el modo no se conoce hasta que corre, el caso
  `Mode=Auto` arrancado por el SCM se comprueba en `Program.cs` una vez resuelto el modo.
- **El Event Log sólo se registra en Windows.** `AddEventLog` lanza
  `PlatformNotSupportedException` fuera de Windows, así que hay un guard explícito: forzar
  `Mode=WindowsService` en Linux arranca y loguea a archivo en vez de morir con una excepción de
  plataforma.

La poda se ejecuta al arrancar, con el archivo del día ya escrito. Compara las **fechas del
nombre**, no la fecha de modificación: un log de hace tres días que acaba de tocarse (un reinicio)
no debe desaparecer. Sólo borra archivos cuyo nombre coincide con el patrón propio y que cuelgan
de la carpeta de logs configurada.

## Rutas

**Toda ruta relativa cuelga de `AppContext.BaseDirectory`, no del directorio de trabajo.** Un
Windows Service arranca con `C:\Windows\System32` como CWD, así que resolver contra el CWD dejaría
los correos y el PFX escribiendo en System32. `HostPath` centraliza esa semántica:

- `ContentRootPath` se fija a `AppContext.BaseDirectory` en `Program.cs`. Si no, `appsettings.json`
  no se encontraría en modo servicio.
- `Storage:Directory`, `Certificate:Path` y `Hosting:LogDirectory` se resuelven con `HostPath`.

```
C:\tools\smtp-mockup\
├── smtp-mockup.exe
├── appsettings.json      <- se lee SIEMPRE de aquí
├── data\messages\        <- los correos capturados
├── certs\dev.pfx         <- el certificado del listener STARTTLS
└── logs\                 <- sólo en modo servicio
```

Comprobación rápida de que la semántica es la correcta: arrancar el `.exe` desde otro directorio y
verificar que `data\` y `certs\` aparecen junto al `.exe`, no en el CWD.

## Certificados

`Certificate:Mode=Auto` (default) busca en este orden: `Certificate:Path` → certificado de
desarrollo de `dotnet dev-certs` → PFX autogenerado y guardado en `Certificate:Path`.

**El certificado de desarrollo pertenece al usuario interactivo, no a la cuenta del servicio.** Con
`LocalSystem` el `CurrentUser\My` es el de SYSTEM, así que no lo ve y cae al PFX autogenerado, que
ningún cliente confía hasta que se instala explícitamente (ver `docs/certificate-trust.md` §4.1).
Para que un cliente confíe sin instalar nada, lo más simple es arrancar el servicio con una cuenta
de dominio: hereda el almacén de certificados de ese usuario.

## Permisos por cuenta

La cuenta del servicio tiene que poder **leer** `appsettings.json` y `smtp-mockup.exe`, y
**escribir** en `data\`, `certs\` y `logs\`. Las carpetas las crea el proceso al arrancar, con los
permisos de su propia cuenta; el script de instalación no las crea ni ajusta ACLs.

| Cuenta | Red | Consecuencia |
| --- | --- | --- |
| `LocalSystem` | Sí | `CurrentUser\My` es el de SYSTEM: no ve `dotnet dev-certs` |
| `NetworkService` | Sí | Privilegios de red mínimos; opción razonable en desarrollo |
| `LocalService` | No | El mockup sigue funcionando en localhost |
| `DOMINIO\usuario` | Sí | Hereda los certificados de ese usuario; requiere `-Password` |

`LocalSystem` también tiene el SID de SYSTEM como owner de los ficheros que crea, lo que hace que
quitarlos después requiera SUBs/F o cambiar la herencia. `NetworkService` es más manejable para
desinstalar.

## Publicación

```bash
dotnet publish src/SmtpMockup.Host -p:PublishProfile=win-x64
```

Sale en `publish/win-x64/`. El equivalente sin perfil:

```bash
dotnet publish src/SmtpMockup.Host -c Release -r win-x64 --self-contained true
```

Ambos producen lo mismo: **self-contained, single-file, sin trimming**.

- **Sin trimming**: MailKit/MimeKit y el binder de `Options` usan reflexión. Un publish recortado
  rompe en runtime, no al compilar (DESIGN §9).
- **`IncludeNativeLibrariesForSelfExtract`**: sin él Windows bloquea el `.exe` mientras se extrae la
  caché nativa.
- **`IncludeAllContentForSelfExtract` se deja OFF**: extraería `wwwroot` a `%TEMP%` en cada
  arranque, y como servicio eso es escribir en disco en cada start.

Los perfiles se importan **después** del cuerpo del `.csproj`, así que una condición del `.csproj`
sobre `$(RuntimeIdentifier)` todavía no ve el RID que fija el perfil. Por eso los valores viven en
el `.pubxml`; el `.csproj` los repite para el caso `-r win-x64` a pelo, donde el RID sí es una
propiedad global durante la evaluación.

Aunque el binario sea único, **`appsettings.json` y `wwwroot/` siguen siendo archivos aparte**:
hay que distribuir los tres juntos junto al `.exe`. `wwwroot/` no es opcional desde que existe la
UI: sin él la página se sirve sin CSS y **sin `_framework/blazor.web.js`**, que es el script que
abre el circuito de SignalR, así que el listado se ve pero no se actualiza y ningún botón
responde (SPEC §11, criterio 8).

```bash
# Comprobación rápida de que la publicación está completa
ls publish/win-x64/wwwroot/_framework/blazor.web.js
ls publish/win-x64/wwwroot/_content/MudBlazor/MudBlazor.min.css
```

El script `install-service.ps1` no copia archivos: registra el servicio apuntando al `.exe` que
se le pase. La carpeta que contiene el `.exe` (con su `wwwroot`) es la que hay que desplegar.

## Instalar como servicio

```powershell
# Desde una consola de PowerShell como Administrador
.\scripts\install-service.ps1 -Path C:\tools\smtp-mockup\smtp-mockup.exe
.\scripts\install-service.ps1 -Name smtp-mockup-dev -Account '.\devuser' -Password 'secret' -DelayedStart
```

El script, con el `.exe` ya publicado:

1. Escribe `Hosting:LogDirectory` y `Hosting:EventLogSource` en el `appsettings.json` que hay junto
   al `.exe` (es donde la app los lee; como variables de PowerShell no cambiarían nada).
2. Registra el servicio con arranque automático y reinicio ante fallo (3 intentos, 60 s).
3. Aplica la cuenta **siempre** con `sc config obj=`, también para las cuentas de sistema: omitirlo
   deja al servicio corriendo como LocalSystem.
4. Registra el origen del Event Log si falta.
5. Si cualquier paso posterior a `sc create` falla, borra el servicio a medio configurar: es peor
   que no tener servicio.

Sobre `-StartupType`:

| Valor | `sc` | Notas |
| --- | --- | --- |
| `Automatic` | `start= auto` | |
| `AutomaticDelayedStart` | `start= auto` + `start= delayed-auto` | `delayed-auto` **sólo** existe en `sc config`, no en `sc create` |
| `Manual` | `start= demand` | |
| `Disabled` | `start= disabled` | |

### Operación

```powershell
Start-Service -Name smtp-mockup
Get-Service -Name smtp-mockup
Restart-Service -Name smtp-mockup
Stop-Service -Name smtp-mockup

# Logs
Get-Content C:\tools\smtp-mockup\logs\smtp-mockup-2026-10-03.log -Tail 50 -Wait
Get-WinEvent -LogName Application -FilterHashtable @{ LogName='Application'; SourceName='smtp-mockup' } -MaxEvents 20
```

Para arrancar desde una terminal **el mismo binario del servicio**, con el log en pantalla, hay que
forzar el modo consola, porque si lo arranca el SCM gana:

```powershell
.\smtp-mockup.exe --Hosting:Mode=Console
```

Parado ordenado: 5 s de drain para que los circuitos de Blazor terminen y los listeners SMTP cierren
las conexiones abiertas. Un `Stop-Service` no corta en seco.

## Desinstalar

```powershell
.\scripts\uninstall-service.ps1
.\scripts\uninstall-service.ps1 -Name smtp-mockup-dev -DeleteData
```

- Para el servicio y quita el origen del Event Log, que resuelve leyendo `Hosting:EventLogSource`
  del `appsettings.json` (puede no ser el nombre del servicio). `-KeepEventLogSource` lo conserva y
  `-EventLogSource` lo sobrescribe.
- **No borra `data\`, `logs\` ni `certs\`**: son datos del usuario, y borrarlos por sorpresa sería
  peor que dejar archivos huérfanos. `-DeleteData` los elimina explícitamente.
- Si `sc delete` falla con *marked for deletion*, normalmente queda un handle abierto sobre el `.exe`:
  reinicia la consola de PowerShell y vuelve a intentarlo.

## Probar los scripts

La parte con parsing de texto está en `scripts/ServiceImagePath.ps1` (leída desde el `ImagePath` del
registro) para poder testearla sin Windows ni un servicio instalado:

```bash
pwsh -File scripts/ServiceImagePath.Tests.ps1
```

Son 14 casos sin Pester ni dependencias. Merece la pena que sea código real y no una copia: el
parsing de rutas con comillas y espacios es justo lo que se rompe en silencio, y una comilla
colgando hace que `-DeleteData` no encuentre el ejecutable **después** de haber borrado el servicio.

## Verificar en Windows

Pendiente de una máquina Windows real; en Linux sólo se puede validar la detección a consola:

- [ ] `Start-Service` / `Stop-Service` / `Restart-Service` y arranque tras reiniciar la máquina.
- [ ] Que la cuenta configurada es la que corre el proceso (`Get-CimInstance Win32_Service`).
- [ ] Que el Event Log recibe entradas con el origen configurado.
- [ ] Que el servicio escribe en `data\`, `certs\` y `logs\` con esa cuenta.
- [ ] Que el `.exe` single-file arranca igual y desde otro directorio de trabajo.
- [ ] Que `dotnet dev-certs` se ve con una cuenta de dominio y **no** con `LocalSystem`.
- [ ] Que `-AutomaticDelayedStart` deja el servicio en arranque diferido de verdad.
## La UI y el servicio

Con `Web:Enabled=true` (por defecto) el proceso también escucha HTTP en
`Web:BindAddress:Web:Port`, que por defecto es `127.0.0.1:8080`. Lo que cambia al instalarlo como
servicio:

| Tema | Servicio | Consola |
|------|----------|---------|
| Assets | Hay que desplegar `wwwroot/` junto al `.exe` | `dotnet run` los resuelve desde `obj/` |
| Raíces de rutas | `AppContext.BaseDirectory`, no el CWD del SCM | igual |
| Cortafuegos | `8090`/puerto elegido probablemente cerrado en un servidor | sin restricciones |
| Prueba de humo desde otro equipo | no aplica | `curl http://127.0.0.1:8080/_framework/blazor.web.js` ⇒ `200` |

Detalle completo de la UI en [`web-ui.md`](web-ui.md).
