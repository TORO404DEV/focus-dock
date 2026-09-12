using System.Text.RegularExpressions;

namespace PomoDock.Core;

/// <summary>Turns tool receipts into a sentence a person would say after the work is done.</summary>
public static class AgentReply
{
    public static bool IsScaffold(string? summary)
    {
        if (string.IsNullOrWhiteSpace(summary)) return true;
        return Regex.IsMatch(summary.Trim(), """
            ^P[aá]gina\s+\d+\s+(abierta|creada)$
            |^Estás en la p[aá]gina\s+\d+$
            |^Page\s+\d+\s+(opened|created)$
            |^You are on page\s+\d+$
            """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);
    }

    public static IReadOnlyList<AgentActionReceipt> Visible(IReadOnlyList<AgentActionReceipt>? receipts)
    {
        var useful = (receipts ?? []).Where(receipt => !string.IsNullOrWhiteSpace(receipt.Summary) && receipt.Summary != "✓").ToList();
        if (useful.Count <= 1) return useful;
        var work = useful.Where(receipt => !IsScaffold(receipt.Summary)).ToList();
        return work.Count > 0 ? work : useful;
    }

    /// <summary>
    /// Voice + chat copy while a plan is waiting for confirmation. Never claims the work is done.
    /// </summary>
    public static string Pending(AgentPlan plan, bool spanish)
    {
        var labels = plan.Steps
            .Select(step => (step.Label.Length > 0 ? step.Label : step.Tool).Trim())
            .Where(label => label.Length > 0)
            .Take(4)
            .ToList();
        string what = labels.Count > 0
            ? string.Join(spanish ? "; " : "; ", labels)
            : (plan.Understood.Trim().Length > 0 ? plan.Understood.Trim() : (spanish ? "estos cambios" : "these changes"));
        what = StripDoneClaim(what);
        if (what.Length == 0) what = spanish ? "estos cambios" : "these changes";
        if (spanish)
            return $"Tengo un plan listo, pero todavía no lo ejecuté: {what}. Di que sí o pulsa ejecutar para confirmarlo.";
        return $"I have a plan ready, but I have not run it yet: {what}. Say yes or press run to confirm.";
    }

    /// <summary>
    /// After tools run, never keep “pending confirmation” copy. Prefer receipt summaries so voice
    /// and chat match ACCIONES REALIZADAS.
    /// </summary>
    public static string After(string planMessage, IReadOnlyList<AgentActionReceipt> receipts, bool spanish)
    {
        var visible = Visible(receipts);
        string fromReceipts = visible.Count == 0
            ? ""
            : string.Join(". ", visible.Select(receipt => receipt.Summary.Trim().TrimEnd('.'))) + ".";

        string message = (planMessage ?? "").Trim();
        if (visible.Count > 0 && (AsksConfirmation(message) || !LooksHonestlyDone(message)))
            return fromReceipts;

        if (LooksHonestlyDone(message))
        {
            bool promisedPage = Regex.IsMatch(message, @"p[aá]gina nueva|new page", RegexOptions.IgnoreCase);
            bool createdPage = receipts.Any(receipt =>
                string.Equals(receipt.Tool, "workspace.add_page", StringComparison.OrdinalIgnoreCase));
            if (promisedPage && !createdPage)
                message = Regex.Replace(message, @"\s*en una p[aá]gina nueva", spanish ? " en esta página" : " on this page", RegexOptions.IgnoreCase);
            return message;
        }

        if (fromReceipts.Length > 0) return fromReceipts;
        return message.Length > 0 ? message : (spanish ? "Listo." : "Done.");
    }

    public static bool AsksConfirmation(string message) =>
        message.Length > 0 && Regex.IsMatch(message, """
            \bconfirm
            |propuest
            |preparad
            |falta que
            |en cuanto (lo )?confirm
            |solo falta
            |pendiente de
            |todavía no
            |todavia no
            |awaiting
            |as soon as you confirm
            |when you confirm
            |pulsa ejecutar
            |press run
            """, RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace);

    public static bool LooksHonestlyDone(string message)
    {
        if (message.Length == 0 || AsksConfirmation(message)) return false;
        if (Regex.IsMatch(message, @"^(Plan:|Voy a\b|I will\b|Dej[eé] propuesto|Dej[eé] preparado|Tengo un plan)", RegexOptions.IgnoreCase))
            return false;
        return Regex.IsMatch(message, @"^(Listo|Done|Te dej|Cre[eé]|Añad[ií]|Agend|Marqu[eé]|Archiv|Borr[eé]|Elimin[eé]|Pus[eé]|Qued[oó]|I (left|created|added|set|marked|deleted|removed))", RegexOptions.IgnoreCase)
            || Regex.IsMatch(message, @"\b(qued[oó] eliminad|ya (está|esta|quedó|quedo) (borrad|eliminad)|all (deleted|removed))\b", RegexOptions.IgnoreCase);
    }

    private static string StripDoneClaim(string text)
    {
        string value = Regex.Replace(text, @"^(Listo[,.]?\s*|Done[,.]?\s*)", "", RegexOptions.IgnoreCase).Trim();
        value = Regex.Replace(value, @"\b(ya (lo )?(elimin[eé]|borr[eé]|qued[oó] eliminad\w*)|all (expenses )?deleted)\b", "", RegexOptions.IgnoreCase);
        return Regex.Replace(value, @"\s{2,}", " ").Trim(' ', ',', ';', '.');
    }
}
