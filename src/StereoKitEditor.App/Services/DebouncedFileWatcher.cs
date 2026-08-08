namespace StereoKitEditor.App.Services;

public sealed class DebouncedFileWatcher : IDisposable
{
    private static readonly HashSet<string> SourceExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".cs", ".csproj", ".fs", ".fsproj", ".vb", ".vbproj", ".props", ".targets", ".resx",
    };
    private static readonly HashSet<string> AssetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".glb", ".gltf", ".png", ".jpg", ".jpeg", ".skmeta",
    };

    private readonly FileSystemWatcher _watcher;
    private readonly Func<string, bool> _include;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly HashSet<string> _pending = new(StringComparer.OrdinalIgnoreCase);
    private Timer? _timer;
    private bool _disposed;

    public DebouncedFileWatcher(
        string root,
        Func<string, bool> include,
        TimeSpan? debounce = null)
    {
        Root = Path.GetFullPath(root);
        _include = include ?? throw new ArgumentNullException(nameof(include));
        _debounce = debounce ?? TimeSpan.FromMilliseconds(650);
        Directory.CreateDirectory(Root);
        _watcher = new FileSystemWatcher(Root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            EnableRaisingEvents = false,
        };
        _watcher.Changed += HandleChanged;
        _watcher.Created += HandleChanged;
        _watcher.Deleted += HandleChanged;
        _watcher.Renamed += HandleRenamed;
        _watcher.Error += (_, args) => Error?.Invoke(this, args.GetException());
    }

    public string Root { get; }
    public bool IsEnabled
    {
        get => _watcher.EnableRaisingEvents;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _watcher.EnableRaisingEvents = value;
            if (!value)
            {
                lock (_gate)
                {
                    _pending.Clear();
                    _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
                }
            }
        }
    }

    public event EventHandler<IReadOnlyList<string>>? FilesChanged;
    public event EventHandler<Exception>? Error;

    public static bool IsProjectSourcePath(string path) =>
        !HasIgnoredDirectory(path) && SourceExtensions.Contains(Path.GetExtension(path));

    public static bool IsAssetSourcePath(string path) =>
        !HasIgnoredDirectory(path) && AssetExtensions.Contains(Path.GetExtension(path));

    private static bool HasIgnoredDirectory(string path)
    {
        var segments = Path.GetFullPath(path).Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return segments.Any(segment => segment.Equals("bin", StringComparison.OrdinalIgnoreCase)
            || segment.Equals("obj", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".git", StringComparison.OrdinalIgnoreCase)
            || segment.Equals(".skinny", StringComparison.OrdinalIgnoreCase));
    }

    private void HandleChanged(object sender, FileSystemEventArgs args) => Queue(args.FullPath);

    private void HandleRenamed(object sender, RenamedEventArgs args)
    {
        Queue(args.OldFullPath);
        Queue(args.FullPath);
    }

    private void Queue(string path)
    {
        if (!_include(path))
        {
            return;
        }

        lock (_gate)
        {
            _pending.Add(Path.GetFullPath(path));
            _timer ??= new Timer(_ => Flush(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
        }
    }

    private void Flush()
    {
        string[] changed;
        lock (_gate)
        {
            if (_pending.Count == 0 || _disposed)
            {
                return;
            }

            changed = _pending.Order(StringComparer.OrdinalIgnoreCase).ToArray();
            _pending.Clear();
        }

        FilesChanged?.Invoke(this, changed);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watcher.EnableRaisingEvents = false;
        _watcher.Dispose();
        _timer?.Dispose();
    }
}
