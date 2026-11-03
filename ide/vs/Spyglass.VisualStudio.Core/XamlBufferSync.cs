namespace Spyglass.VisualStudio.Core;

/// <summary>
/// Editor buffers → hub, independent of the editor API (the VS Code extension's activate() logic, design/hot-reload §8):
/// only files some connected app can reload (hotReload/status.files) are sent; edits are debounced per document
/// (<see cref="DelayMs"/>, 0 = only on save) and sent as full text; open documents are announced with didOpen whenever
/// they become reloadable or the connection is re-established (with the unsaved text when dirty), and didClose follows
/// when the editor closes them. The editor front end calls <see cref="Opened"/>, <see cref="Changed"/>,
/// <see cref="Saved"/> and <see cref="Closed"/>; the text is read lazily, only when it is sent.
/// </summary>
public sealed class XamlBufferSync : IDisposable
{
    sealed class Document(string path, Func<string> text, bool isDirty)
    {
        public string Path = path;
        public Func<string> Text = text;
        public bool IsDirty = isDirty;
        public long Version = 1;
    }

    readonly HubClient _client;
    readonly Debouncer _debounce;
    readonly object _gate = new();
    readonly Dictionary<string, Document> _docs = new(StringComparer.Ordinal);   // open in the editor, by PathKey
    readonly HashSet<string> _announced = new(StringComparer.Ordinal);            // didOpen sent on this connection
    bool _enabled = true;

    public XamlBufferSync(HubClient client)
    {
        _client = client;
        _debounce = new Debouncer(Send);
        _client.Connected += OnConnected;
        _client.StatusChanged += OnStatus;
    }

    /// <summary>Milliseconds without typing before an edit is sent; 0 or less = only on save.</summary>
    public int DelayMs { get; set; } = 300;

    /// <summary>Off: open documents are closed on the hub (the apps fall back to files on disk) and nothing is sent.</summary>
    public bool Enabled
    {
        get { lock (_gate) return _enabled; }
        set
        {
            List<string> close;
            lock (_gate)
            {
                if (_enabled == value) return;
                _enabled = value;
                close = value ? [] : _announced.Select(k => _docs.TryGetValue(k, out var d) ? d.Path : k).ToList();
                _announced.Clear();
            }
            foreach (var p in close) _client.DidClose(p);
            if (value) AnnounceAll();
        }
    }

    public static bool IsXaml(string path) =>
        path.EndsWith(".axaml", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase);

    /// <summary>Files announced to the hub on the current connection (for status display and tests).</summary>
    public IReadOnlyList<string> Announced
    {
        get { lock (_gate) return _announced.Select(k => _docs[k].Path).ToList(); }
    }

    /// <summary>The editor's current text of an open XAML document (null when it is not open).</summary>
    public string? TryGetText(string path)
    {
        Func<string>? text;
        lock (_gate) text = _docs.TryGetValue(HubClient.PathKey(path), out var d) ? d.Text : null;
        try { return text?.Invoke(); }
        catch { return null; }
    }

    public void Opened(string path, Func<string> text, bool isDirty)
    {
        if (!IsXaml(path)) return;
        var key = HubClient.PathKey(path);
        lock (_gate)
        {
            if (_docs.TryGetValue(key, out var d)) { d.Text = text; d.IsDirty = isDirty; }   // a second view of the same file
            else _docs[key] = new Document(path, text, isDirty);
        }
        Announce(key);
    }

    /// <summary>The buffer changed (typing, undo, paste…); <paramref name="text"/> reads the new content.</summary>
    public void Changed(string path, Func<string> text, bool isDirty)
    {
        if (!IsXaml(path)) return;
        var key = HubClient.PathKey(path);
        lock (_gate)
        {
            if (!_docs.TryGetValue(key, out var d)) _docs[key] = d = new Document(path, text, isDirty);
            d.Text = text;
            d.IsDirty = isDirty;
            d.Version++;
            if (!_enabled) return;
        }
        if (!_client.CanReload(path)) return;
        var ms = DelayMs;
        if (ms <= 0) return;   // only on save
        _debounce.Schedule(key, TimeSpan.FromMilliseconds(ms));
    }

    public void Saved(string path, Func<string>? text = null)
    {
        if (!IsXaml(path)) return;
        var key = HubClient.PathKey(path);
        lock (_gate)
        {
            if (!_docs.TryGetValue(key, out var d)) return;
            if (text is not null) d.Text = text;
            d.IsDirty = false;
            if (!_enabled) return;
        }
        // A save sends a pending edit now instead of waiting out the typing delay.
        if ((DelayMs <= 0 || _debounce.Cancel(key)) && _client.CanReload(path)) Send(key);
    }

    public void Closed(string path)
    {
        if (!IsXaml(path)) return;
        var key = HubClient.PathKey(path);
        _debounce.Cancel(key);
        bool wasAnnounced;
        lock (_gate)
        {
            _docs.Remove(key);
            wasAnnounced = _announced.Remove(key);
        }
        if (wasAnnounced) _client.DidClose(path);
    }

    public void Dispose()
    {
        _client.Connected -= OnConnected;
        _client.StatusChanged -= OnStatus;
        _debounce.Dispose();
    }

    void OnConnected()
    {
        lock (_gate) _announced.Clear();
        AnnounceAll();
    }

    void OnStatus(HotReloadStatusParams? status)
    {
        if (status is null) { lock (_gate) _announced.Clear(); return; }
        AnnounceAll();   // new apps may make more open files reloadable
    }

    void AnnounceAll()
    {
        List<string> keys;
        lock (_gate) keys = _docs.Keys.ToList();
        foreach (var k in keys) Announce(k);
    }

    /// <summary>didOpen once per connection; a dirty buffer (reconnected with unsaved edits) also gets didChange.</summary>
    /// <returns>True when the current text was sent.</returns>
    bool Announce(string key)
    {
        lock (_gate)
        {
            if (!_enabled || !_client.IsConnected || !_docs.TryGetValue(key, out var d) || _announced.Contains(key)) return false;
            if (!_client.CanReload(d.Path)) return false;
            _announced.Add(key);
            var text = d.Text();
            // Both under the lock: a concurrent Send must not slip a didChange in before this didOpen.
            _client.DidOpen(d.Path, d.Version, text);
            if (d.IsDirty) _client.DidChange(d.Path, d.Version, text);
            return d.IsDirty;
        }
    }

    void Send(string key)
    {
        if (Announce(key)) return;
        lock (_gate)
        {
            if (!_enabled || !_announced.Contains(key) || !_docs.TryGetValue(key, out var d)) return;
            _client.DidChange(d.Path, d.Version, d.Text());
        }
    }
}
