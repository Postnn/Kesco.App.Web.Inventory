using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Components.Select;
using Clayzor.Lib.Web.Controls.Services;
using Clayzor.Lib.Web.DBSelect.Equipment;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.JSInterop;
using MudBlazor;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

/// <summary>Чтение почтовых списков, подготовка и сверка черновика.</summary>
public partial class SlBlForm : IAsyncDisposable
{
    private const string DraftScope = "main";
    private readonly string _draftOwner = Guid.NewGuid().ToString("N");
    private readonly ClayEntityFormOptions _formOptions = new() { MaxWidth = "100%" };
    private string DraftRootSelector => $".slbl-draft-{_draftOwner}";
    private static readonly ClayToolbarButtonOptions SaveButton = new() { Text = "Сохранить", Icon = Icons.Material.Filled.Save };
    private static readonly ClayToolbarButtonOptions RefreshButton = new() { Text = "Обновить", Icon = Icons.Material.Filled.Refresh };
    private static readonly ClayToolbarButtonOptions CloseButton = new() { Text = "Отмена", Icon = Icons.Material.Filled.Close };
    private readonly ClaySelectOptions AddressOptions = new() { Label = "E-mail адрес", Required = true, AutoSelectSingle = true };
    private readonly ClayTextAreaOptions TextOptions = new() { Label = "Список адресов", Lines = 12, DraftKey = "slbl.text" };
    private static readonly ClayRadioOption<SlBlKind?>[] Kinds =
    [
        new(SlBlKind.Safe, "Список безопасных адресов (письма с этих адресов не будут попадать в спам-карантин)"),
        new(SlBlKind.Blocked, "Список заблокированных адресов (письма с этих адресов всегда будут попадать в спам-карантин)"),
    ];

    /// <summary>Назначает уникальный CSS-корень для изоляции черновика формы.</summary>
    public SlBlForm() => _formOptions.Class = $"slbl-draft-{_draftOwner}";

    /// <summary>Необязательный составной ключ ящика и списка.</summary>
    [Parameter] public string? Id { get; set; }

    [Inject] private DbManager Db { get; set; } = null!;
    [Inject] private AuthenticationStateProvider Authentication { get; set; } = null!;
    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    [Inject] private IDialogService Dialogs { get; set; } = null!;
    [Inject] private IJSRuntime JS { get; set; } = null!;
    [Inject] private NavigationManager Navigation { get; set; } = null!;

