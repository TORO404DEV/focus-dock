using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PomoDock.Core;

namespace PomoDock.Web;

internal sealed record AgentRunResult(string Message, IReadOnlyList<AgentActionReceipt> Receipts, AgentPlan? Plan = null, bool NeedsApproval = false, bool Silent = false);

/// <summary>
/// The desktop's PomoAgent, line for line where it can be: observe, plan, then act. Queries run
/// immediately. Writes wait for an approved <see cref="AgentPlan"/>. Generated prose never touches
/// the database. The brain is always DeepSeek here; the desktop's local model and its regex
/// fallback have no web counterpart, and the chat never asks without a key.
/// </summary>
internal sealed class PomoAgent
{
    private readonly MainWindow owner;
    private readonly Func<string, IAgentConversation> beginConversation;
    private readonly AgentToolbox tools;
    private readonly AgentMemory memory;
    private IAgentConversation? session;
    private string sessionPrompt = "";

    public PomoAgent(MainWindow owner)
        : this(owner, prompt => DeepSeekAgentModel.Begin(owner.Store, prompt)) { }

    internal PomoAgent(MainWindow owner, Func<string, IAgentConversation> beginConversation)
    {
        this.owner = owner;
        this.beginConversation = beginConversation;
        tools = new AgentToolbox(owner);
        memory = new AgentMemory(owner.Store);
    }

