# smtp-mockup — SPEC

Servidor SMTP falso para desarrollo: acepta cualquier correo, sin autenticación, sin relay,
lo persiste como JSON y lo expone vía API HTTP de lectura/borrado.

- **Estado del documento:** v1.0 (borrador para revisión)
- **Stack:** .NET 10 (`net10.0`), C# 14, proyectos SDK-style
- **Documento contraparte:** `DESIGN.md` (decisiones técnicas, alternativas y plan)

---

## 1. Objetivo y alcance

### 1.1 Objetivo
Proveer un ejecutable local de un solo proceso que:
1. Escucha SMTP en dos puertos configurables e independientes (8025 plano, 8443 STARTTLS).
2. Acepta y persiste **todo** correo recibido, sin autenticación, sin validación de destinatario
   y sin entrega externa.
3. Expone una API HTTP para listar, ver, descargar y borrar los correos almacenados.
4. Corre como `exe` standalone (self-contained, single-file) o como Windows Service en Windows,
   y como aplicación de consola en Linux/macOS.

### 1.2 Fuera de alcance (v1)
- Relay o entrega real a servidores externos.
- Autenticación SMTP (AUTH), DKIM/SPF, validación de envelopes, anti-spam, cuotas por usuario.
- Clustering, réplicas, base de datos, búsqueda full-text, UI web.
- Cifrado implícito (SMTPS / puerto 465).
- Envío de correos desde el mockup (solo recepción).

---

## 2. Requisitos funcionales

| ID | Requisito | Criterio de aceptación |
|----|-----------|------------------------|
| RF-01 | Aceptar cualquier correo sin autenticación | `MAIL FROM`/`RCPT TO` sin `AUTH` ⇒ `250` y entrega final `250 OK` con `id` asignado |
| RF-02 | Sin relay real | Ninguna conexión SMTP saliente; el mensaje solo se persiste |
| RF-03 | Puerto 8025 (plano) | Escucha configurable (default `8025`), anuncia `250 STARTTLS`, responde `454` si la actualización falla |
| RF-04 | Puerto 8443 (STARTTLS) | Carga `ServerCertificate` desde PFX; anuncia `STARTTLS`; responde `220` tras el handshake TLS |
| RF-05 | Habilitación independiente | `Enabled=false` en un puerto ⇒ no se crea listener; el otro sigue operando |
| RF-06 | Persistencia JSON | Un archivo por correo en `Storage:Directory`, escritura atómica, sin mezclar mensajes |
| RF-07 | Configurable por `appsettings.json` | Directorio, puertos, límites, API y verbosidad sin recompilar |
| RF-08 | API HTTP de lectura | `GET /api/messages` con filtros y paginación; `GET /api/messages/{id}`; descarga de adjunto |
| RF-09 | API HTTP de borrado | `DELETE /api/messages/{id}` y borrado masivo con filtros; operación idempotente |
| RF-10 | Un solo ejecutable | `dotnet publish` self-contained single-file; en Windows también como Windows Service |
| RF-11 | multipart/MIME completo | `text/plain` y `text/html` por separado; adjuntos en base64 con metadatos |
| RF-12 | Diagnóstico | Respuestas coherentes con RFC 5321; log estructurado; `/api/health` |

---

## 3. Requisitos no funcionales

- **RNF-01 Latencia:** un `DATA` de 1 MB persiste en < 500 ms en disco local.
- **RNF-02 Concurrencia:** N conexiones SMTP simultáneas (`MaxConcurrentConnections`, default 20)
  sin pérdida ni interleaving de mensajes; escrituras aisladas por mensaje.
- **RNF-03 Memoria:** streaming del cuerpo hasta el límite configurado, sin duplicar el mensaje
  completo más de una vez.
- **RNF-04 Aislamiento:** un parseo fallido no tumba el listener; el cliente recibe `550` y el
  servidor sigue aceptando conexiones.
- **RNF-05 Portabilidad:** mismo comportamiento en Windows, Linux y macOS, sin dependencias nativas.
- **RNF-06 Clean Code / TDD:** lógica de negocio en `Core` con tests unitarios; parsing, storage y
  API cubiertos con tests de integración (xUnit + `Microsoft.AspNetCore.Mvc.Testing` + MailKit).
- **RNF-07 Sin secretos en disco:** el certificado por defecto es autofirmado y de solo desarrollo.

---

## 4. Supuestos y valores por defecto

