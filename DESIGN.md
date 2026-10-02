# smtp-mockup — DESIGN

Documento técnico que acompaña a `SPEC.md`: decisiones de arquitectura, flujo de datos,
componentes, concurrencia, empaquetado y estrategia de pruebas (TDD).

- **Paquete objetivo:** `net10.0`
- **Lenguaje:** C# 14 (`LangVersion=latest`, `Nullable=enable`, `TreatWarningsAsErrors`)
- **Formato de solución:** `slnx` (solución de .NET 10) o `sln` clásica si la tooling lo exige

---

## 1. Decisiones de arquitectura (ADR-resumen)

| # | Decisión | Alternativas descartadas | Motivo |
|---|----------|-------------------------|--------|
| D-01 | Servidor SMTP sobre `MailKit.Net.Smtp.SmtpServer` | Implementar RFC 5321 a mano | MailKit es el servidor SMTP de referencia en .NET, con `SmtpServer` y eventos (`MessageReceived`, `Authenticate`) ya probados |
| D-02 | Persistencia en archivos JSON (`FileSystemMailStore`) | SQLite; LiteDB; MongoDB | Cero dependencia nativa, diff-friendly, inspeccionable, portable |
| D-03 | Directorio por día + ULID | Guid plano; timestamp+Guid | Orden cronológico sin campo extra ni coordinación |
| D-04 | Un solo proceso: Kestrel + listeners SMTP como hosted services | Dos procesos | Requisito de ejecutable único y ciclo de vida compartido |
| D-05 | Dominio sin dependencias de infraestructura (proyecto `Core`) | Modelo único compartido | Evita arrastrar ASP.NET/MailKit a los tests de dominio |
| D-06 | Minimal APIs + `Microsoft.AspNetCore.OpenApi` | Controllers | Menor superficie, arranque rápido, OpenAPI oficial |
| D-07 | Índice en memoria con TTL corto | Re-escaneo en cada request | `GET /api/messages` es el endpoint caliente; TTL de 5 s |
| D-08 | `System.Text.Json` con `JsonSerializerContext` (source-gen) | Newtonsoft | AOT-friendly, rápido, tipado |
| D-09 | xUnit + `WebApplicationFactory` + `MailKit` como cliente | Solo unitarios | Verifica el flujo real SMTP → JSON → API |
| D-10 | Hosting dual consola/Windows Service con `Microsoft.Extensions.Hosting.WindowsServices` | Código por plataforma | Un solo entry point; `ServiceBase` se activa solo en Windows |

Dependencias previstas (versiones a fijar al implementar):

```
MailKit                                          (SmtpServer + cliente de tests)
MimeKit                                          (parsing MIME)
Microsoft.Extensions.Hosting
Microsoft.Extensions.Hosting.WindowsServices     (Host)
Microsoft.Extensions.Configuration.Json           (Host)
Microsoft.Extensions.Logging.Console
Microsoft.AspNetCore.OpenApi                      (Api)
xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk
Microsoft.AspNetCore.Mvc.Testing                 (tests de API)
```

---

## 2. Arquitectura de componentes

```
                 ┌──────────────────────── Host (composition root) ──────────────────────┐
                 │ Program.cs: builder → Options+Validate → DI → AddHostedService        │
                 └──┬──────────────┬────────────────────┬───────────────────┬────────────┘
                    │              │                    │                   │
        SmtpListenerService  SmtpListenerService  TempFilePurgerHostedSvc   Kestrel (Minimal API)
           (puerto 8025)       (puerto 8443)                             ┌──────────┴─────────────┐
                    │              │                                    │                        │
                    └──────┬───────┘                          MessageApiModule         MessageApiModule
                           │                                   (GET)                      (DELETE)
                     MimeKitParser                                     │                        │
                           │                                   IMailQuery             IMessageCommand
             ReceivedMessageFactory                                          │                        │
                           └──────────────► IMessageParser/IMailStore ◄─────┴────────────────────────┘
                                                  (Core)         FileSystemMailStore (Storage)
```

Regla de dependencia: todas las flechas apuntan hacia `Core`. `Core` no referencia ASP.NET Core ni MailKit.

---

## 3. Flujo de un mensaje (secuencia)

1. Cliente conecta al puerto (8025 o 8443); el servidor responde `220 smtp-mockup ESMTP`.
2. `EHLO` ⇒ capabilities `SIZE`, `8BITMIME`, `SMTPUTF8`, `ENHANCEDSTATUSCODES` y `STARTTLS`
   cuando corresponda. `AUTH` **nunca** se anuncia.
3. `MAIL FROM` / `RCPT TO` se acumulan en la sesión del servidor.
4. `DATA` ⇒ MailKit entrega el stream MIME y dispara `MessageReceived` con `RemoteEndPoint`,
   `Helo`, opciones de transporte y estado TLS.
5. Se lee el stream hasta `MaxMessageSizeBytes` y se parsea con `MimeMessage.Load(stream)`;
   si se excede ⇒ `552` y se descarta.
