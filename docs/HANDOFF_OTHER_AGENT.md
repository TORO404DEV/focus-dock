# Handoff para el otro agente (Canvas / integración)

## Contexto

Hay **dos líneas de trabajo** en paralelo sobre `focus-dock` / PomoDock:

| Agente | Rama / foco | Estado |
|---|---|---|
| **Agente local (fases 0–7)** | `cursor/local-agent-all-phases-2f4d` · PR https://github.com/TORO404DEV/focus-dock/pull/1 | **Listo**: Core + UI agente, CI verde, versionado **0.2.1** |
| **Canvas multidireccional** (u otros) | Su propia rama / cambios de canvas, status bar, teclado HWND, páginas | **En curso** — no pisar |

El agente del plan local **no debe mezclarse a la fuerza** con el canvas. Este documento es la instrucción para que **tú** (agente de canvas / integración) juntes todo al final.

## Qué ya está hecho (no rehacer)

En `cursor/local-agent-all-phases-2f4d`:

1. **Fase 0** — `focus.summarize` determinista + `AgentLoopGuard` + clasificación de tools  
2. **Fases 1–2** — plan / aprobación / editar / cancelar  
3. **Fases 3–4** — chat ✦, actividad, recibos, undo, nota de voz hold/release/drag-cancel  
4. **Fases 5–6** — conversaciones + memoria personal (Ajustes → Agente)  
5. **Fase 7** — catálogo de tools PomoDock (timer, tasks, todo, habits, agenda, notes, settings, workspace)  
6. Versión app/instalador: **0.2.1**  
7. Plan vivo: `docs/LOCAL_AGENT_PLAN.md`  
8. Tests Core: **112** pasando; CI Windows del PR en verde  

Archivos clave (namespace `PomoDock.Core.Agent` / `PomoDock.App`):

- `src/PomoDock.Core/Agent/*`
- `src/PomoDock.App/AgentChat.cs`, `AgentHostAdapter.cs`, `AgentVoiceNote.cs`
- Botón ✦ en `MainWindow.xaml` / wiring en `MainWindow.xaml.cs`
- Pestaña Agente en `SettingsWindow.cs`
- Strings `agent.*` / `settings.agent*` / `chrome.agent` en `es.json` / `en.json`
- Tests: `FocusSummaryTests.cs`, `AgentLoopGuardTests.cs`

## Qué debes hacer tú (integración final)

1. **Termina tu feature de canvas** en tu rama, con commits propios. No reviertas ni reescribas los archivos del agente salvo conflictos inevitables.
2. Cuando el canvas esté estable:
   ```bash
   git fetch origin
   git checkout <tu-rama-canvas>
   git merge origin/cursor/local-agent-all-phases-2f4d
   ```
   (o rebase, si el equipo lo prefiere; en conflictos, prioriza **comportamiento del canvas** en layout/páginas/HWND y **comportamiento del agente** en `Agent/*` + botón ✦ + settings Agente).
3. Resuelve conflictos típicos en:
   - `MainWindow.xaml` / `MainWindow.xaml.cs` (chrome: deja ✦ + tus controles de canvas)
   - `SettingsWindow.cs` (mantén pestaña `agent` + tus tabs)
   - `Strings/es.json` + `en.json` (unión de claves, no borrar `agent.*`)
   - `PomoDock.App.csproj` Version → deja **0.2.1** o súbela a **0.2.2** si añadiste más
4. Corre tests:
   ```bash
   dotnet run --project tests/PomoDock.Tests -c Release
   ```
5. **En Windows** (obligatorio para WPF):
   ```powershell
   # Cerrar PomoDock si está abierto
   Get-Process PomoDock -ErrorAction SilentlyContinue | Stop-Process -Force
   # Rebuild + instalador desk actual
   .\scripts\build-installer.ps1 -Version 0.2.1
   ```
   Salida esperada: `artifacts\PomoDock-Setup-0.2.1.exe`
6. Abre un PR de integración (canvas + agente) hacia `main`, o actualiza el PR #1 tras el merge, según convenga.
7. Smoke manual rápido:
   - Abrir ✦ → «¿cuántas horas enfoqué el último mes?» → respuesta con `focus.summarize`
   - Mutación («recuerda que…») → plan → Aprobar
   - Navegación canvas / páginas / widgets sin regresiones

## Qué NO hacer

- No reinicies Fase 0–7 desde cero.
- No cambies el contrato de `focus.summarize` ni el anti-bucle sin tests.
- No empujes a `main` el canvas sin el merge del agente si el usuario pidió “todo junto”.
- No toques el trabajo del otro agente mientras aún esté `RUNNING`; espera a que deje la rama estable.

## Contacto / PR del agente local

- PR: https://github.com/TORO404DEV/focus-dock/pull/1  
- Rama: `cursor/local-agent-all-phases-2f4d`  
- Plan: `docs/LOCAL_AGENT_PLAN.md`  
