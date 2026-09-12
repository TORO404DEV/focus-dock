using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PomoDock.Core;

namespace PomoDock.App;

internal sealed record AgentRunResult(string Message, IReadOnlyList<AgentActionReceipt> Receipts, AgentPlan? Plan = null, bool NeedsApproval = false);

/// <summary>
/// Observe, plan, then act. Queries run immediately. Writes wait for an approved <see cref="AgentPlan"/>.
/// Generated prose never touches the database.
/// </summary>
internal sealed class PomoAgent
{
    private readonly MainWindow owner;
    private readonly Func<string, IAgentConversation> beginConversation;
    private readonly AgentToolbox tools;
    private readonly AgentMemoryStore memory;
    private IAgentConversation? session;
    private string sessionPrompt = "";

    public PomoAgent(MainWindow owner, LocalAgentModel model) : this(owner, model.Begin) { }

    public PomoAgent(MainWindow owner, Settings settings)
        : this(owner, prompt => DeepSeekAgentModel.Begin(settings, prompt)) { }

    internal PomoAgent(MainWindow owner, Func<string, IAgentConversation> beginConversation)
    {
        this.owner = owner;
        this.beginConversation = beginConversation;
        tools = new AgentToolbox(owner);
        memory = new AgentMemoryStore(owner.Store);
    }

