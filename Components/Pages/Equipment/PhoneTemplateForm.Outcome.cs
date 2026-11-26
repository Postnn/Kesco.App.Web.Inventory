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
    private IReadOnlyList<ClayDraftRecoveryCandidate> _journalCandidates = [];
    private string? _notSentAttempt;
    private string? _journalNotice;
    private string? _submittedTemplate;
    private bool _journalReady;
    private bool _journalBusy;
    private bool _knownWrite;
    private bool _confirmedStored;
    private int _journalEmployeeId;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool TemplateWriteBlocked => !_journalReady || _journalBusy || _templateAttempt is not null ||
        _knownWrite || _notSentAttempt is not null || _journalCandidates.Count > 0 || TemplateInputBlocked;

    private async Task InitializeTemplateJournalAsync(int id, int employeeId, int version)
    {
        ClayDraftRegistration? registration = null;
        try
        {
            registration = await Drafts.RegisterHandleAsync(TemplateJournalForm, id.ToString(CultureInfo.InvariantCulture),
                employeeId.ToString(CultureInfo.InvariantCulture), 1, $".{_journalRoot}", MaximumTemplateRestoreBytes, _pageCts.Token);
            if (_disposed || version != _loadVersion || id != Id)
                return;
            _templateJournal = registration;
            _journalEmployeeId = employeeId;
            var restoredRegistration = registration;
            registration = null;
            if (!restoredRegistration.Restore.HasDraft && restoredRegistration.Restore.OperationSnapshot is null)
                await AdoptSingleTemplateCandidateAsync(restoredRegistration, version);
            if (_disposed || version != _loadVersion || !ReferenceEquals(_templateJournal, restoredRegistration))
                return;

            RestoreTemplateJournal(_templateJournal);
            if (_model is { } model)
                await RestoreTemplateInputAsync(_templateJournal, model, version);
            await ScanTemplateJournalAsync(_templateJournal);
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
        _knownWrite = _confirmedStored = _templateAttempt is null
            ? _protectedSubmission?.Confirmed == true
            : _templateAttempt.Status == ClayDraftOperationStatus.Confirmed && IsExpectedAttempt(_templateAttempt);
        if (_knownWrite && _protectedSubmission is { } known)
            _submittedTemplate = known.Text;
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

    private async Task AdoptSingleTemplateCandidateAsync(ClayDraftRegistration registration, int version)
    {
        var token = _pageCts.Token;
        var candidates = await registration.ListRecoveryCandidatesAsync(token)
            ?? throw new InvalidOperationException("Не удалось проверить предыдущие попытки сохранения.");
        if (_disposed || version != _loadVersion || !ReferenceEquals(_templateJournal, registration) ||
            IsModified || candidates is not [{ CanRecover: true } candidate] ||
            CurrentUser is not { } previousActor)
            return;

        var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), token);
        if (!_disposed && version == _loadVersion && ReferenceEquals(_templateJournal, registration) &&
            !IsModified && actor?.Id == previousActor.Id && actor.HasRole(Роли.Дежурный_инженер))
            await registration.RecoverListedDraftAsync(candidate.DraftId, token);
    }

    private async Task ScanTemplateJournalAsync(ClayDraftRegistration registration)
    {
        _journalReady = false;
        var candidates = await registration.ListRecoveryCandidatesAsync(_pageCts.Token)
            ?? throw new InvalidOperationException("Журнал не подтвердил поиск сохранений.");
        var health = await registration.GetHealthAsync(_pageCts.Token);
        if (!ReferenceEquals(_templateJournal, registration))
            return;
        _journalCandidates = candidates;
        _journalReady = health is { Healthy: true, PendingWrites: 0 } &&
            (_templateAttempt is null ? !health.HasOperation : IsExpectedAttempt(_templateAttempt));
        ShowTemplateJournalState();
    }

    private async Task BeginTemplateWriteAsync(ClayDraftRegistration registration, EquipmentModel model,
        EquipmentModelWriteBaseline baseline, string submitted, int version, CancellationToken ct)
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
        var attemptId = Guid.NewGuid().ToString("N");
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
        if (_loading || _saving || _journalBusy || _disposed || _model is not { } model || _templateAttempt is null)
            return;
        var version = _loadVersion;
        _journalBusy = true;
        try
        {
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
            : _templateAttempt is not null ? "Результат предыдущей записи шаблона неизвестен. Повтор записи заблокирован до сверки."
            : _journalCandidates.Count > 0 ? "Есть сохранённая попытка. Повтор записи заблокирован."
            : !_journalReady ? "Журнал сохранения недоступен. Запись не отправлена." : null;
    }

    private async ValueTask ReleaseTemplateJournalAsync()
    {
        var journal = _templateJournal;
        _templateJournal = null;
        _journalEmployeeId = 0;
        _templateAttempt = null;
        _journalCandidates = [];
        _notSentAttempt = null;
        _journalNotice = null;
        _submittedTemplate = null;
        _protectedSubmission = null;
        _scheduledInput = null;
        _inputNotice = null;
        _restoreTemplateRawRequested = _rawPending = _inputSaveFailed = _inputMissingOriginal = false;
        _rawRestoreFlight = null;
        _journalReady = _knownWrite = _confirmedStored = false;
        if (journal is not null)
            await journal.DisposeAsync();
    }

    private static bool IsJournalError(Exception error) => error is JSException or JSDisconnectedException or
        Microsoft.Data.SqlClient.SqlException or InvalidOperationException or UnauthorizedAccessException or
        JsonException or OperationCanceledException;
}
