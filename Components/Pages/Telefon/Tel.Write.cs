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
    private TelWriteProgress? _telProgress;
    private (string Key, int Actor, Guid Instance)? _telRamOwner;
    private string? _telJournalKey;
    private string? _telJournalNotice;
    private int _telJournalActor;
    private string? _telSubmittedFingerprint;
    private bool _telJournalReady;
    private bool _telJournalBusy;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool TelWriteBlocked => _telChildOpen || TelWriteBlockedOutsideChild;

    private bool TelWriteBlockedOutsideChild => !_telJournalReady || _telJournalBusy || _telEditorTransition || TelInputBlocked ||
        _telProgress is not null || _telCloseRevision is not null;

    private bool CanRetryAts => _telProgress is { LocalProgress: true, Stage: TelWriteStage.SqlConfirmed, SqlConfirmed: true,
            PhoneId: > 0, SqlFingerprint.Length: > 0, Pending.Count: > 0 };
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
            if (!IsCurrent(version) || CurrentUser?.Id != actor.Id) return;
            var token = _pageCts.Token;
            var userKey = actor.Id.ToString(CultureInfo.InvariantCulture);
            bool Current() => IsCurrent(version) && CurrentUser?.Id == actor.Id && !token.IsCancellationRequested;
            if (_telRamOwner is not { } owner || owner.Key != key || owner.Actor != actor.Id)
            {
                owner = (key, actor.Id, Guid.NewGuid());
                _telRamOwner = owner;
            }
            opened = await Drafts.RegisterRamHandleAsync(TelJournalForm, key, userKey,
                1, $".{_telJournalRoot}", owner.Instance, token);
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

    private void RestoreTelJournal()
    {
        var restored = _telJournal!.Restore;
        _telProgress = null;
        if (restored.TypedScopes?.TryGetValue(TelProgressScope, out var json) == true &&
            JsonSerializer.Deserialize<TelWriteProgress>(json) is { Version: 1, Pending: not null } progress)
            _telProgress = progress;
        ShowPendingAts();
    }

    private async Task ScanTelJournalAsync(ClayDraftRegistration registration)
    {
        var version = _operationVersion;
        _telJournalReady = false;
        var health = await registration.GetHealthAsync(_pageCts.Token);
        _telJournalReady = health is { Healthy: true, PendingWrites: 0 };
        UpdateTelJournalNotice();
    }


    private Task RefreshTelAsync() => RefreshAsync();

    private bool IsTelWriteCurrent(int version) => IsCurrent(version) && _telJournalKey == JournalKey(Id, CopyId);

    private string CurrentTelInputFingerprint() =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { Phone = _model, Relations = _relations, _parallelQuickNumber, _hasReturnDate, _servicesUserChoice, _directoryUserChoice })));

    private bool HasLaterTelInput => _telSubmittedFingerprint is { } fingerprint
        ? fingerprint != CurrentTelInputFingerprint()
        : HasUnsavedDraft;

    private async Task BeginTelWriteAsync(Employee actor, int phoneId, bool deleting, PhoneSaveRequest request,
        IReadOnlyList<PhoneSyncOperation> pre, int version)
    {
        var registration = _telJournal ?? throw new InvalidOperationException("Хранилище черновика недоступно.");
        if (TelWriteBlocked || actor.Id != _telJournalActor || !IsTelWriteCurrent(version))
            throw new InvalidOperationException("Состояние текущего номера не подтверждено.");
        if (!deleting)
            await EnsureTelInputRestoredAsync(registration, version);
        await registration.OpenScopeAsync(TelProgressScope, _pageCts.Token);
        if (!deleting)
        {
            if (HasLaterTelInput)
                throw new InvalidOperationException("Ввод изменился до отправки. Проверьте данные перед сохранением.");
            await PersistTelInputScopeAsync(registration, SerializeTelInput());
        }
        if (HasLaterTelInput)
            throw new InvalidOperationException("Ввод изменился до отправки. Черновик сохранён, запись не отправлена.");
        var attemptId = Guid.NewGuid().ToString("N");
        await SaveTelProgressAsync(new TelWriteProgress(attemptId, deleting, phoneId, TelWriteStage.BeforeSql, pre,
            LocalProgress: true));
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
            if (!IsTelWriteCurrent(version) || _telProgress is not { LocalProgress: true } progress)
                throw new OperationCanceledException();
            var post = postSqlPlan();
            await SaveTelProgressAsync(progress with
            {
                Stage = TelWriteStage.SqlDispatching, SqlFingerprint = SqlFingerprint(request), SqlPlanReady = true,
                Pending = post,
            });
        },
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
            if (!IsTelWriteCurrent(version) || !progress.LocalProgress)
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
        if (_telProgress is not { } progress || _telJournal is not { } registration)
            return;
        if (dispatched || progress.PreEffect)
        {
            await MarkTelManualAsync(progress with { SqlConfirmed = false, SqlRejected = !dispatched });
            return;
        }
        _telProgress = null;
        ShowPendingAts();
        await registration.DiscardScopeAsync(TelProgressScope);
        UpdateTelJournalNotice();
    }

    private async Task MarkTelManualAsync(TelWriteProgress progress)
    {
        _telProgress = progress with { Stage = TelWriteStage.Manual };
        await SaveTelProgressAsync(_telProgress);
        UpdateTelJournalNotice();
    }

    private async Task FinishTelJournalAsync()
    {
        if (_telProgress is not { } progress || _telJournal is null)
            return;
        if (!progress.LocalProgress)
            throw new InvalidOperationException("Временное состояние операции не подтверждено.");
        if (progress.Stage != TelWriteStage.Complete)
            await SaveTelProgressAsync(progress with { Stage = TelWriteStage.Complete });
        UpdateTelJournalNotice();
    }

    private async Task ClearTelProgressAsync()
    {
        if (_telJournal is { } registration)
            await registration.DiscardScopeAsync(TelProgressScope);
        _telProgress = null;
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

            if (_telProgress is { SqlConfirmed: true, Pending.Count: 0, Stage: TelWriteStage.Complete or TelWriteStage.SqlConfirmed } complete)
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
                _telJournalNotice = "Не удалось прочитать состояние номера. Состояние попытки сохранено.";
        }
        finally { _telJournalBusy = false; }
    }

    private void ShowPendingAts()
    {
        _pendingOperations = _telProgress is { SqlConfirmed: true } progress ? progress.Pending : [];

    }

    private void UpdateTelJournalNotice()
    {
        _telJournalNotice = _telProgress is { Stage: TelWriteStage.Complete } complete
            ? complete.LocalProgress
                ? "Результат подтверждён. Для получения актуальных данных обновите форму."
                : "Временное состояние телефонной операции не подтверждено."
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
                        : _telProgress is not null
                        ? "Есть незавершённая операция АТС. Повтор заблокирован."
                        : !_telJournalReady ? "Хранилище черновика недоступно." : null;
    }

    private async Task ResetTelJournalAsync()
    {
        var registration = _telJournal;
        _telJournal = null;
        _telCloseRevision = null;
        ResetTelInput();
        _telJournalKey = null;
        _telProgress = null;
        _telJournalReady = false;
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
        string? SqlFingerprint = null, bool SqlPlanReady = false, bool LocalProgress = false,
        int NextPreStep = 0, int NextPostStep = 0);
}
