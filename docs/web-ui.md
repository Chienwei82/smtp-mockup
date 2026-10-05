# UI web (Blazor Web App, Interactive Server)

## Qué es

La UI es una **Blazor Web App** sobre .NET 10 con el modo de renderizado **Interactive Server**.
Vive en su propio proyecto, `src/SmtpMockup.Web`, que es una *Razor Class Library*: aporta los
componentes, las páginas y el CSS, mientras que `SmtpMockup.Host` sigue siendo el composition root
(Kestrel, ciclo de vida, logging y apagado ordenado). Así la UI se puede probar con bUnit sin
arrancar un servidor.

- El proceso ya era el mismo (D-04): **no hay una segunda aplicación ni un puerto nuevo**.
- Los componentes **inyectan `IMessageStore` directamente**. No hay cliente HTTP, ni DTOs de
  transporte, ni CORS (D-06).
- El circuito de SignalR ya es el canal de las actualizaciones en vivo; no hace falta un hub.

## Páginas

| Ruta | Contenido |
|------|-----------|
| `/` | Listado paginado, filtros, selección múltiple y los tres borrados |
| `/messages/{id}` | Detalle por pestañas: Resumen, Texto, HTML, Headers, JSON, Adjuntos |
| `/messages/{id}?tab=HTML` | Abre el detalle en una pestaña concreta (enlace directo) |
| `/stats` | Conteo por día y por dominio destinatario |
| `/settings` | Configuración efectiva, uptime, escritura del storage, puertos |
| `/about` | Versión y endpoints activos |

## Listado

- Orden descendente por `receivedAtUtc` con desempate por id, paginado con
  `Web:DefaultPageSize` (50) y tope `Web:MaxPageSize` (200).
- Filtros combinados con AND: texto libre, remitente, destinatario, asunto, con/sin adjuntos y
  rango de fechas. Un rango invertido **no** devuelve cero resultados en silencio: muestra un
  aviso en la propia lista (SPEC §10.3).
- Borrar uno, borrar seleccionados y borrar todos. **Los tres piden confirmación** con un diálogo
  propio (sin JavaScript, comprobable con bUnit). «Borrar todos» aplica **el filtro activo**, no
  la página visible: es lo que espera quien acaba de filtrar por un remitente.
- La selección se poda al recargar: un id que ya no está en la página no sobrevive al cambio de
  filtro, para que «borrar seleccionados» no apunte a mensajes inexistentes.

## Detalle

- **Resumen**: sobre completo (HELO, IP/puerto, MAIL FROM, RCPT TO, transporte, TLS), direcciones
  con el Bcc recuperado del sobre, asunto crudo y decodificado.
- **Texto**: el `text/plain` en un `<pre>`.
- **HTML**: dentro de un `<iframe sandbox="" srcdoc="...">`. El sandbox va **vacío** a propósito:
  sin `allow-scripts` el correo no puede ejecutar nada ni navegar. Por encima de
  `Web:MaxHtmlPreviewBytes` el iframe se sustituye por el HTML como texto escapado, porque un
  `srcdoc` de 20 MB por el circuito deja la pestaña sin responder.
- **Headers**: crudos y en orden, tal como los entrega el cliente.
- **JSON**: el documento reserializado con las mismas reglas con las que lo escribió el store, para
  que lo que se ve sea lo que hay en el archivo.
- **Adjuntos**: nombre saneado, tipo, tamaño, SHA-256, marca de inline y de «no guardado»
  (`omitted: true`, cuando el adjunto supera `Storage:MaxInlineAttachmentBytes`).

## Actualización en vivo

```text
IMessageStore.Changed ─┐
                       ├─► MessageChangeNotifier (debounce Web:LiveUpdateDebounceMilliseconds)
FileSystemWatcher ─────┘                                        │
                                                                ▼
                                                        componentes InvokeAsync + StateHasChanged
```

- El evento del store se agrupa: una ráfaga de 50 correos produce **una** notificación.
- El aviso llega en un hilo ajeno al circuito (el del listener SMTP), así que el componente
  recarga con `InvokeAsync` **y llama a `StateHasChanged()`**: `InvokeAsync` sólo encola el trabajo
  en el dispatcher, no re-renderiza. Es el fallo más fácil de cometer y el que hace que la UI
  «funcione pero no se actualice».
- `FileSystemWatcher` cubre los cambios hechos **fuera** del proceso (copiar un JSON a mano,
  borrarlos con el explorador). Al detectar uno reconstruye el índice y avisa por el mismo camino.
  Dos cautelas, ambas aprendidas probando:
  - se **suprimen** los eventos provocados por el propio store (ventana de 1 s): si no, cada
    correo recibido recorrería el índice entero otra vez;
  - sólo se notifica si el conjunto de ids **cambió**: un rebuild que no altera nada no debe
    re-renderizar nada.
