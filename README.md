# smtp-mockup

Servidor SMTP falso para desarrollo, con UI web incluida. **Recibe** todo el correo que le mandes,
sin autenticación y sin relay, lo guarda como un JSON por mensaje y lo muestra en una interfaz Blazor
que se actualiza sola. Un solo proceso, un solo ejecutable, sin dependencias nativas.

- **SMTP en claro** en `127.0.0.1:8025` (sin credenciales, sin cifrar).
- **SMTP con STARTTLS** en `127.0.0.1:8443`; el certificado autofirmado se genera solo la primera
  vez y se persiste.
- **UI web** en `http://127.0.0.1:8080/`: listado, filtros, detalle con cuerpos y adjuntos,
  descargas y borrado.
- Persistencia en archivos JSON particionados por día, junto al ejecutable (`data/messages/`).

No envía correo a ningún sitio, no valida destinatarios, no autentica y **no es un relay** (cero
conexiones SMTP salientes). Es una herramienta local de desarrollo.

## Requisitos

| | |
|---|---|
| Para compilar desde el código | **.NET SDK 10** (verificado con 10.0.112) |
| Para usar el binario publicado | ninguno: es *self-contained* |
| Plataformas | Windows, Linux, macOS (sin dependencias nativas) |

## Arranque rápido

Desde el código:

```bash
dotnet run --project src/SmtpMockup.Host
```

Desde un binario publicado:

```bash
./smtp-mockup                     # Linux / macOS
smtp-mockup.exe                   # Windows
```

Con los valores por defecto quedan escuchando `127.0.0.1:8025` (SMTP en claro),
`127.0.0.1:8443` (STARTTLS) y `127.0.0.1:8080` (la UI). Las primeras líneas del log dicen el modo
de ejecución, los puertos y dónde se escribe:

```
info: SmtpMockup.Startup[0] Running as Console (configured Hosting:Mode=Auto, started by SCM=False); …
info: SmtpMockup.Startup[0] SMTP listeners configured: plain=True on 127.0.0.1:8025, secure=True on 127.0.0.1:8443 (security=StartTls, max 25 MB)
info: SmtpMockup.Smtp.SmtpListenerService[0] SMTP listener started kind=Plain endpoint=127.0.0.1:8025 security=None maxMessageSizeBytes=26214400 certificate=(none)
info: SmtpMockup.Startup[0] Web UI configured on http://127.0.0.1:8080
info: Microsoft.Hosting.Lifetime[14] Now listening on: http://127.0.0.1:8080
```

## Configuración

Todo se configura en `appsettings.json`, en la carpeta **del ejecutable** (no en el directorio de
trabajo: es lo que permite que el mismo binario funcione con doble clic y como servicio de Windows).
La precedencia es la habitual de .NET: `appsettings.json` → variables de entorno → argumentos de
línea de comandos.

### Argumentos de línea de comandos

```bash
./smtp-mockup --Smtp:Plain:Port=9025 --Web:Port=9080
./smtp-mockup --Smtp:StartTls:Enabled=false --Web:BindAddress=0.0.0.0

# Desde otra máquina de la red local (el log avisa: no hay autenticación en ningún sitio).
./smtp-mockup --Smtp:Plain:BindAddress=0.0.0.0 --Web:BindAddress=0.0.0.0

# Puertos efímeros: los elige el sistema. El log dice cuáles son, en la línea
# "SMTP listener started kind=… endpoint=…" y en "Now listening on:".
./smtp-mockup --Smtp:Plain:Port=0 --Smtp:StartTls:Port=0 --Web:Port=0
```

### Variables de entorno

Doble guion bajo como separador (`__`), cómodo para containers y para no depender del shell:

```bash
Smtp__Plain__Port=9325 Smtp__StartTls__Enabled=false Web__Port=9380 ./smtp-mockup
```

### Claves disponibles

