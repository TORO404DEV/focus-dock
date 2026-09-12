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
| Informe (▥) | Abrir: `app.open_panel {"panel":"report"}`; lectura: `focus.*` / `get_state` | “Abre el informe” / “¿Cuántas horas el mes pasado?” |
| Tareas de enfoque (☷) | `app.open_panel {"panel":"tasks"}` + `add_focus_task`, `focus_tasks.*`, `timer`+`task`/`free` | “Enfoque libre” / “Marca hecha Docs” |
| Proyectos de enfoque | `focus_projects.list/add/rename/delete` | “Crea proyecto Blog” |
| Notificaciones | `app.open_panel {"panel":"notifications"}`, `notifications.list`, `notifications.dismiss` | “Abre notificaciones” |
| Ajustes | `app.open_panel {"panel":"settings"}`, `settings.read/update`, `settings.apply_rhythm`, `sounds.*`, `memory.*` | “Aplica ritmo deep” |
| Pantalla completa | `app.fullscreen` | “Activa pantalla completa” |
| Carpeta de datos | `app.open_data_folder` | “Abre la carpeta de datos” |
| + WIDGET | `add_widget` (`notes\|todo\|habits\|calendar\|stats\|timer\|finance\|web`) | “Añade widget finanzas” |
| + WIDGET ventana HWND | **Límite**: requiere picker visual | Agente lo declara |
| Minimizar / maximizar / cerrar | **Límite** (seguridad) | — |
| Layouts | `layouts.list/save/load/delete` + `app.open_panel {"panel":"layouts"}` | “Guarda layout Trabajo” |
| Dock páginas ↑↓←→ / + | `workspace.goto`, `workspace.add_page`, `workspace.rename_page`, `workspace.set_home` | “Ve a la página 2”, “llama página 3 casa”, “ve a casa” |

## Temporizador Pomodoro

| Acción | Tool |
|---|---|
| Start / pause / resume | `timer {"command":"start\|pause"}` |
| Focus / short / long | `timer {"command":"select","phase":"..."}` |
| Reset / skip | `timer {"command":"reset\|skip"}` |
| Elegir tarea / enfoque libre | `timer {"command":"start","task":"..."}` / `timer {"command":"free"}` |
| Añadir / quitar timer de página | `add_widget kind:timer` / `workspace.remove_timer` |
| Duración / ritmo | `set_focus_duration`, `settings.update`, `settings.apply_rhythm` |

## Widgets genéricos (chrome de card)

| Acción | Tool |
|---|---|
| Mover | `workspace.move_widget` (salta a la página del widget) |
| Redimensionar | `workspace.resize_widget` |
| Contraer / expandir | `workspace.collapse_widget` |
| Renombrar | `workspace.rename_widget` |
| Cerrar / quitar | `workspace.remove_widget` |
| Vaciar todos los de la página actual | `workspace.clear_widgets` |
| Traer al frente / focus | `workspace.focus` |
| Web: URL / reload / keep-alive | `workspace.set_web_url`, `workspace.reload_web`, `workspace.set_web_keepalive` |
| Window: crop / release / reconnect | **Límite HWND** |

## To Do

| Acción | Tool |
|---|---|
| Crear / completar / borrar / editar | `add_todo`, `complete_todo`, `delete_todo`, `todo.update` |
| Subir / bajar | `todo.move` |
| Buscar | `todo.search` |
| Ordenar por urgencia | `todo.sort` |
| Limpiar hechas | `todo.clear_done` |
| Filtros UI today/open/done | Lectura vía `todo.search` + `open` |

## Hábitos

| Acción | Tool |
|---|---|
| Crear / marcar / editar / archivar | `add_habit`, `mark_habit`, `habits.update` (cadence/days), `archive_habit` |
| Contador parcial / meta | `habits.set_count`, `habits.set_target` |
| Reordenar | `habits.move` |
| Restaurar archivado | `habits.restore` |
| Borrar para siempre | `habits.delete` |
| Heatmap / premios UI | Lectura parcial vía `habits.search` / `get_state` |

## Calendario

| Acción | Tool |
|---|---|
| Crear / actualizar / completar / borrar | `add_event`, `calendar.update`, `complete_event`, `delete_event` (`scope`: series\|occurrence) |
| Próximos / buscar | `calendar.upcoming`, `calendar.search` |
| Vistas month/week/agenda | `calendar.set_view` |
| Periodo prev/next/hoy | `calendar.navigate` |
| Mostrar/ocultar hechos | `calendar.set_show_done` |

## Finanzas

| Acción | Tool |
|---|---|
| Añadir NL / fijo / sub | `finance.add`, `finance.add_recurring` |
| Resumen / buscar | `finance.summary`, `finance.search` |
| Marcar pagado / borrar / archivar / vaciar gastos | `finance.mark_paid`, `finance.delete`, `finance.clear_expenses`, `finance.archive` |
| Moneda | `finance.set_currency` |

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

## Focus / informe

| Acción | Tool |
|---|---|
| Totales / sesiones / comparar | `focus.summarize`, `focus.query_sessions`, `focus.compare_periods` |
| Proyectos | `focus.list_projects` |
| Abrir panel informe | `app.open_panel {"panel":"report"}` |
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
| Voz / memoria agente | `agent_voice_*`, `agent_memory_enabled`, `agent_send_voice_on_release` |
| Packs de sonido | `sounds.list` + `sounds.set` |
| API key DeepSeek | **Límite** (secreto; solo UI de ajustes) |

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
| Minimizar / maximizar / cerrar proceso | Seguridad / chrome de ventana |
| Pegar/borrar API key DeepSeek vía chat | Secreto; solo UI de ajustes |

Cuando el usuario pida un límite: `finish` con explicación corta y la alternativa manual.

## Definición de hecho (objetivo “control total”)

- Cada acción manual del inventario apunta a una tool tipada **o** a la tabla de límites conscientes.
- `AgentCatalog.ModelActions` ⊆ switch de `AgentToolbox` (salvo `finish`).
- `AgentRules.md` documenta args que el executor realmente aplica.
- Mutaciones de nota/widget resuelven la página correcta (no solo la actual).
