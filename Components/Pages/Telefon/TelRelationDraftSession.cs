using System.Text.Json;
using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

/// <summary>Локальная область ввода вложенного редактора в текущей регистрации Tel.</summary>
public sealed class TelRelationDraftSession<T> where T : class
{
    private readonly ClayDraftRegistration _registration;
    private readonly CancellationToken _cancellation;
    private readonly Func<bool> _current;
    private readonly Action<string?> _remember;
    private readonly Func<T, Task<long>> _accept;
    private string? _published;
    private bool _restoreRaw;
    private bool _triedRaw;
    private bool _autoSaveFailed;
    private bool _closed;
    private long _publishSequence;

    internal TelRelationDraftSession(ClayDraftRegistration registration, string scope, string rootClass,
        CancellationToken cancellation, Func<bool> current, Action<string?> remember, Func<T, Task<long>> accept,
        string? stored, bool raw, T original, DateTime originalChangedAtUtc)
    {
        _registration = registration;
        _cancellation = cancellation;
        _current = current;
        _remember = remember;
        _accept = accept;
        Scope = scope;
        RootClass = rootClass;
        Original = original;
        OriginalChangedAtUtc = originalChangedAtUtc;
        var restored = stored is null ? null : JsonSerializer.Deserialize<RelationDraft>(stored);
        Initial = restored?.Current ?? original;
        Conflicted = restored is not { Version: 1, Original: not null, Current: not null }
            ? stored is not null || raw
            : restored.OriginalChangedAtUtc != originalChangedAtUtc ||
              JsonSerializer.Serialize(restored.Original) != JsonSerializer.Serialize(original);
        _published = stored;
        _restoreRaw = raw;
        if (Conflicted)
            Error = "Связь изменилась или её исходный снимок недоступен. Ввод оставлен для сверки.";
    }

    /// <summary>Точный ключ дочерней области в регистрации текущего номера и сотрудника.</summary>
    public string Scope { get; }

    /// <summary>Класс DOM-корня текущей регистрации для портала диалога.</summary>
    public string RootClass { get; }

    internal T Initial { get; }
    private T Original { get; }
    private DateTime OriginalChangedAtUtc { get; }
    internal bool Conflicted { get; }
    internal bool Busy { get; private set; }
    internal bool NeedsRawRestore => _restoreRaw;
    internal bool CanApply => !_closed && !Conflicted && !Busy && !_restoreRaw;
    internal string? Error { get; private set; }

    internal async Task InitializeAsync()
    {
        if (_published is null && !Conflicted)
            await PersistAsync(Initial);
    }

    internal async Task<bool> RenderedAsync(T value)
    {
        if (_closed || !_current() || Busy)
            return false;
        if (!_restoreRaw)
        {
            var previous = Error;
            await TryPersistAsync(value);
            return previous != Error;
        }
        if (_triedRaw)
            return false;
        _triedRaw = true;
        await RestoreAsync();
        return true;
    }

    internal async Task RestoreAsync()
    {
        if (_closed || !_current() || Busy || !_restoreRaw)
            return;
        Busy = true;
        try
        {
            var result = await _registration.ApplyRawWithResultAsync(Scope, _cancellation);
            if (!_current())
                return;
            _restoreRaw = result is null || result.Pending.Count > 0;
            if (_restoreRaw)
                Error = "Часть ввода ещё не восстановлена. Повторите восстановление.";
            else if (!Conflicted)
                Error = null;
        }
        catch (Exception ex) when (IsDraftError(ex))
        {
            Error = "Не удалось восстановить ввод связи.";
        }
        finally { Busy = false; }
    }

    internal async Task<bool> ApplyAsync(T value)
    {
        if (!CanApply || !_current())
            return false;
        Busy = true;
        try
        {
            await PersistAsync(value);
            if (!_current())
                return false;
            var parentRevision = await _accept(value);
            if (parentRevision <= 0 || !_current())
                return false;
            return await ClearAsync(parentRevision);
        }
        catch (Exception ex) when (IsDraftError(ex))
        {
            Error = "Не удалось принять черновик связи. Ввод оставлен в редакторе.";
            return false;
        }
        finally { Busy = false; }
    }

    internal async Task<bool> CancelAsync()
    {
        if (_closed || !_current())
            return true;
        if (Busy)
            return false;
        Busy = true;
        try
        {
            var state = await _registration.ReadEditorScopeAsync(Scope, _cancellation);
            return await ClearAsync(state.Revision);
        }
        catch (Exception ex) when (IsDraftError(ex))
        {
            Error = "Не удалось отменить черновик связи. Ввод оставлен в редакторе.";
            return false;
        }
        finally { Busy = false; }
    }

    private async Task<bool> ClearAsync(long expectedRevision)
    {
        if (!_current() || !await _registration.ReplaceEditorScopeAsync(Scope, expectedRevision, null, _cancellation))
        {
            Error = "Черновик изменился. Проверьте ввод перед повтором.";
            return false;
        }
        _closed = true;
        _remember(null);
        return _current();
    }

    private async Task TryPersistAsync(T value)
    {
        if (Conflicted)
            return;
        try
        {
            await PersistAsync(value);
            if (_autoSaveFailed)
                Error = null;
            _autoSaveFailed = false;
        }
        catch (Exception ex) when (IsDraftError(ex))
        {
            _autoSaveFailed = true;
            Error = "Не удалось сохранить черновик связи.";
        }
    }

    private async Task PersistAsync(T value)
    {
        var state = JsonSerializer.Serialize(new RelationDraft(1, Original, value, OriginalChangedAtUtc));
        if (_closed || !_current() || _published == state)
            return;
        var publish = ++_publishSequence;
        if (await _registration.SaveTypedScopeAsync(Scope, state, _cancellation) <= 0)
            throw new InvalidOperationException("Хранилище не подтвердило черновик связи.");
        if (!_closed && _current() && publish == _publishSequence)
        {
            _published = state;
            _remember(state);
        }
    }

    private static bool IsDraftError(Exception exception) =>
        exception is InvalidOperationException or OperationCanceledException or Microsoft.JSInterop.JSException
            or Microsoft.JSInterop.JSDisconnectedException;

    private sealed record RelationDraft(int Version, T Original, T Current, DateTime OriginalChangedAtUtc);
}
