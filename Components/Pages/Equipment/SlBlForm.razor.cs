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
using MudBlazor.Extensions;
using MudBlazor.Extensions.Options;

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
    private Guid _draftInstance = Guid.NewGuid();
    private SlBlCloseRequest? _pendingDraftClose;
    private bool _editorClosed;
    private bool _legacyEvidenceTransferred;
    private bool _restoreRaw;
    private bool _rawPending;
    private bool _rawFailed;
    private bool _applyingRaw;
    private ElementReference _editorScope;
    private IJSObjectReference? _draftTextModule;
    private SlBlDifferences? _differences;
    private SlBlIntent? _partialIntent;
    private ClayDraftRegistration? _draft;
    [Inject] private IServiceProvider Services { get; set; } = null!;
    private SlBlSqlSynchronization? _sync;
    private bool _available;
    private bool _needsReview;
    private bool _uncertain;
    private bool _serverPending;
    private SlBlIntent? _pendingIntent;
    private ClayDraftOperation? _pendingOperation;
    private ClayDraftOperation? _verifiedCleanup;
    private SlBlIntent? _verifiedReload;
    private ListComparison? _comparison;
    private bool CompletionPending => _verifiedCleanup is not null || _verifiedReload is not null;
    private bool NeedsLeaveConfirmation => !_editorClosed && (Dirty || _rawPending || _uncertain || CompletionPending || _pendingDraftClose is not null);
    private bool Dirty => !string.Equals(_text, _original, StringComparison.Ordinal);
    private bool SlBlFocusReady => !_disposed && !_busy && !_initialRead && CurrentUser is not null &&
        !_restoreRaw && !_rawPending && !_applyingRaw && !_uncertain && !CompletionPending &&
        _pendingDraftClose is null && !_editorClosed;
    private bool CanSave => !_busy && !_uncertain && !_serverPending && !_rawPending && !_applyingRaw && _available &&
        !_needsReview && !CompletionPending && _pendingDraftClose is null && !_editorClosed && Dirty && _draft is not null;

    /// <inheritdoc />
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (!firstRender)
        {
            if (_restoreRaw && !_busy)
            {
                _restoreRaw = false;
                await RestoreRawAsync(pendingOnly: false);
                if (!_disposed) StateHasChanged();
            }
            return;
        }

        await LoadInitialAsync();
    }

    private async Task LoadInitialAsync()
    {
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
        if (_busy || _applyingRaw || _uncertain || CompletionPending || _pendingDraftClose is not null ||
            _editorClosed || value == _address) return;
        _busy = true;
        try
        {
            var decision = await ReadCloseDecisionAsync(_lifetime.Token);
            if (await CanDiscardAsync(decision, _lifetime.Token)) await LoadAddressAsync(value, null, decision);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось загрузить списки выбранного адреса."; }
        finally { _busy = false; }
    }

    private async Task LoadAddressAsync(string? address, SlBlKind? preferredKind, SlBlCloseDecision? decision = null)
    {
        var key = address is not null ? await SlBlData.FindAddressAsync(Db, address, _lifetime.Token) : null;
        if (_disposed) return;
        if (address is not null && key is null) { _error = "Почтовый адрес не найден или недоступен."; return; }
        var lists = key is not null ? await SlBlData.ReadAsync(Db, key, _lifetime.Token) : null;
        if (_disposed) return;
        if (address is not null && lists is null) { _error = "Не удалось подтвердить актуальные списки выбранного адреса."; return; }
        async Task PublishAsync()
        {
            _address = key;
            _lists = lists;
            _error = null;
            await SetKindAsync(preferredKind ?? lists?.InitialKind);
        }
        if (decision is null) await PublishAsync();
        else await CloseOwnedDraftAsync(decision, PublishAsync, _lifetime.Token);
    }

    private async Task ChangeKindAsync(SlBlKind? value)
    {
        if (_busy || _applyingRaw || _uncertain || CompletionPending || _pendingDraftClose is not null ||
            _editorClosed || value == _kind || _address is not { } address) return;
        _busy = true;
        try
        {
            var decision = await ReadCloseDecisionAsync(_lifetime.Token);
            if (!await CanDiscardAsync(decision, _lifetime.Token)) return;
            var latest = await SlBlData.ReadAsync(Db, address, _lifetime.Token);
            if (latest is null) { _error = "Не удалось подтвердить актуальные почтовые списки."; return; }
            await CloseOwnedDraftAsync(decision, async () =>
            {
                _lists = latest;
                _error = null;
                await SetKindAsync(value);
            }, _lifetime.Token);
        }
        catch (Exception error) when (error is not OperationCanceledException) { _error = "Не удалось загрузить выбранный список."; }
        finally { _busy = false; }
    }

    private async Task SetKindAsync(SlBlKind? kind)
    {
        _editorClosed = false;
        _legacyEvidenceTransferred = false;
        _kind = kind;
        _uncertain = false;
        _pendingIntent = null;
        _pendingOperation = null;
        _available = false;
        _serverPending = false;
        _needsReview = false;
        _comparison = null;
        _original = _text = kind is { } selected ? _lists?.GetText(selected) ?? "" : "";
        _partialIntent = null;
        _differences = null;
        _restoreRaw = _rawPending = _rawFailed = false;
        if (_address is null || kind is null || CurrentUser is not { } actor) return;
        try
        {
            var address = _address;
            var entityKey = $"{address}:{(byte)kind}";
            var userKey = actor.Id.ToString(CultureInfo.InvariantCulture);
            var instance = _draftInstance;
            var ct = _lifetime.Token;
            await ReadAuthorizedWriteStateAsync(address, kind.Value, ct);
            bool Current() => IsCurrentListEditor(actor.Id, address, kind.Value, instance, ct);
            if (!Current()) return;
            Task<ClayDraftRegistration> OpenRam(CancellationToken token) =>
                Drafts.RegisterRamHandleAsync("slbl", entityKey, userKey, 1, DraftRootSelector, instance, token);

            ClayDraftRegistration registration;
            if (_available && _sync is { } sync && !_legacyEvidenceTransferred)
            {
                registration = await Drafts.TransferLegacyAndRegisterRamAsync("slbl", entityKey, userKey,
                    async (evidence, token) =>
                    {
                        await sync.ImportLegacyOperationsAsync(address, kind.Value, ParseLegacySlBlEvidence(evidence, entityKey), token);
                        await ReadAuthorizedWriteStateAsync(address, kind.Value, token);
                        if (!Current() || !_available)
                            throw new InvalidOperationException("Доступ к почтовому списку изменился.");
                        return await OpenRam(token);
                    }, Current, ct);
                _legacyEvidenceTransferred = true;
            }
            else
                registration = await OpenRam(ct);
            if (!Current()) { await registration.DisposeAsync(); return; }
            _draft = registration;
            _uncertain = _draft.Restore.Operation is not null;
            if (_draft.Restore.OperationSnapshot is { } marker)
            {
                var pending = JsonSerializer.Deserialize<SlBlIntent>(marker.Data);
                if (marker.Kind != "slbl.sync" || pending is null || pending.OperationId == Guid.Empty ||
                    pending.Address != _address || pending.Kind != kind || pending.Desired is null ||
                    SlBlData.Validate(pending.Desired) is not null) throw new JsonException();
                _pendingIntent = pending;
                _pendingOperation = marker;
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
            if (_pendingOperation is { Status: ClayDraftOperationStatus.Confirmed } confirmed && _pendingIntent is { } intent &&
                Guid.TryParseExact(confirmed.AttemptId, "N", out var confirmedId) && confirmedId == intent.OperationId &&
                _sync is not null && _lists?.GetText(kind.Value) == intent.Desired &&
                await _sync.ReadReceiptAsync(intent, _lifetime.Token) is { State: InventoryOperationState.Confirmed } receipt &&
                receipt.AttemptId == intent.OperationId)
            {
                RecordVerified(intent, confirmed, keepLocal: registration.Restore.TypedScopes?.ContainsKey(DraftScope) == true);
                _error = "Синхронизация подтверждена. Нажмите «Обновить», чтобы завершить обработку черновика.";
            }
            if (_uncertain) _error = "Требуется сверка незавершённой синхронизации. Нажмите Обновить.";
            else if (_serverPending) _error = "В серверном журнале есть незавершённая синхронизация. Повтор заблокирован.";
            else if (!CompletionPending) await PersistListDraftAsync();
            if (_disposed) return;
            var url = Navigation.GetUriWithQueryParameter("id", $"{_address}:{(byte)kind}");
            await JS.InvokeVoidAsync("claySlBl.rememberUrl", _lifetime.Token, url);
            _restoreRaw = _rawPending = _draft.Restore.HasDraft && !_uncertain && !CompletionPending;
            if (_uncertain) await ConfirmRawReadyAsync(registration, _lifetime.Token);
        }
        catch (Exception e) when (e is JSException or InvalidOperationException or JsonException or UnauthorizedAccessException
            or Microsoft.Data.SqlClient.SqlException or TimeoutException)
        {
            _available = false;
            _error = "Не удалось восстановить черновик в браузере.";
        }
    }

    private async Task ReadAuthorizedWriteStateAsync(string address, SlBlKind kind, CancellationToken ct)
    {
        _available = false;
        if (_sync is not { } sync || CurrentUser is not { } actor) return;
        var state = await sync.ReadWriteStateAsync(address, kind, ct);
        if (state.Allowed && !_legacyEvidenceTransferred && _draft is { } existing)
        {
            var entityKey = $"{address}:{(byte)kind}";
            var instance = _draftInstance;
            await Drafts.TransferLegacyEvidenceAsync("slbl", entityKey, actor.Id.ToString(CultureInfo.InvariantCulture),
                async (evidence, token) =>
                {
                    await sync.ImportLegacyOperationsAsync(address, kind, ParseLegacySlBlEvidence(evidence, entityKey), token);
                    state = await sync.ReadWriteStateAsync(address, kind, token);
                    if (!state.Allowed || !ReferenceEquals(existing, _draft) || !IsCurrentListEditor(actor.Id, address, kind, instance, ct))
                        throw new InvalidOperationException("Доступ к почтовому списку изменился.");
                }, ct, retireSnapshotsAfterImport: true);
            _legacyEvidenceTransferred = true;
        }
        (_available, _serverPending) = state;
        if (_serverPending && !_uncertain)
            _error = "В серверном журнале есть незавершённая синхронизация. Повтор заблокирован.";
    }

    private bool IsCurrentListEditor(int employeeId, string address, SlBlKind kind, Guid instance, CancellationToken ct) =>
        !_disposed && !ct.IsCancellationRequested && CurrentUser?.Id == employeeId &&
        _address == address && _kind == kind && _draftInstance == instance;

    private static IReadOnlyCollection<Guid> ParseLegacySlBlEvidence(
        IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, string entityKey)
    {
        var attempts = new List<Guid>(evidence.Count);
        foreach (var item in evidence)
        {
            if (item.Kind != "slbl.sync" || item.Data != entityKey ||
                !Guid.TryParseExact(item.AttemptId, "N", out var id) || id == Guid.Empty)
                throw new InvalidOperationException("Не удалось подтвердить прежнюю попытку синхронизации списка.");
            attempts.Add(id);
        }
        return attempts;
    }

    private bool CanRetryRaw => !_busy && !_applyingRaw && !_uncertain && !CompletionPending && _draft is not null;

    private Task RetryRawAsync() => RestoreRawAsync(pendingOnly: true);

    private async Task RestoreRawAsync(bool pendingOnly)
    {
        if (!CanRetryRaw || _disposed || _draft is not { } registration) return;
        var ct = _lifetime.Token;
        _applyingRaw = _rawPending = true;
        try
        {
            var applied = pendingOnly
                ? await registration.ApplyPendingRawAsync(DraftScope, ct)
                : await registration.ApplyRawWithResultAsync(DraftScope, ct);
            if (await ConfirmRawReadyAsync(registration, ct) && !_disposed && ReferenceEquals(registration, _draft))
                _rawPending = applied.Pending.Count != 0;
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            if (!_disposed && ReferenceEquals(registration, _draft))
                _rawPending = _rawFailed = true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        finally { _applyingRaw = false; }
    }

    private async Task<bool> ConfirmRawReadyAsync(ClayDraftRegistration registration, CancellationToken ct)
    {
        try
        {
            var readiness = await registration.GetRestoreReadinessAsync(ct);
            if (_disposed || ct.IsCancellationRequested || !ReferenceEquals(registration, _draft)) return false;
            _rawFailed = readiness.FailedScopes.Contains(DraftScope);
            _rawPending = _rawFailed || readiness.PendingScopes.Contains(DraftScope);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            if (_disposed || !ReferenceEquals(registration, _draft)) return false;
            _rawPending = _rawFailed = true;
        }
        return !_rawPending;
    }
    private async Task<string?> ReadEditorTextAsync(CancellationToken ct)
    {
        _draftTextModule ??= await JS.InvokeAsync<IJSObjectReference>("import", ct, "./js/slblDraftText.js");
        return await _draftTextModule.InvokeAsync<string?>("readText", ct, _editorScope);
    }

    private void SetEditorText(string text)
    {
        if (string.Equals(_text, text, StringComparison.Ordinal)) return;
        _text = text;
        _partialIntent = null;
        _differences = null;
    }

    private async Task ChangeTextAsync(string? value)
    {
        if (_busy || _pendingDraftClose is not null || _editorClosed) return;
        SetEditorText(value ?? "");
        try
        {
            if (_draft is not null)
                await PersistListDraftAsync();
        }
        catch (Exception e) when (e is JSException or InvalidOperationException) { _available = false; _error = "Не удалось сохранить черновик в браузере. Скопируйте текст перед закрытием."; }
    }

    private sealed record ListDraft(string Address, SlBlKind Kind, string Original, string Current, bool NeedsReview = false,
        SlBlIntent? PartialIntent = null, SlBlDifferences? Differences = null);

    private sealed record ListComparison(string Original, string Local, string Fresh, ClayDraftRebaseState Storage);

    private Task PersistListDraftAsync() => _draft!.SaveTypedScopeAsync(DraftScope,
        JsonSerializer.Serialize(new ListDraft(_address!, _kind!.Value, _original, _text, _needsReview, _partialIntent, _differences)), _lifetime.Token);

    private async Task SaveAsync()
    {
        if (!CanSave || _draft is not { } registration || _address is null || _kind is not { } kind || _sync is null)
            return;

        _busy = true;
        var ct = _lifetime.Token;
        try
        {
            if (!await ConfirmRawReadyAsync(registration, ct)) return;
            var browserText = await ReadEditorTextAsync(ct);
            if (browserText is null)
            {
                _error = "Не удалось получить текущий текст списка. Повторите сохранение.";
                return;
            }
            SetEditorText(browserText);
            if (!Dirty) return;
            if (SlBlData.Validate(_text) is { } invalid) { _error = invalid; return; }

            var intent = new SlBlIntent(Guid.NewGuid(), _address, kind, _original, _text);
            if (!await ConfirmAsync("Данная операция может выполняться до двух минут. Вы уверены, что хотите выполнить данную операцию?")) return;
            if (!await ConfirmRawReadyAsync(registration, ct)) return;
            var confirmedText = await ReadEditorTextAsync(ct);
            if (!string.Equals(confirmedText, intent.Desired, StringComparison.Ordinal))
            {
                if (confirmedText is not null)
                {
                    SetEditorText(confirmedText);
                    await PersistListDraftAsync();
                }
                _error = "Текст списка изменился. Проверьте его и подтвердите сохранение ещё раз.";
                return;
            }
            await PersistListDraftAsync();
            await registration.EnsureCommandAllowedAsync(ct);
            if (!await ConfirmRawReadyAsync(registration, ct)) return;
            var attempt = await registration.BeginOperationAsync(new ClayDraftOperationIntent("slbl.sync",
                JsonSerializer.Serialize(intent), [DraftScope], intent.OperationId.ToString("N")), _lifetime.Token);
            _pendingIntent = intent;
            _pendingOperation = attempt;
            _uncertain = true;
            if (!await CheckBeforeDispatchAsync(registration, attempt, intent, ct)) return;
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
                    await RecordPartialAsync(intent, ct);
                await registration.RejectOperationAsync(attempt.Revision, ct);
                ClearPendingOperation();
                _error = result.Outcome == SlBlOutcome.Partial ? "Список сохранён частично. Сверьте различия." : result.Message ?? "Изменились права или исходный список.";
                if (result.Outcome == SlBlOutcome.Rejected) await NoticeChangedBaselineAsync(intent, ct);
                return;
            }
            RecordVerified(intent, attempt);
            if (await FinishVerifiedAsync(registration, ct))
            {
                var decision = await ReadCloseDecisionAsync(ct);
                if (!Dirty && !_rawPending && decision.Scope is not { HasRaw: true } &&
                    await ConfirmAsync("Закрыть форму?"))
                    await CloseOwnedDraftAsync(decision, () => CloseAfterVerifiedAsync(ct), ct);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (!_disposed) { _error = "Не удалось завершить сохранение или сверку. Проверьте фактическое состояние списка."; }
        finally { _busy = false; }
    }

    private async Task<bool> CheckBeforeDispatchAsync(ClayDraftRegistration registration, ClayDraftOperation attempt,
        SlBlIntent intent, CancellationToken ct)
    {
        var ready = await ConfirmRawReadyAsync(registration, ct);
        string? text = null;
        if (ready)
        {
            try { text = await ReadEditorTextAsync(ct); }
            catch (Exception error) when (error is JSException or InvalidOperationException) { }
            ready = await ConfirmRawReadyAsync(registration, ct);
        }
        ct.ThrowIfCancellationRequested();
        if (_disposed || !ReferenceEquals(registration, _draft)) return false;
        if (ready && string.Equals(text, intent.Desired, StringComparison.Ordinal)) return true;

        await registration.RejectOperationAsync(attempt.Revision, ct);
        ClearPendingOperation();
        if (ready && text is not null)
        {
            SetEditorText(text);
            await PersistListDraftAsync();
        }
        if (ready) _error = "Текст не подтверждён перед отправкой. Проверьте его и повторите сохранение.";
        return false;
    }

    private void ClearPendingOperation()
    {
        _uncertain = false;
        _pendingIntent = null;
        _pendingOperation = null;
    }

    private async Task RecordPartialAsync(SlBlIntent intent, CancellationToken ct)
    {
        _partialIntent = intent;
        _needsReview = true;
        try
        {
            var latest = await SlBlData.ReadAsync(Db, intent.Address, ct);
            if (latest is not null)
            {
                _lists = latest;
                _original = latest.GetText(intent.Kind);
                _needsReview = false;
            }
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
        await PersistListDraftAsync();
    }

    private void RecordVerified(SlBlIntent intent, ClayDraftOperation attempt, bool keepLocal = false)
    {
        _verifiedCleanup = attempt;
        _verifiedReload = intent;
        var previous = _lists ?? new SlBlLists();
        _lists = intent.Kind == SlBlKind.Safe
            ? previous with { SafeList = intent.Desired }
            : previous with { BlockedList = intent.Desired };
        _original = intent.Desired;
        if (!keepLocal) _text = intent.Desired;
        _partialIntent = null;
        _differences = null;
        _needsReview = false;
        ClearPendingOperation();
    }

    private async Task<bool> FinishVerifiedAsync(ClayDraftRegistration registration, CancellationToken ct)
    {
        if (_verifiedCleanup is { } attempt)
        {
            try
            {
                if (attempt.Status != ClayDraftOperationStatus.Confirmed)
                {
                    await registration.RecordConfirmedOperationAsync(attempt.Revision, ct);
                    _verifiedCleanup = attempt with { Status = ClayDraftOperationStatus.Confirmed };
                }
                await registration.CommitOperationAsync(attempt.Revision, cancellationToken: ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _error = "Синхронизация подтверждена. Нажмите «Обновить», чтобы завершить обработку черновика.";
                return false;
            }
            _verifiedCleanup = null;
        }

        if (_verifiedReload is not { } known) return true;
        try
        {
            var latest = await SlBlData.ReadAsync(Db, known.Address, ct);
            if (latest is null) throw new InvalidOperationException("Почтовый список не найден после синхронизации.");
            var scope = await registration.ReadEditorScopeAsync(DraftScope, ct);
            if (scope.HasOperation) throw new InvalidOperationException("Завершение попытки не подтверждено.");
            ct.ThrowIfCancellationRequested();

            _lists = latest;
            var fresh = latest.GetText(known.Kind);
            var rawReady = await ConfirmRawReadyAsync(registration, ct);
            ct.ThrowIfCancellationRequested();
            var keepLocal = !rawReady || scope.HasRaw || !string.Equals(_text, known.Desired, StringComparison.Ordinal);
            _original = keepLocal ? known.Desired : fresh;
            if (!keepLocal) _text = fresh;
            _needsReview = keepLocal && !string.Equals(fresh, known.Desired, StringComparison.Ordinal);
            _restoreRaw = scope.HasRaw;
            _rawPending = scope.HasRaw || !rawReady;
            _verifiedReload = null;
            _error = keepLocal
                ? "Синхронизация подтверждена. Более поздний ввод сохранён."
                : "Синхронизация подтверждена. Список обновлён.";
            return !keepLocal;
        }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _error = "Синхронизация подтверждена, список не перечитан. Нажмите «Обновить» для повтора чтения.";
            return false;
        }
    }

    private async Task CloseAfterVerifiedAsync(CancellationToken ct)
    {
        try { await JS.InvokeVoidAsync("clayModelEquipmentForm.refreshOpener", ct); }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            _error = "Синхронизация подтверждена. Родительское окно не обновилось.";
        }
        try { await JS.InvokeVoidAsync("clayModelEquipmentForm.close", ct); }
        catch (Exception e) when (e is JSException or InvalidOperationException)
        {
            _error = "Синхронизация подтверждена. Форма осталась открытой.";
        }
    }

    private async Task NoticeChangedBaselineAsync(SlBlIntent intent, CancellationToken ct)
    {
        try
        {
            var latest = await SlBlData.ReadAsync(Db, intent.Address, ct);
            if (latest is null || string.Equals(latest.GetText(intent.Kind), intent.Original, StringComparison.Ordinal))
                return;
            _needsReview = true;
            _error = "Список в базе изменился. Нажмите «Обновить» для сверки с черновиком.";
        }
        catch (Exception) when (!ct.IsCancellationRequested) { }
    }

    private async Task PrepareComparisonAsync(SlBlLists latest, string address, SlBlKind kind,
        ClayDraftRegistration registration, CancellationToken ct)
    {
        _comparison = null;
        if (!await ConfirmRawReadyAsync(registration, ct)) return;
        var local = await ReadEditorTextAsync(ct);
        if (local is null)
        {
            _error = "Не удалось получить текущий текст списка. Повторите сверку.";
            return;
        }

        _differences = SlBlData.Validate(local) is null
            ? await SlBlData.CompareAsync(Db, new SlBlKey(address, kind), local, ct)
            : null;
        var storage = await registration.ReadRebaseStateAsync(DraftScope, ct);
        _comparison = new ListComparison(_original, local, latest.GetText(kind), storage);
        _error = "Выберите, какой список перенести на актуальную версию.";
    }

    private async Task ApplyComparisonAsync(bool keepLocal)
    {
        if (_busy || _applyingRaw || _uncertain || CompletionPending || _comparison is not { } comparison ||
            _draft is not { } registration || _address is not { } address || _kind is not { } kind)
            return;
        _busy = true;
        var ct = _lifetime.Token;
        try
        {
            if (!await ConfirmRawReadyAsync(registration, ct)) return;
            if (!string.Equals(await ReadEditorTextAsync(ct), comparison.Local, StringComparison.Ordinal))
            {
                _error = "Локальный текст изменился. Нажмите «Обновить» для новой сверки.";
                return;
            }

            var latest = await SlBlData.ReadAsync(Db, address, ct);
            if (latest is null || !string.Equals(latest.GetText(kind), comparison.Fresh, StringComparison.Ordinal))
            {
                _error = "Список в базе снова изменился или недоступен. Повторите сверку.";
                return;
            }

            var chosen = keepLocal ? comparison.Local : comparison.Fresh;
            var rawToDiscard = keepLocal ? [] : comparison.Storage.RawKeys.Where(key => key == "data:slbl.text").ToArray();
            var snapshot = JsonSerializer.Serialize(new ListDraft(address, kind, comparison.Fresh, chosen));
            if (!await ConfirmRawReadyAsync(registration, ct)) return;
            if (!await registration.RebaseScopeAsync(DraftScope, comparison.Storage.Revision, snapshot, rawToDiscard, ct))
            {
                _error = "Черновик изменился после сверки. Нажмите «Обновить» и проверьте новый снимок.";
                return;
            }

            _lists = latest;
            _original = comparison.Fresh;
            _text = chosen;
            _comparison = null;
            _partialIntent = null;
            _differences = null;
            _needsReview = false;
            _error = null;
            _available = false;
            try { _available = _sync is not null && await _sync.CanWriteAsync(address, kind, ct); }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                _error = "Исходный список обновлён. Не удалось проверить право синхронизации.";
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception) when (!ct.IsCancellationRequested)
        {
            _error = "Не удалось завершить сверку. Исходный снимок и локальный текст остаются на форме.";
        }
        finally { _busy = false; }
    }

    private async Task RefreshAsync()
    {
        if (_busy || _applyingRaw) return;
        if (_pendingDraftClose is { } pendingClose)
        {
            _busy = true;
            try { await ResumeOwnedDraftCloseAsync(pendingClose, _lifetime.Token); }
            finally { _busy = false; }
            return;
        }
        if (_editorClosed) return;
        if (CompletionPending && _draft is { } receiptRegistration)
        {
            _busy = true;
            try { await FinishVerifiedAsync(receiptRegistration, _lifetime.Token); }
            catch (OperationCanceledException) when (_disposed) { }
            finally { _busy = false; }
            return;
        }
        if (_rawPending && !_uncertain)
        {
            await RetryRawAsync();
            return;
        }
        if (_uncertain)
        {
            if (_pendingIntent is not { } pending || _pendingOperation is not { } pendingMarker || _sync is null || _draft is null) return;
            _busy = true;
            try
            {
                var check = await SlBlSynchronization.ReconcileAsync(_sync, pending, _lifetime.Token);
                _differences = check.Differences;
                var receipt = Guid.TryParseExact(pendingMarker.AttemptId, "N", out var pendingId) && pendingId == pending.OperationId
                    ? await _sync.ReadReceiptAsync(pending, _lifetime.Token)
                    : null;
                if (receipt is { State: InventoryOperationState.Confirmed } && receipt.AttemptId == pending.OperationId)
                {
                    if (check.Outcome == SlBlOutcome.Verified)
                    {
                        RecordVerified(pending, pendingMarker, keepLocal: true);
                        await FinishVerifiedAsync(_draft, _lifetime.Token);
                    }
                    else
                        _error = "Почтовая процедура ответила. Фактический список отличается от сохранённой команды. Требуется сверка.";
                    (_available, _serverPending) = await _sync.ReadWriteStateAsync(pending.Address, pending.Kind, _lifetime.Token);
                    return;
                }
                _error = check.Outcome == SlBlOutcome.Verified
                    ? "Фактический список совпадает с сохранённой командой, но исход попытки не подтверждён. Повтор синхронизации заблокирован."
                    : "Фактический список отличается от сохранённой команды. Повтор синхронизации заблокирован.";
            }
            catch (Exception) when (!_disposed) { _error = "Сверка не завершена. Повторите чтение."; }
            finally { _busy = false; }
            return;
        }
        if (_address is not { } address || _kind is not { } kind)
        {
            if (!Dirty)
            {
                _busy = true;
                await LoadInitialAsync();
            }
            return;
        }
        _busy = true;
        try
        {
            var decision = await ReadCloseDecisionAsync(_lifetime.Token);
            var latest = await SlBlData.ReadAsync(Db, address, _lifetime.Token);
            if (latest is not null && _draft is { } comparisonRegistration &&
                (_needsReview || !string.Equals(latest.GetText(kind).Trim(), _original.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                _needsReview = true;
                await PrepareComparisonAsync(latest, address, kind, comparisonRegistration, _lifetime.Token);
                return;
            }
            if (latest is null)
            {
                _error = "Актуальный список недоступен. Повторите чтение для сверки.";
                return;
            }
            if (!string.Equals(latest.GetText(kind).Trim(), _text.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                if (SlBlData.Validate(_text) is null)
                    _differences = await SlBlData.CompareAsync(Db, new(address, kind), _text, _lifetime.Token);
                if (!await ConfirmAsync("Изменения не сохранены! Вы хотите заново загрузить данные?")) return;
                latest = await SlBlData.ReadAsync(Db, address, _lifetime.Token);
                if (latest is null) { _error = "Актуальный список недоступен. Повторите чтение для сверки."; return; }
            }
            if (!IsCurrentCloseDecision(decision)) return;
            if (_draft is { } current && decision.Scope is { } scope)
            {
                var fresh = latest.GetText(kind);
                if (scope.HasOperation ||
                    !await current.ReplaceEditorScopeAsync(DraftScope, scope.Revision,
                        JsonSerializer.Serialize(new ListDraft(address, kind, fresh, fresh)), _lifetime.Token) ||
                    !IsCurrentCloseDecision(decision))
                {
                    _error = "Ввод изменился перед обновлением. Проверьте изменения и повторите чтение.";
                    return;
                }
                AcceptFreshList(latest, kind);
                await ReadAuthorizedWriteStateAsync(address, kind, _lifetime.Token);
                return;
            }
            _lists = latest;
            await SetKindAsync(kind);
        }
        catch (Exception e) when (e is not OperationCanceledException) { _error = "Не удалось перечитать или сверить фактический список."; }
        finally { _busy = false; }
    }

    private async Task<bool> CanDiscardAsync(SlBlCloseDecision decision, CancellationToken ct)
    {
        if (!Dirty && !_rawPending && !_uncertain && !CompletionPending &&
            decision.Scope is not { HasRaw: true } and not { HasOperation: true })
            return true;

        var dialog = await Dialogs.ShowExAsync<ClayEditorCancelDialog>("Отмена изменений", new DialogParameters(),
            new DialogOptionsEx { DragMode = MudDialogDragMode.Simple, CloseButton = true, CloseOnEscapeKey = true });
        DialogResult? result;
        try { result = await dialog.Result.WaitAsync(ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            dialog.Close(DialogResult.Cancel());
            return false;
        }
        if (ct.IsCancellationRequested || !IsCurrentCloseDecision(decision) ||
            result is not { Canceled: false, Data: ClayEditorCancelChoice choice })
            return false;
        if (choice == ClayEditorCancelChoice.ResetAndStay)
        {
            await ResetListFromDatabaseAsync(decision, ct);
            return false;
        }
        return choice == ClayEditorCancelChoice.CloseWithoutSaving;
    }

    private async Task ResetListFromDatabaseAsync(SlBlCloseDecision decision, CancellationToken ct)
    {
        if (_uncertain || CompletionPending || _serverPending || decision.Scope is { HasOperation: true })
        {
            _error = "Сначала сверьте исход предыдущей синхронизации.";
            return;
        }
        if (decision.Address is not { } address || decision.Kind is not { } kind ||
            decision.Registration is not { } registration || decision.Scope is not { } scope)
        {
            _error = "Не удалось подтвердить черновик выбранного списка. Ввод сохранён.";
            return;
        }

        var identity = (await Authentication.GetAuthenticationStateAsync().WaitAsync(ct)).User.Identity;
        var actor = await Employee.GetCurrentAsync(Db, identity?.IsAuthenticated == true ? identity.Name : null, ct);
        if (!IsCurrentCloseDecision(decision)) return;
        if (actor is null || actor.Id != decision.Employee)
        {
            _error = "Не удалось подтвердить сотрудника. Ввод сохранён.";
            return;
        }
        var latest = await SlBlData.ReadAsync(Db, address, ct);
        if (!IsCurrentCloseDecision(decision)) return;
        if (latest is null)
        {
            _error = "Актуальный список недоступен. Ввод сохранён.";
            return;
        }
        var state = _sync is { } sync ? await sync.ReadWriteStateAsync(address, kind, ct) : (false, false);
        if (!IsCurrentCloseDecision(decision)) return;
        if (state.Item2)
        {
            _serverPending = true;
            _error = "Сначала сверьте исход предыдущей синхронизации.";
            return;
        }
        var fresh = latest.GetText(kind);
        var replaced = await registration.ReplaceEditorScopeAsync(DraftScope, scope.Revision,
            JsonSerializer.Serialize(new ListDraft(address, kind, fresh, fresh)), ct);
        if (!IsCurrentCloseDecision(decision)) return;
        if (!replaced)
        {
            _error = "Ввод изменился перед отменой. Проверьте изменения и повторите действие.";
            return;
        }
        (_available, _serverPending) = state;
        AcceptFreshList(latest, kind);
    }

    private void AcceptFreshList(SlBlLists latest, SlBlKind kind)
    {
        _lists = latest;
        _text = _original = latest.GetText(kind);
        _needsReview = _restoreRaw = _rawPending = _rawFailed = false;
        _partialIntent = null;
        _differences = null;
        _comparison = null;
        _error = null;
    }

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
        if (_busy || _applyingRaw || _pendingDraftClose is not null) return;
        _busy = true;
        try
        {
            var ct = _lifetime.Token;
            if (_editorClosed)
            {
                await JS.InvokeVoidAsync("clayModelEquipmentForm.close", ct);
                return;
            }
            var decision = await ReadCloseDecisionAsync(ct);
            if (await CanDiscardAsync(decision, ct))
                await CloseOwnedDraftAsync(decision, () => JS.InvokeVoidAsync("clayModelEquipmentForm.close", ct).AsTask(), ct);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is JSException or InvalidOperationException or UnauthorizedAccessException
            or Microsoft.Data.SqlClient.SqlException or TimeoutException)
        {
            _error = "Не удалось завершить закрытие формы. Проверьте состояние черновика.";
        }
        finally { _busy = false; }
    }

    private async Task BeforeNavigationAsync(LocationChangingContext context)
    {
        if (_allowNavigation) { _allowNavigation = false; return; }
        if (_applyingRaw || _pendingDraftClose is not null || _busy && !_initialRead) { context.PreventNavigation(); return; }
        if (_editorClosed || _draft is null && !NeedsLeaveConfirmation) return;
        context.PreventNavigation();
        _busy = true;
        try
        {
            using var request = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken, _lifetime.Token);
            var decision = await ReadCloseDecisionAsync(request.Token);
            if (!await CanDiscardAsync(decision, request.Token) || request.IsCancellationRequested) return;
            await CloseOwnedDraftAsync(decision, () =>
            {
                _allowNavigation = true;
                Navigation.NavigateTo(context.TargetLocation);
                return Task.CompletedTask;
            }, request.Token);
        }
        catch (OperationCanceledException) when (context.CancellationToken.IsCancellationRequested || _disposed) { }
        catch (Exception error) when (error is JSException or InvalidOperationException or UnauthorizedAccessException
            or Microsoft.Data.SqlClient.SqlException or TimeoutException)
        {
            _error = "Не удалось подтвердить закрытие локального редактора.";
        }
        finally { _busy = false; }
    }

    private async Task<SlBlCloseDecision> ReadCloseDecisionAsync(CancellationToken ct)
    {
        var registration = _draft;
        var text = _text;
        var address = _address;
        var kind = _kind;
        var employee = CurrentUser?.Id;
        var instance = _draftInstance;
        var scope = registration is null ? null : await registration.ReadEditorScopeAsync(DraftScope, ct);
        return new SlBlCloseDecision(registration, scope, text, address, kind, employee, instance);
    }

    private bool IsCurrentCloseDecision(SlBlCloseDecision decision) =>
        !_disposed && ReferenceEquals(_draft, decision.Registration) && _text == decision.Text &&
        _address == decision.Address && _kind == decision.Kind && CurrentUser?.Id == decision.Employee &&
        _draftInstance == decision.Instance;

    private async Task CloseOwnedDraftAsync(SlBlCloseDecision decision, Func<Task> afterClose, CancellationToken ct)
    {
        if (!IsCurrentCloseDecision(decision) || ct.IsCancellationRequested) return;
        if (decision.Scope is { HasOperation: true } operation && decision.Registration is { } owned)
        {
            if (decision.Address is not { } address || decision.Kind is not { } kind || _sync is not { } sync)
            {
                _error = "Не удалось подтвердить сохранность сведений о синхронизации. Черновик сохранён.";
                return;
            }
            var evidence = await owned.ReadCloseOperationEvidenceAsync(operation.Revision, ct);
            if (evidence is null || !IsCurrentCloseDecision(decision))
                throw new InvalidOperationException("Не удалось подтвердить сведения о синхронизации.");
            var entityKey = $"{address}:{(byte)kind}";
            await sync.ImportLegacyOperationsAsync(address, kind, ParseLegacySlBlEvidence([evidence], entityKey), ct);
            if (!IsCurrentCloseDecision(decision) || ct.IsCancellationRequested) return;
        }
        else if (_uncertain)
        {
            _error = "Не удалось подтвердить сохранность сведений о синхронизации. Черновик сохранён.";
            return;
        }
        if (decision.Registration is not { } registration || decision.Scope is not { } scope)
        {
            _editorClosed = true;
            await FinishOwnedDraftCloseAsync(afterClose);
            return;
        }
        var pending = new SlBlCloseRequest(registration, scope.Revision, afterClose);
        _pendingDraftClose = pending;
        await ResumeOwnedDraftCloseAsync(pending, ct);
    }

    private async Task ResumeOwnedDraftCloseAsync(SlBlCloseRequest request, CancellationToken ct)
    {
        bool Current() => !_disposed && ReferenceEquals(_pendingDraftClose, request) && ReferenceEquals(_draft, request.Registration);
        if (!Current()) return;
        try
        {
            if (!await request.Registration.CloseRamIfRevisionAsync(request.Revision, ct))
            {
                if (Current())
                {
                    _pendingDraftClose = null;
                    _error = "Ввод изменился перед закрытием. Проверьте изменения и подтвердите закрытие ещё раз.";
                }
                return;
            }
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException or TimeoutException)
        {
            if (Current()) _error = "Удаление черновика не подтверждено. Нажмите «Обновить», чтобы повторить закрытие.";
            return;
        }
        if (!Current()) return;
        _draft = null;
        _pendingDraftClose = null;
        _draftInstance = Guid.NewGuid();
        _editorClosed = true;
        _error = null;
        await FinishOwnedDraftCloseAsync(request.AfterClose);
    }

    private async Task FinishOwnedDraftCloseAsync(Func<Task> afterClose)
    {
        try { if (!_disposed) await afterClose(); }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (!_disposed) _error = "Черновик удалён. Не удалось завершить переход или закрыть окно.";
        }
    }

    private sealed record SlBlCloseDecision(ClayDraftRegistration? Registration, ClayDraftEditorScopeState? Scope,
        string Text, string? Address, SlBlKind? Kind, int? Employee, Guid Instance);
    private sealed record SlBlCloseRequest(ClayDraftRegistration Registration, long Revision, Func<Task> AfterClose);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        await _lifetime.CancelAsync();
        if (_draft is not null) await _draft.DisposeAsync();
        if (_draftTextModule is not null)
        {
            try { await _draftTextModule.DisposeAsync(); }
            catch (JSDisconnectedException) { }
        }
        _lifetime.Dispose();
    }
}
