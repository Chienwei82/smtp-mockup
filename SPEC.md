# smtp-mockup — SPEC

Servidor SMTP falso para desarrollo con UI web incluida: acepta cualquier correo, sin
autenticación y sin relay, lo persiste como JSON y lo muestra en una interfaz Blazor para leerlo y
borrarlo.

- **Estado del documento:** v2.0 (post cambio de rumbo: se elimina la API HTTP)
- **Stack:** .NET 10 (`net10.0`), C# 14, Blazor Web App (Interactive Server), MailKit + MimeKit
- **Documento contraparte:** `DESIGN.md` (decisiones técnicas, empaquetado y plan)

> **v2.0 es incompatible con v1.0 en la capa de exposición:** no hay endpoints HTTP, no hay OpenAPI,
> no hay CORS, no hay proyecto `SmtpMockup.Api`. Ver §12 (Registro de cambios).

---

## 1. Objetivo y alcance

### 1.1 Objetivo
Proveer un ejecutable local de un solo proceso que:
1. Escucha SMTP en dos puertos configurables e independientes (8025 plano, 8443 STARTTLS).
2. Acepta y persiste **todo** correo recibido, sin autenticación, sin validación de destinatario
   y sin entrega externa.
3. Expone una **UI web Blazor** en el mismo proceso (Interactive Server) para listar, buscar, ver,
   descargar adjuntos y borrar los correos almacenados, leyendo directamente del `IMessageStore`.
4. Actualiza la UI en vivo cuando llega o se borra un correo, sin recargar ni consultar por HTTP.
5. Corre como `exe` standalone (self-contained) o como Windows Service en Windows, y como aplicación
   de consola en Linux/macOS.

### 1.2 Fuera de alcance (v1)
- Relay o entrega real a servidores externos.
- Autenticación SMTP (AUTH), DKIM/SPF, validación de envelopes, anti-spam, cuotas por usuario.
- **API HTTP pública para los correos** (JSON-RPC, OpenAPI, webhook): la UI habla directo con
  `IMessageStore`. Solo existe el endpoint técnico de salud del propio Blazor (§9.7).
- Frontend SPA separado (React/Angular/Vue) o pipeline de assets con Node.
- Clustering, réplicas, base de datos, búsqueda full-text.
- Cifrado implícito (SMTPS / puerto 465) y envío de correos desde el mockup (solo recepción).
- Cuentas de usuario, multi-tenancy, autenticación de la UI.

---

## 2. Requisitos funcionales

| ID | Requisito | Criterio de aceptación |
|----|-----------|------------------------|
| RF-01 | Aceptar cualquier correo sin autenticación | `MAIL FROM`/`RCPT TO` sin `AUTH` ⇒ `250` y entrega final `250 OK` con `id` asignado |
| RF-02 | Sin relay real | Ninguna conexión SMTP saliente; el mensaje solo se persiste |
| RF-03 | Puerto 8025 (plano) | Escucha configurable (default `8025`); con `Security=StartTls` anuncia `250 STARTTLS`, responde `454` si la actualización falla |
| RF-04 | Puerto 8443 (STARTTLS) | Carga el certificado según `Certificate:Mode` (`Auto` o `File`), **anuncia `250 STARTTLS`** y responde `220` tras el handshake; con `Security=Implicit` acepta el socket ya cifrado (SMTPS) |
| RF-05 | Habilitación independiente | `Enabled=false` en un puerto ⇒ no se crea listener; el otro sigue operando |
| RF-06 | Persistencia JSON | Un archivo por correo en `Storage:Directory`, escritura atómica, sin mezclar mensajes |
| RF-07 | Configurable por `appsettings.json` | Directorio, puertos, límites, UI y verbosidad sin recompilar |
| RF-08 | UI web de lectura | Listado con búsqueda/filtros y paginación; detalle con cuerpos, headers y metadatos de adjuntos |
| RF-09 | UI web de borrado | Borrado individual, borrado masivo con filtros, con confirmación explícita |
| RF-10 | Un solo ejecutable | `dotnet publish` self-contained y single-file (`dotnet publish src/SmtpMockup.Host -p:PublishProfile=win-x64`); en Windows también como Windows Service, autodetectado. Operación documentada en `docs/hosting-modes.md` |
| RF-11 | multipart/MIME completo | `text/plain` y `text/html` por separado; adjuntos en base64 con metadatos |
| RF-12 | Diagnóstico | Respuestas RFC 5321, log estructurado y panel de estado en la propia UI |
| RF-13 | **Actualización en vivo** | Un correo que llega o se borra aparece/desaparece en la UI abierta sin recargar, en < 2 s |
| RF-14 | Descarga de adjuntos y MIME raw | La UI permite descargar cada adjunto y el `.eml` original desde el `IMessageStore` |
| RF-15 | Certificado en modo Auto | Primer arranque: usa el certificado de desarrollo de `dotnet dev-certs`; si no existe, genera uno autofirmado y lo **persiste** en `Certificate:Path`, resuelto contra el directorio del ejecutable |
| RF-16 | Generación de certificado controlable | Con `Certificate:AutoGenerateSelfSigned=false` y sin certificado disponible, el proceso **falla al arrancar** con un mensaje que nombra `Certificate:Path` y `Certificate:AutoGenerateSelfSigned` |
| RF-17 | Confianza del certificado documentada | `docs/certificate-trust.md` explica cómo confiar el PFX generado en Windows (PowerShell, `certmgr.msc`, `certlm.msc`) y cómo verificar el handshake |

---

## 3. Requisitos no funcionales

- **RNF-01 Latencia SMTP:** un `DATA` de 1 MB persiste en < 500 ms en disco local.
- **RNF-02 Concurrencia SMTP:** conexiones simultáneas sin pérdida ni interleaving de mensajes;
  escrituras aisladas por mensaje. El límite por conexión lo pone la librería
  (`SmtpServer`), no la configuración: `MaxConcurrentConnections` se retiró de la SPEC en v2.1
  por no tener equivalente en la API.
