# Inventario de acciones PomoDock → agente

Documento de entrenamiento y cobertura: **cada acción manual** de PomoDock y la tool (o límite) del agente.
El prompt embebido del modelo es `src/PomoDock.App/AgentRules.md` (se regenera en el build como recurso).
Este archivo es la fuente humana completa; `AgentRules.md` es el subconjunto que el modelo debe memorizar.
Cerebro del chat: **DeepSeek API** (`deepseek-chat`). Datos y tools se ejecutan en el PC.

## Cómo “entrenar” al agente

No hay fine-tune. La inteligencia operativa sale de:

1. **`AgentRules.md`** — system prompt + catálogo tipado.
2. **`AgentCatalog` / `AgentGrammar`** — solo names válidos (GBNF).
3. **`AgentIntent` / `TryDirect`** — atajos sin LLM (rápidos).
4. **`AgentToolbox`** — ejecución real contra el estado vivo.
5. **Este inventario** — checklist de cobertura y ejemplos de frases.

Regla de oro: si el usuario puede hacerlo a mano, debe existir una tool o un mensaje explícito de límite (nunca fingir).

---

## Chrome / ventana principal

| Acción manual | Tool | Ejemplo |
|---|---|---|
| Abrir agente (✦) | `app.open_panel {"panel":"agent"}` | “Abre el agente” |
| Ocultar agente (✦ otra vez / × de la tarjeta) | `app.hide_agent` o `app.open_panel {"panel":"agent","open":false}` | “Cierra el chat” |
| Mover / redimensionar / contraer / llenar el chat | `workspace.move_widget` / `resize_widget` / `collapse_widget` / `fill_widget` con `{"kind":"agent"}` (no aparece en PANTALLA ACTUAL) | “Pon el chat a la izquierda” |
| Voz del chat (botón altavoz) | `settings.update {"agent_voice_enabled":true}` | “Activa la voz” |
| Escucha siempre activa (switch del chat; ✦ lleno = on, pulso solo al armar la orden o transcribir voz) | `settings.update {"agent_listen_enabled":true}` | “hey pomodock …” (el nombre por defecto es pomodock). El chat muestra un log de lo que oye el mic (música ignorada, transcripción, orden). La música de YouTube no debe hacer parpadear ✦. |
| Informe (▥) | Abrir: `app.open_panel {"panel":"report"}`; cerrar: `{"panel":"report","open":false}`; lectura: `focus.*` / `timer.inspect` / `get_state` | “Abre el informe” / “¿Cuántas horas el mes pasado?” / “Cierra el informe” |
| Tareas de enfoque (☷) | `app.open_panel {"panel":"tasks"}` + `add_focus_task`, `focus_tasks.*`, `timer`+`task`/`free`. Frecuentes: `focus_tasks.save_template` / `{"on":false}` para quitar | “Enfoque libre” / “Marca hecha Docs” / “Quita la plantilla Docs” |
| Proyectos de enfoque | `focus_projects.list/add/rename/delete` | “Crea proyecto Blog” |
| Notificaciones | Abrir: `app.open_panel {"panel":"notifications"}`; cerrar: `{"open":false}`; listar/leer: `notifications.list`, `notifications.dismiss` (sin id = todas + cierra toasts y banner) | “Abre notificaciones” / “Marca todas leídas” |
| Reloj del header (pausa/reanuda el pomodoro) | `timer {"command":"pause"}` / `{"command":"start"}` | “Pausa” |
| Ajustes | `app.open_panel {"panel":"settings"}`, `settings.read/update`, `settings.apply_rhythm`, `sounds.*`, `memory.*` | “Aplica ritmo deep” |
| Pantalla completa F11 de PomoDock | `app.fullscreen` | “Pon PomoDock a pantalla completa (F11)” |
| Arrastrar / redimensionar / doble clic título de la ventana PomoDock | **Límite** (chrome OS; mismo grupo que minimizar/maximizar/cerrar) | — |
| Llenar lienzo de un widget | `workspace.fill_widget` o `add_widget {...,"fill":true}` | “Ábrelo en pantalla completa” / el botón expandir de la tarjeta |
| Carpeta de datos | `app.open_data_folder` | “Abre la carpeta de datos” |
| + WIDGET | `add_widget` (`notes\|todo\|habits\|calendar\|stats\|timer\|finance\|memento\|web\|window`) | “Añade widget finanzas” / “añade memento mori” |
| + WIDGET ventana HWND | `add_widget {"kind":"window","app":"brave","url":"https://...","query":"lofi","fill":true}` | “Abre Brave en YouTube lofi a pantalla completa” (una `query` de YouTube abre el primer vídeo, no la lista) |
| Minimizar / maximizar / cerrar | **Límite** (seguridad) | — |
| Layouts | `layouts.list/save/load/delete` + `app.open_panel {"panel":"layouts"}`. El diálogo UI no borra; `layouts.delete` es del agente | “Guarda layout Trabajo” |
| Dock páginas ↑↓←→ / + | `workspace.goto`, `workspace.add_page`, `workspace.rename_page`, `workspace.set_home` | “Ve a la página 2”, “llama página 3 casa”, “ve a casa” |
| Mapa de páginas (Ctrl+M) | `app.open_panel {"panel":"page_map"}` (cerrar: `{"panel":"page_map","open":false}`). Clic = `workspace.goto`; clic derecho = `workspace.delete_page` | “Abre el mapa de páginas” |
| Chat del agente: hilo nuevo / historial / borrar conversación | **Límite** (chrome del propio chat; el agente opera en el hilo actual) | — |
| Chat: enviar / micrófono (mantener para hablar) | El propio turno: el usuario le habla al agente. No hay tool para “enviarse un mensaje” | — |
| Chat: confirmar / editar / cancelar un plan | Chrome del chat (el modelo ya espera “sí” / “no” en el hilo). **Límite**: no hay tool `confirm_plan` | — |
| Chat: reintentar / copiar error | **Límite** (chrome del chat) | — |
| Chat: deshacer el último resultado | Botón junto al recibo; no hay tool `undo` (evitar bucles). Pedir la mutación inversa. | — |
| Dictado por micrófono en un campo (tareas, notas, agenda…) | Equivale a escribir: el agente usa la tool de ese dominio (`add_todo`, `notes.append`, `add_event`…) | “Añade una tarea comprar pan” |

