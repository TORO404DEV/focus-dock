using PomoDock.Core;

namespace PomoDock.Web;

/// <summary>A To Do post-it floating above the workspace, where it was last dropped.</summary>
internal sealed class FloatingNote(TodoTask task, int left, int top)
{
    public TodoTask Task { get; } = task;
    public int Left { get; set; } = left;
    public int Top { get; set; } = top;
    /// <summary>Stacking order: the note touched last is on top, without moving it in the page.</summary>
    public int Z { get; set; }
}

public partial class MainWindow
{
    private readonly List<FloatingNote> floatingNotes = [];
    private int floatingTop;
    private Guid? notesHistoryFor;

    /// <summary>Opens a task's post-it, or brings it forward when it is already open.</summary>
    internal void OpenTodoNote(TodoTask task)
    {
        var open = floatingNotes.FirstOrDefault(note => note.Task.Id == task.Id);
        if (open is not null) { BringNoteForward(open); return; }
        // Cascade from the top of the workspace so a new note is not born under the To Do card.
        int stack = floatingNotes.Count;
        floatingNotes.Add(new FloatingNote(task, 28 + stack * 22, 96 + stack * 22) { Z = ++floatingTop });
        StateHasChanged();
    }

    private void BringNoteForward(FloatingNote note)
    {
        if (note.Z == floatingTop) return;
        note.Z = ++floatingTop;
        StateHasChanged();
    }

    /// <summary>The page moved a post-it by its header; it stays where it was dropped.</summary>
    [Microsoft.JSInterop.JSInvokable]
    public void FloatMoved(string id, double left, double top)
    {
        if (!Guid.TryParse(id, out var key) || floatingNotes.FirstOrDefault(note => note.Task.Id == key) is not { } note) return;
        note.Left = (int)Math.Round(left);
        note.Top = (int)Math.Round(top);
    }

    private void CloseTodoNote(FloatingNote note)
    {
        floatingNotes.Remove(note);
        StateHasChanged();
    }

    internal void ShowNotesHistory(Guid current)
    {
        notesHistoryFor = current;
        StateHasChanged();
    }

    private void CloseNotesHistory()
    {
        notesHistoryFor = null;
        StateHasChanged();
    }
}
