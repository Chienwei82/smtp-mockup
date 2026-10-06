# smtp-mockup — DESIGN

Documento técnico que acompaña a `SPEC.md`: decisiones de arquitectura, flujo de datos,
componentes, empaquetado (incluido el de los assets estáticos de Blazor) y estrategia de pruebas (TDD).

- **Paquete objetivo:** `net10.0`
- **Lenguaje:** C# 14 (`LangVersion=latest`, `Nullable=enable`, `TreatWarningsAsErrors`)
- **Formato de solución:** `slnx` (solución de .NET 10) o `sln` clásica si la tooling lo exige
- **Versión del documento:** v2.3 (v2.0: Blazor Web App en lugar de API HTTP; v2.1: ajuste del puerto por defecto de la UI y scripts nuevos; v2.2: se retira el soporte de macOS; v2.3: estilo Material You dark del prototipo y accesibilidad)

> Cambio de rumbo aplicado: se elimina la API HTTP (proyecto `Api`, endpoints, OpenAPI, CORS) y se
> agrega `SmtpMockup.Web` (Blazor Web App, Interactive Server) dentro del mismo proceso. Ver §11.

---

## 1. Decisiones de arquitectura (ADR-resumen)

| # | Decisión | Alternativas descartadas | Motivo |
|---|----------|-------------------------|--------|
| D-01 | **Servidor SMTP sobre `SmtpServer` 11.1.0 (cosullivan)** | ~~`MailKit.Net.Smtp.SmtpServer`~~; implementar RFC 5321 a mano; `Rnwood.SmtpServer` | **`MailKit` nunca publicó un `SmtpServer`**: el tipo sólo existe en el repo de GitHub, no en ningún paquete NuGet (verificado por reflexión en 4.3.0, 4.6.0, 4.10.0 y 4.18.1). `SmtpServer` es MIT, netstandard2.1, expone `IMessageStore.SaveAsync` que **devuelve la `SmtpResponse`**, así que 250/451/550/552 son de primera clase sin implementar RFC 5321. `Rnwood.SmtpServer` se descartó: la 4.0.0-beta rompe el diálogo EHLO (responde `250` y luego emite las extensiones sueltas sin prefijo `250-`) y la 3.0.25 es sólo .NET Framework. |
| D-02 | Persistencia en archivos JSON (`FileSystemMailStore`) | SQLite; LiteDB; MongoDB | Cero dependencia nativa, diff-friendly, inspeccionable, portable |
| D-03 | Directorio por día + ULID | Guid plano; timestamp+Guid | Orden cronológico sin campo extra ni coordinación |
| D-04 | Un solo proceso: Kestrel/Blazor + listeners SMTP como hosted services | Dos procesos | Requisito de ejecutable único y ciclo de vida compartido |
| D-05 | Dominio sin dependencias de infraestructura (proyecto `Core`) | Modelo único compartido | Evita arrastrar ASP.NET/Blazor/MailKit a los tests de dominio |
| D-06 | **Blazor Web App con Interactive Server** como única superficie de lectura | Minimal APIs + JSON; React/Vue SPA; Blazor WebAssembly | Un solo lenguaje y un solo proceso; sin CORS, sin API intermedia, sin toolchain de Node; circuito SignalR ya resuelve la actualización en vivo |
| D-07 | Índice en memoria con TTL corto | Re-escaneo en cada request | El listado es la pantalla caliente; TTL de 5 s cubre cambios externos |
| D-08 | `System.Text.Json` con `JsonSerializerContext` (source-gen) | Newtonsoft | AOT-friendly, rápido, tipado |
| D-09 | xUnit + bUnit + `WebApplicationFactory` + MailKit como cliente | Solo unitarios | Verifica el flujo real SMTP → JSON → UI → borrado |
| D-10 | Hosting dual consola/Windows Service autodetectado con `Microsoft.Extensions.Hosting.WindowsServices` | Código por plataforma | Un solo entry point; `UseWindowsService()` se aplica sólo si el SCM arrancó el proceso. La resolución del modo es lógica pura testeable sin Windows, y todas las rutas cuelgan de `AppContext.BaseDirectory` porque el SCM arranca con `C:\Windows\System32` como CWD. Ver `docs/hosting-modes.md` |
| D-11 | **Evento `IMailStore.Changed`** para notificar guardado/borrado | Polling desde la UI; SignalR hub dedicado; broadcaster de eventos de .NET | El store ya es la fuente de verdad; un evento es el mecanismo más simple y testeable para refrescar en vivo |
| D-12 | **Certificado en modo `Auto` (dev-certs o generación persistida) o `File`** | Solo PFX obligatorio; solo autofirmado en memoria | Cero configuración para arrancar y, a la vez, control explícito cuando se quiere un certificado real |
| D-13 | Publicación como `.exe` single-file self-contained (sin trimming) + `appsettings.json` y `wwwroot` como archivos junto al binario | Single-file estricto que embeba todo; publish desde CDN de estáticos | Sin trimming porque MailKit/MimeKit y el binder de `Options` usan reflexión, y un publish recortado rompe en runtime y no al compilar. Los static web assets se publican como archivos; `wwwroot` se extrae a `%TEMP%` en cada arranque si se embebiese, lo que como servicio es escribir en disco en cada start. Perfil: `src/SmtpMockup.Host/Properties/PublishProfiles/win-x64.pubxml` |
| D-14 | **8443 anuncia `STARTTLS`** (opcional, no exigido), con `Security` por listener | `STARTTLS` obligatorio; TLS implícito (SMTPS) por defecto | Un mockup se usa para probar clientes reales, y casi todos abidecen el `STARTTLS` anunciado; exigirlo rompería a los clientes que no actualizan. `Security=Implicit` ofrece SMTPS para quien lo necesite |

