using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class ModelEquipmentForm
{
    private const string JournalForm = "inventory.model-equipment";
    private const string JournalReceiptScope = "receipt";
    private const string ModelWriteKind = "equipment-model.save";

    private readonly string _journalRoot = $"model-write-{Guid.NewGuid():N}";
    private ClayDraftRegistration? _journal;
    private ClayDraftOperation? _writeAttempt;
    private Guid _draftInstance = Guid.NewGuid();
    private KnownModelWrite? _knownWrite;
    private KnownModelWrite? _restoredBrowserReceipt;
    private InventoryOperationReceipt? _serverLatest;
    private bool _serverPending;
    private string? _notSentAttempt;
    private string? _journalNotice;
    private bool _journalReady;
    private bool _journalBusy;
    private bool _confirmedRecorded;
    private bool _lateInput;
    private int _journalEmployeeId;
    private int _journalModelId;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool WriteBlocked => !_journalReady || _journalBusy || InputBlocked || _writeAttempt is not null ||
        _knownWrite is not null || _restoredBrowserReceipt is not null || _serverPending || _notSentAttempt is not null;

    private async Task InitializeJournalAsync(int id, int initialType, int version)
    {
        if (CurrentUser is not { } actor || !_canEdit)
            return;
        var entityKey = id > 0 ? id.ToString(CultureInfo.InvariantCulture) : $"new:{initialType.ToString(CultureInfo.InvariantCulture)}";
        ClayDraftRegistration? registration = null;
        try
        {
            var modelId = id > 0 ? id : 0;
            var actorKey = actor.Id.ToString(CultureInfo.InvariantCulture);
            var token = _pageCts.Token;
            (InventoryOperationReceipt? Latest, bool HasPending) server = default;
            bool Current() => !_disposed && !token.IsCancellationRequested &&
                _loadVersion == version && CurrentUser?.Id == actor.Id;
            registration = await Drafts.TransferLegacyAndRegisterRamAsync(JournalForm,
                modelId > 0 ? modelId.ToString(CultureInfo.InvariantCulture) : "new", actorKey, async (evidence, ct) =>
                {
                    var attempts = ValidateLegacyModelEvidence(evidence, modelId);
                    var principal = await ReadPrincipalAsync();
                    if (!Current()) throw new OperationCanceledException(ct);

                    if (attempts.Count != 0)
                        await EquipmentModel.ImportLegacySaveOperationsAsync(Db, principal, actor.Id, modelId, attempts, ct);
                    server = await EquipmentModel.ReadSaveJournalAsync(Db, principal, actor.Id, modelId, ct);
                    if (!Current()) throw new OperationCanceledException(ct);

                    return await Drafts.RegisterRamHandleAsync(JournalForm, entityKey,
                        actorKey, 1, $".{_journalRoot}", _draftInstance, ct);
                }, Current, token);
            if (!Current()) return;
            _journal = registration;
            _modelDraftClosed = false;
            var restoredRegistration = registration;
            registration = null;
            _journalEmployeeId = actor.Id;
            _journalModelId = modelId;
            _serverLatest = server.Latest;
            _serverPending = server.HasPending;
            if (_disposed || version != _loadVersion || !ReferenceEquals(_journal, restoredRegistration))
                return;

            RestoreJournal();
            await RestoreEditorInputAsync(restoredRegistration, version);
            await ScanJournalAsync(restoredRegistration);
            await ReconcileServerReceiptAsync(restoredRegistration, version);
            UpdateJournalNotice();
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (!_disposed && version == _loadVersion)
            {
                _journalReady = false;
                _journalNotice = "Журнал сохранения недоступен. Запись заблокирована.";
            }
        }
        finally
        {
            if (registration is not null)
                await registration.DisposeAsync();
        }
    }

    private void RestoreJournal()
    {
        var restored = _journal!.Restore;
        _writeAttempt = restored.OperationSnapshot;
        _confirmedRecorded = _writeAttempt?.Status == ClayDraftOperationStatus.Confirmed;
        _knownWrite = null;
        _restoredBrowserReceipt = restored.TypedScopes?.TryGetValue(JournalReceiptScope, out var json) == true
            ? JsonSerializer.Deserialize<KnownModelWrite>(json) : null;
    }

    private async Task ReconcileServerReceiptAsync(ClayDraftRegistration registration, int version)
    {
        if (!IsEditorContextCurrent(registration, version) || _knownWrite is not null || CurrentUser is not { } actor)
            return;
        var operation = _writeAttempt;
        var attemptText = operation?.AttemptId ?? _restoredBrowserReceipt?.AttemptId;
        if (!Guid.TryParse(attemptText, out var attemptId) || attemptId == Guid.Empty ||
            operation is not null && (operation.Kind != ModelWriteKind || operation.Scopes is not [{ Name: EditorScope }]))
            return;

        var token = _pageCts.Token;
        var modelId = _journalModelId;
        var receipt = await EquipmentModel.ReadSaveReceiptAsync(Db, await ReadPrincipalAsync(), actor.Id, modelId, attemptId, token);
        if (!IsEditorContextCurrent(registration, version) || _journalModelId != modelId || CurrentUser?.Id != actor.Id ||
            !ReferenceEquals(_writeAttempt, operation) || receipt is null || receipt.AttemptId != attemptId)
            return;

        if (receipt.State == InventoryOperationState.Rejected && operation is { Status: not ClayDraftOperationStatus.Confirmed })
        {
            await registration.RejectOperationAsync(operation.Revision, token);
            if (IsEditorContextCurrent(registration, version) && ReferenceEquals(_writeAttempt, operation))
            {
                _writeAttempt = null;
                _restoredBrowserReceipt = null;
            }
            return;
        }
        if (receipt is not { State: InventoryOperationState.Confirmed, ResultEntityId: > 0 } ||
            modelId > 0 && receipt.ResultEntityId != modelId)
            return;

        var fingerprint = _restoredBrowserReceipt is { } browserReceipt && browserReceipt.AttemptId == attemptText
            ? browserReceipt.Fingerprint : null;
        if (operation is not null)
        {
            using var intent = JsonDocument.Parse(operation.Data);
            if (!intent.RootElement.TryGetProperty("Id", out var id) || !id.TryGetInt32(out var submittedId) || submittedId != modelId ||
                !intent.RootElement.TryGetProperty("Fingerprint", out var value) || value.GetString() is not { Length: > 0 } submitted)
                return;
            fingerprint = submitted;
        }
        if (string.IsNullOrEmpty(fingerprint))
            return;

        _knownWrite = new KnownModelWrite(attemptText!, receipt.ResultEntityId.Value, fingerprint);
        _restoredBrowserReceipt = null;
    }

    private static IReadOnlyCollection<Guid> ValidateLegacyModelEvidence(
        IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, int modelId)
    {
        var expected = modelId.ToString(CultureInfo.InvariantCulture);
        var attempts = new HashSet<Guid>();
        foreach (var item in evidence)
        {
            if (item is null || item.Kind != ModelWriteKind || item.Data != expected ||
                !Guid.TryParseExact(item.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty)
                throw new InvalidOperationException("Прежняя попытка не соответствует открытой модели оборудования.");
            attempts.Add(attemptId);
        }
        return attempts;
    }

    private async Task ScanJournalAsync(ClayDraftRegistration registration, bool refreshServer = false)
    {
        _journalReady = false;
        var health = await registration.GetHealthAsync(_pageCts.Token);
        var version = _loadVersion;
        if (refreshServer)
        {
            if (CurrentUser is not { } actor || actor.Id != _journalEmployeeId)
                throw new UnauthorizedAccessException("Не удалось подтвердить сотрудника для чтения журнала.");
            var modelId = _journalModelId;
            var server = await EquipmentModel.ReadSaveJournalAsync(Db, await ReadPrincipalAsync(), actor.Id, modelId, _pageCts.Token);
            if (!IsEditorContextCurrent(registration, version) || _journalModelId != modelId || CurrentUser?.Id != actor.Id)
                return;
            _serverLatest = server.Latest;
            _serverPending = server.HasPending;
        }
        if (!ReferenceEquals(_journal, registration))
            return;
        _journalReady = health is { Healthy: true, PendingWrites: 0 } &&
            (!health.HasOperation || _writeAttempt is not null);
        UpdateJournalNotice();
    }

    private async Task PrepareJournalAsync(ClayDraftRegistration registration, int id, DateTime? expectedChangedAt,
        string fingerprint, string input, int employeeId, Guid serverAttemptId, CancellationToken ct)
    {
        var version = _loadVersion;
        var route = (Id, CodeType, LegacyCodeType);
        bool Current() => IsEditorContextCurrent(registration, version) && route == (Id, CodeType, LegacyCodeType) &&
            CurrentUser?.Id == employeeId;
        await ScanJournalAsync(registration);
        if (WriteBlocked || !Current() || employeeId != _journalEmployeeId || id != _journalModelId)
            throw new InvalidOperationException("Журнал текущей модели не готов. Запись не отправлена.");
        await registration.EnsureCommandAllowedAsync(ct);
        await EnsureEditorRawReadyAsync(registration, ct);
        await registration.OpenScopeAsync(JournalReceiptScope, ct);
        if (!Current() || _model is not { } model || EditableFingerprint(model) != fingerprint)
            throw new InvalidOperationException("Ввод изменился до отправки. Проверьте данные и повторите сохранение.");
        var typedRevision = await SaveEditorScopeAsync(registration, input, ct);
        var attemptId = serverAttemptId.ToString("N");
        _notSentAttempt = attemptId;
        var data = JsonSerializer.Serialize(new { Id = id, ExpectedChangedAt = expectedChangedAt, Fingerprint = fingerprint });
        var attempt = await registration.BeginOperationAsync(
            new ClayDraftOperationIntent(ModelWriteKind, data, [EditorScope], AttemptId: attemptId,
                BlockOtherDraftOperations: true), ct);
        if (attempt.AttemptId != attemptId || attempt.Kind != ModelWriteKind || attempt.Data != data ||
            attempt.Status != ClayDraftOperationStatus.Running)
            throw new InvalidOperationException("Журнал не подтвердил текущую попытку. Запись не отправлена.");
        _writeAttempt = attempt;
        _notSentAttempt = null;
        if (attempt.Scopes.SingleOrDefault(scope => scope.Name == EditorScope)?.TypedRevision != typedRevision ||
            EditableFingerprint(model) != fingerprint)
            throw new InvalidOperationException("Ввод изменился до отправки. Черновик сохранён, запись не отправлена.");
    }

    private async Task FinishKnownJournalAsync(ClayDraftRegistration registration)
    {
        if (_writeAttempt is not { } attempt || _knownWrite is not { } receipt)
            return;
        if (!_confirmedRecorded)
        {
            await registration.RecordConfirmedOperationAsync(attempt.Revision);
            _confirmedRecorded = true;
        }
        if (await registration.SaveTypedScopeAsync(JournalReceiptScope, JsonSerializer.Serialize(receipt)) <= 0)
            throw new InvalidOperationException("Журнал не подтвердил сохранение идентификатора результата.");
        await registration.CommitOperationAsync(attempt.Revision);
        _writeAttempt = null;
    }

    private async Task MarkWriteFailureAsync(ClayDraftRegistration registration, bool wasDispatched, bool noEffect)
    {
        if (_writeAttempt is { } attempt)
        {
            try
            {
                if (!wasDispatched || noEffect)
                {
                    await registration.RejectOperationAsync(attempt.Revision);
                    _writeAttempt = null;
                }
                else
                {
                    _writeAttempt = attempt with { Status = ClayDraftOperationStatus.Unknown };
                    await registration.UnknownOperationAsync(attempt.Revision);
                }
            }
            catch (Exception ex) when (IsJournalError(ex))
            {
                if (!wasDispatched || noEffect)
                    _notSentAttempt = attempt.AttemptId;
            }
        }
        UpdateJournalNotice();
    }

    private async Task RetryJournalAsync()
    {
        if (_journalBusy || _loading || _saving || _disposed)
            return;
        _journalBusy = true;
        try
        {
            if (_journal is not { } registration)
            {
                await InitializeJournalAsync(Id, InitialTypeId, _loadVersion);
                return;
            }
            if (_notSentAttempt is { } notSent)
            {
                _writeAttempt = await registration.RetryStorageAsync(notSent, _pageCts.Token);
                _notSentAttempt = null;
            }
            await ReconcileServerReceiptAsync(registration, _loadVersion);
            if (_knownWrite is not null)
                await FinishKnownJournalAsync(registration);
            await ScanJournalAsync(registration, refreshServer: true);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            _journalReady = false;
            UpdateJournalNotice();
        }
        finally { _journalBusy = false; }
    }

    private async Task ReadKnownWriteAsync()
    {
        if (_journalBusy || _loading || _saving || _disposed || _knownWrite is not { } receipt ||
            _journal is not { } registration || _writeAttempt is not null ||
            !_journalReady)
            return;
        if (_model is { } model && (receipt.SubmittedFields is not null || _scheduledInput is not null || IsModified) &&
            EditableFingerprint(model) != receipt.Fingerprint)
        {
            _lateInput = true;
            UpdateJournalNotice();
            return;
        }
        _journalBusy = true;
        try { await PublishSavedModelAsync(receipt.Id, registration); }
        catch (Exception ex) when (IsJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException)
        {
            _journalNotice = "Сохранение подтверждено. Не удалось перечитать модель или завершить журнал. Повтор записи заблокирован.";
        }
        finally { _journalBusy = false; }
    }

    private async Task ObserveWriteAsync()
    {
        if (_journalBusy || _loading || _saving || _disposed || CurrentUser is null)
            return;
        _journalBusy = true;
        var version = _loadVersion;
        try
        {
            if (_journal is { } registration)
            {
                await ReconcileServerReceiptAsync(registration, version);
                if (!IsEditorContextCurrent(registration, version))
                    return;
                await ScanJournalAsync(registration, refreshServer: true);
                if (_knownWrite is not null)
                {
                    await FinishKnownJournalAsync(registration);
                    UpdateJournalNotice();
                    return;
                }
                if (_writeAttempt is null)
                    return;
            }
            var id = Id;
            var row = id > 0 ? await EquipmentModel.GetForEditAsync(Db, id, _pageCts.Token) : null;
            if (_disposed || version != _loadVersion)
                return;
            _journalNotice = row is null
                ? "Сохранённая модель не определена. Состояние попытки не меняется."
                : $"Последнее изменение (UTC): {row.ChangedAt:dd.MM.yyyy HH:mm:ss}. " +
                  (_knownWrite is null
                      ? "Состояние строки не доказывает исход попытки. Повтор заблокирован."
                      : "Сохранение подтверждено. Введённые после записи значения оставлены в форме.");
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException)
        {
            if (!_disposed && version == _loadVersion)
                _journalNotice = "Не удалось прочитать состояние модели. Повтор заблокирован.";
        }
        finally { _journalBusy = false; }
    }

    private async Task ClearConfirmedReceiptAsync(ClayDraftRegistration registration)
    {
        await registration.DiscardScopeAsync(JournalReceiptScope);
        _knownWrite = null;
        _confirmedRecorded = false;
        _lateInput = false;
        UpdateJournalNotice();
    }

    private async Task ResetJournalAsync()
    {
        var registration = _journal;
        _journal = null;
        ResetEditorInput();
        _journalReady = false;
        _journalNotice = null;
        _serverLatest = null;
        _serverPending = false;
        _journalModelId = 0;
        _restoredBrowserReceipt = null;
        _writeAttempt = null;
        _knownWrite = null;
        _notSentAttempt = null;
        _confirmedRecorded = false;
        _lateInput = false;
        if (registration is not null)
            await registration.DisposeAsync();
    }

    private void UpdateJournalNotice()
    {
        _journalNotice = _knownWrite is not null
            ? _lateInput
                ? "Сохранение подтверждено. Поздний ввод оставлен в форме для сверки."
                : "Сохранение подтверждено. Нажмите «Обновить» для чтения сохранённой модели."
            : _notSentAttempt is not null
                ? "Запись не отправлена. Нажмите «Обновить» для проверки состояния записи."
                : _writeAttempt is not null || _restoredBrowserReceipt is not null || _serverPending
                        ? "Результат сохранения модели неизвестен. Повтор заблокирован."
                        : !_journalReady ? "Журнал сохранения недоступен. Запись заблокирована." : null;
    }

    private static string EditableFingerprint(EquipmentModel model) =>
        Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            model.EquipmentTypeId, model.ResourceId, model.Name, model.PartNumber, model.Cpu,
            model.MonitorSize, model.MemoryTypeId, model.MemoryBanks, model.IsMultimedia,
            model.NetworkInterfaces, model.NetworkName, model.Driver, model.LifeTime, model.Description,
        })));

    private static bool IsJournalError(Exception ex) =>
        ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or JsonException or
            Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException or TimeoutException;

    private sealed record KnownModelWrite(string AttemptId, int Id, string Fingerprint)
    {
        [System.Text.Json.Serialization.JsonIgnore]
        internal ModelEditorFields? SubmittedFields { get; init; }
    }
}
