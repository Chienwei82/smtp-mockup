## Qué cambia

<!-- Una frase. Si el cambio es observable desde fuera (la UI, el SMTP, la config), dilo aquí. -->

## Por qué

<!-- El problema que resuelve. Si cierra un punto del SPEC o de la deuda técnica del README, cita el número. -->

## Cómo lo he comprobado

<!-- Qué has ejecutado y qué has visto. El CI hace `dotnet build -warnaserror` + la suite entera
     en ubuntu y windows, y publica el zip de las dos plataformas; no hace falta que lo
     repitas, pero sí que digas cómo lo has probado en local. -->

## Checklist

- [ ] `python3 scripts/publish.py --zip` publica y verifica sin errores en local
- [ ] La suite está en verde (`dotnet test`)
- [ ] Actualizado `README.md` / `SPEC.md` si cambia algo de la interfaz pública o de la config
- [ ] Añadido un test que falla antes del cambio (o explico por qué no hacía falta)
- [ ] Sin secretos: ni claves, ni certificados, ni `data/` ni `publish/` en el diff