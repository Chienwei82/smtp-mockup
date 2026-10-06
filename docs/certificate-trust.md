# Confiar el certificado de smtp-mockup

El listener de 8443 **anuncia `STARTTLS`**: el cliente conecta en claro, ve la extensión en el
EHLO y decide si actualiza la conexión. El certificado que presenta sale de `Certificate:*`. Si no
hay ninguno configurado y `Certificate:AutoGenerateSelfSigned` es `true`, el mockup genera uno
autofirmado (`CN=localhost`, SAN `localhost` / `127.0.0.1` / `::1`) y lo guarda en
`certs/dev.pfx`, **junto al ejecutable** (las rutas relativas se resuelven contra el directorio
del binario, no contra el directorio de trabajo). Los arranques siguientes reutilizan ese PFX,
así que el thumbprint no cambia y el cliente puede cachear la excepción de confianza.

Un certificado autofirmado no está en ninguna cadena de confianza: hasta que no lo confiás
explícitamente, cada cliente SMTP fallará el handshake con un error de validación. Esta página
explica cómo resolverlo **en Windows**, que es donde el almacén de confianza se usa de verdad.

> Con `Smtp:StartTls:Security=Implicit` el mismo puerto se comporta como SMTPS (el socket se
> acepta ya cifrado, sin comando `STARTTLS`). La confianza del certificado es la misma; lo único
> que cambia es que el cliente no necesita hacer nada para cifrar.

## 1. Averiguar qué certificado está en uso

El log de arranque dice exactamente cuál se presentó:

```
CertificateResolved mode=Auto source=GeneratedSelfSigned subject=CN=localhost thumbprint=B205070B0616210FF0A1739AC56D378C287B5364 notAfter=2027-10-02T23:21:03.0000000+00:00
```

`source` puede ser:

| `source` | Significado |
|----------|-------------|
| `DevelopmentCertificate` | Se está usando el certificado de `dotnet dev-certs https` (ya confiable en la mayoría de las máquinas de desarrollo) |
| `PersistedFile` | Se cargó el PFX de `Certificate:Path` |
| `GeneratedSelfSigned` | Se generó ahora y se persistió en `Certificate:Path` |

Si `source` es `DevelopmentCertificate`, no tenés que hacer nada: `dotnet dev-certs https --trust`
ya lo instaló en el almacén de confianza del usuario.

## 2. Exportar el PFX

El archivo ya está en disco: `certs\dev.pfx` junto al `smtp-mockup.exe` (o donde apunte
`Certificate:Path`). La contraseña es `Certificate:Password`, vacía por defecto.

Para ver el thumbprint desde PowerShell:

```powershell
Get-PfxCertificate .\certs\dev.pfx | Select-Object Subject, Thumbprint, NotAfter
```

## 3. Confiarlo en Windows

### Opción A — PowerShell, almacén "CurrentUser" (recomendada)

Suficiente para el usuario que ejecuta el mockup y para sus clientes. No pide permisos
administrativos y no altera el almacén de la máquina.

```powershell
# Requiere el PFX en un archivo; se pide la contraseña si la tiene.
Import-Certificate -FilePath .\certs\dev.pfx `
    -CertStoreLocation Cert:\CurrentUser\Root