| Clave | Default | Qué hace |
|---|---|---|
| `Smtp:Plain:Enabled` | `true` | Activa el listener en claro |
| `Smtp:Plain:Port` | `8025` | Puerto del listener en claro (`0` = efímero) |
| `Smtp:Plain:BindAddress` | `127.0.0.1` | Dirección de binding; fuera de loopback ⇒ aviso en el log |
| `Smtp:Plain:Security` | `None` | `None` \| `StartTls` \| `Implicit` |
| `Smtp:StartTls:Enabled` | `true` | Activa el listener cifrado |
| `Smtp:StartTls:Port` | `8443` | Puerto del listener cifrado (`0` = efímero) |
| `Smtp:StartTls:BindAddress` | `127.0.0.1` | Dirección de binding; fuera de loopback ⇒ aviso en el log |
| `Smtp:StartTls:Security` | `StartTls` | `StartTls` (anuncia, no exige) \| `Implicit` (SMTPS) |
| `Smtp:MaxMessageSizeMb` | `25` | Por encima ⇒ `552` y ningún archivo. Máximo `2047` |
| `Certificate:Mode` | `Auto` | `Auto` \| `File` |
| `Certificate:Path` | `certs/dev.pfx` | Relativa ⇒ bajo el ejecutable |
| `Certificate:Password` | *(vacío)* | Contraseña del PFX |
| `Certificate:AutoGenerateSelfSigned` | `true` | Generar uno autofirmado si no hay certificado |
| `Certificate:UseDevelopmentCertificate` | `true` | Usar `dotnet dev-certs https` si existe |
| `Storage:Directory` | `data/messages` | Raíz de los JSON |
| `Storage:MaxInlineAttachmentBytes` | `4194304` | Por encima ⇒ adjunto `omitted: true` (`0` = sin tope) |
| `Storage:KeepRawMime` | `true` | Conservar el MIME original para descargarlo |
| `Storage:MaxRawMimeBytes` | `10485760` | Tope del MIME crudo (`0` = sin tope) |
| `Storage:WriteIndented` | `true` | JSON con sangría |
| `Web:Enabled` | `true` | Si es `false` no hay UI, pero el SMTP sigue |
| `Web:Port` | `8080` | Puerto HTTP (`0` = efímero) |
| `Web:BindAddress` | `127.0.0.1` | Fuera de loopback ⇒ aviso en el log (no hay auth) |
| `Web:Title` | `smtp-mockup` | Título del navegador y de la barra superior |
| `Web:DefaultPageSize` | `50` | Filas por página |
| `Web:MaxPageSize` | `200` | Tope; superarlo muestra aviso en la UI |
| `Web:MaxHtmlPreviewBytes` | `2097152` | Por encima el HTML se muestra como texto |
| `Web:LiveUpdateDebounceMilliseconds` | `250` | Agrupación de notificaciones de la UI |
| `Web:WatchDirectory` | `true` | `FileSystemWatcher` para cambios externos |
| `Web:ShowRawMimeDownload` | `true` | Botón de descarga `.eml` |
| `Hosting:Mode` | `Auto` | `Auto` \| `Console` \| `WindowsService` |
| `Hosting:LogDirectory` | `logs` | Carpeta del log en archivo (modo servicio) |
| `Hosting:LogFileName` | `smtp-mockup-{Date}.log` | `{Date}` = `yyyy-MM-dd` |
| `Hosting:LogRetentionDays` | `7` | Poda de logs; `0` desactiva |
| `Hosting:EventLogSource` | `smtp-mockup` | Origen en el Event Log de Application |
| `Hosting:EventLogLevel` | `Information` | Nivel mínimo enviado al Event Log |
### Validación *fail-fast*

La configuración inválida **mata el proceso al arrancar**, con código de salida 1 y un mensaje que
nombra la clave, sin stack trace:

```console
$ ./smtp-mockup --Certificate:Mode=File --Certificate:Path=certos/inexistente.pfx
'Certificate:Mode' is 'File' but the certificate file does not exist (resolved path: '/tmp/x/certos/inexistente.pfx').
$ echo $?
1
```

Se validan, entre otros: puertos fuera de rango, los dos listeners con el mismo puerto, `Security` o
`Mode` con un valor desconocido, los dos listeners deshabilitados, un `Web:BindAddress` inválido,
puertos que chocan con los de SMTP y el modo servicio sin `Hosting:LogDirectory`.

## Mandar correo al mockup

Todos los ejemplos de esta sección están **verificados contra el binario publicado** de este
repositorio, no copiados de la documentación de ninguna librería.

### PowerShell

```powershell
$msg = [System.Net.Mail.MailMessage]::new()
$msg.From       = 'dev@example.com'
$msg.To.Add('destino@example.com')
$msg.Subject    = 'Prueba PowerShell'
$msg.Body       = 'Hola desde PowerShell'
$msg.IsBodyHtml = $false

# Sin credenciales: el mockup no las pide.
$smtp = [System.Net.Mail.SmtpClient]::new('127.0.0.1', 8025)
$smtp.EnableSsl = $false
$smtp.Send($msg)
$smtp.Dispose(); $msg.Dispose()
```