- **RNF-03 Memoria:** streaming del cuerpo hasta el límite configurado, sin duplicar el mensaje
  completo más de una vez.
- **RNF-04 Aislamiento:** un parseo fallido no tumba el listener; el cliente recibe `550` y el
  servidor sigue aceptando conexiones.
- **RNF-05 Portabilidad:** mismo comportamiento en Windows, Linux y macOS, sin dependencias nativas.
- **RNF-06 Clean Code / TDD:** lógica de negocio en `Core` con tests unitarios; parsing, storage y
  componentes Blazor cubiertos con tests (xUnit + `bunit` + `Microsoft.AspNetCore.Mvc.Testing`).
- **RNF-07 Sin secretos en disco:** el certificado Auto se genera con clave aleatoria fuerte y el
  PFX se marca como solo desarrollo.
- **RNF-08 Latencia de UI:** el circuito SignalR de Blazor no debe bloquear la persistencia; la
  notificación se emite *después* del rename atómico, nunca antes.
- **RNF-09 Publicación sin assets rotos:** el publish debe entregar los assets estáticos de Blazor
  (`wwwroot`, `_framework`) junto al ejecutable o embebidos, en Windows/Linux/macOS (§11.3).
- **RNF-10 Segundos de arranque:** la UI responde el primer render en < 3 s tras `250` de arranque.

---

## 4. Supuestos y valores por defecto

| Supuesto | Valor por defecto | Justificación |
|----------|-------------------|---------------|
| UI web escucha en | `http://127.0.0.1:8080` | Loopback: los correos son sensibles y la UI no tiene auth |
| Puerto SMTP plano | `8025` | Convención de herramientas similares (MailHog/Mailpit) |
| Puerto SMTP STARTTLS | `8443` | Equivalente TLS de 8025 |
| Dirección de binding SMTP | `127.0.0.1` (`Smtp:Plain:BindAddress`, `Smtp:StartTls:BindAddress`) | Igual que la UI: el mockup acepta cualquier correo sin autenticación, así que exponerlo a la red local es una decisión explícita, no un default. Fuera de loopback ⇒ **warning** en el log, no error |
| Render mode de Blazor | `InteractiveServer` | Un solo proceso, sin cliente HTTP ni API intermedia |
| Autenticación SMTP | ninguna | Requisito; no se implementa en v1 |
| STARTTLS en 8025 | anunciado, opcional | El cliente puede intentarlo; `454` si no se puede actualizar |
| Cifrado implícito | no soportado | Requisito explícito |
| Modo de certificado | `Auto` | Cero configuración para arrancar; se persiste tras el primer uso |
| Banner SMTP | `220 smtp-mockup ESMTP` | RFC 5321 |
| Directorio de datos | `./data/messages` | Relativo al ContentRoot |
| Fechas | UTC ISO-8601 | Determinismo entre plataformas |
| Retención | sin expiración automática | v1; borrado manual desde la UI |
| Id de mensaje | ULID (26 chars, ordenable) | Orden cronológico por nombre de archivo sin coordinación |
| Log level | `Information` | `Debug` para trazas de comandos SMTP |

---

## 5. Estructura de la solución

```
smtp-mockup/
├── smtp-mockup.slnx
├── Directory.Build.props
├── global.json
├── .editorconfig
├── README.md
├── SPEC.md
├── DESIGN.md
├── src/
│   ├── SmtpMockup.Core/          # Dominio: modelos, contratos (incl. IMessageStore), opciones
│   ├── SmtpMockup.Smtp/          # SmtpServer + MimeKit: adaptador al pipeline, opciones por puerto
│   ├── SmtpMockup.Storage/       # FileSystemMailStore + notificación de cambios
│   ├── SmtpMockup.Web/           # Blazor Web App (Interactive Server): páginas, componentes, wwwroot
│   └── SmtpMockup.Host/          # Executable único: composition root, config, Windows Service
├── tests/
│   ├── SmtpMockup.Core.Tests/
│   ├── SmtpMockup.Storage.Tests/
│   ├── SmtpMockup.Smtp.Tests/
│   └── SmtpMockup.Web.Tests/     # bUnit (componentes) + WebApplicationFactory (hosting)
└── data/
    └── messages/                 # Runtime (gitignored)
```

Responsabilidades:
- **Core** — `ReceivedMessage`, `EnvelopeInfo`, `MailAddressInfo`, `HeaderInfo`, `BodyInfo`,
  `AttachmentInfo`, `SizeInfo`, `RawMimeInfo`, `MessageSummary`, `MailQuery` (filtros/paginación),
  `MessageId` (ULID), `MessagePath`, `Result<T>`/`AppError`, opciones POCO y el contrato
  **`IMessageStore`** con su mecanismo de notificación (§9.6). Sin dependencias de I/O, ASP.NET,
  Blazor ni MailKit.
- **Smtp** — `SmtpListenerService` (un listener por puerto habilitado) y `MockupMessageStore`, el
  adaptador que implementa `MessageStore` de la librería `SmtpServer`: recibe el MIME crudo del
  `DATA` más el sobre, lo convierte al modelo de §7 y lo persiste vía `IMessageStore`. Como
  `SaveAsync` devuelve la `SmtpResponse` del servidor, los códigos 250/451/550/552 se emiten desde el
  propio adaptador. Incluye `CertificateProvider` (modos `Auto`/`File`).
- **Storage** — `FileSystemMailStore` (implementa `IMessageStore`), `MailJsonSerializer`,
  escritura atómica, `FileMailIndex` para listados y filtros, `MailStoreChangeNotifier`.
- **Web** — Blazor Web App: `Components/App.razor`, `Routes.razor`, `Components/Pages/*`,
  `Components/Shared/*`, `wwwroot/`. Consumye `IMessageStore` por inyección directa.
