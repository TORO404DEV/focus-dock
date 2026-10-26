using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>What a dialog asks: pick one, type one, read one, or pick a widget.</summary>
public enum DialogKind { Choose, Prompt, Alert, ChooseWidget }

/// <summary>
/// One open dialog. The desktop's dialogs block the thread until answered; in the browser the
/// same question is a task that completes when the person answers, so callers still read top
/// to bottom: <c>if (await Choose(...) != 0) return;</c>.
/// </summary>
public sealed class DialogRequest
{
    public Guid Id { get; } = Guid.NewGuid();
    public DialogKind Kind { get; init; }
    public string Title { get; init; } = "";
    public string Label { get; init; } = "";
    public string Value { get; set; } = "";
    public string[] Options { get; init; } = [];
    public TaskCompletionSource<(int Choice, string? Text)> Answer { get; } = new();
    public Action? Closed { get; set; }

    public void Complete(int choice, string? text = null)
    {
        Answer.TrySetResult((choice, text));
        Closed?.Invoke();
    }

    public void Cancel() => Complete(-1);
}

public partial class MainWindow
{
    private readonly List<DialogRequest> dialogs = [];

    private async Task<(int Choice, string? Text)> Ask(DialogRequest request)
    {
        request.Closed = () =>
        {
            dialogs.Remove(request);
            StateHasChanged();
        };
        dialogs.Add(request);
        StateHasChanged();
        return await request.Answer.Task;
    }

    internal async Task<int> Choose(string title, string[] options) =>
        (await Ask(new DialogRequest { Kind = DialogKind.Choose, Title = title, Options = options })).Choice;

    internal async Task<string?> Prompt(string title, string label, string value = "")
    {
        var answer = await Ask(new DialogRequest { Kind = DialogKind.Prompt, Title = title, Label = label, Value = value });
        return answer.Choice == 0 ? answer.Text?.Trim() : null;
    }

    internal async Task Alert(string title, string message) =>
        await Ask(new DialogRequest { Kind = DialogKind.Alert, Title = title, Label = message });

    internal async Task<int> ChooseWidget() =>
        (await Ask(new DialogRequest { Kind = DialogKind.ChooseWidget, Title = L.T("widgets.addTitle") })).Choice;
}