`Send-MailMessage` también funciona, pero está obsoleto en PowerShell 7; lo portable es la API de
`System.Net.Mail` de arriba.

Para STARTTLS contra el puerto 8443 hay que validar el certificado a mano:

```powershell
$smtp = [System.Net.Mail.SmtpClient]::new('127.0.0.1', 8443)
$smtp.EnableSsl = $true                               # en SmtpClient esto significa STARTTLS
$smtp.ServerCertificateValidationCallback = { $true } # sólo para desarrollo
$smtp.Send($msg)
```

### .NET (MailKit / MimeKit)

```csharp
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

var message = new MimeMessage();
message.From.Add(new MailboxAddress("Dev", "dev@example.com"));
message.To.Add(new MailboxAddress(null, "destino@example.com"));
message.Subject = "Prueba .NET";

var builder = new BodyBuilder
{
    TextBody = "Hola desde .NET",
    HtmlBody = "<p>Hola <b>.NET</b></p>",
};
builder.Attachments.Add("nota.txt", new MemoryStream("contenido"u8.ToArray()));
message.Body = builder.ToMessageBody();

using var client = new SmtpClient
{
    ServerCertificateValidationCallback = (_, _, _, _) => true, // sólo desarrollo
};

await client.ConnectAsync("127.0.0.1", 8025, SecureSocketOptions.None);
await client.SendAsync(message);
await client.DisconnectAsync(true);
```

Dos detalles de MimeKit 4.x que hacen fallar el ejemplo si se copia de documentación más vieja: no
existe `MimeAttachment` (los adjuntos se agregan al `BodyBuilder`) y `Body.AddAttachment` ya no
está (`BodyBuilder.Attachments.Add(...)` devuelve el `MimeEntity`).

Sobre el puerto 8443, `SecureSocketOptions.StartTls` (o `Auto` con `EnableSsl` activo) fuerza la
subida de nivel. Sin el callback de validación, .NET rechaza el certificado autofirmado antes de
enviar nada: es el comportamiento correcto del cliente, no un fallo del mockup.

### Node.js (nodemailer)

```bash
npm install nodemailer
```

```js
const nodemailer = require('nodemailer');

// Puerto 8025, en claro. ignoreTLS evita que nodemailer intente STARTTLS por su cuenta.
const plain = nodemailer.createTransport({ host: '127.0.0.1', port: 8025, ignoreTLS: true });

// Puerto 8443 con STARTTLS obligatorio y certificado autofirmado aceptado.
const secure = nodemailer.createTransport({
  host: '127.0.0.1',
  port: 8443,
  requireTLS: true,
  tls: { rejectUnauthorized: false },
});

await plain.sendMail({
  from: 'node@example.com',
  to: 'destino@example.com',
  subject: 'Prueba Node',
  text: 'Hola desde Node',
  html: '<p>Hola <b>Node</b></p>',
});

await secure.sendMail({
  from: 'node@example.com',
  to: 'destino@example.com',
  subject: 'Node STARTTLS',
  text: 'Cifrado',
});
```

Nodemailer no tiene servidor propio: **siempre** hay que pasar `host` y `port`. No usar
`service: 'gmail'` & compañía, que apunta a la nube.

### Python (smtplib, sin dependencias)

```python
import smtplib, ssl

s = smtplib.SMTP('127.0.0.1', 8025)
s.sendmail('dev@example.com', ['destino@example.com'],
           'Subject: Prueba Python\r\n\r\nHola desde Python')
s.quit()

ctx = ssl._create_unverified_context()          # certificado autofirmado
s = smtplib.SMTP('127.0.0.1', 8443)
assert s.has_extn('starttls')
s.starttls(context=ctx)
s.sendmail('dev@example.com', ['destino@example.com'],
           'Subject: STARTTLS\r\n\r\nHola cifrado')
s.quit()
```

## Abrir la UI

Con el proceso corriendo, en <http://127.0.0.1:8080/>:

| Ruta | Contenido |
|---|---|
| `/` | Listado paginado con búsqueda y filtros; borrado individual, de seleccionados y de todos |
| `/messages/{id}` | Detalle: Resumen, Texto, HTML, Headers, JSON, Adjuntos |
| `/messages/{id}?tab=HTML` | Abre el detalle en una pestaña concreta |
| `/stats` | Conteo por día y por dominio destinatario |
| `/settings` | Configuración efectiva, puertos y estado del storage (sólo lectura) |
| `/about` | Versión y endpoints activos |