Dependencias previstas (versiones a fijar al implementar):

```
SmtpServer 11.1.0 (cosullivan)                    (servidor SMTP; MIT; netstandard2.1)
MailKit 4.18.1                                    (cliente SMTP, sólo tests)
MimeKit 4.18.1                                    (parsing MIME)
Microsoft.Extensions.Hosting
Microsoft.Extensions.Hosting.WindowsServices     (Host)
Microsoft.Extensions.Configuration.Json           (Host)
Microsoft.Extensions.Logging.Console
Microsoft.AspNetCore.Components.Web              (Web, incluido en el framework reference de ASP.NET Core)
bunit                                            (tests de componentes)
xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk
Microsoft.AspNetCore.Mvc.Testing                (test de hosting del Host)
```

`Microsoft.AspNetCore.OpenApi` queda **fuera**: no hay endpoints que documentar.

---

## 2. Arquitectura de componentes

```
                  ┌──────────────────── Host (composition root) ─────────────────────┐
                  │ Program.cs: Options+Validate → DI → AddRazorComponents → MapRazor │
                  └──┬───────────────┬───────────────────┬───────────────┬────────────┘
                     │               │                   │               │
     SmtpListenerService  SmtpListenerService  TempFilePurger  Blazor Interactive Server
        (puerto 8025)       (puerto 8443)      HostedService   (Kestrel :8888, circuito)
                     │               │                   │        ┌──────┴───────┐
                     └───────┬───────┘                   │        │              │
                      MimeKitParser                       │   MessageList   MessageDetail
                             │                            │   MessageFilter Stats/Settings
                             ▼                            │        │              │
                     ReceivedMessageFactory                │        └───┬──────────┘
                             │                            │            │
                             └──► IMailStore (Core) ◄──────┴────────────┘
                                       │  event Changed
                        ┌──────────────┴──────────────┐
                 FileSystemMailStore            FileMailIndex
                 (+ MailStoreChangeNotifier)
```

Punto clave: la UI **no** cruza HTTP. Los componentes reciben `IMailStore` por inyección directa y
se suscriben a `IMailStore.Changed` (D-11) para refrescar en vivo.

---

## 3. Flujo de un mensaje (secuencia)

1. Cliente ⇒ `EHLO` / `MAIL FROM` / `RCPT TO` / `DATA`.
2. `SmtpServer` (cosullivan) entrega el MIME crudo del `DATA` y el sobre a
   `MockupMessageStore.SaveAsync`.