## Temporizador Pomodoro

| Acción | Tool |
|---|---|
| Start / pause / resume | `timer {"command":"start\|pause"}` (el reloj del header es lo mismo) |
| Focus / short / long | `timer {"command":"select","phase":"..."}` |
| Reset / skip | `timer {"command":"reset\|skip"}` |
| Posponer alerta de fin (toast 5 min) | `timer {"command":"snooze","minutes":5}` |
| Toast: empezar descanso / otro enfoque / empezar enfoque | `timer {"command":"select","phase":"..."}` luego `{"command":"start"}` |
| Toast × / callar aviso | `notifications.dismiss` (cierra toasts y banner; en alerta de timer también para la alarma). × solo visual no cambia la fase |
| Elegir tarea / enfoque libre | `timer {"command":"start","task":"..."}` / `timer {"command":"free"}` |
| Leer fase / restante | `timer.inspect` / `get_state {"scope":"timer"}` |
| Añadir / quitar timer de página | `add_widget kind:timer` / `workspace.remove_timer` |
| Llenar lienzo / restaurar | `workspace.fill_widget {"kind":"timer","fill":true\|false}` |
| Mover / redimensionar | `workspace.move_widget {"kind":"timer","x":40,"y":50}` / `workspace.resize_widget {"kind":"timer","width":430,"height":330}` |
| Duración / ritmo | `set_focus_duration`, `settings.update`, `settings.apply_rhythm` |

## Widgets genéricos (chrome de card)

| Acción | Tool |
|---|---|
| Mover | `workspace.move_widget` (salta a la página del widget) |
| Redimensionar | `workspace.resize_widget` |
| Contraer / expandir | `workspace.collapse_widget` |
| Llenar lienzo (botón expandir) | `workspace.fill_widget` / `add_widget fill:true` |
| Renombrar | `workspace.rename_widget` |
| Cerrar / quitar | `workspace.remove_widget` |
| Vaciar todos los de la página actual | `workspace.clear_widgets` |
| Traer al frente / focus | `workspace.focus` |
| Web: URL / reload / keep-alive | `workspace.set_web_url`, `workspace.reload_web`, `workspace.set_web_keepalive` |
| Window: ir a una URL (Brave/YouTube ya incrustado) | `workspace.set_web_url` (también navega el HWND) |
| Cambiar canción/vídeo | `workspace.play_youtube {"query":"canción artista"}` — si ya suena algo, **cierra** esa ventana incrustada y su widget y abre Brave de nuevo con el primer resultado; si no hay player, abre Brave. `fill:true` llena el lienzo. «Cambia la canción a X de Y» usa el primer resultado. |
| Window: crop / release / reconnect | Crop/release siguen siendo manuales; lanzar e incrustar: `add_widget kind:window` |
| Stats: meta diaria (menú de la tarjeta) | `settings.update {"daily_goal_minutes":120}` |

## To Do

| Acción | Tool |
|---|---|
| Crear / completar / borrar / editar | `add_todo`, `complete_todo`, `delete_todo`, `todo.update` |
| Subir / bajar | `todo.move` |
| Buscar | `todo.search` |
| Ordenar por urgencia | `todo.sort` |
| Limpiar hechas | `todo.clear_done` |
| Filtros UI today/open/done | `todo.set_filter {"filter":"today\|open\|done\|all"}` (persiste en la tarjeta). Lectura: `todo.search` |