- **Host** — `Program.cs`, registro DI, `appsettings.json`, hosting como Windows Service, scripts de
  publish.

Dependencias (sin ciclos):
`Host → {Web, Smtp, Storage}` · `Web → Core` · `Smtp → Core` · `Storage → Core` · `Core → nada`.
**El proyecto `SmtpMockup.Api` ya no existe.**

---

## 6. Configuración (`appsettings.json`)

```json
{
  "Logging": {
    "LogLevel": { "Default": "Information", "SmtpMockup.Smtp": "Debug", "SmtpMockup.Web": "Information" }
  },
  "Smtp": {
    "MaxMessageSizeMb": 25,
    "Plain": {
      "Enabled": true,
      "Port": 8025,
      "BindAddress": "127.0.0.1",
      "Security": "None"
    },
    "StartTls": {
      "Enabled": true,
      "Port": 8443,
      "BindAddress": "127.0.0.1",
      "Security": "StartTls"
    }
  },
  "Certificate": {
    "Mode": "Auto",
    "Path": "certs/dev.pfx",
    "Password": "",
    "AutoGenerateSelfSigned": true,
    "UseDevelopmentCertificate": true
  },
  "Storage": {
    "Directory": "data/messages",
    "WriteIndented": true,
    "MaxInlineAttachmentBytes": 4194304,
    "MaxRawMimeBytes": 10485760,
    "KeepRawMime": true
  },
  "Web": {
    "Enabled": true,
    "Port": 8080,
    "BindAddress": "127.0.0.1",
    "Title": "smtp-mockup",
    "DefaultPageSize": 50,
    "MaxPageSize": 200,
    "ShowRawMimeDownload": true,
    "MaxHtmlPreviewBytes": 2097152
  }
}
```

### 6.1 Bloque `Web:*` (reemplaza a `Api:*`)

| Clave | Tipo | Default | Regla |
|-------|------|---------|-------|
| `Enabled` | bool | `true` | `false` ⇒ no se mapea Blazor; el proceso sigue sirviendo SMTP |
| `Port` | int | `8080` | 0–65535 (0 = puerto efímero); ocupado ⇒ fail-fast |
| `BindAddress` | string | `127.0.0.1` | literal IPv4/IPv6; fuera de loopback ⇒ warning en log |
| `Title` | string | `smtp-mockup` | título del navegador y de la barra superior; vacío ⇒ `smtp-mockup` |
| `DefaultPageSize` | int | `50` | usada cuando el usuario no elige tamaño de página |
| `MaxPageSize` | int | `200` | excederlo ⇒ error de validación visible en la UI (no clamp silencioso) |
| `ShowRawMimeDownload` | bool | `true` | muestra el botón de descarga `.eml` |
| `MaxHtmlPreviewBytes` | int | `2 MB` | por encima, el HTML se muestra como texto escapado |

### 6.2 Certificado: modo `Auto` vs `File`

| Clave | Tipo | Default | Regla |
|-------|------|---------|-------|
| `Mode` | string | `Auto` | `Auto` o `File`; otro valor ⇒ fail-fast nombrando la clave |
| `Path` | string | `certs/dev.pfx` | Obligatoria en `File`. Las rutas **relativas** se resuelven contra el directorio del ejecutable (`AppContext.BaseDirectory`), no contra el directorio de trabajo |
| `Password` | string | `""` | Vacía = PFX sin contraseña. Sobrescribir por variable de entorno, no en el archivo |
| `AutoGenerateSelfSigned` | bool | `true` | Si no hay certificado disponible, generar uno autofirmado y persistirlo. `false` ⇒ error fatal al arrancar |
| `UseDevelopmentCertificate` | bool | `true` | Consultar el almacén `CurrentUser\My` para el certificado de `dotnet dev-certs https`. `false` ⇒ nunca se mira ese almacén |

| `Mode` | Comportamiento |
|--------|----------------|
| `Auto` (default) | 1) Si `UseDevelopmentCertificate`, busca el certificado de desarrollo de .NET en el almacén personal (identificado por el OID `1.3.6.1.4.1.311.20.2.1` que añade `dotnet dev-certs`). 2) Si no, carga el PFX de `Path` si existe. 3) Si tampoco hay, **genera** un autofirmado (CN=`localhost`, SAN `localhost`/`127.0.0.1`/`::1`, EKU `serverAuth`, sin CA, 365 días, RSA 2048) y lo **persiste** en `Path` (escritura atómica, modo 600 en Unix). 4) Si el PFX existe pero la contraseña falla ⇒ regenera y vuelve a persistir (con warning), no error fatal. 5) Si `AutoGenerateSelfSigned=false` y no hay nada ⇒ **error fatal** que nombra `Certificate:Path` y `Certificate:AutoGenerateSelfSigned`. |
| `File` | Carga estricta de `Path`; si no existe o la contraseña es incorrecta ⇒ **error fatal** con mensaje explícito. El proceso sale con código 1 y el mensaje por stderr, sin stack trace. |

En Windows, un servicio registrado con la cuenta **LocalSystem** no ve el `CurrentUser\My` del
usuario interactivo, así que cae al certificado generado y persistido; ver
`docs/certificate-trust.md` §4.1. El certificado se resuelve **una vez por proceso** (singleton)
antes de abrir el socket, de modo
que todas las sesiones presentan el mismo thumbprint y el cliente puede cachear la excepción de
confianza. El arranque registra `CertificateResolved` con `mode`, `source`
(`DevelopmentCertificate` \| `PersistedFile` \| `GeneratedSelfSigned`), `subject`, `thumbprint`
y `notAfter`. Ver `docs/certificate-trust.md` para confiarlo en Windows.

### 6.3 Cifrado por listener (`Security`)