    public async Task<AgentRunResult> InterpretAsync(
        string request,
        IReadOnlyList<AgentChatMessage> history,
        IProgress<AgentNotice>? activity,
        CancellationToken cancellationToken)
    {
        request = request.Trim();
        bool useDeepSeek = DeepSeekAgentModel.IsConfigured(owner.Settings);

        // Same AgendaQuickAdd path as the calendar's natural-language field — never leave
        // "recuérdame … en un minuto" to the LLM (it invents todos on the wrong day).
        if (TryScheduleDirect(request, out var scheduledPlan))
            return await FinishDirect(scheduledPlan, activity, cancellationToken);

        if (AgentIntent.IsUpcomingCalendarQuery(request))
        {
            activity?.Report(new AgentNotice(AgentEventKind.Reading, Es() ? "Consultando · CALENDARIO" : "Reading · CALENDAR"));
            using var arguments = JsonDocument.Parse("{\"days\":3650,\"limit\":10,\"include_done\":false}");
            var result = await tools.ExecuteAsync("calendar.upcoming", arguments.RootElement, cancellationToken);
            return new AgentRunResult(result.Summary, result.Success ? [result.Receipt("calendar.upcoming")] : []);
        }

        // Regex shortcuts stay only as a fallback when there is no API key.
        if (!useDeepSeek)
        {
            if (TryDirect(request, out var direct))
                return await FinishDirect(direct, activity, cancellationToken);

            if (AgentIntent.IsStandaloneFocusQuestion(request) && FocusHistory.TryGuessPeriod(request, out var period))
            {
                activity?.Report(new AgentNotice(AgentEventKind.Reading, L.T("agent.consultingFocus")));
                var sessions = owner.Store.Sessions();
                var zone = TimeZoneInfo.Local;
                var today = DateOnly.FromDateTime(DateTime.Now);
                var range = FocusHistory.Resolve(period, today, earliest: FocusHistory.EarliestDay(sessions, zone));
                var summary = FocusHistory.Summarize(sessions, range, zone);
                var seeded = await tools.ExecuteAsync("focus.summarize", JsonDocument.Parse($"{{\"period\":\"{period}\"}}").RootElement, cancellationToken);
                return new AgentRunResult(FocusHistory.Describe(summary, Es()), [seeded.Receipt("focus.summarize")]);
            }
        }

        var pending = new List<AgentPlanStep>();
        var queryReceipts = new List<AgentActionReceipt>();
        var guard = new AgentLoopGuard();
        string lastSummary = "";
        string understood = request.Length <= 80 ? request : request[..80] + "…";

        var conversation = Session();
        // Always inject the UI thread. The API session alone is not enough: it can be reset on
        // day/hour boundaries, and follow-ups like "cuáles son" need the prior topic.
        string input = Opening(request, history, null);

        for (int step = 0; step < 6; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity?.Report(new AgentNotice(AgentEventKind.Interpreting, step == 0 ? L.T("agent.liveWait") : L.T("agent.liveWaitStep", step + 1)));
            string lastLive = "";
            var tokens = new Progress<string>(chunk =>
            {
                if (chunk.Length == 0) return;
                if (chunk == "\u0001")
                {
                    activity?.Report(new AgentNotice(AgentEventKind.Interpreting, L.T("agent.liveWait")));
                    return;
                }
                if (chunk == lastLive) return;
                lastLive = chunk;
                activity?.Report(new AgentNotice(AgentEventKind.AnswerDelta, chunk));
            });
            string raw;
            try
            {
                raw = AgentThink.Strip(await conversation.AskAsync(input, tokens, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                ResetSession();
                throw;
            }
            catch (OperationCanceledException)
            {
                ResetSession();
                return Stop(queryReceipts,
                    "DeepSeek tardó demasiado. Reintenta o dile la orden en una frase concreta.",
                    "DeepSeek took too long. Retry or say the order in one concrete sentence.");
            }
            catch (Exception ex)
            {
                ResetSession();
                try { System.IO.File.AppendAllText(System.IO.Path.Combine(owner.Store.DirectoryPath, "errors.log"), $"{DateTimeOffset.Now:O} agent-llm {ex}\n"); } catch { }
                // Never fall back to brittle regex writes when DeepSeek is the brain — that created
                // finance rows titled with the whole user sentence.
                if (!useDeepSeek && TryDirect(request, out var recovered))
                    return await FinishDirect(recovered, activity, cancellationToken);
                string detail = Trim(ex.Message, 180);
                return Stop(queryReceipts,
                    $"DeepSeek no pudo responder ({detail}). Revisa la API key, el saldo o la red y reintenta.",
                    $"DeepSeek could not answer ({detail}). Check the API key, balance, or network and retry.");
            }
            if (conversation is LocalAgentModel.Conversation live && live.IsCorrupt)
                ResetSession();
            if (!AgentJson.TryReadTurn(raw, out var turn, out var error))
            {
                if (guard.NoteMalformed())
                {
                    if (!useDeepSeek && TryDirect(request, out var recovered))
                        return await FinishDirect(recovered, activity, cancellationToken);
                    return Stop(queryReceipts,
                        "No pude leer una acción válida. Dime la orden en una frase, por ejemplo: crea una nota amarilla con los hábitos de hoy.",
                        "I could not read a valid action. Say the order in one sentence, for example: create a yellow note with today's habits.");
                }
                input = $"""
                    Tu respuesta no cumplió el protocolo ({error}). Devuelve únicamente un objeto JSON válido,
                    sin Markdown ni explicación, con action y arguments. Respuesta anterior: {raw}
                    """;
                continue;
            }
            guard.NoteValid();
            string action = turn.Action.Trim().ToLowerInvariant();
            if (action is "finish" or "plan")
                return await FinishInterpret(request, turn, understood, pending, queryReceipts, activity, cancellationToken);
            if (!AgentCatalog.Exists(action))
            {
                input = $"""
                    '{action}' no existe. No inventes herramientas.
                    Páginas del workspace: workspace.list, workspace.add_page, workspace.rename_page, workspace.set_home, workspace.goto, workspace.focus, workspace.delete_empty_pages.
                    Página ≠ widget. Para renombrar una página usa workspace.rename_page, nunca workspace.rename_widget.
                    Una pantalla vacía es una página sin widgets ni temporizador. Para borrarlas usa workspace.delete_empty_pages, nunca delete_widget.
                    """;
                continue;
            }

            var risk = AgentCatalog.RiskOf(action);
            if (guard.IsRepeat(action, turn.Arguments, risk, out var prior))
            {
                if (guard.TooManyRepeats())
                    return pending.Count > 0
                        ? await FinishInterpret(request, turn, understood, pending, queryReceipts, activity, cancellationToken)
                        : Stop(queryReceipts,
                            $"Me detuve porque el modelo intentó repetir '{action}' después de recibir su resultado.",
                            $"I stopped because the model tried to repeat '{action}' after receiving its result.");
                input = $"""
                    ACCIÓN BLOQUEADA: {action} ya se llamó con los mismos argumentos.
                    RESULTADO YA DISPONIBLE: {prior}
                    No la repitas. Termina con action=finish si ya puedes responder, o elige una herramienta diferente.
                    """;
                continue;
            }

            if (risk != AgentRisk.Read)
            {
                pending.Add(new AgentPlanStep
                {
                    Tool = action,
                    ArgumentsJson = turn.Arguments.ValueKind == JsonValueKind.Object ? turn.Arguments.GetRawText() : "{}",
                    Risk = risk,
                    Label = AgentCatalog.Describe(action, turn.Arguments.ValueKind == JsonValueKind.Object ? turn.Arguments.GetRawText() : "{}", turn.Message)
                });
                guard.Remember(action, turn.Arguments, risk, "{\"success\":true,\"proposed\":true}", false, true);
                input = $"""
                    PROPUESTA REGISTRADA, no ejecutada: {action} {turn.Arguments}.
                    Sigue proponiendo pasos o termina con action=finish y un resumen del plan.
                    """;
                continue;
            }

            activity?.Report(new AgentNotice(AgentEventKind.Reading, $"{L.T("agent.consulting")} · {action.ToUpperInvariant()}"));
            var result = await tools.ExecuteAsync(action, turn.Arguments, cancellationToken);
            guard.Remember(action, turn.Arguments, risk, result.Json, result.Changed, result.Success);
            lastSummary = result.Summary;
            if (result.Success) queryReceipts.Add(result.Receipt(action));
            input = $"""
                RESULTADO DE {turn.Action}: {result.Json}
                Continúa. No repitas una consulta que ya tuvo success=true.
                Si ya puedes responder, action=finish. Si hay que cambiar datos, propón las herramientas de escritura y luego finish.
                """;
        }

        if (pending.Count > 0)
            return await FinishInterpret(request, new AgentModelTurn { Action = "finish", Message = lastSummary, Understood = understood }, understood, pending, queryReceipts, activity, cancellationToken);
        return Stop(queryReceipts,
            $"No pude cerrar la orden dentro del presupuesto seguro. Último resultado: {(string.IsNullOrWhiteSpace(lastSummary) ? "ninguno" : lastSummary)}.",
            $"I could not finish within the safe action budget. Last result: {(string.IsNullOrWhiteSpace(lastSummary) ? "none" : lastSummary)}.");
    }

    public async Task<AgentRunResult> ExecuteAsync(AgentPlan plan, IProgress<AgentNotice>? activity, CancellationToken cancellationToken)
    {
        var receipts = new List<AgentActionReceipt>();
        // One EJECUTAR / "sí" already approved the whole plan — do not re-ask per item.
        tools.PlanApproved = true;
        owner.AgentPlanApproved = true;
        try
        {
            for (int i = 0; i < plan.Steps.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var step = plan.Steps[i];
                activity?.Report(new AgentNotice(AgentEventKind.Executing, L.T("agent.executingStep", i + 1, plan.Steps.Count, step.Label.ToUpperInvariant())));
                using var args = JsonDocument.Parse(string.IsNullOrWhiteSpace(step.ArgumentsJson) ? "{}" : step.ArgumentsJson);
                var result = await tools.ExecuteAsync(step.Tool, args.RootElement, cancellationToken);
                if (result.Changed || result.Success) receipts.Add(result.Receipt(step.Tool));
                if (!result.Success && step.Risk == AgentRisk.Destructive)
                    return new AgentRunResult(result.Summary, receipts);
                await Task.Delay(plan.Steps.Count > 1 ? 450 : 280, cancellationToken);
            }

            string summary = AgentReply.After(plan.Message, receipts, Es());
            activity?.Report(new AgentNotice(AgentEventKind.Done, L.T("agent.ready")));
            return new AgentRunResult(summary, receipts);
        }
        finally
        {
            tools.PlanApproved = false;
            owner.AgentPlanApproved = false;
        }
    }

    public async Task<AgentRunResult> UndoAsync(AgentActionReceipt receipt, CancellationToken cancellationToken)
    {
        if (!receipt.CanUndo) return new AgentRunResult(Es() ? "Esa acción no se puede deshacer." : "That action cannot be undone.", []);
        using var args = JsonDocument.Parse(receipt.UndoArgumentsJson ?? "{}");
        var result = await tools.ExecuteAsync(receipt.UndoTool!, args.RootElement, cancellationToken);
        return new AgentRunResult(result.Summary, result.Success ? [result.Receipt(receipt.UndoTool!)] : []);
    }

    private async Task<AgentRunResult> FinishDirect(AgentPlan plan, IProgress<AgentNotice>? activity, CancellationToken cancellationToken)
    {
        if (plan.CanAutoRun)
            return await ExecuteAsync(plan, activity, cancellationToken);
        activity?.Report(new AgentNotice(AgentEventKind.Done, L.T("agent.ready")));
        return new AgentRunResult(plan.Message, [], plan, plan.HasMutations);
    }

    private async Task<AgentRunResult> FinishInterpret(
        string request,
        AgentModelTurn turn,
        string understood,
        List<AgentPlanStep> pending,
        List<AgentActionReceipt> queries,
        IProgress<AgentNotice>? activity,
        CancellationToken cancellationToken)
    {
        if (pending.Count == 0 && AgentIntent.LooksLikeWrite(request))
        {
            if (!DeepSeekAgentModel.IsConfigured(owner.Settings)
                && TryDirect(request, out var recovered) && recovered.HasMutations)
                return await FinishDirect(recovered, activity, cancellationToken);
            return Stop([],
                "No hice ese cambio. Dímelo en una frase concreta, por ejemplo: crea una nota amarilla en esta página.",
                "I did not make that change. Say it in one concrete sentence, for example: create a yellow note on this page.");
        }
        string message = string.IsNullOrWhiteSpace(turn.Message)
            ? (pending.Count == 0 ? (Es() ? "Listo." : "Done.") : (Es() ? "Plan listo." : "Plan ready."))
            : turn.Message.Trim();
        if (pending.Count == 0 && queries.Count > 0)
            message = EnrichBareAnswer(message, queries);
        string said = string.IsNullOrWhiteSpace(turn.Understood) ? understood : turn.Understood.Trim();
        if (said.Length < 12 || said is "true" or "false" or "ok" or "yes")
            said = pending.Count == 0 ? message : (Es() ? "Voy a: " : "I will: ") + string.Join(Es() ? "; " : "; ", pending.Select(step => step.Label));
        var plan = new AgentPlan
        {
            Understood = said,
            Message = message,
            Steps = pending,
            Risk = pending.Count == 0 ? AgentRisk.Read : pending.Max(step => step.Risk),
            Changes = pending.Count == 0 ? "" : (Es() ? $"{pending.Count} cambio(s) previsto(s)" : $"{pending.Count} planned change(s)")
        };
        if (plan.CanAutoRun)
            return await ExecuteAsync(plan, activity, cancellationToken);
        // Destructive / approval plans must never speak or store a "already done" message.
        if (plan.HasMutations && !AgentReply.AsksConfirmation(plan.Message))
            plan.Message = AgentReply.Pending(plan, Es());
        IReadOnlyList<AgentActionReceipt> receipts = pending.Count > 0 ? [] : queries;
        return new AgentRunResult(plan.Message, receipts, plan, plan.HasMutations);
    }

    private static AgentRunResult Stop(IReadOnlyList<AgentActionReceipt> receipts, string spanish, string english) =>
        new(Es() ? spanish : english, receipts);

    private bool TryDirect(string request, out AgentPlan plan)
    {
        plan = new AgentPlan();
        if (AgentIntent.IsIntroOrHelp(request))
        {
            plan = AgentPlan.Reply(Es() ? "presentación" : "introduction", AgentIntent.IntroReply(request, Es()));
            return true;
        }
        if (AgentIntent.IsDeleteEmptyPages(request))
        {
            var empty = owner.Settings.WorkspacePages
                .Select((page, index) => (page, index))
                .Where(item => !item.page.HasContent)
                .ToList();
            if (empty.Count == 0)
            {
                plan = AgentPlan.Reply(Es() ? "páginas vacías" : "empty pages",
                    Es() ? "No hay páginas vacías. Una página vacía es la que no tiene widgets ni temporizador." : "There are no empty pages. An empty page has no widgets and no timer.");
                return true;
            }
            string names = string.Join(", ", empty.Select(item => Es() ? $"página {item.index + 1}" : $"page {item.index + 1}"));
            plan = new AgentPlan
            {
                Understood = Es()
                    ? $"Voy a borrar {empty.Count} página(s) sin widgets: {names}. Las páginas con notas, tareas u otros widgets no se tocan."
                    : $"I will delete {empty.Count} empty page(s): {names}. Pages with widgets stay.",
                Message = Es() ? $"Plan: eliminar {empty.Count} páginas vacías." : $"Plan: delete {empty.Count} empty pages.",
                Changes = Es() ? $"{empty.Count} página(s) vacías" : $"{empty.Count} empty page(s)",
                Risk = AgentRisk.Destructive,
                Steps =
                [
                    new AgentPlanStep
                    {
                        Tool = "workspace.delete_empty_pages",
                        ArgumentsJson = "{}",
                        Risk = AgentRisk.Destructive,
                        Label = Es() ? $"Borrar {empty.Count} páginas vacías: {names}" : $"Delete {empty.Count} empty pages: {names}"
                    }
                ]
            };
            return true;
        }
        if (AgentIntent.TryCreateNote(request, out var note))
        {
            string body = note.Text;
            if (note.HabitsToday)
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                var lines = HabitStore.For(owner.Store).Book.Habits.Where(habit => !habit.Archived)
                    .Select(habit => (habit.IsComplete(today) ? "✓ " : "☐ ") + habit.Name).ToList();
                body = lines.Count == 0
                    ? (Es() ? "Hoy no hay hábitos en la lista." : "There are no habits listed for today.")
                    : string.Join("\n", lines);
            }
            var args = JsonSerializer.Serialize(new { title = note.Title, text = body, color = note.Color, habits_today = note.HabitsToday });
            var steps = new List<AgentPlanStep>();
            bool newPage = AgentIntent.WantsNewPage(request) && !owner.CurrentWorkspacePageIsBlank;
            if (newPage)
                steps.Add(new AgentPlanStep { Tool = "workspace.add_page", ArgumentsJson = "{}", Risk = AgentRisk.Write, Label = Es() ? "Crear página nueva" : "Create a new page" });
            string colorWord = AgentIntent.ColorLabel(note.Color, Es());
            string shown = body.Length == 0 ? "" : (body.Length <= 140 ? body : body[..140].TrimEnd() + "…");
            steps.Add(new AgentPlanStep { Tool = "add_note", ArgumentsJson = args, Risk = AgentRisk.Write, Label = Es() ? $"Nota {colorWord}" : $"{colorWord} note" });
            string where = newPage
                ? (Es() ? "en una página nueva" : "on a new page")
                : (Es() ? "en esta página" : "on this page");
            string content = shown.Length == 0 ? "" : (Es() ? $" con: {shown}" : $" with: {shown}");
            plan = new AgentPlan
            {
                Understood = newPage
                    ? (Es() ? $"Crear una página nueva y una nota {colorWord} ahí." : $"Create a new page and a {colorWord} note on it.")
                    : (Es() ? $"Crear una nota {colorWord} en esta página." : $"Create a {colorWord} note on this page."),
                Message = Es()
                    ? $"Listo. Te dejé una nota {colorWord} {where}{content}."
                    : $"Done. I left a {colorWord} note {where}{content}.",
                Changes = Es() ? (newPage ? "1 página y 1 nota" : "1 nota") : (newPage ? "1 page and 1 note" : "1 note"),
                Risk = AgentRisk.Write,
                Steps = steps
            };
            return true;
        }
        if (AgentIntent.TryAddPage(request))
        {
            if (owner.CurrentWorkspacePageIsBlank)
            {
                plan = AgentPlan.Reply(Es() ? "página" : "page",
                    Es() ? "Esta página ya está vacía. Dime qué widget pongo aquí." : "This page is already empty. Tell me which widget to add here.");
                return true;
            }
            plan = new AgentPlan
            {
                Understood = Es() ? "Crear una página nueva y abrirla." : "Create a new page and open it.",
                Message = Es() ? "Voy a abrir una página nueva." : "I will open a new page.",
                Changes = Es() ? "1 página" : "1 page",
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "workspace.add_page", ArgumentsJson = "{}", Risk = AgentRisk.Write, Label = Es() ? "Crear página nueva" : "Create a new page" }]
            };
            return true;
        }
        if (AgentIntent.TryAddTodo(request, out var todoText))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? "Añadir una tarea." : "Add a task.",
                Message = Es() ? "Voy a añadir esa tarea." : "I will add that task.",
                Changes = Es() ? "1 tarea" : "1 task",
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "add_todo", ArgumentsJson = JsonSerializer.Serialize(new { text = todoText }), Risk = AgentRisk.Write, Label = Es() ? "Crear tarea" : "Create task" }]
            };
            return true;
        }
        if (AgentIntent.TrySchedule(request, DateTime.Now, out var scheduled))
            return TryScheduleDirect(request, out plan);

        if (AgentIntent.TryCompleteTodo(request, out var todoTitle))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Completar «{todoTitle}»." : $"Complete «{todoTitle}».",
                Message = Es() ? $"Voy a marcar «{todoTitle}» como hecha." : $"I will mark «{todoTitle}» done.",
                Changes = todoTitle,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "complete_todo", ArgumentsJson = JsonSerializer.Serialize(new { title = todoTitle, done = true }), Risk = AgentRisk.Write, Label = todoTitle }]
            };
            return true;
        }
        if (AgentIntent.TryAddHabit(request, out var habitName))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Crear el hábito «{habitName}»." : $"Create the habit «{habitName}».",
                Message = Es() ? $"Voy a añadir el hábito «{habitName}»." : $"I will add the habit «{habitName}».",
                Changes = habitName,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "add_habit", ArgumentsJson = JsonSerializer.Serialize(new { name = habitName, cadence = "daily" }), Risk = AgentRisk.Write, Label = habitName }]
            };
            return true;
        }
        if (AgentIntent.TryMarkHabit(request, out var markName))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Marcar el hábito «{markName}»." : $"Check off «{markName}».",
                Message = Es() ? $"Voy a marcar «{markName}»." : $"I will check off «{markName}».",
                Changes = markName,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "mark_habit", ArgumentsJson = JsonSerializer.Serialize(new { name = markName, done = true }), Risk = AgentRisk.Write, Label = markName }]
            };
            return true;
        }
        if (AgentIntent.TryAddEvent(request, out var evt))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Agendar «{evt.Title}»." : $"Schedule «{evt.Title}».",
                Message = Es() ? $"Voy a crear el evento «{evt.Title}»." : $"I will create the event «{evt.Title}».",
                Changes = evt.Title,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "add_event", ArgumentsJson = JsonSerializer.Serialize(new { title = evt.Title, start = evt.Start, minutes = 60 }), Risk = AgentRisk.Write, Label = evt.Title }]
            };
            return true;
        }
        if (AgentIntent.TryAddWidget(request, out var widgetKind))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Añadir un widget {widgetKind} en esta página." : $"Add a {widgetKind} widget on this page.",
                Message = Es() ? $"Voy a añadir {widgetKind} aquí." : $"I will add {widgetKind} here.",
                Changes = widgetKind,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "add_widget", ArgumentsJson = JsonSerializer.Serialize(new { kind = widgetKind }), Risk = AgentRisk.Write, Label = widgetKind }]
            };
            return true;
        }
        if (AgentIntent.TryTimer(request, out var timerCommand))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Temporizador: {timerCommand}." : $"Timer: {timerCommand}.",
                Message = Es() ? $"Voy a {timerCommand} el temporizador." : $"I will {timerCommand} the timer.",
                Changes = timerCommand,
                Risk = AgentRisk.Batch,
                Steps = [new AgentPlanStep { Tool = "timer", ArgumentsJson = JsonSerializer.Serialize(new { command = timerCommand }), Risk = AgentRisk.Batch, Label = timerCommand }]
            };
            return true;
        }
        if (AgentIntent.TryRenamePage(request, out var renamePageNumber, out var renamePageName))
        {
            object args = renamePageNumber > 0
                ? new { page = renamePageNumber, name = renamePageName }
                : new { name = renamePageName };
            plan = new AgentPlan
            {
                Understood = Es()
                    ? (renamePageNumber > 0
                        ? $"Renombrar la página {renamePageNumber} a «{renamePageName}»."
                        : $"Renombrar esta página a «{renamePageName}».")
                    : (renamePageNumber > 0
                        ? $"Rename page {renamePageNumber} to “{renamePageName}”."
                        : $"Rename this page to “{renamePageName}”."),
                Message = Es() ? $"Voy a llamar esa página «{renamePageName}»." : $"I will name that page “{renamePageName}”.",
                Changes = renamePageName,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "workspace.rename_page", ArgumentsJson = JsonSerializer.Serialize(args), Risk = AgentRisk.Write, Label = renamePageName }]
            };
            return true;
        }
        if (AgentIntent.TryGotoHome(request))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? "Ir a la página principal (casa)." : "Go to the home page.",
                Message = Es() ? "Voy a casa." : "Going home.",
                Changes = "",
                Risk = AgentRisk.Read,
                Steps = [new AgentPlanStep { Tool = "workspace.goto", ArgumentsJson = JsonSerializer.Serialize(new { home = true }), Risk = AgentRisk.Read, Label = Es() ? "Casa" : "Home" }]
            };
            return true;
        }
        if (AgentIntent.TryGotoPage(request, out var pageNumber))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Ir a la página {pageNumber}." : $"Go to page {pageNumber}.",
                Message = Es() ? $"Voy a la página {pageNumber}." : $"I will go to page {pageNumber}.",
                Changes = "",
                Risk = AgentRisk.Read,
                Steps = [new AgentPlanStep { Tool = "workspace.goto", ArgumentsJson = JsonSerializer.Serialize(new { page = pageNumber }), Risk = AgentRisk.Read, Label = Es() ? $"Página {pageNumber}" : $"Page {pageNumber}" }]
            };
            return true;
        }
        if (AgentIntent.TrySetFocusDuration(request, out var minutes))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Dejar el enfoque en {minutes} minutos." : $"Set focus to {minutes} minutes.",
                Message = Es() ? $"Voy a poner el enfoque en {minutes} minutos." : $"I will set focus to {minutes} minutes.",
                Changes = $"{minutes} min",
                Risk = AgentRisk.Batch,
                Steps = [new AgentPlanStep { Tool = "set_focus_duration", ArgumentsJson = JsonSerializer.Serialize(new { minutes }), Risk = AgentRisk.Batch, Label = $"{minutes} min" }]
            };
            return true;
        }
        if (AgentIntent.TryDeleteTodo(request, out var deleteTodo))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Eliminar la tarea «{deleteTodo}»." : $"Delete the task «{deleteTodo}».",
                Message = Es() ? $"Voy a borrar la tarea «{deleteTodo}»." : $"I will delete the task «{deleteTodo}».",
                Changes = deleteTodo,
                Risk = AgentRisk.Destructive,
                Steps = [new AgentPlanStep { Tool = "delete_todo", ArgumentsJson = JsonSerializer.Serialize(new { title = deleteTodo }), Risk = AgentRisk.Destructive, Label = deleteTodo }]
            };
            return true;
        }
        if (AgentIntent.TryArchiveHabit(request, out var archiveHabit))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Archivar el hábito «{archiveHabit}»." : $"Archive the habit «{archiveHabit}».",
                Message = Es() ? $"Voy a archivar «{archiveHabit}»." : $"I will archive «{archiveHabit}».",
                Changes = archiveHabit,
                Risk = AgentRisk.Destructive,
                Steps = [new AgentPlanStep { Tool = "archive_habit", ArgumentsJson = JsonSerializer.Serialize(new { name = archiveHabit }), Risk = AgentRisk.Destructive, Label = archiveHabit }]
            };
            return true;
        }
        if (AgentIntent.TryCompleteEvent(request, out var doneEvent))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Completar el evento «{doneEvent}»." : $"Complete the event «{doneEvent}».",
                Message = Es() ? $"Voy a marcar «{doneEvent}» como hecho." : $"I will mark «{doneEvent}» done.",
                Changes = doneEvent,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "complete_event", ArgumentsJson = JsonSerializer.Serialize(new { title = doneEvent, done = true }), Risk = AgentRisk.Write, Label = doneEvent }]
            };
            return true;
        }
        if (AgentIntent.TryDeleteEvent(request, out var deleteEvent))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Eliminar el evento «{deleteEvent}»." : $"Delete the event «{deleteEvent}».",
                Message = Es() ? $"Voy a borrar el evento «{deleteEvent}»." : $"I will delete the event «{deleteEvent}».",
                Changes = deleteEvent,
                Risk = AgentRisk.Destructive,
                Steps = [new AgentPlanStep { Tool = "delete_event", ArgumentsJson = JsonSerializer.Serialize(new { title = deleteEvent }), Risk = AgentRisk.Destructive, Label = deleteEvent }]
            };
            return true;
        }
        if (AgentIntent.TryAddFinance(request, out var financeLine))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? "Registrar un movimiento." : "Record a money movement.",
                Message = Es() ? "Voy a apuntarlo en finanzas." : "I will add it to finance.",
                Changes = financeLine,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "finance.add", ArgumentsJson = JsonSerializer.Serialize(new { text = financeLine }), Risk = AgentRisk.Write, Label = financeLine }]
            };
            return true;
        }
        if (AgentIntent.TryMarkFinancePaid(request, out var paidTitle))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Marcar pagado «{paidTitle}»." : $"Mark «{paidTitle}» paid.",
                Message = Es() ? $"Voy a marcar «{paidTitle}» como pagado este mes." : $"I will mark «{paidTitle}» paid this month.",
                Changes = paidTitle,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "finance.mark_paid", ArgumentsJson = JsonSerializer.Serialize(new { title = paidTitle }), Risk = AgentRisk.Write, Label = paidTitle }]
            };
            return true;
        }
        if (AgentIntent.TryAddFocusTask(request, out var focusTask))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"Añadir tarea de enfoque «{focusTask}»." : $"Add focus task «{focusTask}».",
                Message = Es() ? $"Voy a añadir «{focusTask}» al enfoque." : $"I will add «{focusTask}» to focus.",
                Changes = focusTask,
                Risk = AgentRisk.Write,
                Steps = [new AgentPlanStep { Tool = "add_focus_task", ArgumentsJson = JsonSerializer.Serialize(new { name = focusTask }), Risk = AgentRisk.Write, Label = focusTask }]
            };
            return true;
        }
        if (AgentIntent.TryListSounds(request))
        {
            var kind = Regex.IsMatch(request, @"alarma|alarm", RegexOptions.IgnoreCase) ? SoundKind.Alarm
                : Regex.IsMatch(request, @"recordatorio|reminder", RegexOptions.IgnoreCase) ? SoundKind.Reminder
                : Regex.IsMatch(request, @"clic|click", RegexOptions.IgnoreCase) ? SoundKind.Click
                : SoundKind.Ambient;
            string names = string.Join(", ", SoundLibrary.OfKind(kind).Select(sound => sound.Name));
            string current = kind switch
            {
                SoundKind.Alarm => SoundLibrary.Find(owner.Settings.FocusEndSound)?.Name ?? owner.Settings.FocusEndSound,
                SoundKind.Reminder => SoundLibrary.Find(owner.Settings.ReminderSound)?.Name ?? owner.Settings.ReminderSound,
                SoundKind.Click => SoundLibrary.Find(owner.Settings.ClickSound)?.Name ?? owner.Settings.ClickSound,
                _ => SoundLibrary.Find(owner.Settings.AmbientSound)?.Name ?? owner.Settings.AmbientSound
            };
            plan = AgentPlan.Reply(Es() ? "sonidos" : "sounds",
                Es()
                    ? $"Ahora mismo suena {current}. Puedes usar: {names}. Dime cuál pongo."
                    : $"Right now it is {current}. You can use: {names}. Tell me which one to use.");
            return true;
        }
        if (AgentIntent.TrySetSound(request, out var soundAsk))
        {
            var kind = soundAsk.Slot switch
            {
                "focus_end" or "break_end" => SoundKind.Alarm,
                "reminder" => SoundKind.Reminder,
                "click" => SoundKind.Click,
                _ => SoundKind.Ambient
            };
            var sound = SoundLibrary.Resolve(soundAsk.Query, kind) ?? SoundLibrary.Resolve(soundAsk.Query);
            if (sound is null)
            {
                plan = AgentPlan.Reply(Es() ? "sonido" : "sound",
                    Es() ? $"No encontré el sonido «{soundAsk.Query}». Pídeme primero la lista de sonidos." : $"I could not find the sound «{soundAsk.Query}». Ask me for the sound list first.");
                return true;
            }
            var args = JsonSerializer.Serialize(new { slot = soundAsk.Slot, id = sound.Id });
            plan = new AgentPlan
            {
                Understood = Es() ? $"Usar {sound.Name} para el enfoque." : $"Use {sound.Name} for focus.",
                Message = Es() ? $"Voy a dejar el sonido {sound.Name}." : $"I will set the sound to {sound.Name}.",
                Changes = sound.Name,
                Risk = AgentRisk.Batch,
                Steps = [new AgentPlanStep { Tool = "sounds.set", ArgumentsJson = args, Risk = AgentRisk.Batch, Label = Es() ? $"Usar {sound.Name}" : $"Use {sound.Name}" }]
            };
            return true;
        }
        if (AgentIntent.TryRemoveTimer(request, out var timerAsk))
        {
            var args = timerAsk.Page is { } page ? JsonSerializer.Serialize(new { page }) : "{}";
            string where = timerAsk.Page is { } n ? (Es() ? $"de la página {n}" : $"from page {n}") : (Es() ? "de esta página" : "from this page");
            plan = new AgentPlan
            {
                Understood = Es() ? $"Quitar el temporizador {where}." : $"Remove the timer {where}.",
                Message = Es() ? $"Voy a quitar el temporizador {where}." : $"I will remove the timer {where}.",
                Changes = Es() ? "1 temporizador" : "1 timer",
                Risk = AgentRisk.Destructive,
                Steps = [new AgentPlanStep { Tool = "workspace.remove_timer", ArgumentsJson = args, Risk = AgentRisk.Destructive, Label = Es() ? $"Quitar temporizador {where}" : $"Remove timer {where}" }]
            };
            return true;
        }
        if (AgentIntent.TryRemoveWidget(request, out var widgetAsk))
        {
            var args = JsonSerializer.Serialize(new { title = widgetAsk.Target, page = widgetAsk.Page });
            plan = new AgentPlan
            {
                Understood = Es() ? $"Quitar {widgetAsk.Target}." : $"Remove {widgetAsk.Target}.",
                Message = Es() ? $"Voy a quitar {widgetAsk.Target}." : $"I will remove {widgetAsk.Target}.",
                Changes = widgetAsk.Target,
                Risk = AgentRisk.Destructive,
                Steps = [new AgentPlanStep { Tool = "workspace.remove_widget", ArgumentsJson = args, Risk = AgentRisk.Destructive, Label = Es() ? $"Quitar {widgetAsk.Target}" : $"Remove {widgetAsk.Target}" }]
            };
            return true;
        }
        if (AgentIntent.IsWhatDoYouKnow(request))
        {
            var facts = memory.Book.Enabled ? memory.Book.Search("", 20) : [];
            string text = facts.Count == 0
                ? (Es() ? "No tengo recuerdos personales confirmados todavía." : "I do not have confirmed personal memories yet.")
                : string.Join("\n", facts.Select(item => $"• {item.Content}"));
            plan = AgentPlan.Reply(Es() ? "qué sabes de mí" : "what do you know about me", text);
            return true;
        }
        if (AgentIntent.TryRemember(request, out var fact))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"recordar: {fact}" : $"remember: {fact}",
                Changes = Es() ? "1 recuerdo" : "1 memory",
                Risk = AgentRisk.Write,
                Message = Es() ? $"Voy a recordar: {fact}" : $"I will remember: {fact}",
                Steps = [new AgentPlanStep { Tool = "memory.confirm", ArgumentsJson = JsonSerializer.Serialize(new { text = fact }), Risk = AgentRisk.Write, Label = fact }]
            };
            return true;
        }
        if (AgentIntent.TryForget(request, out var query))
        {
            plan = new AgentPlan
            {
                Understood = Es() ? $"olvidar: {query}" : $"forget: {query}",
                Changes = Es() ? "1 recuerdo eliminado" : "1 memory removed",
                Risk = AgentRisk.Destructive,
                Message = Es() ? $"Voy a olvidar lo relacionado con: {query}" : $"I will forget anything matching: {query}",
                Steps = [new AgentPlanStep { Tool = "memory.forget", ArgumentsJson = JsonSerializer.Serialize(new { text = query }), Risk = AgentRisk.Destructive, Label = query }]
            };
            return true;
        }
        return false;
    }

    public void ResetSession()
    {
        session?.Dispose();
        session = null;
        sessionPrompt = "";
    }

    private IAgentConversation Session()
    {
        // System prompt is date+hour stable; do not recreate the API chat every wall-clock second.
        string prompt = SystemPrompt();
        if (session is not null && sessionPrompt == prompt) return session;
        session?.Dispose();
        session = beginConversation(prompt);
        sessionPrompt = prompt;
        return session;
    }

    private string Opening(string request, IReadOnlyList<AgentChatMessage> history, string? seededFocus)
    {
        var prior = PriorTurns(history, request);
        var recent = prior.TakeLast(10).Select(FormatHistoryLine);
        string memoryBlock = owner.Settings.AgentMemoryEnabled && memory.Book.Enabled ? memory.Book.ContextBlock() : "";
        string screen = owner.DescribeCurrentScreenForAgent();
        string thread = recent.Any()
            ? "HILO RECIENTE (contexto obligatorio; resuelve referencias como «cuáles», «eso», «el anterior» contra este hilo):\n"
              + string.Join("\n", recent) + "\n"
            : "";
        return $"""
            FECHA LOCAL: {DateTime.Now:yyyy-MM-dd HH:mm:ss}
            IDIOMA: {Strings.Culture.TwoLetterISOLanguageName}
            PANTALLA ACTUAL (fuente de verdad; no inventes widgets):
            {screen}
            MEMORIA:
            {(string.IsNullOrWhiteSpace(memoryBlock) ? "(vacía)" : Trim(memoryBlock, 400))}
            {thread}{(seededFocus is null ? "" : "DATOS REALES DE ENFOQUE:\n" + seededFocus + "\nUsa estos números. No llames focus.summarize otra vez. Termina con finish.\n")}PETICIÓN ACTUAL: {request}
            Si la petición es corta o anafórica («cuáles son», «y eso», «dímelos», «borra ese»), continúa el tema del HILO RECIENTE; no cambies a get_state ni a un resumen global.
            Si pide una nota, usa add_note con el texto que dictó (nunca dejes text vacío ni copies el título NOTAS). Si pide página nueva y una nota, workspace.add_page y luego add_note.
            Si pregunta por páginas vacías, workspace.delete_empty_pages. Nunca finish mintiendo que ya lo hiciste.
            Si pide eliminar/vaciar todos los widgets de la pantalla/página actual, usa workspace.clear_widgets. Solo propón remove_widget para títulos que aparezcan en PANTALLA ACTUAL.
            Página ≠ widget: «renombra/llama/nombra la página…» → workspace.rename_page. «ve a casa/principal/home» → workspace.goto con home:true o name. Nunca uses rename_widget ni focus para páginas.
            Si pregunta quién eres o qué puedes hacer, finish con un message natural (2-5 frases). No copies un párrafo memorizado ni empieces siempre igual.
            Finanzas: finance.add con text compacto tipo "-10 deepseek api" o "+2500 salario" — NUNCA copies la frase del usuario como título.
            Si pide borrar todos los gastos, usa finance.clear_expenses (un solo paso), no N veces finance.delete.
            Al listar hábitos/tareas/eventos, el message debe nombrarlos; no digas solo el número.
            """;
    }

    private static IReadOnlyList<AgentChatMessage> PriorTurns(IReadOnlyList<AgentChatMessage> history, string request)
    {
        if (history.Count == 0) return history;
        var last = history[^1];
        if (last.Role == "you" && string.Equals(last.Text.Trim(), request.Trim(), StringComparison.Ordinal))
            return history.Take(history.Count - 1).ToList();
        return history;
    }

    private static string FormatHistoryLine(AgentChatMessage message)
    {
        string role = message.Role switch
        {
            "you" => "usuario",
            "plan" => "plan",
            "error" => "error",
            _ => "agente"
        };
        string text = Trim(message.Text.Replace('\n', ' '), 220);
        if (message.Receipts is { Count: > 0 })
        {
            string extras = string.Join("; ", message.Receipts
                .Where(receipt => !string.IsNullOrWhiteSpace(receipt.Summary))
                .Select(receipt => Trim(receipt.Summary.Replace('\n', ' '), 100))
                .Take(3));
            if (extras.Length > 0) text = text.Length > 0 ? text + " · " + extras : extras;
        }
        return $"{role}: {text}";
    }

    internal static string SystemPrompt(DateTime? clock = null) =>
        AgentInstructions.Build(clock ?? DateTime.Now);

    private static bool Es() => Strings.Culture.TwoLetterISOLanguageName == "es";
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>
    /// When the model answers with only a count ("3 hábitos.") but the tool already returned names,
    /// prefer the richer receipt so chat and voice stay useful.
    /// </summary>
    private static string EnrichBareAnswer(string message, IReadOnlyList<AgentActionReceipt> queries)
    {
        var last = queries.LastOrDefault(receipt => !string.IsNullOrWhiteSpace(receipt.Summary));
        if (last is null) return message;
        string summary = last.Summary.Trim();
        if (summary.Length <= message.Length) return message;
        if (!Regex.IsMatch(message.Trim(), @"^\d+\s+\S+", RegexOptions.IgnoreCase)
            && !Regex.IsMatch(message.Trim(), @"^(hay|tienes|son)\s+\d+\b", RegexOptions.IgnoreCase))
            return message;
        // Receipt carries "3 hábitos: a, b, c" — use it when the spoken line was only the count.
        if (summary.Contains(':') || summary.Contains(','))
            return summary.TrimEnd('.') + ".";
        return message;
    }

    /// <summary>
    /// Calendar quick-add for "recuérdame…" / schedule phrases — identical to the agenda input.
    /// </summary>
    private bool TryScheduleDirect(string request, out AgentPlan plan)
    {
        plan = new AgentPlan();
        if (!AgentIntent.TrySchedule(request, DateTime.Now, out var scheduled)) return false;
        var arguments = JsonSerializer.Serialize(new
        {
            title = scheduled.Title,
            start = scheduled.Start.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
            minutes = scheduled.Minutes,
            all_day = scheduled.AllDay,
            reminders = scheduled.Reminders,
            notes = scheduled.Notes,
            location = scheduled.Location,
            color = scheduled.Color,
            repeat = scheduled.Repeat.ToString().ToLowerInvariant(),
            interval = scheduled.Interval,
            days = scheduled.Days.Select(day => day.ToString()),
            until = scheduled.Until?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
            count = scheduled.Count
        });
        string when = scheduled.AllDay
            ? scheduled.Start.ToString("ddd d MMM", Strings.Culture)
            : scheduled.Start.ToString(Es() ? "ddd d MMM 'a las' HH:mm" : "ddd d MMM 'at' HH:mm", Strings.Culture);
        plan = new AgentPlan
        {
            Understood = Es() ? $"Recordarte «{scheduled.Title}» {when}." : $"Remind you about «{scheduled.Title}» on {when}.",
            Message = Es() ? $"Listo. Te recordaré «{scheduled.Title}» {when}." : $"Done. I'll remind you about «{scheduled.Title}» on {when}.",
            Changes = Es() ? "1 recordatorio en el calendario" : "1 calendar reminder",
            Risk = AgentRisk.Write,
            Steps = [new AgentPlanStep { Tool = "add_event", ArgumentsJson = arguments, Risk = AgentRisk.Write, Label = Es() ? $"Crear recordatorio: {scheduled.Title}" : $"Create reminder: {scheduled.Title}" }]
        };
        return true;
    }
}
