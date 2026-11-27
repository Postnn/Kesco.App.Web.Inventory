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
    private const string CreationKind = "equipment.create";
    private const string RawPendingMessage = "Часть ввода ожидает восстановления. Нажмите «Обновить» для повтора.";
    private readonly string _creationRoot = $"equipment-ins-draft-{Guid.NewGuid():N}";
    private Guid _creationInstance = Guid.NewGuid();
    private readonly ClayToolbarButtonOptions _openCreatedOptions = new() { Text = "Открыть созданное оборудование" };
    private readonly ClayToolbarButtonOptions _newCreationOptions = new() { Text = "Начать другое создание" };

    private readonly ClayToolbarButtonOptions _reviewCreationOptions = new() { Text = "Принять актуальный исходный контекст" };

    [Inject] private ClayDraftService Drafts { get; set; } = null!;
    private ClayDraftRegistration? _creationDraft;
    private CreationReceipt? _creationResult;
    private bool _creationServerChecked;
    private int? _priorCreatedEquipmentId;
    private string? _creationSentSnapshot;
    private string? _creationLateEditorPayload;
    private bool _retainedCreationInput;
    private bool _creationUncertain;
    private bool _creationRawPending;
    private bool _creationRawFailed;
    private bool _applyingCreationRaw;
    private bool _restoreCreationRaw;
    private string? _freshOriginal;
    private string? _lastEditorDraft;
    private Task? _creationAutosave;
    private bool _sourceUnavailable;
    private bool _draftNeedsReview;
    private bool _editorRestored;
    private bool _editorSaveFailed;

    private bool CreationBlocked => _creationDraft is null || _creationUncertain ||
        _pendingCreationClose is not null || _creationDraftClosed ||
        _creationResult is null && (!_creationServerChecked || _sourceUnavailable || _draftNeedsReview || _editorSaveFailed) ||
        _creationRawPending || _applyingCreationRaw;

    private async Task AttachCreationDraftAsync(Employee? actor, string? principal, int version)
    {
        if (actor?.HasRole(Роли.Специалист_по_инвентаризации_оборудования) != true) return;
        var source = SourceKey;
        var ct = _pageCts.Token;
        bool Current() => !ct.IsCancellationRequested && IsCurrent(version) && CurrentUser?.Id == actor.Id && SourceKey == source;
        try
        {
            var userKey = actor.Id.ToString(CultureInfo.InvariantCulture);
            var registration = await Drafts.RegisterRamHandleAsync("equipment-ins", source, userKey,
                1, "." + _creationRoot, _creationInstance, ct);
            if (!Current())
            {
                await registration.DisposeAsync();
                return;
            }

            _creationDraft = registration;
            _creationServerChecked = true;
            if (IsCurrentCreationInput(registration, version))
                await RestoreCreationStateAsync(actor, registration, source, version);
        }
        catch (OperationCanceledException) when (!Current()) { }
        catch (Exception error) when (error is JSException or InvalidOperationException or JsonException or UnauthorizedAccessException
            or Microsoft.Data.SqlClient.SqlException or TimeoutException)
        {
            if (!IsCurrent(version)) return;
            if (_creationDraft is not null) _creationUncertain = true;
            _creationServerChecked = false;
            _validationMessage = "Не удалось проверить защищённый черновик. Нажмите «Обновить», чтобы повторить проверку.";
        }
    }

    private async Task RestoreCreationStateAsync(Employee actor, ClayDraftRegistration registration, string source, int version)
    {
        var restore = registration.Restore;

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

    private async Task<bool> ReadCreationServerStateAsync(Employee actor, string? principal, int version, CancellationToken ct)
    {
        if (!IsCurrent(version) || CurrentUser?.Id != actor.Id) return false;
        _creationServerChecked = false;
        var current = await Service.GetCurrentUserAsync(principal, ct);
        if (!IsCurrent(version)) return false;
        _creationServerChecked = true;
        return current?.Id == actor.Id && current.HasRole(Роли.Специалист_по_инвентаризации_оборудования);
    }

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
    private async Task FinishCreationAsync(ClayDraftRegistration registration, CancellationToken ct)
    {
        var version = _operationVersion;
        if (_creationResult is not { } receipt)
            return;
        if (_creationLateEditorPayload is { } later)
        {
            var revision = await registration.SaveTypedScopeAsync(CreationScope, later, ct);
            if (!IsCurrentCreationInput(registration, version)) return;
            if (revision <= 0)
                throw new InvalidOperationException("Более поздний ввод ещё не подтверждён.");
            _creationLateEditorPayload = null;
        }

        _validationMessage = $"Оборудование №{receipt.EquipmentId} создано.";
    }

    private async Task RetryCreationCompletionAsync()
    {
        if (_saving || _creationDraft is not { } registration)
            return;
        _saving = true;
        var version = _operationVersion;
        try
        {
            if (IsCurrentCreationInput(registration, version) && _creationResult is not null)
                await FinishCreationAsync(registration, _pageCts.Token);
        }
        catch (Exception error) when (error is JSException or InvalidOperationException)
        {
            _validationMessage = _creationResult is { } receipt
                ? $"Оборудование №{receipt.EquipmentId} создано. Для проверки результата нажмите «Обновить»."
                : "Не удалось проверить результат создания. Повтор заблокирован.";
        }
        catch (OperationCanceledException) when (_disposed) { }
        finally { _saving = false; }
    }

    private async Task OpenCreatedEquipment()
    {
        if (_saving || CreationBlocked || _createdEquipmentId is not { } id)
            return;

        try
        {
            _createdEditUrl ??= BuildEditUrl(GetConfiguredUrl("URI_equipment_form"), id);
            var url = _createdEditUrl;
            await PrepareEditorExitAsync(() => NavigateToCreatedAsync(url), _pageCts.Token);
        }
        catch (InvalidOperationException error)
        {
            _validationMessage = $"Оборудование №{id} создано. {error.Message}";
        }
    }

    private Task NavigateToCreatedAsync(string url)
    {
        _allowNavigation = true;
        Navigation.NavigateTo(url);
        return Task.CompletedTask;
    }

    private async Task BeginAnotherCreationAsync()
    {
        if (_saving || CreationBlocked || _creationResult is null || _creationDraft is not { } registration)
            return;

        _saving = true;
        try
        {
            await registration.DiscardScopeAsync(CreationScope, _pageCts.Token);
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
            _creationRawPending || (_editorSaveFailed && !retry) ||
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
                _validationMessage = "Не удалось сохранить черновик. Ввод остался на форме. Для обновления состояния нажмите «Обновить».";
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
    private sealed record CreationReceipt(int EquipmentId);
}
