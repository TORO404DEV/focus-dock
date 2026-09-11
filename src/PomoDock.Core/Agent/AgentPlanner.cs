namespace PomoDock.Core.Agent;

/// <summary>
/// Builds an <see cref="AgentPlan"/> from user text. Prefer deterministic routing for focus
/// history; otherwise emit a draft plan the LLM / host can refine.
/// </summary>
public static class AgentPlanner
{
    public static AgentPlan FromUserText(string text, bool preferDeterministicFocus = true)
    {
        text = (text ?? "").Trim();
        var plan = new AgentPlan { Understood = text.Length == 0 ? "(vacío)" : SummarizeIntent(text) };

        if (text.Length == 0)
        {
            plan.Status = AgentPlanStatus.Failed;
            return plan;
        }

        if (preferDeterministicFocus && FocusSummary.LooksLikeFocusHistoryQuestion(text))
        {
            var period = FocusSummary.InferPeriodFromText(text);
            var project = InferProject(text);
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["period"] = PeriodToken(period)
            };
            if (project is not null) args["project"] = project;
            plan.Steps.Add(new AgentPlanStep
            {
                Call = new AgentToolCall { Name = "focus.summarize", Arguments = args },
                Kind = AgentToolKind.Read,
                Label = $"Consultar enfoque ({PeriodToken(period)})"
            });
            plan.Status = AgentPlanStatus.Draft;
            return plan;
        }

        if (LooksLikeRemember(text, out var memoryText))
        {
            plan.Steps.Add(new AgentPlanStep
            {
                Call = new AgentToolCall
                {
                    Name = "memory.remember",
                    Arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["text"] = memoryText }
                },
                Kind = AgentToolKind.Write,
                Label = "Guardar en memoria"
            });
            plan.Status = AgentPlanStatus.Draft;
            return plan;
        }

        if (LooksLikeForget(text, out var forgetText))
        {
            plan.Steps.Add(new AgentPlanStep
            {
                Call = new AgentToolCall
                {
                    Name = "memory.forget",
                    Arguments = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["text"] = forgetText }
                },
                Kind = AgentToolKind.Destructive,
                Label = "Olvidar recuerdo"
            });
            plan.Status = AgentPlanStatus.Draft;
            return plan;
        }

        if (LooksLikeTimer(text, out var timerTool, out var phase))
        {
            var args = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (phase is not null) args["phase"] = phase;
            plan.Steps.Add(new AgentPlanStep
            {
                Call = new AgentToolCall { Name = timerTool, Arguments = args },
                Kind = AgentToolCatalog.KindOf(timerTool),
                Label = timerTool
            });
            plan.Status = AgentPlanStatus.Draft;
            return plan;
        }

        // Fallback: ask the model path (runtime) — leave empty steps and mark as needing LLM.
        plan.Status = AgentPlanStatus.Draft;
        return plan;
    }

    public static AgentPlan FromToolCalls(string understood, IEnumerable<AgentToolCall> calls)
    {
        var plan = new AgentPlan { Understood = understood, Status = AgentPlanStatus.Draft };
        foreach (var call in calls)
        {
            plan.Steps.Add(new AgentPlanStep
            {
                Call = call,
                Kind = AgentToolCatalog.KindOf(call.Name),
                Label = call.Name
            });
        }
        return plan;
    }

    public static void MarkAwaitingApproval(AgentPlan plan)
    {
        plan.Status = plan.NeedsApproval ? AgentPlanStatus.AwaitingApproval : AgentPlanStatus.Approved;
    }

    public static string PeriodToken(FocusPeriodKind period) => period switch
    {
        FocusPeriodKind.Today => "today",
        FocusPeriodKind.Yesterday => "yesterday",
        FocusPeriodKind.ThisWeek => "this_week",
        FocusPeriodKind.LastWeek => "last_week",
        FocusPeriodKind.ThisMonth => "this_month",
        FocusPeriodKind.LastMonth => "last_month",
        FocusPeriodKind.AllTime => "all_time",
        FocusPeriodKind.Custom => "custom",
        _ => "this_week"
    };

    private static string SummarizeIntent(string text)
    {
        if (FocusSummary.LooksLikeFocusHistoryQuestion(text))
            return $"Consulta de historial de enfoque ({PeriodToken(FocusSummary.InferPeriodFromText(text))}).";
        if (text.Length <= 120) return text;
        return text[..117] + "…";
    }

    private static string? InferProject(string text)
    {
        // "proyecto X" / "project X"
        foreach (var marker in new[] { "proyecto ", "project " })
        {
            var idx = text.ToLowerInvariant().IndexOf(marker, StringComparison.Ordinal);
            if (idx < 0) continue;
            var rest = text[(idx + marker.Length)..].Trim().Trim('"', '«', '»', '\'');
            if (rest.Length == 0) return null;
            var end = rest.IndexOfAny(['?', '.', ',', ';', '!']);
            return (end > 0 ? rest[..end] : rest).Trim();
        }
        return null;
    }

    private static bool LooksLikeRemember(string text, out string memory)
    {
        memory = "";
        var lower = text.ToLowerInvariant();
        foreach (var prefix in new[] { "recuerda que ", "recuerda ", "remember that ", "remember " })
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
            memory = text[prefix.Length..].Trim();
            return memory.Length > 0;
        }
        return false;
    }

    private static bool LooksLikeForget(string text, out string memory)
    {
        memory = "";
        var lower = text.ToLowerInvariant();
        foreach (var prefix in new[] { "olvida que ", "olvida ", "forget that ", "forget " })
        {
            if (!lower.StartsWith(prefix, StringComparison.Ordinal)) continue;
            memory = text[prefix.Length..].Trim();
            return memory.Length > 0;
        }
        return false;
    }

    private static bool LooksLikeTimer(string text, out string tool, out string? phase)
    {
        tool = "";
        phase = null;
        var t = text.ToLowerInvariant();
        if (t.Contains("paus") || t.Contains("pause")) { tool = "timer.pause"; return true; }
        if (t.Contains("salta") || t.Contains("skip")) { tool = "timer.skip"; return true; }
        if (t.Contains("descanso largo") || t.Contains("long break")) { tool = "timer.set_phase"; phase = "LongBreak"; return true; }
        if (t.Contains("descanso") || t.Contains("short break") || t.Contains("break")) { tool = "timer.set_phase"; phase = "ShortBreak"; return true; }
        if (t.Contains("inicia") || t.Contains("arranca") || t.Contains("start") || t.Contains("pomodoro"))
        {
            tool = "timer.start";
            if (t.Contains("enfoque") || t.Contains("focus")) phase = "Focus";
            return true;
        }
        return false;
    }
}
