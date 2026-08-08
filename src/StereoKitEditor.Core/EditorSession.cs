using StereoKitEditor.Scene;

namespace StereoKitEditor.Core;

public sealed class EditorSession
{
    public EditorSession(SceneDocument document, string scenePath, bool migratedFromFormat1 = false)
    {
        Document = document;
        ScenePath = Path.GetFullPath(scenePath);
        RequiresMigrationBackup = migratedFromFormat1;
        IsDirty = migratedFromFormat1;
    }

    public SceneDocument Document { get; private set; }
    public string ScenePath { get; private set; }
    public Guid? SelectedEntityId { get; private set; }
    public long Revision { get; private set; }
    public bool IsDirty { get; private set; }
    public bool RequiresMigrationBackup { get; private set; }
    public CommandHistory History { get; } = new();

    public event EventHandler<SessionChangedEventArgs>? Changed;

    public void Select(Guid? entityId)
    {
        if (entityId is not null && Document.FindEntity(entityId.Value) is null)
        {
            throw new ArgumentException("The selected entity must belong to the active scene.", nameof(entityId));
        }

        if (SelectedEntityId == entityId)
        {
            return;
        }

        SelectedEntityId = entityId;
        Changed?.Invoke(this, new(SessionChangeKind.Selection, Revision));
    }

    public void Execute(ISceneCommand command)
    {
        History.Execute(Document, command);
        MarkDocumentChanged();
    }

    public bool Undo()
    {
        if (!History.Undo(Document))
        {
            return false;
        }

        MarkDocumentChanged();
        return true;
    }

    public bool Redo()
    {
        if (!History.Redo(Document))
        {
            return false;
        }

        MarkDocumentChanged();
        return true;
    }

    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        if (RequiresMigrationBackup && File.Exists(ScenePath))
        {
            var backupPath = ScenePath + ".format1.bak";
            if (!File.Exists(backupPath))
            {
                File.Copy(ScenePath, backupPath);
            }
        }

        await SceneSerializer.SaveAtomicAsync(Document, ScenePath, cancellationToken);
        RequiresMigrationBackup = false;
        IsDirty = false;
        Changed?.Invoke(this, new(SessionChangeKind.Saved, Revision));
    }

    public void Replace(SceneDocument document, string scenePath, bool migratedFromFormat1 = false)
    {
        Document = document;
        ScenePath = Path.GetFullPath(scenePath);
        SelectedEntityId = document.Roots.FirstOrDefault()?.Id;
        Revision++;
        RequiresMigrationBackup = migratedFromFormat1;
        IsDirty = migratedFromFormat1;
        History.Clear();
        Changed?.Invoke(this, new(SessionChangeKind.Reloaded, Revision));
    }

    public void Recover(SceneDocument document)
    {
        Document = document;
        SelectedEntityId = document.Roots.FirstOrDefault()?.Id;
        Revision++;
        RequiresMigrationBackup = false;
        IsDirty = true;
        History.Clear();
        Changed?.Invoke(this, new(SessionChangeKind.Recovered, Revision));
    }

    private void MarkDocumentChanged()
    {
        Revision++;
        IsDirty = true;
        Changed?.Invoke(this, new(SessionChangeKind.Document, Revision));
    }
}

public sealed record SessionChangedEventArgs(SessionChangeKind Kind, long Revision);

public enum SessionChangeKind
{
    Document,
    Selection,
    Saved,
    Reloaded,
    Recovered,
}