| `Security` | Comportamiento |
|-----------|----------------|
| `None` | Texto plano. No se resuelve certificado (así 8025 arranca sin tocar el almacén del usuario) |
| `StartTls` (default en 8443) | El listener anuncia `STARTTLS` en el EHLO y el cliente decide si actualiza; el certificado se usa en la actualización |
| `Implicit` | **TLS implícito** (SMTPS): el socket se acepta ya cifrado y el `220` se emite tras el handshake. No hay comando `STARTTLS` |

`Smtp:Plain:Security` y `Smtp:StartTls:Security` aceptan los tres valores (sin distinguir mayúsculas).
Un valor desconocido falla al arrancar nombrando la clave. Con `Security=StartTls` el
`envelope.transport` del mensaje se registra como `startTls` y `tlsNegotiated` refleja si el
cliente actualizó (el listener anuncia pero no exige, RF-03); con `Implicit` se registra
`implicitTls` con `tlsNegotiated: true` siempre.

Sobrescritura por variables de entorno (prefijo doble guion bajo):
`Smtp__Plain__Port=9025`, `Storage__Directory=/tmp/smtp`, `Web__Port=9090`,
`Smtp__StartTls__Certificate__Password` (secretos fuera del archivo).

Reglas de validación generales:
- Valor fuera de rango (puerto, tamaño, pageSize) ⇒ el host **falla al arrancar** (fail-fast) en vez
  de usar un default silencioso.
- `Web:BindAddress` fuera de loopback ⇒ warning en log.
- `Storage:Directory` se crea automáticamente si no existe.

---

## 7. Esquema JSON del correo (v1) — sin cambios respecto de v1.0

Un correo = un archivo con un objeto JSON. Contrato estable versionado por `schemaVersion`.

```json
{
  "schemaVersion": "1.0",
  "id": "01JQ8Z3K7F9A2B3C4D5E6F7G8H",
  "receivedAtUtc": "2026-02-10T18:04:05.123Z",
  "size": { "totalBytes": 48213, "bodyBytes": 11045, "attachmentCount": 1 },
  "envelope": {
    "helo": "dev-machine.local",
    "remoteIp": "127.0.0.1",
    "remotePort": 54321,
    "mailFrom": "sender@example.com",
    "recipientTo": ["a@example.com", "b@example.com"],
    "authenticated": false,
    "transport": "Plain",
    "tlsNegotiated": false
  },
  "headers": [
    { "name": "Message-ID", "value": "<abc@mail.example.com>" },
    { "name": "X-Custom", "value": "1" }
  ],
  "subject": "Re: prueba",
  "messageId": "abc@mail.example.com",
  "sentDate": "2026-02-10T17:00:00.000Z",
  "from":    [{ "name": "Sender", "address": "sender@example.com" }],
  "to":      [{ "name": null,     "address": "a@example.com" }],
  "cc":      [],
  "bcc":     [{ "name": null,     "address": "hidden@example.com" }],
  "replyTo": [{ "name": null,     "address": "reply@example.com" }],
  "body": {
    "text": "Hola en plano\n",
    "html": "<p>Hola en <b>HTML</b></p>",
    "hasText": true,
    "hasHtml": true
  },
  "attachments": [
    {
      "fileName": "reporte.pdf",
      "contentType": "application/pdf",
      "sizeBytes": 36100,
      "contentId": null,
      "isInline": false,
      "checksumSha256": "9f86d081...",
      "omitted": false,
      "contentBase64": "JVBERi0xLjQK..."
    }
  ],
  "raw": { "sizeBytes": 48213, "truncated": false, "contentBase64": "UmV0dXJuIFRv..." }
}
```

Reglas del esquema (sin cambios):
- Siempre presentes: `schemaVersion`, `id`, `receivedAtUtc`, `size`, `envelope`, `headers`,
  `subject`, `messageId`, `sentDate`, `from/to/cc/bcc/replyTo`, `body`, `attachments`.
  Los valores opcionales usan `null` o `[]`; nunca se omiten campos.
- `subject` se guarda **crudo** (puede ser `=?UTF-8?B?...?=`); la UI muestra el valor decodificado.
- Direcciones: `address` en minúsculas, se conserva `name`, `bcc` deduplicado.
- El sobre (`envelope.recipientTo`) conserva los RCPT TO reales, única fuente de Bcc.
- Attachment sin filename ⇒ `fileName: "unnamed"`; con `Content-ID` ⇒ `isInline: true`.
- Attachment > `Storage:MaxInlineAttachmentBytes` ⇒ `omitted: true` sin `contentBase64`.
  `0` significa "sin tope", igual que en `MaxRawMimeBytes`: un tope a cero desactiva el límite,
  no descarta todos los adjuntos.
- `raw` solo si `Storage:KeepRawMime`; `truncated: true` si se recorta.
- Serialización: `System.Text.Json`, `CamelCase`, `WriteIndented` según config, sin ignorar nulos.
- Fechas: UTC con `Z` y hasta 7 decimales.

---

## 8. Convención de nombres de archivo — sin cambios respecto de v1.0

```
data/messages/2026/02/10/01JQ8Z3K7F9A2B3C4D5E6F7G8H.json
                └año┘└mes┘└día┘ └───────── ULID ─────────┘
```

- ULID = 10 caracteres de timestamp (ms, ordenable) + 16 aleatorios (base32 Crockford, mayúsculas).
  El orden lexicográfico coincide con el orden de recepción.
- Particionado por día: evita directorios con millones de archivos; la UI recorre por rango de fechas.
- Validación con regex `^\d{8}/\d{2}/\d{2}/[0-9A-HJKMNP-TV-Z]{26}\.json$` antes de leer o borrar
  (anti path traversal).
- Escritura atómica: `<id>.json.tmp` → `File.Move(tmp, final, overwrite: false)`.
  Un `.tmp` huérfano se purga al arrancar (mensaje incompleto).
- Codificación UTF-8 sin BOM; el base64 es independiente del fin de línea del host.

---

