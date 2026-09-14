namespace SmartX.Client.Services;

/// <summary>Tiny in-memory notification bus so any page can show a crisp feedback toast.</summary>
public sealed class ToastService
{
    public sealed record Toast(Guid Id, string Kind, string Title, string? Detail, DateTimeOffset At);

    private readonly List<Toast> _toasts = new();
    public IReadOnlyList<Toast> Toasts => _toasts;
    public event Action? Changed;

    public void Show(string kind, string title, string? detail = null)
    {
        var t = new Toast(Guid.NewGuid(), kind, title, detail, DateTimeOffset.Now);
        _toasts.Add(t);
        if (_toasts.Count > 4) _toasts.RemoveAt(0);
        Changed?.Invoke();
        _ = Task.Delay(4500).ContinueWith(_ => Dismiss(t.Id));
    }

    public void Success(string title, string? detail = null) => Show("success", title, detail);
    public void Error(string title, string? detail = null) => Show("error", title, detail);
    public void Info(string title, string? detail = null) => Show("info", title, detail);

    public void Dismiss(Guid id)
    {
        if (_toasts.RemoveAll(t => t.Id == id) > 0) Changed?.Invoke();
    }
}
