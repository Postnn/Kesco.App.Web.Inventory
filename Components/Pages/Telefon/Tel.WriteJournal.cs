using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

public partial class Tel
{
    private const string TelJournalForm = "inventory.tel";
    private const string TelProgressScope = "tel-progress";
    private const string TelAttemptKind = "tel.write";
    private static readonly ClayToolbarButtonOptions RetryAtsOptions = new() { Text = "Повторить неотправленные команды АТС" };
    private static readonly ClayToolbarButtonOptions InspectTelOptions = new() { Text = "Прочитать состояние номера" };

    private readonly string _telJournalRoot = $"tel-write-{Guid.NewGuid():N}";
    private ClayDraftRegistration? _telJournal;
    private ClayDraftOperation? _telMarker;
    private TelWriteProgress? _telProgress;
    private (string Key, int Actor, Guid Instance)? _telRamOwner;
    private string? _telJournalKey;
    private string? _notSentAttemptId;
    private string? _telJournalNotice;
    private int _telJournalActor;
    private string? _telSubmittedFingerprint;
    private bool _telJournalReady;
    private bool _telServerWritePending;
    private (TelWorkflowSummary Summary, int ActorId, int Version)? _telWorkflowSummary;
    private bool _telJournalBusy;
    private bool _telReceiptRecorded;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool TelWriteBlocked => _telChildOpen || TelWriteBlockedOutsideChild;

    private bool TelWriteBlockedOutsideChild => !_telJournalReady || _telServerWritePending || _telJournalBusy || _telEditorTransition || TelInputBlocked || _telMarker is not null ||
        _telProgress is not null || _notSentAttemptId is not null || _telCloseRevision is not null;

    private bool CanRetryAts => _telMarker?.Status == ClayDraftOperationStatus.Running &&
        _telProgress is { ServerWorkflow: true, Stage: TelWriteStage.SqlConfirmed, SqlConfirmed: true,
            PhoneId: > 0, SqlFingerprint.Length: > 0, Pending.Count: > 0 };
    private bool IsTelJournalInteractionBlocked => _loading || _saving || _telJournalBusy || _telEditorTransition || _disposed;

    private TelWorkflowSummary? VisibleTelWorkflowSummary =>
        !_loading && _telWorkflowSummary is { } entry && entry.ActorId == CurrentUser?.Id &&
        IsTelWriteCurrent(entry.Version) ? entry.Summary : null;

    private static string JournalKey(int id, int copyId) =>
        id > 0 ? id.ToString(CultureInfo.InvariantCulture) : $"new:{copyId.ToString(CultureInfo.InvariantCulture)}";

    private async Task AttachTelJournalAsync(Employee actor, int id, int copyId, int version)
    {
        var key = JournalKey(id, copyId);
        if (_telJournal is not null && _telJournalKey == key && _telJournalActor == actor.Id)
            return;
        ClayDraftRegistration? opened = null;
        try
        {
            await ResetTelJournalAsync();
            if (!IsCurrent(version) || CurrentUser?.Id != actor.Id) return;
            var token = _pageCts.Token;
            var userKey = actor.Id.ToString(CultureInfo.InvariantCulture);
            var legacyKey = id > 0 ? key : "new";
            bool Current() => IsCurrent(version) && CurrentUser?.Id == actor.Id && !token.IsCancellationRequested;
            opened = await Drafts.TransferLegacyAndRegisterRamAsync(TelJournalForm, legacyKey, userKey,
                async (evidence, ct) =>
                {
                    await ImportLegacyTelEvidenceAsync(evidence, actor.Id, id, version, ct);
                    if (!Current())
                        throw new InvalidOperationException("Контекст телефонного номера изменился.");
                    if (_telRamOwner is not { } owner || owner.Key != key || owner.Actor != actor.Id)
                    {
                        owner = (key, actor.Id, Guid.NewGuid());
                        _telRamOwner = owner;
                    }
                    return await Drafts.RegisterRamHandleAsync(TelJournalForm, key, userKey,
                        1, $".{_telJournalRoot}", owner.Instance, ct);
                }, Current, token);
            if (!Current())
                return;
            _telJournal = opened;
            opened = null;
            _telJournalActor = actor.Id;
            _telJournalKey = key;
            RestoreTelJournal();
            await ScanTelJournalAsync(_telJournal);
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException or TimeoutException)
        {
            if (IsCurrent(version))
            {
                _telJournalReady = false;
                UpdateTelJournalNotice();
            }
        }
        finally
        {
            if (opened is not null)
                await opened.DisposeAsync();
        }
    }