| Supuesto | Valor por defecto | Justificación |
|----------|-------------------|---------------|
| API HTTP escucha en | `http://127.0.0.1:8080` | Solo localhost: los datos son sensibles y no hay auth |
| Tamaño máximo de mensaje | `25 MB` (`26214400` bytes) | Límite práctico del BCL/MimeKit; configurable |
| Autenticación SMTP | ninguna | Requisito; no se implementa en v1 |
| STARTTLS en 8025 | anunciado, opcional | El cliente puede intentarlo; `454` si no se puede actualizar |
| Cifrado implícito | no soportado | Requisito explícito |
| Nombre en el banner | `smtp-mockup` | Banner RFC 5321 `220 smtp-mockup ESMTP` |
| Directorio de datos | `./data/messages` | Relativo al ContentRoot |
| Fechas | UTC ISO-8601 | Determinismo entre plataformas |
| Retención | sin expiración automática | v1; borrado manual vía API |
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
│   ├── SmtpMockup.Core/          # Dominio: modelos, contratos, opciones (sin I/O)
│   ├── SmtpMockup.Smtp/          # Servidor SMTP (MailKit SmtpServer), certificado, opciones
│   ├── SmtpMockup.Storage/       # IMailStore: persistencia JSON en disco (System.Text.Json)
│   ├── SmtpMockup.Api/           # Minimal APIs: endpoints, DTOs, mapeos, DI
│   └── SmtpMockup.Host/          # Executable único: composition root, config, Windows Service
├── tests/
│   ├── SmtpMockup.Core.Tests/
│   ├── SmtpMockup.Smtp.Tests/
│   ├── SmtpMockup.Storage.Tests/
│   └── SmtpMockup.Api.Tests/
└── data/
    └── messages/                 # Runtime (gitignored)
```

Responsabilidades:
- **Core** — `ReceivedMessage`, `EnvelopeInfo`, `MailAddressInfo`, `HeaderInfo`, `BodyInfo`,
  `AttachmentInfo`, `SizeInfo`, `RawMimeInfo`, contratos `IMessageParser`/`IMailStore`/
  `IMailQuery`/`IMessageCommand`, opciones POCO, `MessageId` (ULID), `Result<T>`/`AppError`.
  Sin dependencias de I/O, ASP.NET ni MailKit.
- **Smtp** — `SmtpMockupServer` (envuelve `MailKit.Net.Smtp.SmtpServer`), opciones por puerto,
  certificado autofirmado de desarrollo, traducción de excepciones a códigos SMTP.
- **Storage** — `FileSystemMailStore` (implementa `IMailStore`), `MailJsonSerializer` con
  opciones centrales, escritura atómica, `FileMailIndex` para listados y filtros.
- **Api** — `MapMessageApi` (Minimal APIs), DTOs separados del dominio, `ProblemDetails`, OpenAPI.
- **Host** — `Program.cs`, registro DI, `appsettings.json`, hosting como Windows Service.

Dependencias (sin ciclos):
`Host → {Api, Smtp, Storage}` · `Api → Core` · `Smtp → Core` · `Storage → Core` · `Core → nada`.

---

## 6. Configuración (`appsettings.json`)

```json
{
  "Logging": { "LogLevel": { "Default": "Information", "SmtpMockup.Smtp": "Debug" } },
  "Smtp": {
    "MaxConcurrentConnections": 20,
    "Plain": {
      "Enabled": true,
      "Port": 8025,
      "BindAddress": "0.0.0.0",
      "AdvertiseStartTls": true,
      "MaxMessageSizeBytes": 26214400,
      "MaxRecipientsPerMessage": 100,
      "ServerName": "smtp-mockup"
    },
    "StartTls": {
      "Enabled": true,
      "Port": 8443,
      "BindAddress": "0.0.0.0",
      "MaxMessageSizeBytes": 26214400,
      "Certificate": { "Path": "certs/dev.pfx", "Password": "" }
    }
  },
  "Storage": {
    "Directory": "data/messages",
    "WriteIndented": true,
    "IndexCacheSeconds": 5,
    "MaxInlineAttachmentBytes": 4194304,
    "KeepRawMime": true
  },
  "Api": {
    "Enabled": true,
    "Url": "http://127.0.0.1:8080",
    "DefaultPageSize": 50,
    "MaxPageSize": 200,
    "ExposeRawMime": true,
    "EnableCors": false
  }
}
```

Sobrescritura por variables de entorno (prefijo doble guion bajo):
`Smtp__Plain__Port=9025`, `Storage__Directory=/tmp/smtp`, `Api__Url=http://0.0.0.0:9000`,
`Smtp__StartTls__Certificate__Password` (secretos fuera del archivo).

Reglas de validación:
- Valor fuera de rango (puerto, tamaño, pageSize) ⇒ el host **falla al arrancar** (fail-fast) en vez
  de usar un default silencioso.