## 9. UI web (Blazor Web App, Interactive Server)

### 9.1 Requisitos técnicos
- Blazor Web App sobre .NET 10, `AddInteractiveServerComponents()`,
  `MapRazorComponents<App>().AddInteractiveServerRenderMode()`.
- **Interactive Server**: el circuito SignalR transporta solo los diffs de UI; los correos viajan
  por el circuito, no por HTTP público.
- Los componentes inyectan `IMessageStore` directamente. **No hay clientes HTTP, ni DTOs de
  transporte HTTP, ni capa de mapeo de red.**
- Sirve estáticos desde `wwwroot` (`app.css`, JS de Blazor, iconos); `UseStaticFiles()` +
  `MapRazorComponents<App>()`.
- Enrutamiento por rutas de archivos (`Routes.razor`), ruta base `/` (no `/api`).
- Anti-forgery no aplica: no hay formularios POST; las acciones son `EventCallback` de componentes.
- Sin autenticación; por eso el bind por defecto es loopback.

### 9.2 Páginas

| Ruta | Página | Contenido |
|------|--------|-----------|
| `/` | `MessageList` | Listado paginado con búsqueda, filtros, borrado individual y masivo |
| `/messages/{id}` | `MessageDetail` | Envelope, headers, cuerpos (texto/HTML), adjuntos, metadata |
| `/stats` | `Stats` | Conteo por día y por dominio destinatario |
| `/settings` | `Settings` (read-only) | Configuración efectiva, rutas, estado del storage, certificado en uso |
| `/about` | `About` | Versión, endpoints SMTP activos, enlace a README |

### 9.3 Layout y comportamiento del listado
- Columnas: recibido, remitente, destinatarios, asunto, tamaño, adjuntos, transporte, acciones.
- Orden por `receivedAtUtc` descendente, desempate por `id`.
- Filtros (todos opcionales, combinados con AND): texto libre (`search`, asunto + remitente +
  destinatario), `from`, `to`, `subject`, `receivedAfter`, `receivedBefore`, `hasAttachments`,
  `sizeMinBytes`, `sizeMaxBytes`.
- Paginación: `page` (1-based) y `pageSize` (default `Web:DefaultPageSize`, tope
  `Web:MaxPageSize`). Exceder el tope muestra un aviso de validación en la propia UI.
- Selección múltiple con checkbox y acción "borrar seleccionados" con diálogo de confirmación.

### 9.4 Detalle del mensaje
- Pestañas o secciones: **Resumen** (envelope: helo, IP/puerto remoto, MAIL FROM, RCPT TO,
  transporte, TLS negociado), **Headers** (crudos, con copiado), **Cuerpo** (tabs Texto/HTML),
  **Adjuntos** (tabla con nombre, tipo, tamaño, SHA-256, badge inline/omitido, botón descargar).
- El HTML se muestra en un `<iframe sandbox>` con `srcdoc` (sin scripts ni acceso al host), y por
  encima de `MaxHtmlPreviewBytes` se muestra como texto escapado en un `<pre>`.
- Nunca se renderiza contenido MIME como HTML sin sanear.

### 9.5 Descargas
- Adjuntos: se decodifica el base64 y se sirve como `Download` de Blazor (`Results.File`) con su
  `Content-Type` y un nombre saneado (`Path.GetFileName`, sin separadores).
- MIME original: botón "descargar .eml" cuando `Storage:KeepRawMime=true` y
  `Web:ShowRawMimeDownload=true`.

### 9.6 Actualización en vivo (notificación desde `IMessageStore`)

`IMessageStore` expone un mecanismo de notificación de cambios que **debe** Rising después de la
escritura atómica, nunca antes.

```csharp
public interface IMailStore
{
    event EventHandler<MailStoreChangedEventArgs>? Changed;

    Task<MailStoreChangeResult> SaveAsync(ReceivedMessage message, CancellationToken ct = default);
    Task<ReceivedMessage?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<MessageSummary>> QueryAsync(MailQuery query, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<bool> DeleteAsync(string id, CancellationToken ct = default);
    Task<int> DeleteManyAsync(MailQuery query, CancellationToken ct = default);
}

public enum MailStoreChangeKind { Stored, Deleted }

public sealed record MailStoreChangedEventArgs(MailStoreChangeKind Kind, MessageSummary? Message)
{
    public DateTimeOffset OccurredAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int DeletedCount { get; init; } = 1; // >1 en borrado masivo
}
```

Reglas:
- El evento se emite **fuera del lock** del índice y con handlers protegidos: una excepción de un
  suscriptor se loguea como `Warning` y nunca rompe la persistencia ni el `250` de SMTP.
- Suscriptores iniciales: el catálogo/listado de la UI, la página de stats y el contador del panel.
- La UI se suscribe en `OnInitializedAsync` y se desuscribe en `DisposeAsync` (evita fugas de circuito).
- Ante `Stored`, si la sesión está en la página 1 y el filtro activo sigue coincidiendo, el listado
  se refresca y la fila nueva se resalta; si no coincide con el filtro, solo sube el contador de la
  cabecera.
- Ante `Deleted`, se quita la fila y se recalcula el total; si la página queda vacía, se retrocede.
- El evento es **en proceso**: si el directorio se modifica por fuera, la UI se entera por el
  `FileSystemWatcher` (`Web:WatchDirectory`) o con el botón "Actualizar".
- Frecuencia: si llegan > 5 eventos en 1 s, se agrupan (debounce de 250 ms) antes de re-renderizar.

### 9.7 Salud y diagnóstico
- `Web:Enabled=false` ⇒ no se mapea Blazor; el proceso sigue sirviendo SMTP y registra el warning.
- El panel de estado (`/settings`) muestra uptime, cantidad de mensajes, si el directorio es
  escribible, thumbprint del certificado y puertos SMTP activos.
- No se exponen endpoints JSON para consulta de correos: la UI es la única superficie de lectura.