    private async Task ImportLegacyTelEvidenceAsync(IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, int employeeId,
        int routePhoneId, int version, CancellationToken token)
    {
        if (!IsCurrent(version) || CurrentUser?.Id != employeeId)
            throw new InvalidOperationException("Контекст телефонного номера изменился.");
        if (evidence.Count == 0) return;

        var expectedPhoneId = Math.Max(0, routePhoneId);
        var requests = new List<(Guid AttemptId, bool Deleting)>(evidence.Count);
        foreach (var entry in evidence)
        {
            if (entry is null || entry.Kind != TelAttemptKind || entry.Data is not { Length: <= 128 } ||
                !Guid.TryParse(entry.AttemptId, out var attemptId) || attemptId == Guid.Empty)
                throw new InvalidOperationException("Прежняя попытка телефонного номера требует сверки.");
            using var data = JsonDocument.Parse(entry.Data);
            var root = data.RootElement;
            if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Count() != 2 ||
                !root.TryGetProperty("PhoneId", out var id) || id.ValueKind != JsonValueKind.Number ||
                !id.TryGetInt32(out var phoneId) || phoneId != expectedPhoneId ||
                !root.TryGetProperty("Deleting", out var deleting) ||
                deleting.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                expectedPhoneId == 0 && deleting.GetBoolean())
                throw new InvalidOperationException("Прежняя попытка относится к другому телефонному номеру.");

            requests.Add((attemptId, deleting.GetBoolean()));
        }
        var principal = await ReadPrincipalAsync();
        if (!IsCurrent(version) || CurrentUser?.Id != employeeId)
            throw new InvalidOperationException("Контекст телефонного номера изменился.");
        await Service.ImportLegacyTelOperationsAsync(principal, employeeId, expectedPhoneId, requests, token);
    }


    private void RestoreTelJournal()
    {
        var restored = _telJournal!.Restore;
        _telMarker = restored.OperationSnapshot;
        _telReceiptRecorded = _telMarker?.Status == ClayDraftOperationStatus.Confirmed;
        _telProgress = null;
        if (restored.TypedScopes?.TryGetValue(TelProgressScope, out var json) == true &&
            JsonSerializer.Deserialize<TelWriteProgress>(json) is { Version: 1, Pending: not null } progress &&
            (_telMarker is { Kind: TelAttemptKind } marker && marker.AttemptId == progress.AttemptId ||
             _telMarker is null && progress.Stage == TelWriteStage.Complete))
            _telProgress = progress;
        ShowPendingAts();
    }

    private async Task ScanTelJournalAsync(ClayDraftRegistration registration)
    {
        var version = _operationVersion;
        _telJournalReady = false;
        var health = await registration.GetHealthAsync(_pageCts.Token);
        if (!await ReadServerTelWorkflowAsync(registration, version))
            return;
        _telJournalReady = health is { Healthy: true, PendingWrites: 0 } &&
            (!health.HasOperation || _telMarker is not null);
        UpdateTelJournalNotice();
    }


    private async Task<bool> ReadServerTelWorkflowAsync(ClayDraftRegistration registration, int version)
    {
        var actorId = _telJournalActor;
        var key = _telJournalKey;
        var token = _pageCts.Token;
        bool Current() => IsTelInputCurrent(registration, version) && key == _telJournalKey &&
            actorId == _telJournalActor && actorId == CurrentUser?.Id;

        if (!Current()) return false;
        var principal = await ReadPrincipalAsync();
        if (!Current()) return false;
        var phoneId = int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : 0;
        _telWorkflowSummary = null;
        var pending = await Service.ReadSqlPendingAsync(principal, actorId, phoneId, token);
        if (!Current()) return false;
        _telServerWritePending = pending;
        if (!pending) return true;

        try
        {
            var summary = await Service.ReadActiveWorkflowAsync(principal, actorId, phoneId, token);
            if (!Current()) return false;
            if (summary is not null)
                _telWorkflowSummary = (summary, actorId, version);
        }
        catch (Exception error) when (IsTelJournalError(error) || error is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            // Ошибка чтения подробностей не отменяет подтверждённую сервером блокировку.
        }
        return Current();
    }

    private bool HasOnlyServerTelWorkflow => _telServerWritePending &&
        _telMarker is null && _telProgress is null && _notSentAttemptId is null;

    private Task RefreshTelAsync() => HasOnlyServerTelWorkflow ? ObserveTelWriteAsync() : RefreshAsync();

    private bool IsTelWriteCurrent(int version) => IsCurrent(version) && _telJournalKey == JournalKey(Id, CopyId);

    private string CurrentTelInputFingerprint() =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Phone = _model, Relations = _relations, _parallelQuickNumber, _hasReturnDate, _servicesUserChoice, _directoryUserChoice })));

    private bool HasLaterTelInput => _telSubmittedFingerprint is { } fingerprint
        ? fingerprint != CurrentTelInputFingerprint()
        : HasUnsavedDraft;

    private async Task BeginTelWriteAsync(Employee actor, int phoneId, bool deleting, PhoneSaveRequest request,
        IReadOnlyList<PhoneSyncOperation> pre, int version)
    {
        var registration = _telJournal ?? throw new InvalidOperationException("Журнал записи недоступен.");
        await ScanTelJournalAsync(registration);
        if (TelWriteBlocked || actor.Id != _telJournalActor || !IsTelWriteCurrent(version))
            throw new InvalidOperationException("Состояние журнала текущего номера не подтверждено.");
        await registration.EnsureCommandAllowedAsync(_pageCts.Token);
        if (!deleting)
            await EnsureTelInputRestoredAsync(registration, version);
        await registration.OpenScopeAsync(TelProgressScope, _pageCts.Token);
        long? acceptedTyped = null;
        if (!deleting)
        {
            if (HasLaterTelInput)
                throw new InvalidOperationException("Ввод изменился до отправки. Проверьте данные перед сохранением.");
            acceptedTyped = await PersistTelInputScopeAsync(registration, SerializeTelInput());
        }
        var workflowId = Guid.NewGuid();
        var attemptId = workflowId.ToString("N");
        _notSentAttemptId = attemptId;
        var fingerprint = SqlFingerprint(request);
        var data = JsonSerializer.Serialize(new { PhoneId = phoneId, Deleting = deleting, request.OriginalPhone.ChangedAtUtc, Fingerprint = fingerprint });
        var attempt = await registration.BeginOperationAsync(
            new ClayDraftOperationIntent(TelAttemptKind, data, deleting ? [] : [TelInputScope], AttemptId: attemptId,
                BlockOtherDraftOperations: true), _pageCts.Token);
        if (attempt.AttemptId != attemptId || attempt.Kind != TelAttemptKind || attempt.Data != data ||
            attempt.Status != ClayDraftOperationStatus.Running)
            throw new InvalidOperationException("Журнал не подтвердил текущую попытку.");
        _telMarker = attempt;
        _notSentAttemptId = null;
        if (!deleting && (attempt.Scopes.SingleOrDefault(scope => scope.Name == TelInputScope)?.TypedRevision != acceptedTyped ||
            HasLaterTelInput))
        {
            await RejectUnsentTelAttemptAsync(registration, attempt);
            throw new InvalidOperationException("Ввод изменился до отправки. Черновик сохранён, запись не отправлена.");
        }
        await SaveTelProgressAsync(new TelWriteProgress(attemptId, deleting, phoneId, TelWriteStage.BeforeSql, pre,
            ServerWorkflow: true));
        var principal = await ReadPrincipalAsync();
        if (!IsTelWriteCurrent(version))
            throw new OperationCanceledException();
        await Service.BeginWorkflowAsync(principal, actor.Id, workflowId, phoneId, deleting, pre, _pageCts.Token);
    }

    private async Task RejectUnsentTelAttemptAsync(ClayDraftRegistration registration, ClayDraftOperation attempt)
    {
        try
        {
            await registration.RejectOperationAsync(attempt.Revision);
            _telMarker = null;
        }
        catch (Exception ex) when (IsTelJournalError(ex))
        {
            _notSentAttemptId = attempt.AttemptId;
            _telJournalReady = false;
        }
        UpdateTelJournalNotice();
    }

    private async Task SaveTelProgressAsync(TelWriteProgress progress)
    {
        var registration = _telJournal ?? throw new InvalidOperationException("Журнал записи недоступен.");
        // Даже при недоступности хранилища в этом circuit сохраняем самую консервативную фазу.
        _telProgress = progress;
        if (await registration.SaveTypedScopeAsync(TelProgressScope, JsonSerializer.Serialize(progress)) <= 0)
            throw new InvalidOperationException("Журнал не подтвердил фазу операции.");
        ShowPendingAts();
    }

    private Guid CurrentSqlAttemptId() => SqlAttemptId(_telProgress);

    private static Guid SqlAttemptId(TelWriteProgress? progress) => Guid.TryParse(progress?.AttemptId, out var id) && id != Guid.Empty
        ? id : throw new InvalidOperationException("Идентификатор SQL-попытки телефонного номера не подтверждён.");

    private TelSqlBoundary SqlBoundary(PhoneSaveRequest request, int version, Action onDispatch,
        Func<IReadOnlyList<PhoneSyncOperation>> postSqlPlan) => new(
        async () =>
        {
            if (!IsTelWriteCurrent(version) || _telProgress is not { ServerWorkflow: true } progress)
                throw new OperationCanceledException();
            var post = postSqlPlan();
            await SaveTelProgressAsync(progress with
            {
                Stage = TelWriteStage.SqlDispatching, SqlFingerprint = SqlFingerprint(request), SqlPlanReady = true,
                Pending = post,
            });
            var principal = await ReadPrincipalAsync();
            if (!IsTelWriteCurrent(version) || _telProgress?.AttemptId != progress.AttemptId)
                throw new OperationCanceledException();
            await Service.MarkWorkflowSqlReadyAsync(principal, _telJournalActor, CurrentSqlAttemptId(), post, _pageCts.Token);
        },
        () =>
        {
            if (!IsTelWriteCurrent(version))
                throw new OperationCanceledException();
            onDispatch();
        }, WorkflowBound: true);

    private async Task<bool> RunJournaledAtsAsync(Employee actor, bool beforeSql, int version)
    {
        while (_telProgress is { Pending.Count: > 0 } progress)
        {
            if (!IsTelWriteCurrent(version) || !progress.ServerWorkflow)
                return false;
            var operation = progress.Pending[0];
            var ordinal = beforeSql ? progress.NextPreStep : progress.NextPostStep;
            var principal = await ReadPrincipalAsync();
            if (!IsTelWriteCurrent(version) || _telProgress?.AttemptId != progress.AttemptId)
                return false;
            var result = await Service.SynchronizeWorkflowStepAsync(CurrentSqlAttemptId(), principal, actor.Id,
                progress.PhoneId, beforeSql, ordinal, operation, _pageCts.Token, async _ =>
                {
                    if (!IsTelWriteCurrent(version))
                        throw new OperationCanceledException();
                    await SaveTelProgressAsync(progress with
                    {
                        Stage = beforeSql ? TelWriteStage.PreAtsDispatching : TelWriteStage.PostAtsDispatching,
                    });
                    if (!IsTelWriteCurrent(version))
                        throw new OperationCanceledException();
                });
            if (!IsTelWriteCurrent(version) || _telProgress?.AttemptId != progress.AttemptId)
                return false;
            if (result.AttemptId != progress.AttemptId || result.Operation != operation)
                throw new InvalidOperationException("Результат АТС не относится к текущей операции.");

            if (result.Phase == PhoneSyncPhase.Succeeded)
            {
                await SaveTelProgressAsync(progress with
                {
                    Stage = beforeSql ? TelWriteStage.BeforeSql : TelWriteStage.SqlConfirmed,
                    PreEffect = beforeSql || progress.PreEffect,
                    Pending = progress.Pending.Skip(1).ToArray(),
                    LastAtsPhase = result.Phase,
                    NextPreStep = beforeSql ? ordinal + 1 : progress.NextPreStep,
                    NextPostStep = beforeSql ? progress.NextPostStep : ordinal + 1,
                });
                continue;
            }

            if (result.Phase is PhoneSyncPhase.NotSent or PhoneSyncPhase.Rejected)
            {
                if (beforeSql)
                    await HandleSqlRejectionAsync(false);
                else
                    await SaveTelProgressAsync(progress with { Stage = TelWriteStage.SqlConfirmed, LastAtsPhase = result.Phase });
            }
            else
            {
                await MarkTelManualAsync(progress with { LastAtsPhase = result.Phase });
            }
            _validationMessage = beforeSql ? "Подготовка АТС не завершена. SQL не отправлен."
                : "SQL подтверждён. Не все команды АТС завершились.";
            if (result.Phase == PhoneSyncPhase.Rejected)
                _validationMessage += " Авторизация или цель АТС не подтверждены.";
            UpdateTelJournalNotice();
            return false;
        }
        return true;
    }

    private async Task HandleSqlRejectionAsync(bool dispatched)
    {
        if (_telProgress is not { } progress || _telMarker is not { } marker || _telJournal is not { } registration)
            return;
        if (dispatched || progress.PreEffect)
        {
            await MarkTelManualAsync(progress with { SqlConfirmed = false, SqlRejected = !dispatched });
            return;
        }
        try
        {
            if (progress.ServerWorkflow)
            {
                var employeeId = _telJournalActor;
                var principal = await ReadPrincipalAsync();
                if (!ReferenceEquals(registration, _telJournal) || !ReferenceEquals(marker, _telMarker))
                    return;
                var abandoned = await Service.TryAbandonWorkflowAsync(principal, employeeId,
                    SqlAttemptId(progress), _pageCts.Token);
                if (!ReferenceEquals(registration, _telJournal) || !ReferenceEquals(marker, _telMarker))
                    return;
                if (!abandoned)
                {
                    await MarkTelManualAsync(progress);
                    return;
                }
            }
            await registration.RejectOperationAsync(marker.Revision);
            _telMarker = null;
            _telProgress = null;
            ShowPendingAts();
            await registration.DiscardScopeAsync(TelProgressScope);
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            _telJournalReady = false;
            if (progress.ServerWorkflow && ReferenceEquals(registration, _telJournal) && ReferenceEquals(marker, _telMarker))
                await MarkTelManualAsync(progress);
        }
        UpdateTelJournalNotice();
    }

    private async Task MarkTelManualAsync(TelWriteProgress progress)
    {
        _telProgress = progress with { Stage = TelWriteStage.Manual };
        try
        {
            await SaveTelProgressAsync(_telProgress);
            if (_telJournal is { } registration && _telMarker is { } marker)
            {
                _telMarker = marker with { Status = ClayDraftOperationStatus.Unknown };
                await registration.UnknownOperationAsync(marker.Revision);
            }
        }
        catch (Exception ex) when (IsTelJournalError(ex)) { }
        UpdateTelJournalNotice();
    }

    private async Task FinishTelJournalAsync()
    {
        if (_telProgress is not { } progress || _telJournal is not { } registration)
            return;
        if (!progress.ServerWorkflow)
            throw new InvalidOperationException("Серверный журнал телефонной операции не подтверждён.");
        var employeeId = _telJournalActor;
        var principal = await ReadPrincipalAsync();
        if (!ReferenceEquals(registration, _telJournal) || !ReferenceEquals(progress, _telProgress))
            return;
        await Service.CompleteWorkflowAsync(principal, employeeId, SqlAttemptId(progress), _pageCts.Token);
        if (!ReferenceEquals(registration, _telJournal) || !ReferenceEquals(progress, _telProgress))
            return;
        if (progress.Stage != TelWriteStage.Complete)
            await SaveTelProgressAsync(progress with { Stage = TelWriteStage.Complete });
        if (ReferenceEquals(registration, _telJournal) && _telMarker is { } marker)
        {
            if (!_telReceiptRecorded)
            {
                await registration.RecordConfirmedOperationAsync(marker.Revision);
                _telReceiptRecorded = true;
            }
            await registration.CommitOperationAsync(marker.Revision);
            _telMarker = null;
        }
        UpdateTelJournalNotice();
    }

    private async Task ClearTelProgressAsync()
    {
        if (_telJournal is { } registration)
            await registration.DiscardScopeAsync(TelProgressScope);
        _telProgress = null;
        _telReceiptRecorded = false;
        ShowPendingAts();
        UpdateTelJournalNotice();
    }

    private async Task RetryJournaledAtsAsync()
    {
        if (IsTelJournalInteractionBlocked || !CanRetryAts || CurrentUser is not { } actor || actor.Id != _telJournalActor)
            return;
        var version = _operationVersion;
        _telJournalBusy = true;
        try
        {
            if (await RunJournaledAtsAsync(actor, false, version))
                await FinishTelJournalAsync();
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException)
        {
            if (_telProgress is { Stage: TelWriteStage.PostAtsDispatching } progress)
                await MarkTelManualAsync(progress);
            else UpdateTelJournalNotice();
        }
        finally { _telJournalBusy = false; }
    }

    private async Task CompleteOrCheckTelJournalAsync()
    {
        if (IsTelJournalInteractionBlocked || _telJournal is not { } registration)
            return;
        var version = _operationVersion;
        _telJournalBusy = true;
        try
        {
            var actor = await Service.GetCurrentUserAsync(await ReadPrincipalAsync(), _pageCts.Token);
            if (!IsTelInputCurrent(registration, version) || actor?.Id != _telJournalActor ||
                !actor.HasRole(Clayzor.Lib.Entities.Роли.Специалист_по_инвентаризации_оборудования))
                return;

            if (_notSentAttemptId is { } notSent)
            {
                _telMarker = await registration.RetryStorageAsync(notSent, _pageCts.Token);
                _notSentAttemptId = null;
            }
            else if (!_telJournalReady && _telMarker is null && _telProgress is null)
            {
                _telMarker = await registration.RetryStorageAsync(null, _pageCts.Token);
            }
            if (_telMarker?.Status != ClayDraftOperationStatus.Unknown &&
                _telProgress is { SqlConfirmed: true, Pending.Count: 0, Stage: TelWriteStage.Complete or TelWriteStage.SqlConfirmed } complete)
            {
                await FinishTelJournalAsync();
                if (!IsTelInputCurrent(registration, version))
                    return;
                if (complete.Deleting)
                    _deleted = true;
                await PublishCompletedTelWriteAsync(complete.PhoneId, complete.Deleting, closeAfterSave: false);
            }
            else
                await ScanTelJournalAsync(registration);
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException)
        {
            UpdateTelJournalNotice();
        }
        finally { _telJournalBusy = false; }
    }

    private async Task ObserveTelWriteAsync()
    {
        if (IsTelJournalInteractionBlocked || CurrentUser is null)
            return;
        _telJournalBusy = true;
        var version = _operationVersion;
        try
        {
            if (HasOnlyServerTelWorkflow && _telJournal is { } registration)
            {
                if (await ReadServerTelWorkflowAsync(registration, version)) UpdateTelJournalNotice();
                return;
            }
            if (await ObserveTelSqlReceiptAsync(version))
                return;
            var phoneId = _telProgress?.PhoneId ?? Id;
            var current = phoneId > 0 ? await Service.GetAsync(phoneId, currentUser: CurrentUser) : null;
            if (IsCurrent(version))
                _telJournalNotice = current is null
                    ? "Текущая строка не найдена. Это не доказывает исход попытки."
                    : $"Последнее изменение (UTC): {current.Phone.ChangedAtUtc:dd.MM.yyyy HH:mm:ss}. Совпадение данных не доказывает исход попытки.";
        }
        catch (Exception ex) when (IsTelJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException)
        {
            if (IsCurrent(version))
                _telJournalNotice = "Не удалось прочитать состояние номера. Журнал не изменён.";
        }
        finally { _telJournalBusy = false; }
    }

    private async Task<bool> ObserveTelSqlReceiptAsync(int version)
    {
        if (_telProgress is not { SqlConfirmed: false, SqlPlanReady: true } progress ||
            progress.Stage is not (TelWriteStage.SqlDispatching or TelWriteStage.Manual) ||
            _telMarker is not { Kind: TelAttemptKind } marker || marker.AttemptId != progress.AttemptId ||
            !Guid.TryParse(progress.AttemptId, out var attemptId) || attemptId == Guid.Empty ||
            CurrentUser is not { } actor || actor.Id != _telJournalActor)
            return false;

        var receipt = await Service.ReadSqlReceiptAsync(await ReadPrincipalAsync(), actor.Id, progress.PhoneId,
            progress.Deleting, attemptId, _pageCts.Token);
        if (!IsTelWriteCurrent(version) || !ReferenceEquals(progress, _telProgress) || !ReferenceEquals(marker, _telMarker))
            return true;
        if (receipt is not { State: InventoryOperationState.Confirmed, ResultEntityId: > 0 } ||
            receipt.AttemptId != attemptId || progress.PhoneId > 0 && receipt.ResultEntityId != progress.PhoneId)
            return false;

        var phoneId = receipt.ResultEntityId.Value;
        // Доказанный успех SQL не подтверждает внешние команды АТС после потерянного ответа.
        await SaveTelProgressAsync(progress with
        {
            SqlConfirmed = true, PhoneId = phoneId, Stage = TelWriteStage.Manual, SqlRejected = false,
        });
        if (!IsTelWriteCurrent(version))
            return true;
        if (progress.ServerWorkflow && progress.Pending.Count == 0)
        {
            if (progress.Deleting) _deleted = true;
            await FinishTelJournalAsync();
            if (IsTelWriteCurrent(version))
                await PublishCompletedTelWriteAsync(phoneId, progress.Deleting, closeAfterSave: false);
        }
        else
        {
            UpdateTelJournalNotice();
        }
        return true;
    }

    private void ShowPendingAts()
    {
        _pendingOperations = _telProgress is { SqlConfirmed: true } progress ? progress.Pending : [];

    }

    private void UpdateTelJournalNotice()
    {
        _telJournalNotice = _notSentAttemptId is not null
            ? "Операция не отправлена. Обновите форму, чтобы проверить её состояние."
            : _telProgress is { Stage: TelWriteStage.Complete } complete
                ? complete.ServerWorkflow
                    ? "Результат подтверждён. Для получения актуальных данных обновите форму."
                    : "Серверный журнал телефонной операции не подтверждён."
                : CanRetryAts
                    ? "SQL подтверждён. Можно повторить только неотправленные команды АТС."
                    : _telProgress is { Stage: TelWriteStage.Manual } progress
                        ? progress.SqlRejected
                            ? "SQL подтверждённо не записан. Предварительная АТС выполнялась, автоматическая компенсация заблокирована."
                            : progress.SqlConfirmed
                            ? "SQL подтверждён. Исход АТС не подтверждён, повтор заблокирован."
                            : "Исход записи или предварительной АТС требует сверки. Повтор и автоматическая компенсация заблокированы."
                        : _telProgress?.SqlConfirmed == true
                            ? "SQL подтверждён. Состояние команды АТС требует сверки, повтор заблокирован."
                            : _telMarker is not null
                            ? "В журнале есть незавершённая попытка. Повтор заблокирован."
                            : _telServerWritePending ? "Сервер обнаружил незавершённую запись телефонного номера. Повтор заблокирован."
                            : !_telJournalReady ? "Журнал записи недоступен." : null;
    }

    private async Task ResetTelJournalAsync()
    {
        var registration = _telJournal;
        _telJournal = null;
        _telCloseRevision = null;
        ResetTelInput();
        _telJournalKey = null;
        _telMarker = null;
        _telProgress = null;
        _notSentAttemptId = null;
        _telJournalReady = false;
        _telServerWritePending = false;
        _telWorkflowSummary = null;
        _telReceiptRecorded = false;
        _telJournalNotice = null;
        _telSubmittedFingerprint = null;
        if (registration is not null)
            await registration.DisposeAsync();
    }

    private static string SqlFingerprint(PhoneSaveRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    private static bool IsTelJournalError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or JsonException or TimeoutException;

    private enum TelWriteStage { BeforeSql = 0, PreAtsDispatching = 1, SqlDispatching = 2, SqlConfirmed = 3, PostAtsDispatching = 4, Manual = 5, Complete = 6 }

    private sealed record TelWriteProgress(
        string AttemptId, bool Deleting, int PhoneId, TelWriteStage Stage, IReadOnlyList<PhoneSyncOperation> Pending,
        bool PreEffect = false, bool SqlConfirmed = false, PhoneSyncPhase? LastAtsPhase = null, bool SqlRejected = false, int Version = 1,
        string? SqlFingerprint = null, bool SqlPlanReady = false, bool ServerWorkflow = false,
        int NextPreStep = 0, int NextPostStep = 0);
}