- `Api:Url` fuera de loopback ⇒ warning en log.
- PFX inexistente en 8443 ⇒ se genera certificado autofirmado en memoria (solo dev) y se loguea el
  thumbprint; si el PFX existe y la contraseña falla ⇒ error fatal.

---

## 7. Esquema JSON del correo (v1)

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

Reglas del esquema:
- Siempre presentes: `schemaVersion`, `id`, `receivedAtUtc`, `size`, `envelope`, `headers`,
  `subject`, `messageId`, `sentDate`, `from/to/cc/bcc/replyTo`, `body`, `attachments`.
  Los valores opcionales usan `null` o `[]`; nunca se omiten campos.
- `subject` se guarda **crudo** (puede ser `=?UTF-8?B?...?=`); la API expone `subjectDecoded`
  como campo derivado, no persistido.
- Direcciones: `address` en minúsculas, se conserva `name`, `bcc` deduplicado.
- El sobre (`envelope.recipientTo`) conserva los RCPT TO reales, que es la única fuente de Bcc.
- Attachment sin filename ⇒ `fileName: "unnamed"`; con `Content-ID` ⇒ `isInline: true`.
- Attachment > `Storage:MaxInlineAttachmentBytes` ⇒ `omitted: true` sin `contentBase64`.
- `raw` solo si `Storage:KeepRawMime`; `truncated: true` si se recorta.
- Serialización: `System.Text.Json`, `CamelCase`, `WriteIndented` según config, sin ignorar nulos.
- Fechas: UTC con `Z` y hasta 7 decimales.

---

## 8. Convención de nombres de archivo

```
data/messages/2026/02/10/01JQ8Z3K7F9A2B3C4D5E6F7G8H.json
                └año┘└mes┘└día┘ └───────── ULID ─────────┘
```

- ULID = 10 caracteres de timestamp (ms, ordenable) + 16 aleatorios (base32 Crockford, mayúsculas).
  El orden lexicográfico coincide con el orden de recepción.
- Particionado por día: evita directorios con millones de archivos; la API recorre por rango de fechas.
- Validación con regex `^\d{8}/\d{2}/\d{2}/[0-9A-HJKMNP-TV-Z]{26}\.json$` antes de leer o borrar
  (anti path traversal).
- Escritura atómica: `<id>.json.tmp` → `File.Move(tmp, final, overwrite: false)`.
  Un `.tmp` huérfano se purga al arrancar (mensaje incompleto).
- Codificación UTF-8 sin BOM; el base64 es independiente del fin de línea del host.

---

## 9. API HTTP (v1)

Minimal API sobre ASP.NET Core, todo bajo `/api`. Sin autenticación (localhost por defecto).

| Método | Ruta | Descripción | Respuesta |
|--------|------|-------------|-----------|
| GET | `/api/health` | Estado del proceso y del storage | `200 { status, uptimeSeconds, storedMessages, storageWritable }` |
| GET | `/api/messages` | Lista paginada y filtrada | `200 { items[], page, pageSize, total }` |
| GET | `/api/messages/stats` | Conteo por día y por dominio destinatario | `200` |
| GET | `/api/messages/{id}` | Mensaje completo (metadatos de adjuntos, sin base64) | `200` / `404` |
| GET | `/api/messages/{id}/raw` | MIME original | `200 message/rfc822` |
| GET | `/api/messages/{id}/attachments/{index}` | Descarga un adjunto | `200` con su content-type |
| DELETE | `/api/messages/{id}` | Borra un mensaje | `204` / `404` |
| DELETE | `/api/messages` | Borrado masivo con los mismos filtros | `200 { deleted }` |

Filtros de `GET /api/messages` (query string, todos opcionales, combinados con AND):
`from`, `to`, `cc`, `bcc`, `subject` (contiene, case-insensitive), `search` (asunto + cuerpos),
`receivedAfter`, `receivedBefore` (ISO-8601 UTC), `hasAttachments`, `sizeMinBytes`, `sizeMaxBytes`.

Paginación: `page` (1-based, default 1) y `pageSize` (default `Api:DefaultPageSize`, tope
`Api:MaxPageSize`). Orden: `receivedAtUtc` descendente, desempate por `id`.

Ejemplo de respuesta de lista (los adjuntos traen metadatos, nunca el base64):

```json
{
  "page": 1,
  "pageSize": 50,
  "total": 2,
  "items": [
    {
      "id": "01JQ8Z3K7F9A2B3C4D5E6F7G8H",
      "receivedAt": "2026-02-10T18:04:05.123Z",
      "subject": "Re: prueba",
      "subjectDecoded": "Re: prueba",
      "from": [{ "name": "Sender", "address": "sender@example.com" }],
      "to": [{ "name": null, "address": "a@example.com" }],
      "sizeBytes": 48213,
      "attachmentCount": 1,
      "hasAttachments": true,
      "remoteIp": "127.0.0.1",
      "transport": "Plain"
    }
  ]
}
```