6. `MimeKitParser` extrae headers, cuerpos, direcciones y adjuntos; `ReceivedMessageFactory`
   arma el modelo de `Core` con `MessageId` ULID.
7. `FileSystemMailStore.SaveAsync` escribe `<partición>/<id>.json.tmp` y renombra atómicamente.
8. Se responde `250 2.0.0 OK id=<ulid>` (ENHANCEDSTATUSCODES permite devolver el id).
9. Log `MessageStored` con id, tamaños, remitente y destinatarios; nunca el contenido.

Los pasos 5–7 ocurren dentro del callback del servidor, de modo que el `250` significa
"persistido": el cliente nunca recibe éxito por un mensaje no guardado.

"persistido": el cliente nunca recibe éxito por un mensaje no guardado.

---

## 4. Detalle por proyecto

### 4.1 `SmtpMockup.Core`
- Modelo: `ReceivedMessage`, `EnvelopeInfo`, `MailAddressInfo`, `HeaderInfo`, `BodyInfo`,
  `AttachmentInfo` (con `contentBase64` y `omitted`), `SizeInfo`, `RawMimeInfo`,
  `TransportKind { Plain, StartTls }`.
- Contratos: `IMessageParser`, `IMailStore` (Save/Get/Delete/DeleteWhere/Query/Count),
  `IMailQuery` e `IMessageCommand` (separación CQRS ligera: la API no depende del store completo).
- Opciones POCO con `IValidateOptions<T>` en cada proyecto dueño.
- `MessageId.New()` (ULID base32 Crockford) y `MessagePath.For(id, receivedAt, dir)`.
- `Result<T>` + `AppError(code, message)` para errores esperables sin excepciones.

### 4.2 `SmtpMockup.Smtp`
- `SmtpMockupServer` envuelve `SmtpServer`: `ServerName`, `MaxMessageSize`, `ServerCertificate`
  (solo STARTTLS), `AuthenticationMechanisms = AuthMechanisms.None`,
  `CheckAuthenticationInterlock = false`, suscripción a `MessageReceived`.
- `SmtpListenerService : BackgroundService` — una instancia por opción habilitada; un fallo de
  arranque (puerto ocupado, PFX inválido) ⇒ log `Critical` + `StopApplication()`.
- `DevelopmentCertificateProvider` — carga el PFX si existe; si no, genera uno autofirmado en
  memoria (SAN: localhost, 127.0.0.1, ::1) y loguea el thumbprint.
- Traducción de excepciones: error de parseo MIME ⇒ `550`, excedente de tamaño ⇒ `552`,
  `IOException`/`MailStoreException` ⇒ `451`.
- Estado de sesión (helo, remote ip, nº rcpt) con `ConcurrentDictionary` acotado.

### 4.3 `SmtpMockup.Storage`
- `FileSystemMailStore`: `FileStreamOptions { CreateNew = true }` al abrir el `.tmp` y
  `File.Move(tmp, final, overwrite: false)`; conflicto de id ⇒ regenerar.
- `MailJsonSerializer`: un único `JsonSerializerOptions` (camelCase, `WriteIndented` según config,
  fechas UTC) más `JsonSerializerContext` source-generated para `ReceivedMessage`.
- `FileMailIndex`: cache en memoria de `MessageSummary` (sin cuerpos ni base64) construida al
  arrancar y refrescada por TTL; los filtros se aplican sobre el índice, no sobre los archivos.
- `MailPathValidator` (regex de partición + ULID) y `PurgeTempFiles` en el arranque.

### 4.4 `SmtpMockup.Api`
- `IMessageApi.MapMessageApi(this WebApplication app)` con `RouteGroupBuilder("/api")`.
- DTOs propios en `Api/Contracts`: `MessageSummaryDto`, `MessageDetailDto`, `PagedResult<T>`,
  `QueryParameters`, `HealthDto`, `DeleteResultDto`. El dominio nunca se serializa directo.
- `IMessageMapper` (Core→DTO) calcula `subjectDecoded` con el decoder de MimeKit.
- Parsing explícito de query: `DateTimeOffset` con `InvariantCulture`, `hasAttachments` bool,
  `pageSize` fuera de rango ⇒ `400` explícito (sin clamp silencioso).
- `IExceptionHandler` global ⇒ `ProblemDetails` RFC 9457.
- `AddOpenApi()` + `MapOpenApi()` ⇒ `/openapi/v1.json` en desarrollo.

### 4.5 `SmtpMockup.Host`
- `AddOptions<T>().Bind(config).ValidateDataAnnotations().ValidateOnStart()`.
- `UseWindowsService()` solo en Windows y solo si el proceso corre como servicio.
- `WebApplication` único que además hospeda los listeners SMTP como `BackgroundService`.
- `SmtpMockup.Host.csproj`: `OutputType=Exe`, `AssemblyName=smtp-mockup`,
  `InvariantGlobalization=true`; en publish: `SelfContained`, `PublishSingleFile=true`,
  `RuntimeIdentifier` por SO/arquitectura. **Sin** trimming (MailKit + reflexión de DI).
