# Memory Bank (local) — Cline

> **Ámbito.** La carpeta `memory-bank/` es **scratch local** del agente: **no se versiona** (está en
> `.gitignore`) ni **forma parte del repositorio**. En un clon limpio **no existe**: no la exijas, no
> la añadas al repo y no cuentes con ella para completar una tarea.
>
> La **fuente de verdad** son los documentos versionados: `SPEC.md` (alcance), `DESIGN.md`
> (decisiones técnicas), `README.md` (punto de entrada) y `docs/`. Esos sí hay que mantenerlos al día.

Mi memoria se reinicia por completo entre sesiones. Si —y solo si— `memory-bank/` existe en esta
máquina, es mi única continuidad entre sesiones: lo leo al empezar la tarea y lo mantengo actualizado.
Si no existe, trabajo únicamente con los documentos versionados y no lo echo en falta.

## Estructura (solo si la carpeta existe localmente)

El memory bank son ficheros Markdown —de núcleo y de contexto opcional— que se construyen unos sobre
otros en una jerarquía clara:

### Ficheros de núcleo
1. `projectbrief.md`
   - Documento base del que parten todos los demás.
   - Se crea al arrancar el proyecto si no existe.
   - Define requisitos y objetivos de núcleo.
   - Fuente de verdad del alcance del proyecto.

2. `productContext.md`
   - Por qué existe el proyecto.
   - Problemas que resuelve.
   - Cómo debería funcionar.
   - Objetivos de experiencia de usuario.

3. `activeContext.md`
   - Foco de trabajo actual.
   - Cambios recientes.
   - Próximos pasos.
   - Decisiones y consideraciones activas.
   - Patrones y preferencias importantes.
   - Aprendizajes y hallazgos del proyecto.

4. `systemPatterns.md`
   - Arquitectura del sistema.
   - Decisiones técnicas clave.
   - Patrones de diseño en uso.
   - Relaciones entre componentes.
   - Rutas de implementación críticas.

5. `techContext.md`
   - Tecnologías usadas.
   - Configuración del entorno de desarrollo.
   - Restricciones técnicas.
   - Dependencias.
   - Patrones de uso de herramientas.

6. `progress.md`
   - Qué funciona.
   - Estado actual.
   - Problemas conocidos.
   - Evolución de las decisiones del proyecto.

### Contexto adicional
Crea ficheros o carpetas adicionales dentro de `memory-bank/` cuando ayuden a organizar:
- Documentación de funcionalidades complejas.
- Especificaciones de integración.
- Documentación de API.
- Estrategias de pruebas.
- Procedimientos de despliegue.
Todo esto es local; **no se versiona**.

## Cuándo se actualiza

El memory bank (si existe) se actualiza cuando:
1. Se descubre un patrón nuevo del proyecto.
2. Se implementa un cambio significativo.
3. El usuario pide **update memory bank** (revisar TODOS los ficheros).
4. Hace falta aclarar contexto.

RECUERDA: tras cada reinicio de memoria empiezo de cero. El memory bank es mi único enlace con el
trabajo anterior, así que debe mantenerse con precisión. Pero es **local**: sin él, `SPEC.md`,
`DESIGN.md`, `README.md` y `docs/` siguen siendo la fuente de verdad y la tarea no se bloquea.