3. `ReceivedMessageFactory` (en `Smtp`) parsea con MimeKit y normaliza: envelope, headers, cuerpos,
   adjuntos, `size`; genera el id ULID con `MessageId.New()`.
4. `IMessageStore.SaveAsync` escribe `<id>.json.tmp` y hace el rename atómico.
5. **`MailStoreChangeNotifier` emite `Changed(Stored, summary)` después del rename** (§7).
6. La UI suscrita ejecuta `InvokeAsync(StateHasChanged)` y recarga el listado.
7. `MockupMessageStore` devuelve `SmtpResponse.Ok`, que es el `250` que ve el cliente.

El orden importa: el `250` (paso 7) y el evento (paso 5) sólo ocurren tras persistencia exitosa.

---

## 4. Detalle por proyecto

### 4.1 `SmtpMockup.Core`
- Modelos: `ReceivedMessage`, `EnvelopeInfo`, `MailAddressInfo`, `HeaderInfo`, `BodyInfo`,
  `AttachmentInfo`, `SizeInfo`, `RawMimeInfo`, `MessageSummary`.
- `MailQuery` (objeto de filtros + paginación), `MessageId` (ULID), `MessagePath`,
  `Result<T>`/`AppError`, `MailStoreException`.
- Contrato **`IMailStore`** con `event Changed`, `SaveAsync`, `GetAsync`, `QueryAsync`, `CountAsync`,
  `DeleteAsync`, `DeleteManyAsync`, y los tipos `MailStoreChangedEventArgs`/`MailStoreChangeKind`.
- Opciones POCO (`SmtpOptions`, `PlainEndpointOptions`, `StartTlsEndpointOptions`,
  `CertificateOptions`, `StorageOptions`, `WebOptions`). Sin I/O, sin ASP.NET, sin Blazor, sin MailKit.

### 4.2 `SmtpMockup.Smtp`
- `SmtpListenerService` (hosted service) por puerto habilitado: construye las opciones del servidor
  (`SmtpServerOptionsBuilder`), su propio `IServiceProvider` y devuelve la puerto real.
- `MockupMessageStore : MessageStore` es el adaptador al pipeline: recibe el MIME crudo del `DATA` más
  el sobre (`MAIL FROM` / `RCPT TO`), lo convierte con `ReceivedMessageFactory` y lo persiste vía
  `IMessageStore`. **Devolver `SmtpResponse.Ok` es lo que produce el `250`**, de modo que el mensaje
  está en disco antes de que el cliente reciba el éxito (DESIGN §3). Los fallos se traducen aquí:
  `ParseException` → `550`, `MessageStoreException` → `451` (`SmtpReplyCode.Aborted`).
- `AcceptAllMailboxFilter` acepta cualquier remitente y destinatario, sin validación (RF-01).
- `CertificateProvider` / `ICertificateProvider`: modo `Auto` (busca el certificado de desarrollo de
  `dotnet dev-certs` en `CurrentUser\My`, exigiendo `CN=localhost` además del OID propio; si no,
  genera y **persiste** un autofirmado con SAN `localhost`/`127.0.0.1`/`::1`) y modo `File`
  (estricto, falla si falta o la contraseña es incorrecta). Singleton: resuelve una vez por
  proceso para que todas las sesiones presenten el mismo thumbprint. Un Windows Service con cuenta
  LocalSystem no ve el `CurrentUser\My` del usuario y cae al certificado generado (limitación
  conocida, documentada en `docs/certificate-trust.md` §4.1).
- `SelfSignedCertificateFactory`: RSA 2048 / SHA-256, CN=`localhost`, SAN loopback, EKU
  `serverAuth`, sin CA, 365 días; persistencia atómica (`.tmp` + `Move`) y modo 600 en Unix.
- `Security` por listener (`None` \| `StartTls` \| `Implicit`). 8443 usa `StartTls`: anuncia la
  extensión en el EHLO y el cliente decide si actualizar (no se exige, RF-03); `Implicit` queda
  disponible para quien lo prefiera (SMTPS). `envelope.transport` refleja el modo real
  (`Plain`/`StartTls`/`ImplicitTls`) y `tlsNegotiated` si la sesión acabó cifrada.