- Los mensajes que llegan por SMTP **aparecen solos** en el listado, sin recargar (evento del store
  con 250 ms de debounce). Los cambios hechos por fuera del proceso (borrar un `.json` a mano)
  también se detectan con `FileSystemWatcher`.
- El HTML del correo se muestra en un `<iframe sandbox="">`: no puede ejecutar nada.
- Adjuntos y MIME original se descargan por HTTP (`/download/message/{id}/attachment/{n}` y `/raw`).
- Con `Web:BindAddress` fuera de loopback aparece un aviso en el log: **la UI no tiene
  autenticación**, no la expongas a una red en la que no se confíe.

Detalle completo en [`docs/web-ui.md`](docs/web-ui.md).

## Dónde quedan los datos

```
data/messages/2026/10/04/01M44BR3JZTFPJ22M60ZBSR52B.json
certs/dev.pfx          ← certificado autofirmado, se crea en el primer arranque
logs/                  ← sólo en modo servicio
```

El nombre del archivo es un ULID (ordenable por tiempo) dentro de una carpeta por día. Cada JSON trae
el sobre (`envelope` con IP y puerto remoto, `MAIL FROM`, `RCPT TO`, transporte y TLS), las
direcciones (`from`/`to`/`cc`/`bcc`, con el **Bcc recuperado del sobre**), los cuerpos de texto y
HTML, los adjuntos con su SHA-256 y el MIME original en base64. El esquema completo está en
[`memory-bank/apiReference.md`](memory-bank/apiReference.md).

Los archivos se pueden leer, editar o borrar a mano: la UI se entera sola.

## Publicación

Lo normal es usar el script, que compila, **pasa los tests** y publica en el orden correcto:

```bash
python3 scripts/publish.py                    # compila, testea y publica (RID del host + win-x64)
python3 scripts/publish.py --rid linux-x64    # sólo un RID
python3 scripts/publish.py --skip-tests       # sin tests, para iterar rápido
python3 scripts/publish.py --zip              # además, un .zip listo para repartir
python3 scripts/publish.py --zip-only         # reempaqueta sin volver a compilar ni testear
python3 scripts/publish.py --clean-data       # borra data/, certs/ y logs/ del RID
python3 scripts/publish.py --help             # todas las opciones
```

No se publica nada si la compilación falla o si hay un test en rojo: es preferible tardar un minuto
más a repartir un binario roto. Al terminar **verifica el artefacto** (que exista el ejecutable y que
`wwwroot/_framework/blazor.web.js` esté dentro) y comprueba que `publish/` siga ignorado por git.

Un detalle que el script respeta por ti: al refrescar un RID **no borra `data/`, `certs/` ni
`logs/`**, porque ahí están los correos que le has mandado y el PFX autofirmado. Se borran sólo con
`--clean-data`.

Si prefieres hacerlo a mano (o no tienes Python):

```bash
# Linux / macOS
dotnet publish src/SmtpMockup.Host -c Release -r linux-x64 --self-contained \
  -p:PublishSingleFile=true -p:PublishTrimmed=false -o publish/linux-x64

# Windows (el perfil del repositorio ya trae los flags)
dotnet publish src/SmtpMockup.Host -p:PublishProfile=win-x64
```

**Hay que distribuir la carpeta completa, no sólo el `.exe`.** El ejecutable es un único fichero
(~57 MB) porque los static web assets de Blazor (`wwwroot/_framework/blazor.web.js`, el CSS de
MudBlazor) **son archivos**, no se pueden incrustar en el binario. Copiar sólo el `.exe` deja una UI
que se ve pero no responde: es la falla más confundida de este proyecto.

Por eso `--zip` empaqueta el ejecutable **y** su `wwwroot` en `publish/smtp-mockup-<rid>.zip`, y
después vuelve a abrir el zip para comprobar que lleva lo que la UI necesita. El zip **no** incluye
`data/`, `certs/` ni `logs/`: son tus correos y el PFX (una clave privada), y no van repartidos.

Sin el script, a mano:

```bash
cd publish/win-x64 && zip -r smtp-mockup-win-x64.zip smtp-mockup.exe wwwroot appsettings.json
```

> Al descomprimir en Linux o macOS el binario puede salir sin permiso de ejecución (según cómo
> extraiga tu herramienta): `chmod +x smtp-mockup`.