```

Un certificado autofirmado en **Root** se considera confiable: el diálogo de advertencia de
Windows aparece igual la primera vez en algunos clientes, pero los clientes .NET (MailKit,
`System.Net.Mail`, `HttpClient`) y las herramientas de línea de comandos pasan sin intervención.

### Opción B — consola de certificados (mmc), paso a paso

1. `Win+R` → `certmgr.msc` → Enter.
2. En **Personal** → **Certificados**, clic derecho → **Importar…**.
3. Seleccioná `certs\dev.pfx`. Si tiene contraseña, escribila; si no, dejala vacía.
4. En **Colocar todos los certificados en el siguiente almacén**, elegí **Entidades de
   certificación raíz de confianza**.
5. **Finalizar**. Aparece el aviso "so está a punto de instalar un certificado de una entidad de
   certificación raíz de confianza…": confirmá con **Sí** — sólo lo hacés porque el generaste vos.

Repetí los pasos 1–5 en **Computadora** (`certlm.msc`) sólo si el proceso corre como
**Windows Service** bajo otra cuenta o si querés que la confianza sea para todos los usuarios.

### Opción C — sólo para un servicio (CurrentUser\My no alcanza)

Si el mockup corre como servicio y el cliente también, importalo en
`certlm.msc` → **Personal** → **Certificados** además de Root, para que el proceso pueda
presentarlo.

## 4. Comprobar que funciona

Lo más simple es el propio handshake STARTTLS:

```powershell
openssl s_client -connect localhost:8443 -starttls smtp -servername localhost
```

Salida esperada cuando el certificado está confiado:

```
depth=0 ... verify return code: 0 (ok)
subject=CN=localhost
```

Con `verify return code: 18 (self-signed certificate)` el handshake funciona pero el certificado
no está en la cadena de confianza: hay que importarlo como en §3. Equivalente en .NET sin
desactivar la validación:

```csharp
using var tcp = new System.Net.Sockets.TcpClient("localhost", 8443);
await using var ssl = new System.Net.Security.SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
await ssl.AuthenticateAsClient("localhost");   // lanza AuthenticationException si no está confiado
```

Si `AuthenticateAsClient` lanza `AuthenticationException`, el certificado todavía no está en el
almacén que usa **ese** proceso: revisá si importaste en `CurrentUser` o en `LocalMachine`, y
recordá que un servicio usa la cuenta con la que corre, no la tuya.

Desde un cliente .NET, el flujo completo:

```csharp
using var client = new SmtpClient();          // MailKit
await client.ConnectAsync("localhost", 8443, SecureSocketOptions.StartTls);
// El cliente ve STARTTLS en el EHLO, actualiza y valida el certificado contra el almacén:
// si está confiado, el handshake pasa sin callback de validación alguno.
```

## 4.1 Limitación conocida: Windows Service con cuenta LocalSystem

`dotnet dev-certs https` instala el certificado de desarrollo en el almacén **`CurrentUser\My`**
del usuario que ejecuta el comando. Un Windows Service registrado con la cuenta por defecto
(`LocalSystem`) **no ve ese almacén**: el suyo es el de `SYSTEM`, que normalmente está vacío.

Consecuencia práctica: el mismo binario presenta **certificados distintos** según cómo se
arranque.

| Cómo se arranca | `source` esperado | Qué hacer |
|-----------------|-------------------|-----------|
| Consola / `dotnet run` con tu usuario | `DevelopmentCertificate` | Nada: `dotnet dev-certs https --trust` ya lo confió |
| Servicio como LocalSystem | `GeneratedSelfSigned` (o `PersistedFile`) | Confiar **ese** certificado, no el del usuario (§3, idealmente en `LocalMachine`) |

No es un fallo del mockup: cuando el dev-certs no está disponible (o el almacén no es accesible),
`CertificateProvider` cae al certificado autogenerado y lo persiste en `certs\dev.pfx`. Lo que
**sí** cambia entre entornos es el thumbprint, así que conviene:

1. **Fijar el certificado con `Certificate:Mode=File`** si querés que sea el mismo sin importar
   la cuenta con la que corra el servicio — es la opción recomendada para instalación.
2. Si dejás `Mode=Auto`, verificá el `thumbprint` en el log del servicio y confiá ese, en
   `LocalMachine` (con permisos de administrador) para que todos los clientes lo acepten.
3. Recordá que el servicio corre con su propio directorio de trabajo: por eso `Certificate:Path`
   relativo se resuelve contra la carpeta del ejecutable, y el PFX debe estar junto al `.exe`.


## 5. Linux

- **Linux** — el almacén del sistema es de todo el sistema, así que lo habitual es que el
  cliente acepte explícitamente el certificado en vez de tocar `/usr/local/share/ca-certificates`
  (que requiere permisos y dejaría el mockup confiable para todos). En los tests de este repo el
  cliente MailKit acepta el certificado con `ServerCertificateValidationCallback`, que es el
  equivalente de "confiar en este certificado para esta aplicación".

## 6. Si preferís un certificado propio

En vez de confiar el autofirmado, se puede apuntar a un PFX real:

```json
{
  "Certificate": {
    "Mode": "File",
    "Path": "certs/mi-certificado.pfx",
    "Password": "s3cret"
  }
}
```

En modo `File` la carga es estricta: si el archivo no existe o la contraseña es incorrecta, el
proceso **falla al arrancar** con un mensaje que nombra la ruta, en vez de generar uno nuevo.