- Apagado ordenado: `HostOptions.ShutdownTimeout = 5s`.

---

## 5. Concurrencia y cancelación

- Cada conexión SMTP corre en su propia tarea; el estado por mensaje (temp file, parseo) es local,
  así que no hay estado compartido mutable salvo el índice en memoria.
- Lecturas de la API pueden coincidir con escrituras: el rename atómico garantiza que nunca se
  lea un JSON parcial.
- Todo I/O recibe `CancellationToken` (en la API, `HttpContext.RequestAborted`).
- Orden de parada: API → SMTP → flush del índice.

## 6. Empaquetado y despliegue

| Destino | Publicación | Resultado |
|---------|-------------|-----------|
| Linux/macOS, consola | `-r linux-x64 --self-contained -p:PublishSingleFile=true` | ejecutable único `smtp-mockup` |
| Windows, exe portable | `-r win-x64` (idem) | `smtp-mockup.exe` |
| Windows Service | `sc.exe create smtp-mockup binPath= "...\smtp-mockup.exe" start= auto` + `UseWindowsService()` | Servicio con reinicio ante fallo |
| Contenedor (fuera de v1) | imagen runtime-deps copiando el publish | rootless con puertos configurables |

Notas: `appsettings.json` se copia junto al binario y puede sobreescribirse por variable de entorno.
Los datos viven fuera del binario (`data/messages`) para que un publish no los pise.

---

## 7. Estrategia de pruebas (TDD)

1. **Unit (`Core.Tests`)** — `MessageId.New()` (unicidad y orden), `MessagePath.For`,
   factory de mensaje con envelope/cuerpo/adjuntos, decodificación de `subject`, rechazo de rutas
   inválidas (path traversal).
2. **Integración Storage (`Storage.Tests`)** — round-trip save/load, nulos y `[]` opcionales,
   atomicidad (no queda `.tmp`), purge de temporales, id inválido, borrado masivo con filtro,
   búsqueda por texto y por rango de fechas.
3. **Integración SMTP (`Smtp.Tests`)** — con MailKit como cliente real: envío sin auth, STARTTLS en
   8443, HTML + adjuntos, rechazo por tamaño (`552`), varios `RCPT TO`, puerto deshabilitado.
4. **Integración API (`Api.Tests`)** — `WebApplicationFactory`: listado con filtros y paginación,
   detalle, 404, id inválido ⇒ 400, borrado individual y masivo con `confirm=true`,
   `ProblemDetails` en errores, descarga de adjunto.
5. **E2E (`Host.Tests`)** — host real con directorios temporales: envío por 8025, lectura por API,
   borrado y apagado ordenado.

Convenciones: un test = un comportamiento; sin `Thread.Sleep` (se usan `TaskCompletionSource`,
polling con timeout y puertos efímeros) para evitar flakes en CI.

CI propuesta: `restore` → `build -warnaserror` → `test` en matrix
`[ubuntu-latest, windows-latest, macos-latest]`, más un job de `publish` single-file.

---

## 8. Observabilidad

- Log estructurado con `ILogger<T>`; eventos: `SmtpServerStarted/Stopped`, `MessageReceived`,
  `MessageStored`, `MessageRejected` (con motivo), `TlsNegotiationFailed`, `PurgeCompleted`.
- `MessageRejected` incluye el motivo (tamaño, MIME inválido, storage) pero **nunca** el contenido.
- `/api/health` como probe; nivel `Debug` del módulo SMTP para trazas de comandos.

---

## 9. Riesgos y mitigaciones

| Riesgo | Mitigación |
|--------|-----------|
| `SmtpServer` no expone todo el estado de sesión (helo/TLS) | Spike inicial sobre la API real; si falta, derivarlo de `MessageReceivedEventArgs` o mantener estado propio por conexión |
| JSON muy grande ⇒ respuestas lentas | `WriteIndented` configurable, DTOs de listado sin cuerpos ni base64 |
| Índice en memoria desactualizado | TTL corto + invalidación en escritura + header de diagnóstico `X-Index-Stale` |
| Crecimiento indefinido de `data/messages` | Purga manual vía API; TTL automático queda como pregunta abierta |
| Trimming rompe MailKit/DI | No usar trimming en v1 |
| Exposición accidental de la API | Bind por defecto `127.0.0.1` + warning en log |

---

## 10. Hoja de ruta de implementación

1. Solución, `global.json`, `Directory.Build.props`, `.editorconfig`.
2. `Core`: modelos, opciones, validación, `MessageId`/`MessagePath`, tests.
3. `Storage`: serializer, `FileSystemMailStore`, índice, tests.
4. `Smtp`: certificado dev, `SmtpMockupServer`, `SmtpListenerService`, tests con MailKit.
5. `Api`: endpoints, DTOs, mapper, ProblemDetails, OpenAPI, tests.
6. `Host`: composition root, `appsettings.json`, logs, apagado, scripts de publish.
7. E2E, README con ejemplos (`swaks`, `curl`, cliente .NET) y CHANGELOG.
