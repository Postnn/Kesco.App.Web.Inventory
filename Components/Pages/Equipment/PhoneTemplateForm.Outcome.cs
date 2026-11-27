using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class PhoneTemplateForm
{
    private const string TemplateJournalForm = "inventory.phone-template";
    private const string TemplateWriteKind = "equipment-phone-template.save";

    private readonly string _journalRoot = $"template-write-{Guid.NewGuid():N}";
    private ClayDraftRegistration? _templateJournal;
    private ClayDraftOperation? _templateAttempt;
    private (int ModelId, int EmployeeId, Guid Instance)? _templateRamOwner;
    private string? _notSentAttempt;
    private string? _journalNotice;
    private string? _submittedTemplate;
    private bool _journalReady;
    private bool _journalBusy;
    private bool _serverWritePending;
    private bool _latestOwnTemplateConfirmed;
    private bool _knownWrite;
    private bool _confirmedStored;
    private int _journalEmployeeId;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool TemplateWriteBlocked => !_journalReady || _journalBusy || _serverWritePending || _templateAttempt is not null ||
        _knownWrite || HasUnprovedTemplateSubmission || _notSentAttempt is not null || _pendingTemplateClose is not null ||
        _templateConflictNeedsReview || TemplateInputBlocked;

    private bool HasUnprovedTemplateSubmission => !_knownWrite && _protectedSubmission is { Confirmed: true };

    private async Task InitializeTemplateJournalAsync(int id, int employeeId, int version)
    {
        ClayDraftRegistration? registration = null;
        try
        {
            var token = _pageCts.Token;
            var entityKey = id.ToString(CultureInfo.InvariantCulture);
            var userKey = employeeId.ToString(CultureInfo.InvariantCulture);
            bool Current() => !_disposed && !token.IsCancellationRequested && version == _loadVersion &&
                id == Id && CurrentUser?.Id == employeeId;

            registration = await Drafts.TransferLegacyAndRegisterRamAsync(TemplateJournalForm, entityKey, userKey, async (evidence, ct) =>
            {
                if (!Current()) throw new InvalidOperationException("Контекст шаблона изменился.");
                if (evidence.Count != 0)
                {
                    var attempts = new List<Guid>(evidence.Count);
                    foreach (var entry in evidence)
                    {
                        if (entry is null || entry.Kind != TemplateWriteKind || entry.Data != entityKey ||
                            !Guid.TryParse(entry.AttemptId, out var attemptId) || attemptId == Guid.Empty)
                            throw new InvalidOperationException("Прежняя попытка относится к другому шаблону.");
                        attempts.Add(attemptId);
                    }

                    var principal = await ReadPrincipalAsync();
                    if (!Current()) throw new InvalidOperationException("Контекст шаблона изменился.");
                    await EquipmentModel.ImportLegacyPhoneTemplateOperationsAsync(Db, principal, employeeId, id, attempts, ct);
                }

                if (!Current()) throw new InvalidOperationException("Контекст шаблона изменился.");
                if (_templateRamOwner is not { } owner || owner.ModelId != id || owner.EmployeeId != employeeId)
                {
                    owner = (id, employeeId, Guid.NewGuid());
                    _templateRamOwner = owner;
                }

                return await Drafts.RegisterRamHandleAsync(TemplateJournalForm, entityKey, userKey,
                    1, $".{_journalRoot}", owner.Instance, ct, MaximumTemplateRestoreBytes);
            }, Current, token);
            if (!Current()) return;
            _templateJournal = registration;
            _journalEmployeeId = employeeId;
            var restoredRegistration = registration;
            registration = null;
            if (_disposed || version != _loadVersion || !ReferenceEquals(_templateJournal, restoredRegistration))
                return;

            RestoreTemplateJournal(_templateJournal);
            if (CurrentUser is { } actor)
                await VerifyTemplateReceiptAsync(_templateJournal, actor, version, _pageCts.Token);
            if (_disposed || version != _loadVersion || !ReferenceEquals(_templateJournal, restoredRegistration))
                return;
            if (_model is { } model)
                await RestoreTemplateInputAsync(_templateJournal, model, version);
            await ScanTemplateJournalAsync(restoredRegistration);
            if (Current() && _journalReady && ReferenceEquals(_templateJournal, restoredRegistration) && _model is { } loadedModel &&
                loadedModel.Id == id && CurrentUser is { } ownerActor && _templateAttempt is null &&
                _protectedSubmission is null && !_serverWritePending)
            {
                try
                {
                    var receipt = await EquipmentModel.ReadLatestPhoneTemplateReceiptAsync(Db, ownerActor, loadedModel, token);
                    if (Current() && ReferenceEquals(_templateJournal, restoredRegistration) &&
                        ReferenceEquals(_model, loadedModel) && _templateAttempt is null && _protectedSubmission is null)
                        _latestOwnTemplateConfirmed = receipt is { State: InventoryOperationState.Confirmed } && receipt.ResultEntityId == id;
                }
                catch (Exception ex) when (IsJournalError(ex))
                {
                    // Состояние записи определяется проверкой журнала, а не доступностью истории.
                }
            }
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (!_disposed && version == _loadVersion)
            {
                _journalReady = false;
                ShowTemplateJournalState();
            }
        }
        finally
        {
            if (registration is not null)
                await registration.DisposeAsync();
        }
    }

    private void RestoreTemplateJournal(ClayDraftRegistration registration)
    {
        _templateAttempt = registration.Restore.OperationSnapshot;
        _protectedSubmission = null;
        if (registration.Restore.TypedScopes?.TryGetValue(TemplateSubmissionScope, out var json) == true)
        {
            var submission = JsonSerializer.Deserialize<TemplateSubmission>(json);
            if (submission is { Revision: > 0, InputRevision: > 0, Text: not null } &&
                submission.ModelId == Id && !string.IsNullOrWhiteSpace(submission.AttemptId) &&
                (_templateAttempt is null ? submission.Confirmed : SubmissionMatches(submission, _templateAttempt)))
                _protectedSubmission = submission;
        }
        _knownWrite = _confirmedStored = false;
    }

    private async Task<bool> VerifyTemplateReceiptAsync(ClayDraftRegistration registration, Employee actor, int version,
        CancellationToken ct)
    {
        var id = Id;
        var marker = _templateAttempt;
        var submission = _protectedSubmission;
        var attemptText = marker is null
            ? submission is { Confirmed: true } ? submission.AttemptId : null
            : IsExpectedAttempt(marker) ? marker.AttemptId : null;
        if (!Guid.TryParse(attemptText, out var attemptId) || attemptId == Guid.Empty)
            return false;

        var receipt = await EquipmentModel.ReadPhoneTemplateReceiptAsync(Db, actor, id, attemptId, ct);
        if (_disposed || ct.IsCancellationRequested || version != _loadVersion || id != Id ||
            actor.Id != CurrentUser?.Id || !ReferenceEquals(_templateJournal, registration) ||
            !ReferenceEquals(marker, _templateAttempt) || !ReferenceEquals(submission, _protectedSubmission))
            return false;
        if (receipt is not { State: InventoryOperationState.Confirmed } || receipt.ResultEntityId != id)
            return false;

        _knownWrite = true;
        _confirmedStored = marker is null || marker.Status == ClayDraftOperationStatus.Confirmed;
        _submittedTemplate = submission?.Text;
        return true;
    }


    private static bool SubmissionMatches(TemplateSubmission submission, ClayDraftOperation attempt) =>
        submission.AttemptId == attempt.AttemptId && submission.Revision == attempt.Revision &&
        attempt.Scopes.Count == 1 && attempt.Scopes[0].Name == TemplateInputScope &&
        attempt.Scopes[0].TypedRevision == submission.InputRevision;

    private bool IsExpectedAttempt(ClayDraftOperation? attempt)
    {
        if (attempt?.Kind != TemplateWriteKind)
            return false;
        using var data = JsonDocument.Parse(attempt.Data);
        return data.RootElement.ValueKind == JsonValueKind.Object &&
            data.RootElement.TryGetProperty("Id", out var id) && id.ValueKind == JsonValueKind.Number &&
            id.TryGetInt32(out var modelId) && modelId == Id;
    }

    private async Task ScanTemplateJournalAsync(ClayDraftRegistration registration)
    {
        _journalReady = false;
        var version = _loadVersion;
        var id = Id;
        var actor = CurrentUser;
        var health = await registration.GetHealthAsync(_pageCts.Token);
        if (actor is null || !ReferenceEquals(_templateJournal, registration) || actor.Id != _journalEmployeeId)
            return;
        var pending = await EquipmentModel.HasPendingPhoneTemplateWriteAsync(Db, actor, id, _pageCts.Token);
        if (_disposed || version != _loadVersion || id != Id || CurrentUser?.Id != actor.Id ||
            !ReferenceEquals(_templateJournal, registration))
            return;
        _serverWritePending = pending;
        _journalReady = health is { Healthy: true, PendingWrites: 0 } &&
            (_templateAttempt is null ? !health.HasOperation : IsExpectedAttempt(_templateAttempt));
        ShowTemplateJournalState();
    }

    private async Task BeginTemplateWriteAsync(ClayDraftRegistration registration, EquipmentModel model,
        EquipmentModelWriteBaseline baseline, string submitted, Guid serverAttemptId, int version, CancellationToken ct)
    {
        await ScanTemplateJournalAsync(registration);
        if (TemplateWriteBlocked || !IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, registration) ||
            CurrentUser?.Id != _journalEmployeeId)
            throw new InvalidOperationException("Журнал не готов. Запись не отправлена.");
        await registration.EnsureCommandAllowedAsync(ct);
        await EnsureTemplateRawReadyAsync(registration, model, version, ct);
        if (!IsCurrentSave(model, version) || model.PhoneTemplate != submitted ||
            !MatchesBrowserTemplateText(submitted, await ReadLiveTemplateAsync(ct)))
            throw new InvalidOperationException("Ввод изменился до отправки. Запись не отправлена.");

        var snapshot = SerializeTemplateDraft(baseline, submitted);
        var expectedInputRevision = await SaveProtectedScopeAsync(registration, TemplateInputScope, snapshot, ct);
        _scheduledInput = snapshot;
        var attemptId = serverAttemptId.ToString("N");
        await registration.OpenScopeAsync(TemplateSubmissionScope, ct);
        var submission = new TemplateSubmission(model.Id, attemptId, 0, expectedInputRevision, submitted, false);
        await SaveProtectedScopeAsync(registration, TemplateSubmissionScope, JsonSerializer.Serialize(submission), ct);

        _notSentAttempt = attemptId;
        var data = JsonSerializer.Serialize(new { model.Id, ExpectedChangedAt = baseline.ChangedAt });
        var attempt = await registration.BeginOperationAsync(
            new ClayDraftOperationIntent(TemplateWriteKind, data, [TemplateInputScope], AttemptId: attemptId,
                BlockOtherDraftOperations: true), ct);
        if (attempt.AttemptId != attemptId || attempt.Kind != TemplateWriteKind || attempt.Data != data ||
            attempt.Status != ClayDraftOperationStatus.Running || attempt.AcceptLegacyTyped)
            throw new InvalidOperationException("Журнал не подтвердил попытку. Запись не отправлена.");
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, registration))
            throw new InvalidOperationException("Контекст журнала изменился. Запись не отправлена.");
        _templateAttempt = attempt;
        _latestOwnTemplateConfirmed = false;
        _notSentAttempt = null;
        submission = submission with { Revision = attempt.Revision };
        if (!SubmissionMatches(submission, attempt) || !IsCurrentSave(model, version) ||
            !MatchesBrowserTemplateText(submitted, await ReadLiveTemplateAsync(ct)))
            throw new InvalidOperationException("Не подтверждена ревизия защищённого ввода. Запись не отправлена.");
        await SaveProtectedScopeAsync(registration, TemplateSubmissionScope, JsonSerializer.Serialize(submission), ct);
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, registration))
            throw new InvalidOperationException("Контекст журнала изменился. Запись не отправлена.");
        _protectedSubmission = submission;
    }

    private async Task RecordTemplateFailureAsync(ClayDraftRegistration registration, bool dispatched, bool noEffect)
    {
        if (_templateAttempt is not { } attempt)
            return;
        try
        {
            if (!dispatched || noEffect)
            {
                await registration.RejectOperationAsync(attempt.Revision, _pageCts.Token);
                _templateAttempt = null;
                if (_protectedSubmission is { Confirmed: false } submission && submission.AttemptId == attempt.AttemptId)
                {
                    await registration.DiscardScopeAsync(TemplateSubmissionScope, _pageCts.Token);
                    _protectedSubmission = null;
                }
            }
            else
            {
                _templateAttempt = attempt with { Status = ClayDraftOperationStatus.Unknown };
                await registration.UnknownOperationAsync(attempt.Revision, _pageCts.Token);
            }
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (!dispatched || noEffect)
                _notSentAttempt = attempt.AttemptId;
        }
    }

    private async Task FinishKnownTemplateWriteAsync(ClayDraftRegistration registration)
    {
        if (!_knownWrite || !ReferenceEquals(_templateJournal, registration) || _templateAttempt is not { } attempt)
            return;
        var submission = _protectedSubmission;
        if (attempt.Scopes.Count > 0 && (submission is null || !SubmissionMatches(submission, attempt)))
            throw new InvalidOperationException("Защищённая ревизия отправки недоступна. Очистка не выполнена.");
        if (!_confirmedStored)
        {
            await registration.RecordConfirmedOperationAsync(attempt.Revision, _pageCts.Token);
            if (!ReferenceEquals(_templateJournal, registration)) return;
            _confirmedStored = true;
            _templateAttempt = attempt with { Status = ClayDraftOperationStatus.Confirmed };
        }
        if (submission is { Confirmed: false })
        {
            var confirmed = submission with { Confirmed = true };
            await SaveProtectedScopeAsync(registration, TemplateSubmissionScope, JsonSerializer.Serialize(confirmed), _pageCts.Token);
            if (!ReferenceEquals(_templateJournal, registration)) return;
            _protectedSubmission = confirmed;
        }
        await registration.CommitOperationAsync(attempt.Revision, cancellationToken: _pageCts.Token);
        if (ReferenceEquals(_templateJournal, registration))
            _templateAttempt = null;
    }

    private async Task ReloadKnownTemplateAsync(EquipmentModel model, int version)
    {
        if (TemplateInputBlocked || _templateJournal is not { } journal)
            throw new InvalidOperationException("Защищённый ввод требует завершения восстановления.");
        var observed = await EquipmentModel.GetForEditAsync(Db, model.Id, _pageCts.Token);
        if (!IsCurrentSave(model, version))
            return;
        if (observed?.EquipmentTypeId != (int)ТипыОборудования.IP_телефон)
            throw new InvalidOperationException("Подтверждённый шаблон больше недоступен.");

        var protectedState = await journal.ReadRebaseStateAsync(TemplateInputScope, _pageCts.Token);
        var local = await ReadLiveTemplateAsync(_pageCts.Token);
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal))
            return;

        var expected = _submittedTemplate ?? _writeBaseline?.PhoneTemplate;
        var keepLocal = !MatchesBrowserTemplateText(expected, local) || protectedState.RawKeys.Count > 0;
        var exactLocal = MatchesBrowserTemplateText(model.PhoneTemplate, local) ? model.PhoneTemplate : local;
        if (keepLocal && observed.PhoneTemplate != expected)
            throw new InvalidOperationException("Актуальный шаблон изменился. Введённый текст сохранён для сверки.");

        var baseline = observed.CaptureWriteBaseline();
        var rebased = keepLocal ? SerializeTemplateDraft(baseline, exactLocal) : null;
        var boundBeforeRebase = model.PhoneTemplate;
        if (rebased is not null && !await journal.RebaseScopeAsync(
            TemplateInputScope, protectedState.Revision, rebased, [], _pageCts.Token))
            throw new InvalidOperationException("Поздний ввод не подтверждён в защищённом черновике.");
        if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal))
            return;

        _writeBaseline = baseline;
        _scheduledInput = rebased;
        model.ChangedAt = observed.ChangedAt;
        model.ChangedById = observed.ChangedById;
        model.ChangedByName = observed.ChangedByName;
        if (model.PhoneTemplate == boundBeforeRebase)
        {
            ++_templateInputEpoch;
            var nextText = keepLocal ? exactLocal : observed.PhoneTemplate;
            if (!MatchesBrowserTemplateText(nextText, local))
                ++_templateEditorRenderKey;
            model.PhoneTemplate = nextText;
        }
        await PersistTemplateInputAsync();
        if (_inputSaveFailed)
            throw new InvalidOperationException("Не удалось подтвердить поздний ввод.");

        if (_protectedSubmission is { Confirmed: true })
        {
            await journal.DiscardScopeAsync(TemplateSubmissionScope, _pageCts.Token);
            if (!IsCurrentSave(model, version) || !ReferenceEquals(_templateJournal, journal)) return;
            _protectedSubmission = null;
        }
        _submittedTemplate = null;
        _knownWrite = false;
        _confirmedStored = false;
        ShowTemplateJournalState();
    }

    private async Task RetryTemplateJournalAsync()
    {
        if (_loading || _saving || _journalBusy || _disposed || CurrentUser is not { } actor || _model is not { } model)
            return;
        var version = _loadVersion;
        _journalBusy = true;
        try
        {
            if (_templateJournal is not { } journal)
            {
                await InitializeTemplateJournalAsync(model.Id, actor.Id, version);
                return;
            }
            if (_notSentAttempt is { } notSent)
            {
                _templateAttempt = await journal.RetryStorageAsync(notSent, _pageCts.Token);
                _notSentAttempt = null;
            }
            await FinishKnownTemplateWriteAsync(journal);
            if (_knownWrite)
                await ReloadKnownTemplateAsync(model, version);
            await ScanTemplateJournalAsync(journal);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            _journalReady = false;
            ShowTemplateJournalState();
        }
        finally { _journalBusy = false; }
    }

    private async Task ObserveTemplateAsync()
    {
        if (_loading || _saving || _journalBusy || _disposed || _model is not { } model || _templateJournal is not { } journal)
            return;
        var version = _loadVersion;
        _journalBusy = true;
        try
        {
            if (CurrentUser is not { } openedBy) return;
            var token = _pageCts.Token;
            var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
            if (!IsCurrentSave(model, version) || actor?.Id != openedBy.Id || !actor.HasRole(Роли.Дежурный_инженер))
                return;
            if (await VerifyTemplateReceiptAsync(journal, actor, version, token))
            {
                await FinishKnownTemplateWriteAsync(journal);
                await ReloadKnownTemplateAsync(model, version);
                await ScanTemplateJournalAsync(journal);
                return;
            }
            var observed = await EquipmentModel.GetForEditAsync(Db, model.Id, _pageCts.Token);
            if (IsCurrentSave(model, version))
                _journalNotice = observed is null
                    ? "Актуальная модель недоступна. Повтор записи заблокирован до сверки."
                    : "Актуальная строка прочитана. Это чтение не подтверждает результат прежней попытки, повтор заблокирован.";
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsCurrentSave(model, version))
                _journalNotice = "Не удалось прочитать актуальный шаблон. Повтор записи заблокирован до сверки.";
        }
        finally { _journalBusy = false; }
    }

    private void ShowTemplateJournalState()
    {
        _journalNotice = _knownWrite ? "Запись шаблона подтверждена. Повтор записи не требуется. Нажмите «Обновить» для чтения сохранённого шаблона."
            : _notSentAttempt is not null ? "Запись не отправлена. Нажмите «Обновить» для проверки состояния записи."
            : _templateAttempt is not null || HasUnprovedTemplateSubmission ? "Результат предыдущей записи шаблона неизвестен. Повтор записи заблокирован до сверки."
            : _serverWritePending ? "Сервер обнаружил незавершённую запись модели. Изменение шаблона заблокировано."
            : !_journalReady ? "Журнал сохранения недоступен. Запись не отправлена." : null;
    }

    private async ValueTask ReleaseTemplateJournalAsync()
    {
        CancelTemplateConflictReview();
        var journal = _templateJournal;
        _templateJournal = null;
        _pendingTemplateClose = null;
        _journalEmployeeId = 0;
        _templateAttempt = null;
        _notSentAttempt = null;
        _journalNotice = null;
        _latestOwnTemplateConfirmed = false;
        _submittedTemplate = null;
        _protectedSubmission = null;
        _scheduledInput = null;
        _inputNotice = null;
        _restoreTemplateRawRequested = _rawPending = _inputSaveFailed = _inputMissingOriginal = false;
        _rawRestoreFlight = null;
        _journalReady = _knownWrite = _confirmedStored = _serverWritePending = false;
        if (journal is not null)
            await journal.DisposeAsync();
    }

    private static bool IsJournalError(Exception error) => error is JSException or JSDisconnectedException or
        Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException or
        JsonException or OperationCanceledException or TimeoutException;
}