Notas:

- **Sin trimming**: MailKit/MimeKit y el binder de `Options` usan reflexión, y un publish recortado
  rompe en runtime, no al compilar.
- No hace falta `PublishSingleFile` si preferís DLLs sueltas, pero **`wwwroot` sigue siendo
  obligatorio**.
- Cero dependencias nativas: el mismo publish vale para Windows, Linux y macOS usando su RID.
- `publish/` está en `.gitignore` y no se versiona: son ~57 MB de binario por RID.

## Instalarlo como servicio de Windows

Requiere PowerShell **como Administrador**.

```powershell
.\scripts\install-service.ps1 -Path C:\tools\smtp-mockup\smtp-mockup.exe
Start-Service -Name smtp-mockup

# Con una cuenta concreta y arranque diferido
.\scripts\install-service.ps1 -Name smtp-mockup-dev -Account '.\devuser' -Password 'secret' -DelayedStart

# Desinstalar (no borra data\, logs\ ni certs\, salvo que lo pidas)
.\scripts\uninstall-service.ps1 -Name smtp-mockup
.\scripts\uninstall-service.ps1 -Name smtp-mockup -DeleteData
```

El mismo binario detecta solo si lo arrancó el SCM: no hay ningún flag que mantener sincronizado con
el registro, y ejecutarlo a mano desde una terminal sigue funcionando en modo consola.

Cuentas disponibles: `LocalSystem` (default), `NetworkService`, `LocalService` o `DOMINIO\usuario`
(que requiere `-Password`).

El servicio escribe log en archivo **y** en el Event Log de Application. `sc create` no fija el
directorio de trabajo (el SCM arranca con `C:\Windows\System32`), por eso la app resuelve
`appsettings.json`, `data/` y `certs/` contra el directorio del ejecutable.

Detalles, permisos por cuenta y operación: [`docs/hosting-modes.md`](docs/hosting-modes.md).

## Certificados

En modo `Auto`, el mockup usa `dotnet dev-certs https` si existe y, si no, genera uno autofirmado
para `CN=localhost` y lo guarda en `certs/dev.pfx` junto al ejecutable (permisos 600 en Unix). El
segundo arranque reutiliza el mismo (mismo thumbprint).

Para un certificado propio:

```json
"Certificate": { "Mode": "File", "Path": "certs/mi.pfx", "Password": "…" }
```

Dos sorpresas que ya están documentadas:

- Si hay un `dotnet dev-certs https` instalado, **no** aparece un `certs/dev.pfx` nuevo: se usa ese.
  Poné `Certificate:UseDevelopmentCertificate=false` si lo que querés es el PFX propio.
- Un servicio corriendo como `LocalSystem` **no ve** el `dotnet dev-certs` del usuario interactivo y
  presenta el certificado generado: distinto thumbprint entre consola y servicio.

Guía completa: [`docs/certificate-trust.md`](docs/certificate-trust.md).

## Checklist de aceptación (contra `SPEC.md` §11)