**Dos detalles no obvios de esta librería, verificados empíricamente:**

1. El extremo **remoto del cliente no** está en `IEndpointDefinition.Endpoint` (ese es el socket de
   escucha). La librería lo publica en `ISessionContext.Properties["EndpointListener:RemoteEndPoint"]`
   como `IPEndPoint`. Leerlo mal produce `remoteIp: "0.0.0.0"` con el puerto del listener.
2. El `IServiceProvider` que recibe el servidor debe vivir **toda** la vida del listener: la librería
   lo consulta en cada sesión. Un `await using` dentro de `StartAsync` lo dispone al salir y el
   listener falla con `ObjectDisposedException` en la primera conexión.

**API de MimeKit 4.x** (cambió respecto de la 2.x/3.x asumida en los docs): `MimeMultipart` ahora es
`Multipart` y **la clase `MimeAttachment` ya no existe**. Los adjuntos se construyen con
`BodyBuilder.Attachments.Add(...)`, que devuelve un `MimeEntity`; `FileName` y `Content` viven en
`MimePart`. El parseo de entrada se hace con `MimeParser`/`MimeMessage.LoadAsync(Stream)`.

### 4.3 `SmtpMockup.Storage`
- `MailJsonSerializer` con opciones centrales (`CamelCase`, `WriteIndented`, sin ignorar nulos) +
  `JsonSerializerContext` source-gen.
- `FileSystemMailStore` (implementa `IMailStore`): escritura atómica, validación de ruta,
  lectura, borrado individual y masivo por filtro.
- `FileMailIndex`: caché en memoria de `MessageSummary` (sin cuerpos ni base64), construida al
  arrancar y refrescada por TTL; los filtros se aplican sobre el índice, nunca sobre los archivos.
- `MailStoreChangeNotifier`: implementa la emisión de `Changed`, con debounce de 250 ms para altas
  ráfagas y handlers protegidos por `try/catch`.
- `PurgeTempFiles` al arranque.

### 4.4 `SmtpMockup.Web` (Blazor Web App, Interactive Server)
- `Components/App.razor` (host, `<HeadOutlet>`, `<Routes>`), `Components/Routes.razor`,
  `Components/_Imports.razor`, `Components/Layout/*` (MainLayout, NavMenu).
- `Components/Pages/*`: `MessageList.razor` (`/`), `MessageDetail.razor` (`/messages/{id}`),
  `Stats.razor` (`/stats`), `Settings.razor` (`/settings`), `About.razor` (`/about`).
- `Components/Shared/*`: `MessageFilter.razor`, `MessageRow.razor`, `BodyViewer.razor`,
  `AttachmentList.razor`, `ConfirmDialog.razor`, `LiveIndicator.razor`.
- Servicios de página (`MessageListState`, `MessageDetailState`): view models propios que tradacen
  `MailQuery` + `MessageSummary` a lo que la vista necesita. **No hay DTOs de transporte HTTP.**
- Los componentes se suscriben a `IMailStore.Changed` en `OnInitializedAsync` y se desuscriben en
  `DisposeAsync`.
- `wwwroot/`: CSS, JS de Blazor, iconos.

### 4.5 `SmtpMockup.Host`
- `AddOptions<T>().Bind(config).ValidateDataAnnotations().ValidateOnStart()`.
- `AddRazorComponents().AddInteractiveServerComponents()`;
  `MapRazorComponents<App>().AddInteractiveServerRenderMode()` solo si `Web:Enabled`.
- URL del host: `builder.WebHost.UseUrls(EndpointAddress.FormatHttpUrl(Web:BindAddress, Web:Port))` solo si
  `Web:Enabled`. Se compone con `EndpointAddress.FormatHttpUrl` y no con una interpolación: un
  literal IPv6 como `::1` produciría `http://::1:8888`, que es una autoridad ambigua y hace que
  Kestrel falle al arrancar.