Errores: siempre `application/problem+json` (RFC 9457) con `type`, `title`, `status`, `detail`,
`traceId` y `errors` cuando haya validación de entrada.
`400` filtros inválidos · `404` inexistente · `405` método no permitido · `500` error inesperado
(jamas stack trace al cliente).

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
| Certificado no cargado | — | Fail-fast: el puerto no inicia, error explícito en log |
| Más de `MaxRecipientsPerMessage` RCPT | `452` | Límite duro |
| Header `Date:` ausente o inválido | — | `sentDate: null`; el mensaje se persiste igual |
| `Subject` codificado no decodificable | — | Se guarda crudo; `subjectDecoded: null` |
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
- Índice en memoria con TTL `IndexCacheSeconds`, invalidado en cada escritura.
- Error de I/O ⇒ `MailStoreException` ⇒ SMTP `451` y HTTP `500`.
- Nunca se loguea el cuerpo del mensaje ni el contenido de adjuntos (solo tamaños y hashes).

### 10.3 API
- `id` no ULID ⇒ `400` (input malformado) y no `404`.
- `pageSize` > máximo ⇒ `400` (no se clampa en silencio).
- `receivedAfter > receivedBefore` ⇒ `400`.
- `DELETE` de id inexistente ⇒ `404`; repetido ⇒ `404` (idempotencia documentada).
- `DELETE /api/messages` sin filtros ⇒ `400` salvo `confirm=true` (protección anti-borrado masivo).
- Cliente desconecta durante la petición ⇒ no se loguea como error.
- CORS deshabilitado por defecto; habilitable con `Api:EnableCors`.

### 10.4 Proceso
- Puerto ocupado ⇒ el proceso falla al arrancar indicando el puerto.
- Puerto deshabilitado ⇒ no se abre ni se anuncia en el log de endpoints activos.
- `Ctrl+C` / `SIGTERM` ⇒ cierre ordenado con drain de 5 s de HTTP y SMTP.
- Windows Service: `Automatic`, `RestartOnFailure`, registro en Event Log.

---

## 11. Criterios de aceptación (E2E)

1. MailKit envía a `127.0.0.1:8025` sin credenciales ⇒ `Ok`; aparece 1 JSON;
   `GET /api/messages` lo lista; `DELETE` lo borra y `GET` devuelve `404`.
2. El mismo envío por 8443 con STARTTLS ⇒ JSON con `transport: "StartTls"` y `tlsNegotiated: true`.
3. Con `Smtp:Plain:Enabled=false` ⇒ 8025 cerrado y 8443 operativo.
4. Mensaje con HTML + 2 adjuntos ⇒ `body.text`/`body.html` poblados y, en cada adjunto,
   `sizeBytes` = longitud del base64 decodificado.
5. Mensaje de 30 MB ⇒ `552` y ningún archivo creado.
6. Ruta inexistente ⇒ `404` problem+json; método incorrecto ⇒ `405`.
7. El ejecutable single-file arranca en Linux, en Windows y como Windows Service sin cambios de código.

---

## 12. Preguntas abiertas

1. **Prompt 7 (API)**: no viene incluido en este prompt. Confirmar el contrato exacto (rutas,
   nombres de campos, forma de paginación, si requiere SSE/WebSocket para ver correos en vivo).
2. ¿Retención automática (TTL) o purga por cantidad máxima de mensajes?
3. ¿Borrado hard o soft delete con papelera? ¿Hace falta "restaurar"?
4. ¿Autenticación para la API (token fijo / API key por header) aunque sea solo localhost?
5. ¿CORS habilitado por defecto para integraciones web en desarrollo?
6. ¿Búsqueda full-text sobre el cuerpo (SQLite FTS / Lucene) o alcanza el filtro `search` lineal?
7. ¿Guardar el MIME raw siempre o bajo demanda con caché en disco?
8. ¿Tope de adjunto inline por configuración global o por entorno?
9. ¿IPv6 en el bind (`::1`) y/o dual-stack (`0.0.0.0` + `::`)?
10. ¿Varias instancias escribiendo en el mismo directorio o el directorio es exclusivo por proceso?
11. ¿Se necesita guardar la conversación SMTP literal para debugging?
12. Nombre del ensamblado/ejecutable y del servicio Windows (`smtp-mockup`).
13. ¿Se publica imagen de contenedor rootless además del self-contained?
14. ¿Cifrado en reposo de los JSON (DPAPI/EFS) o se acepta en claro por ser herramienta de dev?
15. ¿Métricas Prometheus o basta `/api/health`?