Estado al cierre: **21 de 22 criterios verificados**, uno pendiente por falta de una máquina Windows
(#18). «Verificado» significa comprobado contra el **binario publicado** o con un test automatizado;
la última columna dice cuál de las dos cosas lo respalda.

### §11.1 SMTP y persistencia

| # | Criterio | Estado | Evidencia |
|---|---|---|---|
| 1 | Envío a 8025 sin credenciales ⇒ `Ok` y 1 JSON | ✅ | test + manual (los 3 clientes) |
| 2 | 8443 anuncia STARTTLS; sin subirlo sigue aceptando; subiendo da `tlsNegotiated: true` | ✅ | tests + `smtplib`/nodemailer |
| 3 | `Smtp:Plain:Enabled=false` ⇒ 8025 cerrado, 8443 operativo | ✅ | tests (`SmtpListenerSelectionTests`) + manual (conexión rechazada) |
| 4 | HTML + 2 adjuntos ⇒ cuerpos poblados y `sizeBytes` = base64 decodificado | ✅ | tests + MailKit |
| 5 | Mensaje de 30 MB ⇒ `552` y ningún archivo | ✅ | test + `SMTPSenderRefused (552, 'size limit exceeded')` |
| 6 | Primer arranque sin certificado ⇒ genera y persiste el PFX; el segundo lo reutiliza | ✅ | test + manual (thumbprint idéntico) |
| 7 | `Certificate:Mode=File` con ruta inexistente ⇒ salida 1 con mensaje | ✅ | manual |
| 8 | Sin autogeneración y sin certificado ⇒ mensaje nombrando las claves | ✅ | tests de validación |
| 9 | `UseDevelopmentCertificate=false` ⇒ presenta el PFX propio | ✅ | tests |
| 10 | MailKit aceptando el autofirmado envía por 8443 sin credenciales | ✅ | tests de integración |

### §11.2 UI Blazor

| # | Criterio | Estado | Evidencia |
|---|---|---|---|
| 8 | `/` renderiza el listado con el circuito conectado, sin 404 de `_framework` | ✅ | bUnit + `curl` a `/` y a `_framework/blazor.web.js` |
| 9 | Un correo enviado aparece solo en < 2 s sin recargar | ✅ | tests del notificador y del listado |
| 10 | Borrado múltiple con confirmación quita filas **y** archivos | ✅ | bUnit (`ConfirmDialogTests`, `MessageListPageTests`) |
| 11 | Detalle completo y descarga con los bytes y el nombre originales | ✅ | tests + `curl` a `/download/message/{id}/attachment/0` |
| 12 | Búsqueda por texto libre y por rango de fechas | ✅ | tests de `MessageListService` / `MessageFilterModel` |
| 13 | `Web:Enabled=false` ⇒ la UI no responde y el SMTP sigue aceptando | ✅ | manual (conexión rechazada + envío aceptado) |
| 14 | `Web:BindAddress=0.0.0.0` ⇒ warning en el log | ✅ | manual |

### §11.3 Publicación

| # | Criterio | Estado | Evidencia |
|---|---|---|---|
| 15 | El publish produce `.exe` **y** `wwwroot` con `_framework`, y la UI carga sin 404 | ✅ | verificado en linux-x64 y win-x64 |
| 16 | Un publish desde directorio limpio reproduce el mismo resultado | ✅ | verificado |
| 17 | Con el ejecutable movido junto a su `wwwroot` la UI sigue funcionando | ✅ | verificado (copiado a `/tmp` y levantado desde ahí) |
| 18 | Con el servicio Windows instalado, la UI responde con los estáticos | ⚠️ **pendiente** | requiere un host Windows |
| 19 | Plan B documentado: `exe + wwwroot` en la misma carpeta, copia completa | ✅ | este README + el `.pubxml` del repo |

## Cobertura de tests

**239 tests, todos verdes** (`dotnet test`, verificado en esta sesión):

| Proyecto | Tests | Qué cubre |
|---|---|---|
| `SmtpMockup.Core.Tests` | 101 | `MessageId` (ULID), `MessagePath`, `HostingModeResolver`, binding y validación de opciones, conflictos de puerto, paginación con valores hostiles |
| `SmtpMockup.Storage.Tests` | 7 | Resolución de rutas (con el CWD movido) y reconstrucción del índice |
| `SmtpMockup.Smtp.Tests` | 39 | Integración con cliente MailKit real: texto plano, HTML + adjuntos, varios destinatarios, `552`, STARTTLS, `Implicit`, persistencia del PFX, `bodyBytes` en UTF-8, selección de listeners |
| `SmtpMockup.Web.Tests` | 92 | Componentes con bUnit, notificador con debounce, `FileSystemWatcher`, saneado de nombres, decodificación de asunto, descargas, paginación |

Además, 14 casos de `scripts/ServiceImagePath.Tests.ps1` para el parsing de los scripts de servicio,
sin Windows y sin Administrador.

```bash
dotnet test                                              # 239 tests
dotnet build -warnaserror                               # el build trata warnings como errores
pwsh -File scripts/ServiceImagePath.Tests.ps1           # 14 casos de los scripts de servicio
```

### Huecos de cobertura

1. **`FileSystemMessageStore` está infra-cubierto.** 7 tests, y sólo de índice y rutas: no hay test
   de round-trip, de atomicidad (que no queden `.tmp`), de purga, de borrado masivo con filtro, de
   búsqueda por texto ni por rango de fechas. Todo eso se cubre de rebote a través de los tests de
   SMTP, que sólo recorren el camino feliz: si el borrado masivo o la purga dejaran un `.tmp` o
   dejaran el índice corrupto, los tests actuales seguirían en verde.
2. **El composition root sigue sin test de arranque.** Levantar el `Program.cs` real en una prueba
   (un `WebApplicationFactory` que compruebe qué puertos quedan abiertos) es lo que faltaba para que
   un bug como el de `Smtp:Plain:Enabled=false` pasara inadvertido. Mitigado, no resuelto: la
   decisión que se rompió se movió a `SmtpListenerSelection.GetEnabled`, que sí tiene tests, pero
   cualquier otro `if` nuevo en `Program.cs` volverá a estar sin cubrir.
3. **No hay E2E de la UI con navegador.** Los criterios 9 y 10 están cubiertos a nivel de componente
   (bUnit), no de punta a punta. Un Playwright sobre el binario publicado cerraría el círculo.
4. **No hay CI.** Nada corre los tests automáticamente: `restore → build -warnaserror → test` en
   Linux/Windows/macOS, más un `publish` que verifique `wwwroot/_framework`.
5. **`envelope.helo` es siempre `null`** (la librería no expone el dominio EHLO/HELO). El campo
   existe y es nullable, y nada depende de él todavía.

## Deuda técnica

Ordenada por lo que cuesta más si se deja para mañana. Los tres primeros son los que importan.

> **Lo que ya se arregló en la revisión de código** (y por qué no aparece aquí): `Smtp:Plain:Enabled=false`
> no desactivaba el listener en claro (criterio 3 de §11.1); `size.bodyBytes` contaba caracteres en vez
> de bytes UTF-8, así que un correo con acentos o emojis inflaba el tamaño del cuerpo; y la paginación
> desbordaba a negativo con una página enorme Llegada en la URL, lanzando en vez de devolver una lista
> vacía. Los tres tienen su test y están verificados contra el binario publicado.

### 1. `FileSystemMessageStore` sin tests directos 🟡

El store es la pieza con más estados (escritura atómica, índice, purga de temporales, borrado masivo,
búsqueda) y la única sin un test que la ejercite a solas. Es el primer trabajo que merece la pena.

### 2. Sin test de arranque del Host 🟡

Ningún test levanta el `Program.cs` real, así que la parte del composition root que decide qué se
abre (servicios, listeners, URLs) sigue dependiendo de revisión manual. Un `WebApplicationFactory` con
puertos efímeros que compruebe qué queda escuchando cerraría el hueco de raíz, y es lo que faltó
para que el bug de `Smtp:Plain:Enabled` pasara inadvertido.

### 3. Sin CI 🟡

No hay workflow: nada impide mergear con la suite en rojo. El plan ya está escrito en
`memory-bank/techContext.md`: `restore → build -warnaserror → test` en las tres plataformas, más un
job de `publish` que falle si el artifact no trae `wwwroot/_framework`.

### 4. `envelope.helo` siempre `null` 🟢

La librería SMTP no expone el dominio de EHLO/HELO. El campo queda en el esquema y es nullable; si
alguna vez hace falta, hay que subirlo por el cable (`Server.OnHeloReceived`) o sacarlo del primer
header `Received`.

### 5. Nombre del valor de transporte 🟢

El JSON escribe `"transport": "startTls"` mientras que SPEC §11.1 habla de `"StartTls"`. No rompe
nada (nadie lo compara), pero es una discrepancia entre el documento y el dato persistido que va a
confundir a quien lo lea.

### 6. Publicación de Linux sin perfil 🟢

Sólo hay `.pubxml` para `win-x64`; el publish de Linux hay que hacerlo con los flags a mano (el
comando está documentado más arriba). Con un `linux-x64.pubxml` los dos serían simétricos.

### 7. ~~Faltan `scripts/publish.ps1` y `publish.sh`~~ ✅ resuelto

SPEC §11.3 (criterio 19) los menciona. Ahora hay **un** script, `scripts/publish.py`, que hace
compile + test + publish + **empaquetado en zip** (`--zip`), y es el mismo en las tres plataformas.

No se hicieron dos scripts (`publish.ps1` + `publish.sh`) a propósito: dos copias de la misma
lógica se divergen el día que hay que tocar una, y en PowerShell además habría que arrastrar el
módulo de pruebas de parsing que ya existe para los scripts de servicio. Un script en Python cubre
Windows, Linux y macOS sin duplicar nada. La SPEC se actualizó en vez de dejar el nombre literal.

Cerrado además el punto que quedaba abierto de verdad: el zip se **verifica** después de
escribirse (que lleve el ejecutable, `wwwroot/_framework/blazor.web.js` y `appsettings.json`, y que
**no** se lleve `data/`, `certs/` ni `logs/`). Verificado descomprimiendo el zip en `/tmp` y
arrancando desde ahí: UI en 200, estáticos en 200 y correo aceptado.

### 8. ~~Todo el trabajo está sin commitear~~ ✅ resuelto

Estaba todo sin trackear sobre un único commit de documentación. El proyecto está en GitHub con la
implementación, la documentación y los scripts versionados.

## Mejoras futuras

**Opcional, y sólo si hace falta: API de sólo lectura para pruebas automáticas.** Hoy la UI lee
`IMessageStore` directamente (D-06) y no hay contrato HTTP. Eso es lo correcto para el uso real del
mockup, pero significa que **cualquier prueba automatizada de extremo a extremo tiene que
arreglarse con la UI**: o bUnit sobre componentes, o un navegador (Playwright), o leer los JSON del
disco.

Si alguna vez aparece una suite E2E que justifique ese coste, lo más barato es un endpoint mínimo y
**de sólo lectura** —`GET /api/messages?…`, `GET /api/messages/{id}`,
`GET /api/messages/stats`, `GET /health`— que devuelva el mismo modelo de dominio ya serializado (lo
que hay en `StoredJson` e `IMessageStore`), sin DTOs de transporte nuevos y sin tocar la UI. Queda
claramente fuera del SPEC actual, que cerró la pregunta de la API HTTP (SPEC §12): sería una
decisión nueva, con su propio criterio de aceptación, no una tarea de cierre. **Hoy no hace falta**:
`data/messages/*.json` ya es un contrato estable y observable desde fuera.

El resto de la lista, por valor:

1. **Tests directos de `FileSystemMessageStore`** (la deuda #1) y un test de arranque del Host con
   puertos efímeros (la #2). Es lo único que bloquea de verdad.
2. **CI**: un `.github/workflows/ci.yml` con `restore → build -warnaserror → test` en
   Linux/Windows/macOS, más el job de `publish` que verifique `wwwroot/_framework`.
3. **Retención automática (TTL).** `Hosting:LogRetentionDays` ya poda los logs; los mensajes se
   acumulan sin límite. Sería `Storage:RetentionDays` con una poda periódica.
4. **Borrado con papelera.** Hoy el borrado es duro (se borra el archivo). Un `Storage:SoftDelete`
   evitaría el «lo borré sin querer» con poco trabajo.
5. **Búsqueda por texto completo.** El `search` actual mira asunto, remitente y destinatario con
   `Contains`; un índice invertido sobre las cabeceras daría coincidencias parciales de verdad.
6. **Adjuntos grandes fuera del JSON.** Hoy un adjunto de más de 4 MB queda `omitted: true` y no se
   puede descargar. Guardarlo como archivo suelto junto al JSON (el hash como nombre) elimina ese
   techo sin cambiar el esquema.
7. **Límite de conexiones simultáneas.** El RNF-02 lo pide, pero `SmtpServer` 11.1.0 no expone
   ninguna opción de concurrencia (`MaxRetryCount`, `MaxAuthenticationAttempts` y `MaxMessageSize`
   es lo más cercano), así que habría que hacerlo en casa sobre `IServer`'s o cambiar de librería.
   La clave `MaxConcurrentConnections` se retiró de la SPEC y de `appsettings.json` en lugar de
   dejar una opción que no hace nada.
8. **Tope de circuitos de Blazor.** Con muchas pestañas abiertas los circuitos se acumulan; conviene
   un `CircuitOptions` con un límite por cliente.

## Documentos del repositorio

| Documento | Para qué |
|---|---|
| [`SPEC.md`](SPEC.md) | Fuente de verdad del alcance (v2.0: UI Blazor, sin API HTTP) |
| [`DESIGN.md`](DESIGN.md) | Decisiones técnicas (D-01…D-17), flujo de datos, empaquetado, estrategia de TDD |
| [`docs/web-ui.md`](docs/web-ui.md) | Cómo funciona la UI, actualización en vivo, constraints de publicación |
| [`docs/hosting-modes.md`](docs/hosting-modes.md) | Consola vs servicio, publicación, permisos, operación |
| [`docs/certificate-trust.md`](docs/certificate-trust.md) | Cómo confiar el certificado en Windows y en Linux |
| `memory-bank/` | Contexto del proyecto para el trabajo futuro |

## Licencia

MIT (las dependencias también: `SmtpServer` es MIT, MudBlazor es MIT).
