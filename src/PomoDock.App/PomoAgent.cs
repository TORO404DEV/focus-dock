using System.Globalization;
using System.Text.Json;
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

    public PomoAgent(MainWindow owner, LocalAgentModel model) : this(owner, model.Begin) { }

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
        if (TryDirect(request, out var direct))
        {
            activity?.Report(new AgentNotice(direct.HasMutations ? AgentEventKind.PlanReady : AgentEventKind.Done, direct.Understood));
            return new AgentRunResult(direct.Message, [], direct, direct.HasMutations);
        }

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

        var pending = new List<AgentPlanStep>();
        var queryReceipts = new List<AgentActionReceipt>();
        var guard = new AgentLoopGuard();
        string lastSummary = "";
        string understood = request.Length <= 80 ? request : request[..80] + "…";
        string? seededFocus = null;

        if (FocusHistory.TryGuessPeriod(request, out var seededPeriod))
        {
            activity?.Report(new AgentNotice(AgentEventKind.Reading, L.T("agent.consultingFocus")));
            var seeded = await tools.ExecuteAsync("focus.summarize", JsonDocument.Parse($"{{\"period\":\"{seededPeriod}\"}}").RootElement, cancellationToken);
            seededFocus = seeded.Json;
            lastSummary = seeded.Summary;
            queryReceipts.Add(seeded.Receipt("focus.summarize"));
        }

        using var conversation = beginConversation(SystemPrompt());
        string input = Opening(request, history, seededFocus);

        for (int step = 0; step < 12; step++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            activity?.Report(new AgentNotice(AgentEventKind.Interpreting, step == 0 ? L.T("agent.interpreting") : $"{L.T("agent.interpreting")}  {step + 1:00}/12"));
            var tokens = new Progress<string>(_ =>
            {
                activity?.Report(new AgentNotice(AgentEventKind.AnswerDelta, L.T("agent.responding")));
            });
            var raw = AgentThink.Strip(await conversation.AskAsync(input, tokens, cancellationToken));
            if (!AgentJson.TryReadTurn(raw, out var turn, out var error))
            {
                if (guard.NoteMalformed())
                    return Stop(queryReceipts, "El modelo local no produjo una acción válida después de tres intentos.", "The local model did not produce a valid action after three attempts.");
                input = $"""
                    Tu respuesta no cumplió el protocolo ({error}). Devuelve únicamente un objeto JSON válido,
                    sin Markdown ni explicación, con action y arguments. Respuesta anterior: {raw}
                    /no_think
                    """;
                continue;
            }
            guard.NoteValid();
            string action = turn.Action.Trim().ToLowerInvariant();
            if (action is "finish" or "plan")
                return FinishInterpret(turn, understood, pending, queryReceipts);

            var risk = AgentCatalog.RiskOf(action);
            if (guard.IsRepeat(action, turn.Arguments, risk, out var prior))
            {
                if (guard.TooManyRepeats())
                    return pending.Count > 0
                        ? FinishInterpret(turn, understood, pending, queryReceipts)
                        : Stop(queryReceipts,
                            $"Me detuve porque el modelo intentó repetir '{action}' después de recibir su resultado.",
                            $"I stopped because the model tried to repeat '{action}' after receiving its result.");
                input = $"""
                    ACCIÓN BLOQUEADA: {action} ya se llamó con los mismos argumentos.
                    RESULTADO YA DISPONIBLE: {prior}
                    No la repitas. Termina con action=finish si ya puedes responder, o elige una herramienta diferente.
                    /no_think
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
                    Label = AgentCatalog.Label(action, turn.Message)
                });
                guard.Remember(action, turn.Arguments, risk, "{\"success\":true,\"proposed\":true}", false, true);
                input = $"""
                    PROPUESTA REGISTRADA, no ejecutada: {action} {turn.Arguments}.
                    Sigue proponiendo pasos o termina con action=finish y un resumen del plan.
                    /no_think
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
                /no_think
                """;
        }

        if (pending.Count > 0)
            return FinishInterpret(new AgentModelTurn { Action = "finish", Message = lastSummary, Understood = understood }, understood, pending, queryReceipts);
        return Stop(queryReceipts,
            $"No pude cerrar la orden dentro del presupuesto seguro. Último resultado: {(string.IsNullOrWhiteSpace(lastSummary) ? "ninguno" : lastSummary)}.",
            $"I could not finish within the safe action budget. Last result: {(string.IsNullOrWhiteSpace(lastSummary) ? "none" : lastSummary)}.");
    }

    public async Task<AgentRunResult> ExecuteAsync(AgentPlan plan, IProgress<AgentNotice>? activity, CancellationToken cancellationToken)
    {
        var receipts = new List<AgentActionReceipt>();
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
        }

        string summary = receipts.Count == 0
            ? (string.IsNullOrWhiteSpace(plan.Message) ? (Es() ? "Listo." : "Done.") : plan.Message)
            : string.Join("\n", receipts.Select(receipt => "✓ " + receipt.Summary));
        activity?.Report(new AgentNotice(AgentEventKind.Done, L.T("agent.ready")));
        return new AgentRunResult(summary, receipts);
    }

    public async Task<AgentRunResult> UndoAsync(AgentActionReceipt receipt, CancellationToken cancellationToken)
    {
        if (!receipt.CanUndo) return new AgentRunResult(Es() ? "Esa acción no se puede deshacer." : "That action cannot be undone.", []);
        using var args = JsonDocument.Parse(receipt.UndoArgumentsJson ?? "{}");
        var result = await tools.ExecuteAsync(receipt.UndoTool!, args.RootElement, cancellationToken);
        return new AgentRunResult(result.Summary, result.Success ? [result.Receipt(receipt.UndoTool!)] : []);
    }

    private AgentRunResult FinishInterpret(AgentModelTurn turn, string understood, List<AgentPlanStep> pending, List<AgentActionReceipt> queries)
    {
        string message = string.IsNullOrWhiteSpace(turn.Message)
            ? (pending.Count == 0 ? (Es() ? "Listo." : "Done.") : (Es() ? "Plan listo." : "Plan ready."))
            : turn.Message.Trim();
        var plan = new AgentPlan
        {
            Understood = string.IsNullOrWhiteSpace(turn.Understood) ? understood : turn.Understood.Trim(),
            Message = message,
            Steps = pending,
            Risk = pending.Count == 0 ? AgentRisk.Read : pending.Max(step => step.Risk),
            Changes = pending.Count == 0 ? "" : (Es() ? $"{pending.Count} cambio(s) previsto(s)" : $"{pending.Count} planned change(s)")
        };
        return new AgentRunResult(message, queries, plan, plan.HasMutations);
    }

    private static AgentRunResult Stop(IReadOnlyList<AgentActionReceipt> receipts, string spanish, string english) =>
        new(Es() ? spanish : english, receipts);

    private bool TryDirect(string request, out AgentPlan plan)
    {
        plan = new AgentPlan();
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

    private string Opening(string request, IReadOnlyList<AgentChatMessage> history, string? seededFocus)
    {
        var recent = history.TakeLast(6).Select(message => $"{message.Role}: {Trim(message.Text, 180)}");
        string memoryBlock = owner.Settings.AgentMemoryEnabled && memory.Book.Enabled ? memory.Book.ContextBlock() : "";
        return $"""
            FECHA LOCAL: {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({TimeZoneInfo.Local.DisplayName})
            IDIOMA DE LA INTERFAZ: {Strings.Culture.TwoLetterISOLanguageName}
            MEMORIA PERSONAL CONFIRMADA:
            {(string.IsNullOrWhiteSpace(memoryBlock) ? "(vacía)" : memoryBlock)}
            HILO RECIENTE:
            {(recent.Any() ? string.Join("\n", recent) : "(nuevo)")}
            {(seededFocus is null ? "" : "DATOS REALES DE ENFOQUE YA CALCULADOS DESDE Store.Sessions():\n" + seededFocus + "\nUsa estos números. No llames focus.summarize otra vez. Termina con finish.\n")}
            PETICIÓN: {request}
            Empieza. Si para decidir necesitas ver datos reales, usa get_state u otra consulta.
            /no_think
            """;
    }

    internal static string SystemPrompt(DateTime? clock = null) => """
        Eres el agente de acciones de PomoDock. No eres RAG ni un asistente de conversación general.
        Tu misión es cumplir literalmente la petición dentro de las herramientas disponibles.
        Trabajas en español o inglés según el usuario. Hoy es __TODAY__.

        REGLAS:
        - Responde SIEMPRE con un solo JSON, sin Markdown, comentarios ni texto exterior.
        - Forma: {"action":"nombre","arguments":{...},"message":"","understood":""}
        - Haz una llamada por turno. Después recibirás su resultado real y podrás continuar.
        - Nunca afirmes que hiciste algo hasta recibir success=true.
        - Las escrituras NO se ejecutan en esta fase: se registran como plan. El usuario las aprueba después.
        - Usa fechas ISO locales yyyy-MM-dd y horas HH:mm. Resuelve hoy/mañana/días de semana con la fecha dada.
        - "antes del lunes" termina el domingo; "a más tardar el lunes" termina el lunes.
        - "Recuérdame X" es una tarea, no un recuerdo personal. "Recuerda que..." sí es memoria.
        - Para horas, enfoque, pomodoros o proyectos históricos usa SIEMPRE focus.summarize o focus.query_sessions. Nunca calcules esos datos por tu cuenta.
        - period acepta today, yesterday, this_week, last_week, this_month, last_month, last_7_days, last_30_days, all o custom. "El último mes" significa last_month, no this_month.
        - Después de una consulta exitosa, responde con finish; no repitas la misma herramienta.
        - Si ya recibiste DATOS REALES DE ENFOQUE, responde finish con esos números y el rango from/to.
        - Para terminar usa action=finish, arguments={} y message con el resumen factual.
        - No inventes herramientas ni campos.

        HERRAMIENTAS:
        get_state {"scope":"all|todos|calendar|habits|timer|widgets|focus_tasks|focus_history|finance"}
        focus.summarize {"period":"today|yesterday|this_week|last_week|this_month|last_month|last_7_days|last_30_days|all|custom","from":"yyyy-MM-dd","to":"yyyy-MM-dd","project":""}
        focus.query_sessions {"period":"...","project":"","limit":30}
        focus.compare_periods {"left":"last_month","right":"this_month","project":""}
        focus.list_projects {}
        todo.search {"query":"","open":true}
        add_todo {"text":"texto natural completo de la tarea, incluyendo fecha/hora/prioridad"}
        todo.update {"title":"...","new_title":"","due":"yyyy-MM-dd","priority":"none|medium|high"}
        complete_todo {"title":"...","done":true}
        delete_todo {"title":"..."}
        calendar.search {"query":"","from":"yyyy-MM-dd","to":"yyyy-MM-dd"}
        add_event {"title":"...","start":"yyyy-MM-dd HH:mm","minutes":60,"all_day":false,"reminder_minutes":10,"notes":""}
        calendar.update {"title":"...","start":"yyyy-MM-dd HH:mm","minutes":60}
        complete_event {"title":"...","date":"yyyy-MM-dd","done":true}
        delete_event {"title":"..."}
        habits.search {"query":""}
        add_habit {"name":"...","cadence":"daily|weekdays|weekly","times_per_week":3}
        mark_habit {"name":"...","date":"yyyy-MM-dd","done":true}
        habits.update {"name":"...","new_name":"","cadence":"daily|weekdays|weekly"}
        archive_habit {"name":"..."}
        notes.search {"query":""}
        add_note {"title":"...","text":"...","color":"paper|yellow|mint|blue|rose|lavender"}
        notes.append {"title":"...","text":"..."}
        add_widget {"kind":"notes|todo|habits|calendar|stats|timer|finance","title":"opcional"}
        workspace.list {}
        workspace.move_widget {"title":"...","x":0,"y":0}
        add_focus_task {"name":"...","project":"Sin proyecto","estimate":1}
        timer {"command":"start|pause|reset|skip|select","phase":"focus|short|long","task":""}
        timer.inspect {}
        set_focus_duration {"minutes":25}
        settings.read {}
        settings.update {"focus_minutes":25,"short_minutes":5,"long_minutes":15,"daily_goal_minutes":120}
        memory.search {"query":""}
        memory.propose {"text":"...","kind":"preference"}
        memory.confirm {"text":"...","kind":"preference"}
        memory.forget {"text":"..."}
        finance.summary {"month":"yyyy-MM-dd"}
        finance.search {"query":""}
        finance.add {"text":"+2500 salario | -20 chatgpt | sub cursor 20 | fijo renta 800"}
        finance.add_recurring {"title":"...","amount":"20","subscription":true,"category":"ai|hosting|software|rent"}
        finance.mark_paid {"title":"..."}
        finance.delete {"title":"..."}
        finance.archive {"title":"..."}
        finish {}
        """.Replace("__TODAY__", (clock ?? DateTime.Now).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static bool Es() => Strings.Culture.TwoLetterISOLanguageName == "es";
    private static string Trim(string text, int max) => text.Length <= max ? text : text[..max] + "…";
}