- `UseWindowsService()` solo en Windows y solo si corre como servicio.
- `SmtpMockup.Host.csproj`: `OutputType=Exe`, `AssemblyName=smtp-mockup`, `WebRoot` = `wwwroot`
  (heredado del proyecto Web), `InvariantGlobalization=true`; en publish: `SelfContained=true`,
  `RuntimeIdentifier` por SO/arquitectura. **Sin trimming** (MailKit + reflexión de DI).
- Apagado ordenado: `HostOptions.ShutdownTimeout = 5s`.

---

## 5. Concurrencia y cancelación

- Cada conexión SMTP corre en su propia tarea; el estado por mensaje (temporal, parseo) es local, así
  que el único estado mutable compartido es el índice en memoria, protegido por `ReaderWriterLockSlim`
  o `ConcurrentDictionary`.
- `IMailStore.Changed` se emite fuera del lock; los handlers de la UI usan
  `InvokeAsync(StateHasChanged)` y `IDisposable` para desuscribirse.
- Lecturas de la UI pueden competir con escrituras: el rename atómico garantiza que nunca se lea un
  JSON parcial.
- Todos los I/O aceptan `CancellationToken`; las descargas y queries de la UI propagan el token del
  circuito.
- Orden de parada: circuito Blazor → SMTP → flush del índice.

---

## 6. Empaquetado y despliegue

| Destino | Comando / forma | Resultado esperado |
|---------|-----------------|--------------------|
| Linux, exe portable | `dotnet publish -r linux-x64 --self-contained` | `smtp-mockup` + `wwwroot/` |
| Windows, exe portable | `dotnet publish -r win-x64 --self-contained` | `smtp-mockup.exe` + `wwwroot/` |
| Windows Service | `sc.exe create smtp-mockup binPath= "C:\...\smtp-mockup.exe" start= auto` + `UseWindowsService()` | Servicio con reinicio; la UI sirve en `Web:Port` |
| Contenedor (fuera de v1) | imagen copiando el publish | rootless con puertos configurables |

### 6.1 Assets estáticos de Blazor: decisión de publicación (D-13)

Los *static web assets* de una Blazor Web App (los archivos bajo `wwwroot`, incluido `_framework/`)
se publican como **archivos junto al ejecutable**, no se embeben en el binario. Por eso la decisión
es publicar como **`exe` + carpeta `wwwroot`** y tratar esa carpeta como parte inseparable del
deploy:

- El proyecto `Host` referencia `SmtpMockup.Web` y, por el SDK de ASP.NET Core, el publish agrega
  `wwwroot` (y `_content/*` si hubiera librerías de terceros con assets).
- `appsettings.json` se copia junto al binario; los datos viven fuera (`data/messages`) para que un
  publish no los pise.
- Scripts `publish.sh` / `publish.ps1` generan un **zip** con `exe + wwwroot + appsettings.json`
  para distribuir la carpeta completa como una unidad.

**Plan B (documentado y aceptado):** si en algún RID resulta inconsistente empaquetar los estáticos
junto al binario, la alternativa oficial es exactamente la que ya adoptamos: **`exe` + carpeta
`wwwroot`**, y la documentación/README deja explícito que hay que copiar **la carpeta completa**, no
solo el `.exe`. No se fuerza `PublishSingleFile` con `IncludeAllContentForSelfExtract` salvo que se
verifique que no rompe los estáticos y que la auto-extracción es aceptable (escribe a disco y no es
ideal para un servicio).

Criterios verificables (SPEC §11.3): publish limpio produce `wwwroot/_framework/*`; mover `exe` +
`wwwroot` a otra carpeta sigue sirviendo la UI; el servicio Windows publicado sirve los estáticos.

---

## 7. Notificación de cambios (diseño de `IMailStore.Changed`)

Esta es la pieza que habilita la actualización en vivo de la UI sin API HTTP.

```
Smtp (SaveAsync)                    Storage                      Web
     │                                │                            │
     ├─ escribe .tmp ────────────────►│                            │
     ├─ rename atómico ──────────────►│                            │
     │                                ├─ actualiza índice          │
     │                                ├─ emite Changed(Stored) ───►│ InvokeAsync(StateHasChanged)
     │◄── 250 2.0.0 OK id=<ulid>      │                            │ recarga listado
```

