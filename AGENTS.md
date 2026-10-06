# AGENTS.md — instrucciones para agentes

Notas de trabajo para cualquier agente (Cline y similares) que toque este repositorio. Son cortas y
obligatorias.

## Siempre: cierra los procesos que abres

- **Todo proceso `dotnet` que arranques, ciérralo tú al terminar.** No basta con que el comando
  «acabe»: `dotnet build`/`dotnet test`/`dotnet publish` dejan **vivos** servidores de nodos de MSBuild
  (`dotnet .../MSBuild.dll /nodemode:1 /nodeReuse:true`), y el mockup publicado es un proceso de larga
  duración. Al acabar la tarea (y antes de dar nada por hecho):
  - cierra los servidores de MSBuild: `dotnet build-server shutdown`
  - ojo: `dotnet build-server shutdown` **no siempre se lleva los nodos worker**
    (`dotnet .../MSBuild.dll /nodemode:1 /nodeReuse:true`); si siguen ahí, `pkill -x dotnet`
  - para el mockup: `pkill -x smtp-mockup`
  - compruébalo: `pgrep -a dotnet; pgrep -a smtp-mockup` (los dos deben salir vacíos).
- Si lanzas algo en segundo plano, **apunta el PID** (o el nombre del proceso) y mátalo al terminar. No
  dejes demonios vivos entre tareas: ocupan los puertos (8025/8443/8888) y el siguiente arranque falla
  con «dirección ya en uso».
- Ojo con `pkill -f`: el patrón puede coincidir con tu **propia** línea de comandos y matar el shell.
  Usa `pkill -x <nombre>` (coincidencia exacta del nombre del proceso).

## Convenciones del repositorio

- Documentación en **español**; identificadores de código en **inglés**.
- `SPEC.md` y `DESIGN.md` son la **fuente de verdad** y llevan registro de cambios: si tocas un
  comportamiento o un valor por defecto, actualízalos (con su entrada en el registro de cambios).
- `memory-bank/` es **scratch local** del agente: no está versionado (`.gitignore`) ni forma parte del
  repo. Si existe en tu máquina puedes usarlo para continuidad entre sesiones, pero **no** cuentes con
  él en un clon limpio ni lo exijas: la fuente de verdad son `SPEC.md`, `DESIGN.md`, `README.md` y
  `docs/`.
- Scripts en `scripts/`: Python **sin dependencias** (solo la biblioteca estándar) y multiplataforma.
- No se publica ni se hace commit con tests en rojo ni con avisos de compilación (`-warnaserror`).