## Hábitos

| Acción | Tool |
|---|---|
| Crear / marcar / editar / archivar | `add_habit`, `mark_habit`, `habits.update` (cadence/days), `archive_habit` |
| Contador parcial / meta | `habits.set_count`, `habits.set_target` |
| Reordenar | `habits.move` |
| Restaurar archivado | `habits.restore` |
| Borrar para siempre | `habits.delete` |
| Heatmap / premios UI | Lectura vía `habits.search` / `get_state`. Semana del heatmap: `habits.navigate {"to":"prev\|next\|today"}` |

## Calendario

| Acción | Tool |
|---|---|
| Crear / actualizar / completar / borrar | `add_event`, `calendar.update`, `complete_event`, `delete_event` (`scope`: series\|occurrence) |
| Próximos / buscar | `calendar.upcoming`, `calendar.search` |
| Vistas month/week/agenda | `calendar.set_view` |
| Periodo prev/next/hoy | `calendar.navigate` |
| Mostrar/ocultar hechos | `calendar.set_show_done` |
| Posponer toast de recordatorio (5/15 min) | `calendar.snooze {"title":"...","minutes":5}` |
| Completar desde el toast | `complete_event` |
| Cerrar toast (×) | No muta el evento (ya está entregado). Para ocultar: `notifications.dismiss`. Para posponer/completar: tools de arriba |

## Finanzas

| Acción | Tool |
|---|---|
| Añadir NL / fijo / sub | `finance.add`, `finance.add_recurring` |
| Resumen / buscar | `finance.summary`, `finance.search` |
| Marcar pagado / borrar / archivar / vaciar gastos | `finance.mark_paid`, `finance.delete`, `finance.clear_expenses`, `finance.archive` |
| Moneda | `finance.set_currency` |
| Tipo de cambio USD/MXN | `finance.set_rate {"usd_to_mxn":18.5}` o `{"refresh":true}` |
| Vista mes/semana/pestaña | `finance.set_view {"span":"month","tab":"moves\|fixed\|subs","to":"prev\|next\|today\|yyyy-MM"}` |

## Notas

| Acción | Tool |
|---|---|
| Crear con texto/color | `add_note` (presets o `#RRGGBB`) |
| Append texto | `notes.append` (sincroniza editor abierto) |
| Color post-it | `notes.set_color` |
| Reabrir del historial | `notes.reopen` |
| Olvidar del historial | `notes.forget` |
| Bold/listas/checklist ricos | **Límite**: editor WPF; tools usan texto plano |
| Historial / buscar | `notes.search` |
| Copiar texto del historial | Lectura: `notes.search`. Pegar en el portapapeles es chrome local |

## Focus / informe

| Acción | Tool |
|---|---|
| Totales / sesiones / comparar | `focus.summarize`, `focus.query_sessions`, `focus.compare_periods` (incluyen programas en primer plano) |
| Pestañas resumen/detalle y periodo semana/mes/año / prev-next | Equivale a `focus.summarize` / `focus.query_sessions` con `period` o `from`/`to` |
| Buscar en el detalle | `focus.query_sessions {"project":"..."}` |
| Proyectos | `focus.list_projects` |
| Abrir / cerrar panel informe | `app.open_panel {"panel":"report"}` / `{"open":false}` |
| Editar sesión / import CSV / export backup | **Límite UI** (ReportWindow) |

## Ajustes completos

| Acción | Tool (`settings.update` keys) |
|---|---|
| Ritmos con nombre | `settings.apply_rhythm` (`classic\|deep\|sprint\|marathon`) |
| Ritmos y meta | `focus_minutes`, `short_minutes`, `long_minutes`, `long_interval`, `daily_goal_minutes` |
| Auto break/focus | `auto_break`, `auto_focus` |
| Sonido / volúmenes | `sound`, `button_sounds`, `white_noise`, `alarm_*`, `*_volume` + `sounds.set` |
| Colores timer | `accent_color`, `focus_color`, `short_break_color`, `long_break_color` |
| Idioma / tema / motion / topmost | `language`, `dark`, `reduce_motion`, `always_on_top`, `timer_at_bottom` |
| Voz / memoria / escucha / nombre wake | `agent_voice_*`, `agent_memory_enabled`, `agent_send_voice_on_release`, `agent_listen_enabled`, `agent_wake_name` |
| Packs de sonido | `sounds.list` + `sounds.set` |
| API key DeepSeek | **Límite** (secreto; solo UI de ajustes) |
| Deshacer visita al panel de ajustes | Local al diálogo; el agente usa `settings.update` / `settings.apply_rhythm` |
| Cerrar ajustes / tareas / layouts abiertos | **Límite**: son `ShowDialog` y bloquean el hilo UI mientras están abiertos |

