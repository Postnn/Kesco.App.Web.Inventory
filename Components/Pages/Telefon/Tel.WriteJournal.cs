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
    private static readonly ClayToolbarButtonOptions FinishJournalOptions = new() { Text = "Завершить журнал" };
    private static readonly ClayToolbarButtonOptions InspectTelOptions = new() { Text = "Прочитать состояние номера" };
    private static readonly ClayToolbarButtonOptions RecoverTelOptions = new() { Text = "Открыть сохранённую попытку" };

    private readonly string _telJournalRoot = $"tel-write-{Guid.NewGuid():N}";
    private ClayDraftRegistration? _telJournal;
    private ClayDraftOperation? _telMarker;
    private TelWriteProgress? _telProgress;
    private IReadOnlyList<ClayDraftRecoveryCandidate> _telCandidates = [];
    private string? _telJournalKey;
    private string? _notSentAttemptId;
    private string? _telJournalNotice;
    private int _telJournalActor;
    private string? _telSubmittedFingerprint;
    private bool _telJournalReady;
    private bool _telJournalBusy;
    private bool _telReceiptRecorded;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool TelWriteBlocked => !_telJournalReady || _telJournalBusy || _telEditorTransition || _telChildOpen || TelInputBlocked || _telMarker is not null ||
        _telProgress is not null || _notSentAttemptId is not null || _telCandidates.Count > 0;

    private bool CanRetryAts => _telMarker?.Status == ClayDraftOperationStatus.Running &&
        _telProgress is { Stage: TelWriteStage.SqlConfirmed, SqlConfirmed: true, PhoneId: > 0, SqlFingerprint.Length: > 0, Pending.Count: > 0 };
    private bool IsTelJournalInteractionBlocked => _loading || _saving || _telJournalBusy || _telEditorTransition || _disposed;

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
            opened = await Drafts.RegisterHandleAsync(TelJournalForm, key,
                actor.Id.ToString(CultureInfo.InvariantCulture), 1, $".{_telJournalRoot}", _pageCts.Token);
            if (!IsCurrent(version))
                return;
            _telJournal = opened;
            opened = null;
            _telJournalActor = actor.Id;
            _telJournalKey = key;
            RestoreTelJournal();
            await ScanTelJournalAsync(_telJournal);
        }
        catch (Exception ex) when (IsTelJournalError(ex))
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
        _telJournalReady = false;
        var candidates = await registration.ListRecoveryCandidatesAsync(_pageCts.Token)
            ?? throw new InvalidOperationException("Журнал телефонного номера не подтвердил поиск попыток.");
        var health = await registration.GetHealthAsync(_pageCts.Token);
        if (!ReferenceEquals(registration, _telJournal))
            return;
        _telCandidates = candidates;
        _telJournalReady = health is { Healthy: true, PendingWrites: 0 } &&
            (!health.HasOperation || _telMarker is not null);
        UpdateTelJournalNotice();
    }

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
        var attemptId = Guid.NewGuid().ToString("N");
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
        await SaveTelProgressAsync(new TelWriteProgress(attemptId, deleting, phoneId, TelWriteStage.BeforeSql, pre));
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

    private TelSqlBoundary SqlBoundary(PhoneSaveRequest request, int version, Action onDispatch) => new(
        () => SaveTelProgressAsync(_telProgress! with { Stage = TelWriteStage.SqlDispatching, SqlFingerprint = SqlFingerprint(request) }),
        () =>
        {
            if (!IsTelWriteCurrent(version))
                throw new OperationCanceledException();
            onDispatch();
        });

    private async Task<bool> RunJournaledAtsAsync(Employee actor, bool beforeSql, int version)
    {
        while (_telProgress is { Pending.Count: > 0 } progress)
        {
            if (!IsTelWriteCurrent(version))
                return false;
            var operation = progress.Pending[0];
            var result = (await Service.SynchronizeTelPhasedAsync(progress.AttemptId, await ReadPrincipalAsync(), actor.Id,
                progress.PhoneId, [operation], _pageCts.Token, async _ =>
                {
                    if (!IsTelWriteCurrent(version))
                        throw new OperationCanceledException();
                    await SaveTelProgressAsync(progress with
                    {
                        Stage = beforeSql ? TelWriteStage.PreAtsDispatching : TelWriteStage.PostAtsDispatching,
                    });
                    if (!IsTelWriteCurrent(version))
                        throw new OperationCanceledException();
                })).Single();
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
            await registration.RejectOperationAsync(marker.Revision);
            _telMarker = null;
            _telProgress = null;
            ShowPendingAts();
            await registration.DiscardScopeAsync(TelProgressScope);
        }
        catch (Exception ex) when (IsTelJournalError(ex))
        {
            _telJournalReady = false;
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
        if (_telProgress is not { } progress)
            return;
        if (progress.Stage != TelWriteStage.Complete)
            await SaveTelProgressAsync(progress with { Stage = TelWriteStage.Complete });
        if (_telJournal is { } registration && _telMarker is { } marker)
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
        _telJournalBusy = true;
        try
        {
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

    private async Task RecoverTelJournalAsync(ClayDraftRecoveryCandidate candidate)
    {
        if (IsTelJournalInteractionBlocked || !candidate.CanRecover || !_telCandidates.Contains(candidate) ||
            _telJournal is not { } registration || _telMarker is not null || _telProgress is not null || HasUnsavedDraft)
            return;
        _telJournalBusy = true;
        _telAdoptingFingerprint = CurrentTelInputFingerprint();
        try
        {
            if (await registration.RecoverListedDraftAsync(candidate.DraftId, _pageCts.Token))
            {
                RestoreTelJournal();
                await LoadCurrentAsync(Id, CopyId, preservePending: true);
            }
            await ScanTelJournalAsync(registration);
        }
        catch (Exception ex) when (IsTelJournalError(ex))
        {
            _telJournalReady = false;
            UpdateTelJournalNotice();
        }
        finally { _telJournalBusy = false; _telAdoptingFingerprint = null; }
    }

    private async Task ObserveTelWriteAsync()
    {
        if (IsTelJournalInteractionBlocked || CurrentUser is null)
            return;
        _telJournalBusy = true;
        var version = _operationVersion;
        try
        {
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

    private void ShowPendingAts()
    {
        _pendingOperations = _telProgress is { SqlConfirmed: true } progress ? progress.Pending : [];

    }

    private void UpdateTelJournalNotice()
    {
        _telJournalNotice = _notSentAttemptId is not null
            ? "Операция не отправлена. Проверьте журнал."
            : _telProgress?.Stage == TelWriteStage.Complete
                ? "Результат подтверждён. Можно только завершить журнал и перечитать данные."
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
                            : _telCandidates.Count > 0
                                ? "Найдена сохранённая попытка. Новая запись заблокирована."
                                : !_telJournalReady ? "Журнал записи недоступен." : null;
    }

    private async Task ResetTelJournalAsync()
    {
        var registration = _telJournal;
        _telJournal = null;
        ResetTelInput();
        _telJournalKey = null;
        _telMarker = null;
        _telProgress = null;
        _telCandidates = [];
        _notSentAttemptId = null;
        _telJournalReady = false;
        _telReceiptRecorded = false;
        _telJournalNotice = null;
        _telSubmittedFingerprint = null;
        if (registration is not null)
            await registration.DisposeAsync();
    }

    private static string SqlFingerprint(PhoneSaveRequest request) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(request)));

    private static bool IsTelJournalError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or JsonException;

    private enum TelWriteStage { BeforeSql = 0, PreAtsDispatching = 1, SqlDispatching = 2, SqlConfirmed = 3, PostAtsDispatching = 4, Manual = 5, Complete = 6 }

    private sealed record TelWriteProgress(
        string AttemptId, bool Deleting, int PhoneId, TelWriteStage Stage, IReadOnlyList<PhoneSyncOperation> Pending,
        bool PreEffect = false, bool SqlConfirmed = false, PhoneSyncPhase? LastAtsPhase = null, bool SqlRejected = false, int Version = 1, string? SqlFingerprint = null);
}
