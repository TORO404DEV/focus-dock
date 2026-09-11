# Plan maestro: agente local de PomoDock

Documento vivo. Cada fase se marca al cerrarse con pruebas. El agente **no** muestra razonamiento interno crudo: solo información útil y verificable (qué entendió, qué consulta, qué plan propone, qué ejecuta, qué cambió y cómo deshacerlo).

**Integración con otros agentes (canvas, etc.):** ver `docs/HANDOFF_OTHER_AGENT.md`. No rehacer este plan; mergear la rama del agente local al final.

## Objetivo

Un asistente local que opera PomoDock con lenguaje natural: consultas de historial, mutaciones con aprobación, chat con streaming, nota de voz, conversaciones persistentes y memoria personal editable.

## Principios

1. **Local-first.** Datos en `%LOCALAPPDATA%\PomoDock`. LLM vía endpoint configurable (Ollama / API compatible con OpenAI).
2. **Determinismo donde importa.** Agregaciones de enfoque y herramientas de lectura no inventan cifras.
3. **Plan antes de mutar.** Lecturas pueden ir directas; escrituras/destructivas pasan por plan aprobable.
4. **Anti-bucle.** Huellas de herramienta + detector de estancamiento cortan repeticiones con explicación.
5. **Recibos y deshacer.** Toda mutación deja un recibo reversible cuando es posible.
6. **Transparencia útil.** Actividad visible ≠ chain-of-thought.

## Fases

| Fase | Nombre | Estado |
|---:|---|---|
| 0 | Fiabilidad y acceso real (`Store.Sessions`, `focus.summarize`, anti-bucle) | **Hecha** |
| 1 | Separar planificación y ejecución | **Hecha** |
| 2 | Mostrar plan: aprobar / editar / cancelar | **Hecha** |
| 3 | UI chat: streaming, actividad, recibos, deshacer | **Hecha** |
| 4 | Dictado como nota de voz (mantener / soltar / arrastrar cancelar) | **Hecha** |
| 5 | Persistir conversaciones | **Hecha** |
| 6 | Memoria personal local (`recuerda` / `olvida`, editable, exportable) | **Hecha** |
| 7 | Extender a todas las operaciones de PomoDock | **Hecha** (catálogo + host) |

## Fase 0 — Fiabilidad

### Problema
El agente no tenía herramienta sobre `Store.Sessions()`, así que no podía responder “¿cuántas horas enfoqué el último mes?” y repetía pasos.

### Entregables
- `focus.summarize` con periodos: `today`, `yesterday`, `this_week`, `last_week`, `this_month`, `last_month`, `all_time`, `custom`.
- Semana desde lunes; mes anterior = calendario completo anterior.
- Filtro exacto por proyecto; periodos vacíos → totales cero sin inventar.
- Clasificación de herramientas: `Read` | `Write` | `Destructive` | `Control`.
- `AgentLoopGuard`: corta consultas/mutaciones idénticas repetidas con mensaje claro.

### Criterios de aceptación
- Casos “último mes”, “esta semana”, proyecto, vacío e histórico real pasan en pruebas deterministas.
- Ante la pregunta de horas del último mes, el runtime elige `focus.summarize` / `last_month`.
- Un bucle repetido se corta sin duplicar la acción.

## Fase 1 — Plan vs ejecución

- El runtime produce un `AgentPlan` (pasos tipados) antes de mutar.
- Lecturas de una sola herramienta pueden ejecutarse sin plan formal.
- El plan es editable (quitar/reordenar pasos) antes de correr.

## Fase 2 — Aprobación

- UI de plan: **Aprobar**, **Editar**, **Cancelar**.
- Sin aprobación no hay mutaciones ni destructivas.
- Cancelar deja rastro en la conversación (“plan cancelado”).

## Fase 3 — Chat

- Panel de chat estilo conversación con streaming de respuesta.
- Carril de actividad: entendido → consultando → plan → ejecutando → recibo.
- Recibos con acción **Deshacer** cuando el host lo permite.
- Sin overlays de “pensamiento” crudo del modelo.

## Fase 4 — Nota de voz

- En el chat del agente: **mantener** para grabar, **soltar** para enviar, **arrastrar fuera** para cancelar.
- Un solo preview de transcripción (Whisper local), no insertar en otros campos.

## Fase 5 — Conversaciones

- Historial en estado local (`agent-conversations`).
- Abrir / continuar / borrar hilos.
- Tope razonable de mensajes por hilo con recorte FIFO del contexto LLM.

## Fase 6 — Memoria personal

- Hechos locales editables (`agent-memory`).
- Herramientas `memory.remember` / `memory.forget` / `memory.list`.
- Exportar / importar JSON; visible y editable en ajustes del agente.

## Fase 7 — Superficie completa

Herramientas mínimas sobre:
- Temporizador (iniciar / pausar / saltar / fase)
- Tareas y proyectos
- To Do
- Hábitos
- Notas (alta nivel)
- Agenda / recordatorios
- Informes / exportes
- Widgets / páginas (alta nivel)
- Ajustes no destructivos

Cada escritura con recibo; destructivas siempre con plan + aprobación.

## “Ver cómo piensa” (contrato de UI)

| Se muestra | No se muestra |
|---|---|
| Qué entendió | Tokens de reasoning internos |
| Qué información consulta | Prompts del sistema |
| Plan propuesto | Reintentos crudos del modelo |
| Acción en curso | Cadena de pensamiento |
| Recibo + deshacer | |

## Orden de trabajo

Implementar en orden 0 → 7. No abrir UI de chat hasta que Fase 0 tenga pruebas verdes. Versionar en `0.2.x` mientras el agente madura.