- Si `FileSystemWatcher` no se puede instalar (permisos, ruta de red, límite de inotify) se registra
  un warning y la app arranca igual.

## Descargas

Las descargas van por HTTP y **no** por el circuito: varios MB en base64 dentro de SignalR serían
peores que una petición normal. Son las únicas rutas HTTP del producto y **no son una API de
mensajes**: no sirven listados ni consultas, sólo los bytes de un archivo ya elegido en la UI.

| Ruta | Devuelve |
|------|----------|
| `/download/message/{id}/attachment/{index}` | El adjunto decodificado, con su `Content-Type` |
| `/download/message/{id}/raw` | El MIME crudo (`.eml`) si `Storage:KeepRawMime` lo conservó |

El nombre del archivo se sanea en el servidor (`Path.GetFileName` + recorte de separadores, y el
`\` también, que en Linux no es separador): un adjunto llamado `../../evil.exe` se descarga como
`evil.exe`.

## Configuración (`Web:*`)

| Clave | Por defecto | Para qué |
|-------|-------------|----------|
| `Enabled` | `true` | Si es `false` no se mapea la UI; el SMTP sigue funcionando |
| `Port` | `8080` | Puerto HTTP; `0` pide uno efímero y el log dice cuál se acabó usando |
| `BindAddress` | `127.0.0.1` | Literal IPv4/IPv6; fuera de loopback ⇒ warning en el log (no hay autenticación) |
| `DefaultPageSize` | `50` | Filas por página por defecto |
| `MaxPageSize` | `200` | Tope del selector de filas por página |
| `MaxHtmlPreviewBytes` | `2 MiB` | Por encima, el HTML se muestra como texto |
| `LiveUpdateDebounceMilliseconds` | `250` | Agrupación de ráfagas |
| `WatchDirectory` | `true` | Vigilancia del directorio con `FileSystemWatcher` |
| `ShowRawMimeDownload` | `true` | Botón de descarga del `.eml` |

Todas se validan al arrancar (`ValidateOnStart`), igual que el resto de secciones.

## Publicación: por qué `exe` + `wwwroot` y no un único archivo

La publicación **sí** es single-file para el ejecutable, pero los assets estáticos de Blazor
(`wwwroot/_framework`, `wwwroot/_content/MudBlazor`, `app.css`) viajan como archivos al lado.
Verificado con `dotnet publish -p:PublishProfile=win-x64`: un `smtp-mockup.exe` de 54 MB y 19
archivos en `wwwroot/`.

Motivo: `IncludeAllContentForSelfExtract` (la opción que metería el contenido dentro del exe)
extrae a `%TEMP%` en **cada arranque**. Como Windows Service eso es escribir en disco en cada
`Start-Service`, con una cuenta de servicio sin permisos sobre `%TEMP%` de los usuarios, y un
antivirus lo marca. El plan B es «exe + assets juntos», que es lo que hay;
`docs/hosting-modes.md` lo repite porque es el punto donde se rompe un despliegue.

**Trampa importante**: un Host `Microsoft.NET.Sdk.Web` **sin archivos `.razor` propios** no recibe
el paquete `Microsoft.AspNetCore.App.Internal.Assets`, y entonces la publicación se queda sin
`wwwroot/_framework/blazor.web.js`. La página se ve, pero no hay circuito: no se actualiza nada y
ningún botón responde. Por eso `SmtpMockup.Host.csproj` fija:

```xml
<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>
```

Es un fallo silencioso y desconcertante; la prueba de humo es
`curl -o /dev/null -w '%{http_code}' http://127.0.0.1:8080/_framework/blazor.web.js` ⇒ `200`.

## Pruebas

- **Lógica** (`dotnet test tests/SmtpMockup.Web.Tests`): paginación y validaciones del listado,
  modelo de filtros, debounce y agrupación del notificador, vigilancia del directorio, saneado de
  nombres y base64, límite de HTML y decodificación RFC 2047 del asunto.
- **Componentes (bUnit)**: el listado aplica filtros, se refresca sin recarga, pide confirmación
  antes de borrar y borra lo confirmado; el detalle muestra las seis pestañas, mete el HTML en el
  iframe sandbox, enlaza la descarga con el nombre saneado y avisa si el id de la ruta no es válido.
- `InvokeAsync` sin `StateHasChanged` es un fallo silencioso que ninguna prueba de markup detecta a
  simple vista: la prueba de «llega un mensaje con la lista abierta» lo cazó.