---

## 10. Manejo de errores y casos borde

### 10.1 SMTP
| Situación | Código | Comportamiento |
|-----------|--------|----------------|
| Mensaje > `MaxMessageSizeBytes` | `552` | DATA abortado, no se persiste, log `Warning` |
| MIME inválido / no parseable | `550` | No se persiste |
| `MAIL FROM` sin argumentos | `501` | — |
| `RCPT TO` antes de `MAIL FROM` | `503` | — |
| `DATA` sin RCPT | `503` | — |
| `MAIL FROM` sin `EHLO`/`HELO` | `503` | — |
| Cliente desconecta en medio de DATA | — | Se borra el temporal; nunca queda archivo parcial |
| Handshake TLS inválido en 8443 | — | Cierre de conexión, log `Warning` |
| Certificado `Mode: File` ausente o con contraseña incorrecta | — | Fail-fast: el puerto no inicia, error explícito en log |
| Certificado `Mode: Auto` y certificado de desarrollo corrupto | — | Se regenera y se persiste; warning, el puerto inicia |
| Más de `MaxRecipientsPerMessage` RCPT | `452` | Límite duro |
| Header `Date:` ausente o inválido | — | `sentDate: null`; el mensaje se persiste igual |
| `Subject` codificado no decodificable | — | Se guarda crudo; la UI muestra el valor crudo |
| Charset de body no soportado | — | Fallback UTF-8; si falla, `text: null`/`html: null` + log |
| Multipart anidado o `multipart/alternative` | — | Se toma la primera parte text/plain y la primera text/html |
| Adjunto inline sin nombre | — | `fileName: "unnamed"`, `isInline: true` |
| Sin `Message-ID` | — | Se genera `<{id}@smtp-mockup>` |
| Storage sin escritura / disco lleno | `451` | Se responde 451 en vez de perder el cliente |

### 10.2 Storage
- Escritura atómica; si el id final ya existe se genera otro.
- Directorios de partición creados perezosamente.
- `.tmp` huérfanos se purgan al arrancar.
- Archivos que no cumplen el regex se ignoran al indexar (no se listan ni se borran en masa).
- Índice en memoria invalidado en cada escritura (no hay TTL: `Storage:IndexCacheSeconds` se
  retiró de la SPEC en v2.1 por no existir en el código, y un TTL sólo serviría para mostrar
  cambios externos con retraso).
- Error de I/O ⇒ `MailStoreException` ⇒ SMTP `451`; en la UI se muestra un `ErrorMessage` en la
  cabecera de la lista, no una pantalla en blanco.
- Excepción en un handler de `Changed` ⇒ se loguea y se ignora (nunca rompe el guardado).
- Nunca se loguea el cuerpo del mensaje ni el contenido de adjuntos (solo tamaños y hashes).

### 10.3 UI
- `id` con formato inválido en `/messages/{id}` ⇒ página de "mensaje no encontrado" con enlace
  de vuelta, nunca una excepción sin manejar.
- `pageSize` mayor que `Web:MaxPageSize` ⇒ mensaje de validación visible; no se clampa en silencio.
- Rango de fechas invertido (`receivedAfter > receivedBefore`) ⇒ mensaje de validación.
- Borrado masivo sin selección ⇒ acción deshabilitada, no excepción.
- Canal cerrado o cliente desconectado durante una descarga ⇒ se cancela la tarea sin error visible.
- HTML de correo con scripts ⇒ renderizado en `iframe sandbox`; jamás se inyecta en el DOM principal.
- Nombre de adjunto con rutas (`../../x`) ⇒ se sanea con `Path.GetFileName` antes de servir.
- `Web:Enabled=false` ⇒ SMTP sigue funcionando; no se mapea ninguna ruta de la UI.

### 10.4 Proceso
- Puerto ocupado (SMTP o web) ⇒ el proceso falla al arrancar indicando el puerto.
- Componente deshabilitado ⇒ no se abre ni se anuncia en el log de endpoints activos.
- `Ctrl+C` / `SIGTERM` ⇒ cierre ordenado con drain de 5 s: web (circuitos) → SMTP → índice.
- Windows Service: `Automatic`, `RestartOnFailure`, registro en Event Log.

---

## 11. Criterios de aceptación

### 11.1 SMTP y persistencia
1. MailKit envía a `127.0.0.1:8025` sin credenciales ⇒ `Ok`; aparece 1 JSON.
2. El cliente se conecta a 8443 con `SecureSocketOptions.None` ⇒ el EHLO anuncia `STARTTLS` y el
   mensaje se acepta igual (`tlsNegotiated: false`). Con `SecureSocketOptions.StartTls` el mismo
   envío da `transport: "StartTls"` y `tlsNegotiated: true`, con el certificado configurado.
3. Con `Smtp:Plain:Enabled=false` ⇒ 8025 cerrado y 8443 operativo.
4. Mensaje con HTML + 2 adjuntos ⇒ `body.text`/`body.html` poblados y, en cada adjunto,
   `sizeBytes` = longitud del base64 decodificado.
5. Mensaje de 30 MB ⇒ `552` y ningún archivo creado.
6. Primer arranque sin certificado previo ⇒ 8443 arranca, se genera y persiste el PFX junto al
   ejecutable, y el segundo arranque lo reutiliza (thumbprint idéntico).
7. Con `Certificate:Mode=File` y ruta inexistente ⇒ el proceso falla con mensaje explícito
   (código de salida 1, mensaje por stderr, sin stack trace).
8. Con `Certificate:AutoGenerateSelfSigned=false` y sin certificado ⇒ el proceso falla nombrando
   `Certificate:Path` y `Certificate:AutoGenerateSelfSigned`.
9. Con `Certificate:UseDevelopmentCertificate=false` ⇒ el certificado presentado es el PFX propio
   aunque la máquina tenga un `dotnet dev-certs https` instalado.
