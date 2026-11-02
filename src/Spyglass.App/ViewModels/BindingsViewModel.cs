using System.Collections.ObjectModel;
using Avalonia.Threading;
using Spyglass.App.Core;
using Spyglass.Protocol.Messages;

namespace Spyglass.App.ViewModels;

/// <summary>Bindings page (wireframe W4): aggregated binding errors, pushed live by the agent.</summary>
public sealed class BindingsViewModel : ObservableObject
{
    readonly AgentSession _session;
    readonly Action<int> _showElement;

    public BindingsViewModel(AgentSession session, Action<int> showElement)
    {
        _session = session;
        _showElement = showElement;
        session.BindingErrors += e => Dispatcher.UIThread.Post(() => Merge(e));
    }

    public ObservableCollection<BindingErrorRowViewModel> Errors { get; } = new();
    public int Count => Errors.Count;
    public bool IsEmpty => Errors.Count == 0;
    public bool HasErrors => !IsEmpty;
    public string CountText => Count.ToString();

    public async Task LoadAsync()
    {
        try { Merge(await _session.GetBindingErrorsAsync()); }
        catch { /* agent without bindingErrors capability, or disconnected */ }
    }

    void Merge(IReadOnlyList<BindingErrorDto> errors)
    {
        foreach (var e in errors)
        {
            var existing = Errors.FirstOrDefault(r => r.Key == e.Key);
            if (existing is not null) Errors.Remove(existing);
            Errors.Insert(0, new BindingErrorRowViewModel(e, _showElement));   // most recent first
        }
        Raise(nameof(Count));
        Raise(nameof(IsEmpty));
        Raise(nameof(HasErrors));
        Raise(nameof(CountText));
    }
}

public sealed class BindingErrorRowViewModel(BindingErrorDto e, Action<int> showElement) : ObservableObject
{
    string _openError = "";
    public bool CanOpen { get; } = e.Location is not null;
    public string OpenError { get => _openError; private set { Set(ref _openError, value); Raise(nameof(HasOpenError)); } }
    public bool HasOpenError => _openError.Length > 0;
    public RelayCommand OpenCommand => _open ??= new RelayCommand(() => OpenError = OpenInEditor.Open(e.Location));
    RelayCommand? _open;

    public string Key { get; } = e.Key;
    public string Title { get; } = $"{e.TargetType}.{e.Property}  ·  {e.Path}";
    public string Message { get; } = e.Message;
    public string Meta { get; } = string.Join("  ·  ", new[]
    {
        e.Location is { } l ? $"{Path.GetFileName(l.Uri)}:{l.Line}" : "no XAML location",
        $"DataContext {e.DataContextType?.Split('.').Last() ?? "(null)"}",
        $"last {e.LastTime:HH:mm:ss}",
    });
    public string CountText { get; } = $"×{e.Count}";
    public bool CanShow { get; } = e.TargetId is not null;
    public RelayCommand ShowCommand { get; } = new(() => { if (e.TargetId is { } id) showElement(id); });
}
