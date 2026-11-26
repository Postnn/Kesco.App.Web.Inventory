using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;
using EquipmentEntity = Clayzor.Lib.Entities.Inventory.Equipment;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class EquipmentIns
{
    private const string CreationScope = "main";
    private const string ReceiptScope = "creation-result";
    private const string CreationKind = "equipment.create";
    private const string RawPendingMessage = "Часть ввода ожидает восстановления. Нажмите «Обновить» для повтора.";
    private readonly string _creationRoot = $"equipment-ins-draft-{Guid.NewGuid():N}";
    private readonly ClayToolbarButtonOptions _openCreatedOptions = new() { Text = "Открыть созданное оборудование" };
    private readonly ClayToolbarButtonOptions _newCreationOptions = new() { Text = "Начать другое создание" };
    private readonly ClayToolbarButtonOptions _creationRetryOptions = new() { Text = "Завершить обработку черновика" };

    private readonly ClayToolbarButtonOptions _retryCreationRawOptions = new() { Text = "Повторить восстановление ввода" };
    private readonly ClayToolbarButtonOptions _reviewCreationOptions = new() { Text = "Принять актуальный исходный контекст" };

    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    private ClayDraftRegistration? _creationDraft;
    private ClayDraftOperation? _creationAttempt;
    private CreationReceipt? _creationResult;
    private bool _receiptPersisted;
    private bool _creationUncertain;
    private bool _creationRawPending;
    private bool _creationRawFailed;
    private bool _applyingCreationRaw;
    private bool _restoreCreationRaw;
    private string? _notSentCreationId;
    private string? _freshOriginal;
    private string? _lastEditorDraft;
    private Task? _creationAutosave;
    private bool _sourceUnavailable;
    private bool _draftNeedsReview;
    private bool _editorRestored;
    private bool _editorSaveFailed;

    private bool CreationCleanupPending => _creationResult is not null && (!_receiptPersisted || _creationAttempt is not null);
    private bool CreationBlocked => _creationDraft is null || _creationUncertain || CreationCleanupPending ||
        _creationResult is null && CreationRecoveryBlocked ||
        _notSentCreationId is not null || _creationRawPending || _applyingCreationRaw ||
        _creationResult is null && (_sourceUnavailable || _draftNeedsReview || _editorSaveFailed);

    private async Task AttachCreationDraftAsync(Employee? actor, int version)
    {
        if (actor is null) return;
        var source = SourceKey;
        try
        {
            var registration = await Drafts.RegisterHandleAsync("equipment-ins", source,
                actor.Id.ToString(CultureInfo.InvariantCulture), 1, "." + _creationRoot);
            if (!IsCurrent(version))
            {
                await registration.DisposeAsync();
                return;
            }

            _creationDraft = registration;
            RestoreCreationState(actor, registration.Restore, source);
            await ScanCreationRecoveryAsync(registration, version, _pageCts.Token);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or JsonException)
        {
            if (!IsCurrent(version)) return;
            _creationUncertain = true;
            _validationMessage = "Не удалось проверить защищённый черновик. Создание заблокировано.";
        }
    }

    private void RestoreCreationState(Employee actor, ClayDraftRestore restore, string source)
    {
        _creationAttempt = restore.OperationSnapshot;
        if (restore.TypedScopes?.TryGetValue(ReceiptScope, out var stored) == true)
        {
            var receipt = JsonSerializer.Deserialize<CreationReceipt>(stored);
            if (!MatchesReceipt(receipt, source, _creationAttempt))
            {
                _creationUncertain = true;
                _validationMessage = "Не удалось подтвердить происхождение идентификатора. Повтор создания заблокирован.";
                return;
            }

            _creationResult = receipt;
            _receiptPersisted = true;
            _createdEquipmentId = receipt!.EquipmentId;
            _validationMessage = CreationCleanupPending
                ? $"Оборудование №{receipt.EquipmentId} создано. Завершите обработку черновика."
                : $"Оборудование №{receipt.EquipmentId} создано.";
            return;
        }

        if (_creationAttempt is not null)
        {
            SetCreationUnknown();
            return;
        }

        if (!actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            return;

        if (restore.TypedScopes?.TryGetValue(CreationScope, out var payload) == true)
        {
            var saved = JsonSerializer.Deserialize<CreationEditorDraft>(payload);
            if (saved is null || saved.Source != source || saved.Model is null || saved.Model.Id != 0 ||
                string.IsNullOrWhiteSpace(saved.Original))
                throw new JsonException("Контекст черновика оборудования не подтверждён.");

            _model = saved.Model;
            _receiptDate = saved.Date;
            _receiptTime = saved.Time;
            _originalSnapshot = saved.Original;
            _lastEditorDraft = payload;
            _editorRestored = true;
            _draftNeedsReview = _sourceUnavailable || !string.Equals(_freshOriginal, saved.Original, StringComparison.Ordinal);
            if (_draftNeedsReview)
                _validationMessage = _sourceUnavailable
                    ? "Исходное оборудование недоступно. Черновик открыт; создание заблокировано."
                    : "Исходный контекст изменился. Проверьте и примите его для продолжения; черновик сохранён.";
        }

        _restoreCreationRaw = _creationRawPending = restore.HasDraft;
    }

    private static bool MatchesReceipt(CreationReceipt? receipt, string source, ClayDraftOperation? operation) =>
        receipt is { EquipmentId: > 0, Revision: > 0 } && Guid.TryParseExact(receipt.AttemptId, "N", out _) &&
        receipt.Source == source && (operation is null ||
            operation.Kind == CreationKind && operation.AttemptId == receipt.AttemptId && operation.Revision == receipt.Revision);

    private async Task ReleaseCreationDraftAsync()
    {
        var registration = _creationDraft;
        _creationDraft = null;
        if (registration is not null)
            await registration.DisposeAsync();
    }

    private bool IsCurrentCreationInput(ClayDraftRegistration registration, int version) =>
        IsCurrent(version) && ReferenceEquals(_creationDraft, registration);

    private async Task<bool> VerifyCreationRawReadyAsync(ClayDraftRegistration registration, int version, CancellationToken ct)
    {
        try
        {
            var readiness = await registration.GetRestoreReadinessAsync(ct);
            if (!IsCurrentCreationInput(registration, version)) return false;
            _creationRawFailed = readiness.FailedScopes.Contains(CreationScope);
            _creationRawPending = _creationRawFailed || readiness.PendingScopes.Contains(CreationScope);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (!IsCurrentCreationInput(registration, version)) return false;
            _creationRawPending = _creationRawFailed = true;
        }
        return !_creationRawPending;
    }

    private async Task RestoreCreationRawAsync(bool pendingOnly = false)
    {
        if (_creationDraft is not { } registration || _disposed || _saving || _applyingCreationRaw || PreservesCreationOutcome)
            return;
        _restoreCreationRaw = false;
        _applyingCreationRaw = true;
        var version = _operationVersion;
        try
        {
            var raw = pendingOnly
                ? await registration.ApplyPendingRawAsync(CreationScope, _pageCts.Token)
                : await registration.ApplyRawWithResultAsync(CreationScope, _pageCts.Token);
            if (!IsCurrentCreationInput(registration, version)) return;
            if (await VerifyCreationRawReadyAsync(registration, version, _pageCts.Token) && IsCurrentCreationInput(registration, version))
                _creationRawPending = raw.Pending.Count != 0;
        }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException)
        {
            if (IsCurrentCreationInput(registration, version))
                _creationRawPending = _creationRawFailed = true;
        }
        finally
        {
            if (IsCurrentCreationInput(registration, version))
            {
                _applyingCreationRaw = false;
                StateHasChanged();
            }
        }
    }
    private async Task BeginCreationAsync(EquipmentEntity request, ClayDraftRegistration registration, int version, CancellationToken ct)
    {
        var live = Snapshot();
        var snapshot = BuildEditorDraft(request, _originalSnapshot);
        var data = JsonSerializer.Serialize(new
        {
            Source = SourceKey,
            request.EquipmentTypeId, request.ModelId, request.SerialNumber,
            request.MacAddress, request.MacAddress2, request.MacAddressIlo, request.NetworkName,
            request.OwnerId, request.TenantId, request.DocumentReceiptOuId, request.ShipperId, request.Description,
            request.ReceivedAtUtc, request.LocationId, request.LocationDescription, request.EmployeeId,
            request.EmployeeDescription, request.IsAccounting, request.InventoryNumber,
            request.AccountingDocumentId, request.AccountingDescription,
        });

        if (_creationAutosave is { } pending) await pending;
        await ScanCreationRecoveryAsync(registration, version, ct);
        if (CreationBlocked || !IsCurrent(version) || !ReferenceEquals(_creationDraft, registration))
            throw new InvalidOperationException("Не удалось подготовить актуальный черновик создания.");
        await registration.EnsureCommandAllowedAsync(ct);
        await registration.SaveTypedScopeAsync(CreationScope, snapshot, ct);
        _lastEditorDraft = snapshot;
        await registration.OpenScopeAsync(ReceiptScope, ct);
        if (!await VerifyCreationRawReadyAsync(registration, version, ct))
            throw new InvalidOperationException(RawPendingMessage);
        if (live != Snapshot())
            throw new InvalidOperationException("Ввод изменился до отправки. Создание не начато.");
        if (!IsCurrent(version) || !ReferenceEquals(_creationDraft, registration))
            throw new OperationCanceledException();

        var attemptId = Guid.NewGuid().ToString("N");
        _notSentCreationId = attemptId;
        var attempt = await registration.BeginOperationAsync(
            new ClayDraftOperationIntent(CreationKind, data, [CreationScope], attemptId, BlockOtherDraftOperations: true), ct);
        if (!IsCurrent(version) || !ReferenceEquals(_creationDraft, registration))
            throw new OperationCanceledException();
        if (attempt.AttemptId != attemptId || attempt.Kind != CreationKind || attempt.Revision <= 0 ||
            attempt.Status != ClayDraftOperationStatus.Running || attempt.Data != data ||
            attempt.Scopes.Any(scope => scope.Name != CreationScope))
            throw new InvalidOperationException("Хранилище не подтвердило неизменяемую попытку создания.");
        if (!await VerifyCreationRawReadyAsync(registration, version, ct) || live != Snapshot())
        {
            await registration.RejectOperationAsync(attempt.Revision, ct);
            if (IsCurrentCreationInput(registration, version)) _notSentCreationId = null;
            throw new InvalidOperationException("Ввод изменился до отправки. Создание не начато.");
        }
        _creationAttempt = attempt;
        _notSentCreationId = null;
    }

    private void SetCreationUnknown()
    {
        _creationUncertain = true;
        _validationMessage = _creationAttempt is { } attempt
            ? $"Исход попытки {attempt.AttemptId} неизвестен. Повтор создания заблокирован."
            : "Исход создания неизвестен. Повтор создания заблокирован.";
    }

    private async Task MarkCreationUnknownAsync(ClayDraftRegistration registration, ClayDraftOperation attempt)
    {
        SetCreationUnknown();
        try { await registration.UnknownOperationAsync(attempt.Revision, _pageCts.Token); }
        catch (Exception error) when (error is JSException or InvalidOperationException or OperationCanceledException) { }
    }

    private async Task FinishCreationAsync(ClayDraftRegistration registration, CancellationToken ct)
    {
        if (_creationResult is not { } receipt)
            return;

        if (!_receiptPersisted)
        {
            await registration.SaveTypedScopeAsync(ReceiptScope, JsonSerializer.Serialize(receipt), ct);
            _receiptPersisted = true;
        }

        if (_creationAttempt is { } attempt)
        {
            if (attempt.Status != ClayDraftOperationStatus.Confirmed)
            {
                await registration.RecordConfirmedOperationAsync(attempt.Revision, ct);
                _creationAttempt = attempt with { Status = ClayDraftOperationStatus.Confirmed };
            }
            await registration.CommitOperationAsync(attempt.Revision, cancellationToken: ct);
            _creationAttempt = null;
        }
        _validationMessage = $"Оборудование №{receipt.EquipmentId} создано.";
    }

    private async Task RetryCreationCompletionAsync()
    {
        if (_saving || _creationDraft is not { } registration)
            return;
        _saving = true;
        try
        {
            if (_notSentCreationId is { } notSent)
            {
                var remaining = await registration.RetryStorageAsync(notSent, _pageCts.Token);
                _notSentCreationId = null;
                if (remaining is not null)
                {
                    _creationAttempt = remaining;
                    SetCreationUnknown();
                }
                else
                    _validationMessage = "Команда не отправлена. Можно повторить создание.";
            }
            else
                await FinishCreationAsync(registration, _pageCts.Token);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            _validationMessage = _creationResult is { } receipt
                ? $"Оборудование №{receipt.EquipmentId} создано. Завершите обработку черновика."
                : "Не удалось завершить проверку черновика. Создание заблокировано.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _saving = false; }
    }

    private void OpenCreatedEquipment()
    {
        if (_saving || CreationBlocked || _createdEquipmentId is not { } id)
            return;

        try
        {
            _createdEditUrl ??= BuildEditUrl(GetConfiguredUrl("URI_equipment_form"), id);
            _allowNavigation = true;
            Navigation.NavigateTo(_createdEditUrl);
        }
        catch (InvalidOperationException error)
        {
            _validationMessage = $"Оборудование №{id} создано. {error.Message}";
        }
    }

    private async Task BeginAnotherCreationAsync()
    {
        if (_saving || CreationBlocked || _creationResult is null || _creationDraft is not { } registration)
            return;

        _saving = true;
        try
        {
            await registration.DiscardScopeAsync(ReceiptScope, _pageCts.Token);
            await LoadAsync();
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            _validationMessage = "Не удалось завершить прежнее создание. Данные о созданном оборудовании сохранены.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _saving = false; }
    }

    private string BuildEditorDraft(EquipmentEntity model, string original) =>
        JsonSerializer.Serialize(new CreationEditorDraft(SourceKey, original, model, _receiptDate, _receiptTime));

    private async Task AutosaveCreationEditorAsync(bool retry = false)
    {
        if (!_loaded || !_canEdit || _loading || _saving || _disposed || _creationUncertain || _createdEquipmentId is not null ||
            _creationRawPending || _creationRecoveryBusy || _notSentCreationId is not null || (_editorSaveFailed && !retry) ||
            _creationDraft is not { } registration || !IsModified && !_editorRestored)
            return;
        if (_creationAutosave is { } alreadySaving)
        {
            await alreadySaving;
            return;
        }

        var snapshot = BuildEditorDraft(_model, _originalSnapshot);
        if (snapshot == _lastEditorDraft)
        {
            if (_editorSaveFailed)
            {
                _editorSaveFailed = false;
                if (!_draftNeedsReview) _validationMessage = null;
                StateHasChanged();
            }
            return;
        }
        var saving = SaveAsync();
        _creationAutosave = saving;
        try { await saving; }
        finally
        {
            if (ReferenceEquals(_creationAutosave, saving))
                _creationAutosave = null;
        }
        async Task SaveAsync()
        {
            try
            {
                do
                {
                    await registration.SaveTypedScopeAsync(CreationScope, snapshot, _pageCts.Token);
                    if (_disposed || !ReferenceEquals(_creationDraft, registration)) return;
                    _lastEditorDraft = snapshot;
                    if (_editorSaveFailed)
                    {
                        _editorSaveFailed = false;
                        if (!_draftNeedsReview) _validationMessage = null;
                        StateHasChanged();
                    }
                    if (_saving || _loading) return;
                    snapshot = BuildEditorDraft(_model, _originalSnapshot);
                }
                while (snapshot != _lastEditorDraft);
            }
            catch (OperationCanceledException) when (_disposed) { }
            catch (Exception error) when (error is JSException or InvalidOperationException)
            {
                if (!ReferenceEquals(_creationDraft, registration)) return;
                _editorSaveFailed = true;
                _validationMessage = "Не удалось сохранить черновик. Ввод остался на форме. Нажмите «Обновить» для повтора сохранения черновика.";
                StateHasChanged();
            }
        }
    }

    private async Task AcceptCreationBaselineAsync()
    {
        if (_saving || !_draftNeedsReview || _sourceUnavailable || _creationRawPending || _creationUncertain ||
            _createdEquipmentId is not null || _creationDraft is not { } registration || _freshOriginal is not { } fresh)
            return;

        _saving = true;
        var version = _operationVersion;
        var ct = _pageCts.Token;
        try
        {
            if (_creationAutosave is { } pending) await pending;
            var actor = await Service.GetCurrentUserAsync(await PrincipalAsync(), ct);
            if (!IsCurrent(version)) return;
            if (actor?.Id != CurrentUser?.Id || actor?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) != true)
            {
                _validationMessage = "Не удалось подтвердить актуальный доступ. Черновик сохранён.";
                return;
            }

            if (QueryInt("copyId") is > 0 and var copyId)
            {
                var copy = await Service.GetForCopyAsync(copyId);
                if (!IsCurrent(version)) return;
                using var original = JsonDocument.Parse(fresh);
                if (copy is not null)
                {
                    copy.Id = 0;
                    if (copy.EquipmentTypeId is null && QueryInt("codetype") is > 0 and var typeId)
                        copy.EquipmentTypeId = typeId;
                }
                if (copy is null || JsonSerializer.Serialize(copy) != original.RootElement.GetProperty("Model").GetRawText())
                {
                    _validationMessage = "Исходное оборудование снова изменилось или недоступно. Повторите чтение; черновик сохранён.";
                    return;
                }
            }

            if (!await VerifyCreationRawReadyAsync(registration, version, ct)) return;
            var state = await registration.ReadRebaseStateAsync(CreationScope, ct);
            if (!IsCurrent(version)) return;
            var snapshot = BuildEditorDraft(_model, fresh);
            if (!await registration.RebaseScopeAsync(CreationScope, state.Revision, snapshot, [], ct))
            {
                if (IsCurrent(version)) _validationMessage = "Не удалось принять исходный контекст. Черновик сохранён.";
                return;
            }
            if (!IsCurrent(version)) return;

            _originalSnapshot = fresh;
            _lastEditorDraft = snapshot;
            _draftNeedsReview = _editorSaveFailed = false;
            _validationMessage = "Исходный контекст принят. Локальный ввод сохранён.";
        }
        catch (OperationCanceledException) when (_disposed || !IsCurrent(version)) { }
        catch (Exception error) when (error is JSException or InvalidOperationException or JsonException or Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrent(version)) _validationMessage = "Не удалось подтвердить исходный контекст. Черновик сохранён.";
        }
        finally { if (IsCurrent(version)) _saving = false; }
    }

    private sealed record CreationEditorDraft(string Source, string Original, EquipmentEntity Model, DateTime? Date, TimeSpan? Time);
    private sealed record CreationReceipt(string Source, string AttemptId, long Revision, int EquipmentId);
}