Decisiones:
- **Evento (`event EventHandler<MailStoreChangedEventArgs>? Changed`) en `IMailStore`** (D-11). Es
  el punto único donde los tres caminos que mutan datos (guardar, borrar, borrar masivo) avisan.
- Se emite **después** del rename atómico y **fuera** de cualquier lock: un `250` de SMTP o una fila
  en la UI nunca apuntan a un archivo que todavía no existe.
- Handlers protegidos: cada suscriptor se invoca en `try/catch`; una excepción se loguea como
  `Warning` y no afecta al guardado (SPEC §10.2).
- **Debounce de 250 ms** para ráfagas (>5 eventos/seg) para no re-renderizar en cada mensaje.
- Un `MailStoreChangeKind` (`Stored` / `Deleted`) y `MessageSummary` (o `null` en borrados masivos,
  con `DeletedCount`) bastan para que la UI decida sin releer el archivo.
- Notificación **en proceso**: cambios externos al directorio sólo se detectan por el TTL del índice
  o por el botón "Actualizar" (limitación consciente, documentada en SPEC §9.6).

Interfaces afectadas:
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
    public int DeletedCount { get; init; } = 1;
}
```

---

## 8. Estrategia de pruebas (TDD)

1. **Unit (`Core.Tests`)** — `MessageId.New()` (unicidad y orden), `MessagePath.For`, factory de
   mensaje con envelope/cuerpo/adjuntos, decodificación de `subject`, `MailQuery` (parsing de
   filtros), rechazo de rutas inválidas (path traversal), construcción de `MailStoreChangedEventArgs`.
2. **Integración Storage (`Storage.Tests`)** — round-trip save/load, nulos y `[]` opcionales,
   atomicidad (no queda `.tmp`), purge de temporales, id inválido, borrado masivo con filtro, búsqueda
   por texto y por rango de fechas, y **evento `Changed`**: se dispara en `SaveAsync` y en
   `DeleteAsync`, **no** ante fallos de I/O, y una excepción en un handler no propaga.
3. **Integración SMTP (`Smtp.Tests`)** — con MailKit como cliente real: envío sin auth, STARTTLS en
   8443, HTML + adjuntos, rechazo por tamaño (`552`), varios `RCPT TO`, puerto deshabilitado,
   certificado `Auto` (persistencia) y `File` (fatal).
4. **Componentes (`Web.Tests`, bUnit)** — `MessageList` renderiza filas y filtra; `MessageDetail`
   muestra cuerpos y adjuntos; `ConfirmDialog` exige confirmación; el componente se **desuscribe** de
   `Changed` al destruirse; el HTML del correo se renderiza en `iframe sandbox`.
5. **Hosting/E2E (`Web.Tests` con `WebApplicationFactory` o `Host.Tests`)** — host real con
   directorios temporales: envío por 8025 ⇒ aparece en la UI sin recargar; borrado desde la UI ⇒ el
   archivo desaparece; apagado ordenado.

Convenciones: un test = un comportamiento; sin `Thread.Sleep` (se usan `TaskCompletionSource`, polling
con timeout y puertos efímeros) para evitar flakes en CI. Los tests de Blazor usan un
`TestContext` propio con `IMailStore` fake que emite `Changed` de forma determinista.

CI: `restore` → `build -warnaserror` → `test` en matrix `[ubuntu-latest, windows-latest]`,
más un job de `publish` que verifica que el artefacto contiene `exe + wwwroot`.

---

## 9. Observabilidad

- Log estructurado con `ILogger<T>`; eventos: `CertificateResolved` (modo + origen + thumbprint),
  `SmtpServerStarted/Stopped` (puerto + `Security` + thumbprint), `MessageReceived`,
  `MessageStored`, `MessageRejected` (con motivo), `TlsNegotiationFailed`, `PurgeCompleted` y
  `WebServerStarted` (con URL).
- `MessageRejected` incluye el motivo (tamaño, MIME inválido, storage) pero **nunca** el contenido.
- El panel `/settings` de la UI muestra uptime, cantidad de mensajes, si el directorio es escribible,
  thumbprint del certificado y puertos SMTP activos (sustituye al antiguo `/api/health`).
- Nivel `Debug` del módulo SMTP para trazas de comandos.

---

## 10. Riesgos y mitigaciones

| Riesgo | Mitigación |
|--------|-----------|
| Estado de sesión (helo/TLS) limitado en la API de la librería | — | **Resuelto**: el remoto está en `Properties["EndpointListener:RemoteEndPoint"]` y el TLS en `ISessionContext.Pipe.IsSecure`. `helo` no está expuesto y queda `null` (el cliente lo envía y MimeKit no lo conserva en el sobre) |
| Static web assets no embebidos ⇒ "exe solo" no sirve la UI | D-13: publicar y distribuir `exe + wwwroot` como unidad (plan B documentado, SPEC §11.3) |
| Altas ráfagas ⇒ re-renders costosos | Debounce de 250 ms en `MailStoreChangeNotifier` |
| Excepción en un handler de `Changed` rompe el guardado | Handlers protegidos por `try/catch` + log `Warning` |
| Fuga de suscripciones en circuitos Blazor | `DisposeAsync` en todos los componentes que se suscriben |
| JSON muy grande ⇒ listados lentos | `WriteIndented` configurable; el índice guarda solo `MessageSummary` (sin cuerpos ni base64) |
| Índice desactualizado ante cambios externos | TTL corto + invalidación en escritura + botón "Actualizar" |
| Crecimiento indefinido de `data/messages` | Borrado manual desde la UI; TTL automático sigue como pregunta abierta |
| Trimming rompe MailKit/DI | No usar trimming en v1 |
| Exposición accidental de la UI | Bind por defecto `127.0.0.1` + warning en log |
| XSS con el HTML del correo | Render en `iframe sandbox`, sin scripts, con límite de tamaño |

---

## 11. Hoja de ruta de implementación

1. Solución, `global.json`, `Directory.Build.props`, `.editorconfig`, `git init`.
2. `Core`: modelos, `MailQuery`, `MessageId`/`MessagePath`, **contrato `IMailStore` con `Changed`**,
   opciones, tests.
3. `Storage`: serializer, `FileSystemMailStore`, `MailStoreChangeNotifier`, índice, tests (incluido el
   del evento).
4. `Smtp`: `CertificateProvider` (Auto/File), `MockupMessageStore` (adaptador al pipeline),
   `SmtpListenerService`, tests con MailKit.
5. `Web`: plantilla Blazor Web App, layout, páginas, componentes, suscripción a `Changed`,
   descargas, sandbox de HTML, tests bUnit.
6. `Host`: composition root, `appsettings.json` (`Web:*`), logging, apagado, scripts de publish
   (`exe + wwwroot`, zip).
7. E2E, README con ejemplos (`swaks`, navegador, cliente .NET) y CHANGELOG.

---

## 12. Registro de cambios (resumen; el detalle está en `SPEC.md` §12)

### v2.0 — se eliminó
- Proyecto `SmtpMockup.Api` y `SmtpMockup.Api.Tests`; endpoints `/api/*`; paquete
  `Microsoft.AspNetCore.OpenApi` y `/openapi/v1.json`; DTOs de transporte, `IMessageMapper`,
  `PagedResult<T>`, `ProblemDetails`, `IExceptionHandler`; `EnableCors` y toda la config `Api:*`;
  etapa 2 (React 19 + MUI + Vite); criterios de aceptación HTTP.

### v2.0 — se agregó
- Proyecto `SmtpMockup.Web` (Blazor Web App .NET 10, Interactive Server) y `SmtpMockup.Web.Tests`
  (bUnit); config `Web:*` (`Enabled`, `Port` 8888, `BindAddress` 127.0.0.1, `Title`,
  `DefaultPageSize`, `MaxPageSize`, `ShowRawMimeDownload`,
  `MaxHtmlPreviewBytes`); `event IMailStore.Changed` + `MailStoreChangedEventArgs` (D-11);
  `MailStoreChangeNotifier` con debounce; páginas `/`, `/messages/{id}`, `/stats`, `/settings`,
  `/about`; render seguro del HTML en `iframe sandbox`; descargas desde componentes;
  `Certificate:Mode` (`Auto` \| `File`) con persistencia en el primer arranque (D-12) y
  `Certificate:AutoGenerateSelfSigned`; listener 8443 con STARTTLS anunciado (D-14); criterios de
  aceptación de publicación single-file con plan B (SPEC §11.3).

### v2.0 — se mantuvo
SMTP 8025/8443 con habilitación independiente; `SmtpServer` + MimeKit; sin auth ni relay;
persistencia JSON con ULID y partición diaria; esquema JSON y nombres de archivo; ejecutable único
self-contained y hosting como Windows Service; sin trimming.

### Impacto en el árbol
No hay código que borrar (el repo solo tiene documentación y configuración). Lo obsoleto son
referencias documentales y el memory-bank; ver `SPEC.md` §13. En la práctica:
- `smtp-mockup.slnx` / `Directory.Build.props` se crean **ya con `SmtpMockup.Web` en lugar de `Api`**.
- `Microsoft.AspNetCore.OpenApi` se elimina de la lista de paquetes; se agrega `bunit`.
- `Microsoft.AspNetCore.Mvc.Testing` queda solo para el test de hosting.
- El memory-bank (`projectbrief`, `productContext`, `activeContext`, `systemPatterns`,
  `techContext`, `progress`, `apiReference`) se actualiza para reflejar Blazor en lugar de API.

### v2.1 — se agregó (2026-10-06)
- `Web:Port` por defecto `8080` → `8888` (sigue siendo configurable; `0` = puerto efímero).
- `scripts/mockup_mailer.py` (módulo: cliente SMTP a 8025/8443 con STARTTLS opcional + generador de
  historias) y `scripts/send_story_mails.py` (tres correos de prueba: solo texto, texto + HTML, y
  texto + HTML + adjunto).
- `scripts/publish-with-config.py`: variante interactiva de `publish.py` que pregunta si se quiere TLS
  (por defecto **no**) y qué puertos usar (por defecto, los actuales), y escribe la respuesta en el
  `appsettings.json` publicado **antes** de armar el zip.

### v2.2 — se quitó (2026-10-06)
- Soporte de macOS: la matriz de CI pasa a `[ubuntu-latest, windows-latest]`, se elimina el job de
  publish `osx-arm64` y RNF-05/RNF-09 (SPEC) dejan de nombrar macOS. `PfxKeyStorage` mantiene la
  clave efímera solo en Linux (Windows no la usa en el handshake).

### v2.3 — se agregó (2026-10-06)
- Estilo visual Material You dark adoptado del prototipo `docs/UI-Prototype/` (SPEC §9.8):
  - `src/SmtpMockup.Web/Theme/MockupTheme.cs`: `MudTheme` con la paleta teal/azul, Inter y radios
    de 12px del export. Único sitio donde viven los colores que dibuja MudBlazor.
  - `wwwroot/app.css`: tokens `--md-*` del export + piezas del prototipo que MudBlazor no trae
    (barra superior, nav rail, bottom nav, tarjetas de estadísticas, snackbar, diálogo M3).
  - `MainLayout.razor`: shell del export — barra superior con `Web:Title`, nav rail de cuatro
    destinos (bottom nav ≤768px) y `<main id="main-content">`; antes la navegación vivía dentro del
    `MudAppBar` y desbordaba en pantallas estrechas.
- Accesibilidad: `<h1>` por página (habilita `FocusOnNavigate`), skip link, `<th scope="row">`,
  `aria-label` en controles de icono, `aria-live` en contadores, `fieldset`/`legend` en filtros,
  `aria-labelledby`/`describedby` + foco inicial en el diálogo, `prefers-reduced-motion`.
  Cubierto por `tests/SmtpMockup.Web.Tests/AccessibilityTests.cs`.