10. Un cliente MailKit que acepta el certificado autofirmado (`ServerCertificateValidationCallback`)
    envía contra 8443 sin credenciales y el mensaje aparece persistido como el resto.

### 11.2 UI Blazor
8. Navegar a `http://127.0.0.1:8080/` con la UI habilitada ⇒ listado renderizado en el primer
   request (Interactive Server conectado; no hay errores de `_framework` en la consola).
9. Enviar un correo por SMTP con el navegador abierto en el listado ⇒ la fila aparece sola en < 2 s
   sin recargar (verificable con un test de `MailStoreChangedEventArgs` y con inspección manual).
10. Seleccionar 2 correos y borrar ⇒ diálogo de confirmación ⇒ ambos desaparecen de la UI y los
    archivos se borran del disco.
11. Abrir el detalle ⇒ envelope, headers, cuerpo texto/HTML y adjuntos; descargar un adjunto
    devuelve el binario exacto y el nombre original.
12. Búsqueda por texto libre y por rango de fechas ⇒ solo devuelve los mensajes que cumplen el filtro.
13. Con `Web:Enabled=false` ⇒ `http://127.0.0.1:8080/` no responde, pero SMTP sigue aceptando.
14. Con `Web:BindAddress=0.0.0.0` ⇒ warning en el log de arranque.

### 11.3 Publicación single-file con assets estáticos de Blazor (Windows y Linux)
15. `dotnet publish -r win-x64 --self-contained` produce `smtp-mockup.exe` **y** la carpeta
    `wwwroot` con `_framework/*`, `app.css` y los assets de Blazor; el ejecutable arranca y la UI
    carga sin 404 de estáticos.
16. Repetir el publish desde un directorio limpio (sin `bin/obj` previos) reproduce el mismo
    resultado: los static web assets se publican junto al binario.
17. Con el ejecutable movido a otra carpeta junto a su `wwwroot`, la UI sigue funcionando
    (no hay rutas absolutas embebidas).
18. Con el servicio Windows instalado y arrancado (`sc start`), la UI responde en el puerto
    configurado con los estáticos servidos correctamente.
19. **Plan B documentado y verificado:** si el objetivo de *un solo archivo* (.exe sin carpetas)
    resulta inconsistente con los static web assets, se publica como `exe + wwwroot` en la misma
    carpeta (publicación por defecto de Blazor Web App). El README documenta que hay que copiar
    **la carpeta completa**, no solo el `.exe`, y se provee `publish.ps1`/`publish.sh` que empaquetan
    ambas cosas (zip) para distribuir.

---

## 12. Registro de cambios

### v2.0 — 2026-02-10 — Cambio de rumbo: API HTTP → UI Blazor

**Se eliminó**
| Ítem | Motivo |
|------|--------|
| Proyecto `src/SmtpMockup.Api/` | La UI accede al dominio directamente vía inyección; una capa HTTP de solo lectura es redundante |
| Endpoints `/api/messages*`, `/api/health`, `/api/messages/stats`, `/{id}/raw`, `/{id}/attachments/{i}` | Reemplazados por páginas Blazor |
| Paquete `Microsoft.AspNetCore.OpenApi`, `AddOpenApi()`, `MapOpenApi()`, `/openapi/v1.json` | Ya no hay contrato HTTP que documentar |
| DTOs de transporte (`MessageSummaryDto`, `MessageDetailDto`, `PagedResult<T>`, `QueryParameters`, `HealthDto`, `DeleteResultDto`), `IMessageMapper`, `ProblemDetails` RFC 9457, `IExceptionHandler` | Sin endpoints no hay wire format; los componentes usan el modelo de dominio + view models propios |
| `EnableCors` y toda la configuración `Api:*` | Sin API no hay cross-origin; Blazor Server no usa CORS |
| Etapa 2 del plan: frontend React 19 + MUI + Vite, `npm`, carpeta `web/`, proxy `/api` | La UI es Blazor: un solo lenguaje, un solo proceso, sin toolchain de Node |
| Criterios de aceptación HTTP (`404`, `405`, `problem+json`) | Reemplazados por criterios de UI (§11.2) |
| `WebApplicationFactory` como suite principal de la capa web | Ahora la UI se prueba con bUnit; `WebApplicationFactory` queda solo para tests de hosting |

**Se agregó**
| Ítem | Detalle |
|------|---------|
| Proyecto `src/SmtpMockup.Web/` | Blazor Web App .NET 10, Interactive Server, con `Components/Pages`, `Components/Shared` y `wwwroot` |
| Configuración `Web:*` | `Enabled`, `Port` (8080), `BindAddress` (127.0.0.1), `Title`, `DefaultPageSize`, `MaxPageSize`, `ShowRawMimeDownload`, `MaxHtmlPreviewBytes`, `LiveUpdateDebounceMilliseconds`, `WatchDirectory` |
| `event Changed` en `IMailStore` + `MailStoreChangedEventArgs` / `MailStoreChangeKind` | Notificación de guardado y borrado para actualización en vivo (RF-13) |
| `MailStoreChangeNotifier` en Storage | Implementación concreta, con debounce de 250 ms y handlers protegidos |
| Páginas `/`, `/messages/{id}`, `/stats`, `/settings`, `/about` | Listado, detalle, estadísticas, configuración read-only y about |
| Render seguro del HTML de correo | `iframe sandbox` + límite `MaxHtmlPreviewBytes` |
| Descargas desde componentes | `Results.File` / `FileContentResult` con `Content-Type` y nombre saneado |
| `Certificate:Mode` (`Auto` \| `File`) | Modo Auto con certificado de desarrollo de .NET o generación + persistencia en el primer arranque (RF-15) |
| `tests/SmtpMockup.Web.Tests/` | bUnit para componentes + `WebApplicationFactory` para hosting |
| Criterios de aceptación de publicación | §11.3, con plan B documentado (`exe` + `wwwroot`) |
| Paquetes de test | `bunit`; se elimina `Microsoft.AspNetCore.Mvc.Testing` como dependencia de la suite de UI (queda para el test de hosting) |