## Tareas de enfoque (panel ☷)

| Acción | Tool |
|---|---|
| Enfoque libre / elegir tarea y arrancar | `timer {"command":"free"}` / `timer {"command":"start","task":"..."}` |
| Alta / editar / hecha / borrar | `add_focus_task`, `focus_tasks.update`, `focus_tasks.set_done`, `focus_tasks.delete` |
| Frecuentes (estrella) | `focus_tasks.save_template` / `{"on":false}` / `focus_tasks.add_from_template` |
| Proyectos | `focus_projects.list/add/rename/delete` |
| Filtro por proyecto / buscar / mostrar hechas | Vista local del diálogo. Lectura y mutación: `focus_tasks.list` + tools de arriba (el filtro UI no persiste) |

## Memoria personal

| Acción | Tool |
|---|---|
| Buscar / proponer / confirmar / olvidar / exportar | `memory.search/propose/confirm/forget/export` |

## Workspace / páginas

| Acción | Tool |
|---|---|
| Listar widgets | `workspace.list` / `get_state` |
| Página nueva / ir / renombrar / casa / focus | `workspace.add_page`, `workspace.goto`, `workspace.rename_page`, `workspace.set_home`, `workspace.focus` |
| Borrar páginas vacías | `workspace.delete_empty_pages` |
| Borrar página concreta (con contenido) | `workspace.delete_page` |
| Vaciar widgets de la página | `workspace.clear_widgets` |

---

## Frases de regresión (producción)

1. “Crea una nota amarilla: comprar leche”
2. “Registra −20 chatgpt y +2500 salario”
3. “Añade widget web https://example.com”
4. “Renombra el widget NOTAS a Ideas”
5. “Contráelo y luego expándelo”
6. “Guarda layout Focus y cárgalo”
7. “Pon idioma es, dark true, always on top”
8. “Marca todas las notificaciones leídas”
9. “¿Cuántas horas enfoqué el último mes?”
10. “Añade texto a la nota abierta: leche deslactosada”
11. “Enfoque libre”
12. “Borra solo el lunes de la serie basura”
13. “Olvida la nota del historial comprar leche”
14. “Sube la tarea X”
15. “Pon el calendario en semana y ve al mes siguiente”
16. Pregunta vaga / larga: el agente **no debe cerrar la app**; soft-timeout + reintento.
17. “Abre el mapa de páginas”
18. “Pon el tipo de cambio a 19.25” / “actualiza el dólar”
19. “Muestra las hechas en el widget de tareas”
20. “Cierra el informe” / “oculta el chat”
21. “Pospón el recordatorio de la llamada 15 minutos”
22. “Quita la plantilla frecuente Docs”
23. “Marca las notificaciones leídas y cierra el aviso”

## Estabilidad del LLM

- Cerebro: **DeepSeek API** (`deepseek-chat`). Requiere API key (ajustes / setup del agente).
- Datos y tools viven en el PC. Sin llama.cpp / Vulkan en el camino del chat.
- Una sola vía: el modelo propone tools tipadas; el calendario “recuérdame…” usa el mismo `AgendaQuickAdd` que el input de agenda.
- `message` se escribe para voz alta (ver personalidad en `AgentRules.md`).

## Límites conscientes (no fingir)

| Capacidad | Por qué |
|---|---|
| Elegir HWND de ventana externa (+ crop/release/reconnect) | Requiere UI picker |
| Formato rich completo de notas (B/I/U, listas, checklist UI) | Editor WPF; tools usan texto plano |
| Editar sesiones del informe / import-export CSV-JSON | ReportWindow + mutación histórica |
| Minimizar / maximizar / cerrar proceso / arrastrar-redimensionar la ventana | Seguridad / chrome de ventana |
| Pegar/borrar API key DeepSeek vía chat | Secreto; solo UI de ajustes |
| Hilos del chat (nuevo, historial, borrar conversación) | Chrome del propio agente; opera en el hilo actual |
| Botón deshacer junto a un recibo del chat | Evita bucles; pedir la mutación inversa |
| Enviar / micrófono / confirmar-editar-cancelar plan / reintentar error | Chrome del propio chat (el turno *es* esa acción) |
| Cerrar ajustes, tareas de enfoque o layouts mientras el diálogo está abierto | `ShowDialog` bloquea el hilo UI; el usuario cierra con × |

Cuando el usuario pida un límite: `finish` con explicación corta y la alternativa manual.

## Definición de hecho (objetivo “control total”)

- Cada acción manual del inventario apunta a una tool tipada **o** a la tabla de límites conscientes.
- `AgentCatalog.ModelActions` ⊆ switch de `AgentToolbox` (salvo `finish`).
- `AgentRules.md` documenta args que el executor realmente aplica.
- Mutaciones de nota/widget resuelven la página correcta (no solo la actual).