    private readonly CancellationTokenSource _lifetime = new();
    private Employee? CurrentUser { get; set; }
    private SlBlAddressSelectDataSource? _source;
    private SlBlLists? _lists;
    private SlBlKind? _kind;
    private string? _address;
    private string _text = "";
    private string _original = "";
    private string? _error;
    private bool _busy = true;
    private bool _disposed;
    private bool _initialRead = true;
    private bool _allowNavigation;
    private bool _restoreRaw;
    private SlBlDifferences? _differences;
    private SlBlIntent? _partialIntent;
    private ClayDraftRegistration? _draft;
    [Inject] private IServiceProvider Services { get; set; } = null!;
    private SlBlSqlSynchronization? _sync;
    private bool _available;
    private bool _needsReview;
    private bool _uncertain;
    private SlBlIntent? _pendingIntent;
    private long _pendingRevision;
    private bool Dirty => !string.Equals(_text, _original, StringComparison.Ordinal);

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (!_restoreRaw || _draft is null) return;
            _restoreRaw = false;
            try { await _draft.ApplyRawAsync(_lifetime.Token); }
            catch (Exception e) when (e is JSException or InvalidOperationException)
            {
                _error = "Часть ввода не удалось восстановить.";
                if (!_disposed) StateHasChanged();
            }
            return;
        }
        try
        {
            var identity = (await Authentication.GetAuthenticationStateAsync()).User.Identity;
            if (_disposed) return;
            CurrentUser = await Employee.GetCurrentAsync(Db, identity?.IsAuthenticated == true ? identity.Name : null, _lifetime.Token);
            if (_disposed) return;
            if (CurrentUser is null) { _error = "Не удалось подтвердить текущего сотрудника."; return; }
            _source = new(Db);
            _sync = new(Db, identity?.IsAuthenticated == true ? identity.Name : null, Services.GetService<ISlBlMailboxWritePolicy>());
            if (!SlBlKey.TryParse(Id, out var key)) { _error = "Некорректный адрес или вид списка в ссылке."; return; }
            if (key is not null) await LoadAddressAsync(key.Address, key.Kind);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось загрузить почтовые списки."; }
        finally { _initialRead = false; _busy = false; if (!_disposed) StateHasChanged(); }
    }

    private async Task ChangeAddressAsync(string? value)
    {
        if (_busy || _uncertain || value == _address) return;
        _busy = true;
        try { if (await CanDiscardAsync()) await LoadAddressAsync(value, null); }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось загрузить списки выбранного адреса."; }
        finally { _busy = false; }
    }

    private async Task LoadAddressAsync(string? address, SlBlKind? preferredKind)
    {
        var key = address is not null ? await SlBlData.FindAddressAsync(Db, address, _lifetime.Token) : null;
        if (_disposed) return;
        if (address is not null && key is null) { _error = "Почтовый адрес не найден или недоступен."; return; }
        var lists = key is not null ? await SlBlData.ReadAsync(Db, key, _lifetime.Token) : null;
        if (_disposed) return;
        await DiscardAsync();
        _address = key;
        _lists = lists;
        _error = null;
        await SetKindAsync(preferredKind ?? lists?.InitialKind);
    }

    private async Task ChangeKindAsync(SlBlKind? value)
    {
        if (_busy || _uncertain || value == _kind) return;
        _busy = true;
        try
        {
            if (!await CanDiscardAsync()) return;
            await DiscardAsync();
            await SetKindAsync(value);
        }
        finally { _busy = false; }
    }

    private async Task SetKindAsync(SlBlKind? kind)
    {
        _kind = kind;
        _available = false;
        _needsReview = false;
        _original = _text = kind is { } selected ? _lists?.GetText(selected) ?? "" : "";
        _partialIntent = null;
        _differences = null;
        _restoreRaw = false;
        if (_address is null || kind is null || CurrentUser is null) return;
        try
        {
            var registration = await Drafts.RegisterHandleAsync("slbl", $"{_address}:{(byte)kind}",
                CurrentUser.Id.ToString(CultureInfo.InvariantCulture), 1, DraftRootSelector, _lifetime.Token);
            if (_disposed) { await registration.DisposeAsync(); return; }
            _draft = registration;
            _uncertain = _draft.Restore.Operation is not null;
            if (_draft.Restore.OperationSnapshot is { } marker)
            {
                var pending = JsonSerializer.Deserialize<SlBlIntent>(marker.Data);
                if (marker.Kind != "slbl.sync" || pending is null || pending.OperationId == Guid.Empty ||
                    pending.Address != _address || pending.Kind != kind || pending.Desired is null ||
                    SlBlData.Validate(pending.Desired) is not null) throw new JsonException();
                _pendingIntent = pending;
                _pendingRevision = marker.Revision;
            }
            if (_draft.Restore.TypedScopes?.TryGetValue(DraftScope, out var json) == true)
            {
                using var payload = JsonDocument.Parse(json);
                if (payload.RootElement.ValueKind == JsonValueKind.String)
                {
                    _text = payload.RootElement.GetString() ?? _original;
                    _needsReview = true;
                    _error = "Черновик прежней версии. Перед записью требуется сверка исходного списка.";
                }
                else
                {
                    var restored = JsonSerializer.Deserialize<ListDraft>(json) ?? throw new JsonException();
                    if (restored.Address != _address || restored.Kind != kind) throw new JsonException();
                    _needsReview = restored.NeedsReview || _original != restored.Original;
                    if (_needsReview) _error = "Список в базе изменился. Сверьте его с черновиком.";
                    if (restored.PartialIntent is { } partial && partial.Address == _address && partial.Kind == kind &&
                        partial.Desired == restored.Current)
                    {
                        _partialIntent = partial;
                        _differences = restored.Differences;
                        if (!_needsReview) _error = "Список сохранён частично. Сверьте различия.";
                    }
                    _original = restored.Original;
                    _text = restored.Current;
                }
            }
            _uncertain = _draft.Restore.Operation is not null;
            _available = !_needsReview && _sync is not null && await _sync.CanWriteAsync(_address, kind.Value, _lifetime.Token);
            if (!_uncertain) await PersistListDraftAsync();
            else _error = "Требуется сверка незавершённой синхронизации. Нажмите Обновить.";
            if (_disposed) return;
            var url = Navigation.GetUriWithQueryParameter("id", $"{_address}:{(byte)kind}");
            await JS.InvokeVoidAsync("claySlBl.rememberUrl", _lifetime.Token, url);
            _restoreRaw = _draft.Restore.HasDraft;
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or JsonException)
        {
            _available = false;
            _error = "Не удалось восстановить черновик в браузере.";
        }
    }

    private async Task ChangeTextAsync(string? value)
    {
        if (_busy) return;
        _text = value ?? "";
        _partialIntent = null;
        _differences = null;
        try
        {
            if (_draft is not null)
                await PersistListDraftAsync();
        }
        catch (Exception e) when (e is JSException or InvalidOperationException) { _available = false; _error = "Не удалось сохранить черновик в браузере. Скопируйте текст перед закрытием."; }
    }

    private sealed record ListDraft(string Address, SlBlKind Kind, string Original, string Current, bool NeedsReview = false,
        SlBlIntent? PartialIntent = null, SlBlDifferences? Differences = null);

    private Task PersistListDraftAsync() => _draft!.SaveTypedScopeAsync(DraftScope,
        JsonSerializer.Serialize(new ListDraft(_address!, _kind!.Value, _original, _text, _needsReview, _partialIntent, _differences)), _lifetime.Token);

    private async Task SaveAsync()
    {
        if (_busy || _uncertain || !_available || !Dirty || _draft is not { } registration ||
            _address is null || _kind is not { } kind || _sync is null) return;
        var intent = new SlBlIntent(Guid.NewGuid(), _address, kind, _original, _text);
        if (SlBlData.Validate(intent.Desired) is { } invalid) { _error = invalid; return; }
        _busy = true;
        try
        {
            if (!await ConfirmAsync("Данная операция может выполняться до двух минут. Вы уверены, что хотите выполнить данную операцию?")) return;
            await registration.EnsureCommandAllowedAsync(_lifetime.Token);
            var attempt = await registration.BeginOperationAsync(new ClayDraftOperationIntent("slbl.sync",
                JsonSerializer.Serialize(intent), [DraftScope]), _lifetime.Token);
            _pendingIntent = intent;
            _pendingRevision = attempt.Revision;
            _uncertain = true;
            var result = await SlBlSynchronization.ExecuteAsync(_sync, intent, _lifetime.Token);
            _differences = result.Differences;
            if (result.Outcome == SlBlOutcome.Unknown)
            {
                await registration.UnknownOperationAsync(attempt.Revision, _lifetime.Token);
                _error = "Результат синхронизации неизвестен. Требуется сверка.";
                return;
            }
            if (result.Outcome != SlBlOutcome.Verified)
            {
                if (result.Outcome == SlBlOutcome.Partial)
                {
                    _partialIntent = intent;
                    _original = await _sync.ReadOriginalAsync(intent.Address, kind, _lifetime.Token);
                    await PersistListDraftAsync();
                }
                await registration.RejectOperationAsync(attempt.Revision, _lifetime.Token);
                _uncertain = false;
                _error = result.Outcome == SlBlOutcome.Partial ? "Список сохранён частично. Сверьте различия." : "Изменились права или исходный список.";
                return;
            }
            await registration.CommitOperationAsync(attempt.Revision, cancellationToken: _lifetime.Token);
            _partialIntent = null;
            _original = _text = intent.Desired;
            _uncertain = false;
            _lists = await SlBlData.ReadAsync(Db, intent.Address, _lifetime.Token);
            _original = _text = _lists?.GetText(kind) ?? "";
            _error = "Данные успешно сохранены!";
            if (await ConfirmAsync("Закрыть форму?"))
            {
                await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", _lifetime.Token);
                await JS.InvokeVoidAsync("clayModelEquipmentForm.close", _lifetime.Token);
            }
        }
        catch (Exception) when (!_disposed) { _error = "Не удалось завершить сохранение или сверку. Проверьте фактическое состояние списка."; }
        finally { _busy = false; }
    }

    private async Task RefreshAsync()
    {
        if (_busy) return;
        if (_uncertain)
        {
            if (_pendingIntent is not { } pending || _sync is null || _draft is null) return;
            _busy = true;
            try
            {
                var check = await SlBlSynchronization.ReconcileAsync(_sync, pending, _lifetime.Token);
                _differences = check.Differences;
                if (check.Outcome == SlBlOutcome.Verified)
                {
                    await _draft.CommitOperationAsync(_pendingRevision, cancellationToken: _lifetime.Token);
                    _uncertain = false;
                    _original = pending.Desired;
                    _error = "Фактический список соответствует сохранённой команде.";
                    await PersistListDraftAsync();
                }
                else _error = "Фактический список отличается от сохранённой команды. Повтор синхронизации заблокирован.";
            }
            catch (Exception) when (!_disposed) { _error = "Сверка не завершена. Повторите чтение."; }
            finally { _busy = false; }
            return;
        }
        if (_address is null || _kind is not { } kind)
        {
            if (!Dirty) Navigation.NavigateTo(Navigation.Uri, forceLoad: true);
            return;
        }
        _busy = true;
        try
        {
            var latest = await SlBlData.ReadAsync(Db, _address, _lifetime.Token);
            if (latest is null || !string.Equals(latest.GetText(kind).Trim(), _text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (SlBlData.Validate(_text) is null)
                    _differences = await SlBlData.CompareAsync(Db, new(_address, kind), _text, _lifetime.Token);
                if (!await ConfirmAsync("Изменения не сохранены! Вы хотите заново загрузить данные?")) return;
            }
            await DiscardAsync();
            _lists = latest;
            await SetKindAsync(kind);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось перечитать или сверить фактический список."; }
        finally { _busy = false; }
    }

    private Task<bool> CanDiscardAsync() => Dirty ? ConfirmAsync("Отказаться от несохранённых изменений?") : Task.FromResult(true);

    private async Task<bool> ConfirmAsync(string message)
    {
        var dialog = await Dialogs.ShowAsync<ConfirmDialog>("Подтверждение", new DialogParameters<ConfirmDialog>
        {
            { dialog => dialog.Message, message }, { dialog => dialog.ConfirmText, "Да" }, { dialog => dialog.CancelText, "Нет" },
        });
        return await dialog.Result is { Canceled: false };
    }

    private async Task CloseAsync()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            if (!_uncertain)
            {
                if (!await CanDiscardAsync()) return;
                await DiscardAsync();
                _text = _original;
                // В обычной вкладке браузер может оставить редактор открытым.
                if (_address is not null && _kind is { } kind) await SetKindAsync(kind);
            }
            await JS.InvokeVoidAsync("clayModelEquipmentForm.close", _lifetime.Token);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            _error = "Не удалось завершить закрытие формы. Проверьте состояние черновика.";
        }
        finally { _busy = false; }
    }

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_allowNavigation) { _allowNavigation = false; return; }
        if (_busy && !_initialRead) { context.PreventNavigation(); return; }
        if (!Dirty) return;
        context.PreventNavigation();
        if (!await CanDiscardAsync()) return;
        await DiscardAsync();
        _allowNavigation = true;
        Navigation.NavigateTo(context.TargetLocation);
    }

    private async Task DiscardAsync()
    {
        if (_draft is null || _uncertain) return;
        await _draft.DiscardAsync(_lifetime.Token);
        await _draft.DisposeAsync();
        _draft = null;
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_draft is not null) await _draft.DisposeAsync();
        _lifetime.Dispose();
    }
}