**Se mantuvo sin cambios**
- SMTP en 8025 (plano) y 8443 (STARTTLS), habilitación independiente por puerto.
- `SmtpServer` + MimeKit como base; sin autenticación, sin relay.
- Persistencia JSON con partición diaria, ULID y escritura atómica.
- Esquema JSON del correo y convención de nombres de archivo (§7, §8).
- Ejecutable único self-contained y hosting como Windows Service.
- Sin trimming en publish; bibliotecas estándar de .NET.

**Impacto en el árbol**: no hay archivos de código para borrar todavía (el repositorio solo contiene
documentación y configuración). Lo que queda obsoleto son referencias en los documentos y en el
memory-bank; ver §13.

---

## 13. Elementos obsoletos por el cambio y acción propuesta

| Elemento | Ubicación | Estado | Acción propuesta |
|----------|-----------|--------|------------------|
| Proyecto `SmtpMockup.Api` | `SPEC.md` §5 v1, `DESIGN.md` §4.4 v1, memory-bank | Descrito, nunca creado | **Eliminar** de la estructura; ya está fuera de §5/§4.4 v2 |
| `tests/SmtpMockup.Api.Tests` | igual | Descrito, nunca creado | **Reemplazar** por `SmtpMockup.Web.Tests` |
| Paquete `Microsoft.AspNetCore.OpenApi` | `DESIGN.md` §1/§4.4 v1, `techContext.md` | En lista de dependencias | **Quitar** de la lista de NuGet |
| `Microsoft.AspNetCore.Mvc.Testing` | v1 deps | En lista | **Mantener** solo para el test de hosting del Host (no para API); documentar su uso puntual |
| Dependencia `bunit` | — | No existía | **Agregar** a `SmtpMockup.Web.Tests` |
| Config `Api:*` (`Url`, `DefaultPageSize`, `MaxPageSize`, `ExposeRawMime`, `EnableCors`) | `appsettings.json` (spec) | Especificada | **Renombrar** a `Web:*` con los defaults nuevos (§6.1) |
| DTOs `*Dto`, `IMessageMapper`, `PagedResult<T>` | `DESIGN.md` §4.4 v1 | Especificados | **Eliminar**; la UI usa view models en `SmtpMockup.Web/Components` |
| `ProblemDetails` / `IExceptionHandler` | v1 | Especificados | **Eliminar** del stack HTTP; los errores de UI se muestran en componentes (`ErrorMessage`) |
| Etapa 2 (React 19 + MUI + Vite) | planes previos / preguntas abiertas | Mencionada | **Eliminar**; no habrá `package.json`, ni `web/`, ni proxy |
| Pregunta abierta "contrato de API (prompt 7)" | `SPEC.md` §12 v1 | Abierta | **Cerrar**: ya no hay API HTTP que definir |
| `memory-bank/apiReference.md` | memory-bank | Describe la API HTTP | **Actualizar**: dejar solo el esquema JSON y describir la UI/web reference |
| `projectbrief.md`, `productContext.md`, `activeContext.md`, `systemPatterns.md`, `techContext.md`, `progress.md` | memory-bank | Describen la API | **Actualizar** para reflejar Blazor (RF-08, RF-09, RF-13, §9) |
| `smtp-mockup.slnx`, `Directory.Build.props`, proyectos | no existen | — | **Crear** en el paso 1 del roadmap, ya con `SmtpMockup.Web` en lugar de `Api` |
| `.gitignore`, `.editorconfig`, `.gitattributes`, `global.json` | raíz | Válidos | **Sin cambios** (ya contemplan `certs/`, `data/`, `*.pfx`) |

**Nada se borra físicamente hoy**: el repositorio no tiene `bin/`, `obj/`, `.csproj` ni artefactos de
la API. La única acción de borrado pendiente sería `git rm` de archivos de código, si en el futuro
existieran; hoy la limpieza es documental.

---

## 14. Preguntas abiertas

1. **Tamaño del circuito / reconexión:** ¿alcanza con el límite por defecto de Blazor (30 sesiones
   en servidor de workstation) o se configura `CircuitOptions`/`MaxConcurrentTransfers`?
2. **Estilo visual:** ¿CSS propio con Bootstrap incluido en la plantilla, o algún otro (Tailwind,
   MudBlazor)? No se pidió librería de componentes.
3. **Intl:** ¿`@rendermode InteractiveServer` con CultureInfo explícito (es-ES) o el del SO?
4. **Estado inicial de la lista:** ¿el listado debe mostrar los últimos N por defecto o exigir filtro?
5. **Borrado masivo:** ¿confirmación con doble clic o diálogo modal nativo (`<dialog>`/JS)?
6. **Notificación:** ¿basta el evento en proceso, o además un sonido/notificación del navegador
   (Notification API) cuando llega un correo con la pestaña en segundo plano?
7. **Debounce configurable:** ¿exponer `Web:LiveUpdateDebounceMs` o dejarlo fijo en 250 ms?
8. **Retención automática** (TTL o tope de mensajes) en lugar de borrado manual desde la UI.
9. **Papelera:** ¿borrado duro o soft delete con "restaurar"?
10. **Raw MIME:** ¿se guarda siempre o bajo demanda con caché en disco?
11. **IPv6 / dual-stack** en los binds de SMTP y web.
12. **Directorio exclusivo por proceso** o varias instancias sobre el mismo `Storage:Directory`
    (afecta al evento `Changed`, que solo cubre el proceso actual).
13. **Nombre del ejecutable y del servicio Windows** (`smtp-mockup`) y del certificado persistido.
14. **Contenedor rootless** además del ejecutable self-contained.
15. **Límite de `MaxHtmlPreviewBytes` y política de CSP** para el iframe de previsualización.