    public async Task<AgentRunResult> InterpretAsync(
        string request,
        IReadOnlyList<AgentChatMessage> history,
        IProgress<AgentNotice>? activity,
        CancellationToken cancellationToken)
    {
        request = request.Trim();

        // Same AgendaQuickAdd path as the calendar's natural-language field — never leave
        // "recuérdame … en un minuto" to the LLM (it invents todos on the wrong day).
        if (TryScheduleDirect(request, out var scheduledPlan))
            return await FinishDirect(scheduledPlan, activity, cancellationToken);

        if (AgentIntent.TryRemember(request, out var memoryFact))
        {
            var rememberPlan = new AgentPlan
            {
                Understood = Es() ? $"recordar: {memoryFact}" : $"remember: {memoryFact}",
                Changes = Es() ? "1 recuerdo" : "1 memory",
                Risk = AgentRisk.Write,
                Message = Es() ? $"Voy a recordar: {memoryFact}" : $"I will remember: {memoryFact}",
                Steps = [new AgentPlanStep { Tool = "memory.confirm", ArgumentsJson = JsonSerializer.Serialize(new { text = memoryFact, kind = AgentIntent.GuessMemoryKind(memoryFact) }), Risk = AgentRisk.Write, Label = memoryFact }]
            };
            return await FinishDirect(rememberPlan, activity, cancellationToken);
        }

        if (AgentIntent.IsUpcomingCalendarQuery(request))
        {
            activity?.Report(new AgentNotice(AgentEventKind.Reading, Es() ? "Consultando · CALENDARIO" : "Reading · CALENDAR"));
            using var arguments = JsonDocument.Parse("{\"days\":3650,\"limit\":10,\"include_done\":false}");
            var result = await tools.ExecuteAsync("calendar.upcoming", arguments.RootElement, cancellationToken);
            return new AgentRunResult(result.Summary, result.Success ? [result.Receipt("calendar.upcoming")] : []);
        }

        if (AgentPresent.WantsFocusTable(request, history))
        {
            activity?.Report(new AgentNotice(AgentEventKind.Reading, L.T("agent.consultingFocus")));
            string period = AgentPresent.GuessFocusPeriod(request, history);
            using var arguments = JsonDocument.Parse($"{{\"period\":\"{period}\",\"limit\":120}}");
            var result = await tools.ExecuteAsync("focus.query_sessions", arguments.RootElement, cancellationToken);
            string table = AgentPresent.FormatFocusQuery(result.Json, Es());
            return new AgentRunResult(table, result.Success ? [result.Receipt("focus.query_sessions")] : []);
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

        for (int step = 0; step < 8; step++)
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
                Console.Error.WriteLine($"{DateTimeOffset.Now:O} agent-llm {ex}");
                string detail = Trim(ex.Message, 180);
                return Stop(queryReceipts,
                    $"DeepSeek no pudo responder ({detail}). Revisa la API key, el saldo o la red y reintenta.",
                    $"DeepSeek could not answer ({detail}). Check the API key, balance, or network and retry.");
            }
            if (!AgentJson.TryReadTurn(raw, out var turn, out var error))
            {
                if (guard.NoteMalformed())
                {
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
                    Si alguna HABILIDAD APRENDIDA de MEMORIA aplica a esta petición y aún no tiene un paso, propónlo ahora (usa las tools del catálogo: app.hide_agent, finish con speak:false, fill, etc.). Si no falta nada, action=finish.
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
            return new AgentRunResult(summary, receipts, Silent: plan.Silent);
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
            return Stop([],
                "No hice ese cambio. Dímelo en una frase concreta, por ejemplo: crea una nota amarilla en esta página.",
                "I did not make that change. Say it in one concrete sentence, for example: create a yellow note on this page.");
        }
        string message = string.IsNullOrWhiteSpace(turn.Message)
            ? (pending.Count == 0 ? (Es() ? "Listo." : "Done.") : (Es() ? "Plan listo." : "Plan ready."))
            : turn.Message.Trim();
        if (pending.Count == 0 && queries.Count > 0)
            message = EnrichBareAnswer(message, queries);

        // "haz una tabla" is a read of data already fetched — never a sticky note.
        if (AgentPresent.WantsTable(request) && !AgentPresent.WantsANote(request)
            && pending.Count > 0
            && pending.All(step => AgentCatalog.Canonical(step.Tool) is "add_note" or "notes.create" or "notes.append"))
        {
            pending.Clear();
            var focus = queries.LastOrDefault(receipt => receipt.Tool.StartsWith("focus.", StringComparison.OrdinalIgnoreCase));
            if (focus?.PayloadJson is { Length: > 2 } payload)
                message = AgentPresent.FormatFocusQuery(payload, Es());
        }
        else if (pending.Count == 0)
        {
            var focus = queries.LastOrDefault(receipt =>
                string.Equals(receipt.Tool, "focus.query_sessions", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(receipt.PayloadJson));
            if (focus is not null && (AgentPresent.WantsTable(request) || AgentPresent.LooksLikeGrid(message) is false && Regex.IsMatch(message, @"^\d+\s+sesiones", RegexOptions.IgnoreCase)))
                message = AgentPresent.FormatFocusQuery(focus.PayloadJson!, Es());
        }
        string said = string.IsNullOrWhiteSpace(turn.Understood) ? understood : turn.Understood.Trim();
        if (said.Length < 12 || said is "true" or "false" or "ok" or "yes")
            said = pending.Count == 0 ? message : (Es() ? "Voy a: " : "I will: ") + string.Join(Es() ? "; " : "; ", pending.Select(step => step.Label));
        var plan = new AgentPlan
        {
            Understood = said,
            Message = message,
            Steps = pending,
            Risk = pending.Count == 0 ? AgentRisk.Read : pending.Max(step => step.Risk),
            Changes = pending.Count == 0 ? "" : (Es() ? $"{pending.Count} cambio(s) previsto(s)" : $"{pending.Count} planned change(s)"),
            Silent = WantsSilent(turn)
        };
        if (plan.CanAutoRun)
        {
            var executed = await ExecuteAsync(plan, activity, cancellationToken);
            return executed with { Silent = plan.Silent || executed.Silent };
        }
        // Destructive / approval plans must never speak or store a "already done" message.
        if (plan.HasMutations && !AgentReply.AsksConfirmation(plan.Message))
            plan.Message = AgentReply.Pending(plan, Es());
        IReadOnlyList<AgentActionReceipt> receipts = pending.Count > 0 ? [] : queries;
        return new AgentRunResult(plan.Message, receipts, plan, plan.HasMutations, plan.Silent);
    }

    private static AgentRunResult Stop(IReadOnlyList<AgentActionReceipt> receipts, string spanish, string english) =>
        new(Es() ? spanish : english, receipts);

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
            MEMORIA (las habilidades son órdenes permanentes: si aplican a ESTA petición, ejecútalas con tools; no las ignores):
            {(string.IsNullOrWhiteSpace(memoryBlock) ? "(vacía)" : memoryBlock)}
            {thread}{(seededFocus is null ? "" : "DATOS REALES DE ENFOQUE:\n" + seededFocus + "\nUsa estos números. No llames focus.summarize otra vez. Termina con finish.\n")}PETICIÓN ACTUAL: {request}
            Si la petición es corta o anafórica («cuáles son», «y eso», «dímelos», «borra ese»), continúa el tema del HILO RECIENTE; no cambies a get_state ni a un resumen global.
            Si pide una nota, usa add_note con el texto que dictó (nunca dejes text vacío ni copies el título NOTAS). Si pide página nueva y una nota, workspace.add_page y luego add_note.
            Si pide tabla, lista o detalle de sesiones/enfoque: consulta focus.query_sessions y termina con finish. NUNCA crees una nota para “haz una tabla”.
            Si pregunta por páginas vacías, workspace.delete_empty_pages. Nunca finish mintiendo que ya lo hiciste.
            Si pide eliminar/vaciar todos los widgets de la pantalla/página actual, usa workspace.clear_widgets. Solo propón remove_widget para títulos que aparezcan en PANTALLA ACTUAL.
            Si piden enseñarte una habilidad o regla («cuando te diga…», «no lo olvides», «escríbelo en tu memoria»): memory.confirm con kind=skill y el texto completo. No ejecutes esa habilidad en el turno de enseñanza.
            Las Habilidades de MEMORIA mandan sobre el atajo por defecto. Si una habilidad pide ocultar el chat: app.hide_agent. Si pide no hablar: finish {"speak":false}. Reproducir o cambiar canción/vídeo: workspace.play_youtube (si ya hay una en curso, esa tool cierra la ventana incrustada y su widget y abre Brave de nuevo con la pedida; si no hay, abre Brave). Añade fill:true u otros pasos SOLO si una habilidad o la petición lo piden.
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
        PomoDock.App.AgentInstructions.Build(clock ?? DateTime.Now);

    private static bool Es() => Strings.Culture.TwoLetterISOLanguageName == "es";
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    private static bool WantsSilent(AgentModelTurn turn)
    {
        if (turn.Arguments.ValueKind != JsonValueKind.Object) return false;
        if (turn.Arguments.TryGetProperty("speak", out var speak))
        {
            if (speak.ValueKind == JsonValueKind.False) return true;
            if (speak.ValueKind == JsonValueKind.String && speak.GetString() is "false" or "no" or "off" or "0") return true;
        }
        if (turn.Arguments.TryGetProperty("silent", out var silent)
            && (silent.ValueKind == JsonValueKind.True
                || (silent.ValueKind == JsonValueKind.String && silent.GetString() is "true" or "yes" or "on" or "1")))
            return true;
        return false;
    }

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
