using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class ModelEquipmentForm
{
    private const string JournalForm = "inventory.model-equipment";
    private const string JournalReceiptScope = "receipt";
    private const string ModelWriteKind = "equipment-model.save";
    private static readonly ClayToolbarButtonOptions RetryJournalOptions = new() { Text = "Проверить журнал сохранения" };
    private static readonly ClayToolbarButtonOptions ReadKnownOptions = new() { Text = "Перечитать сохранённую модель" };
    private static readonly ClayToolbarButtonOptions ObserveOptions = new() { Text = "Прочитать состояние модели" };
    private static readonly ClayToolbarButtonOptions AdoptJournalOptions = new() { Text = "Открыть сохранённую попытку" };

    private readonly string _journalRoot = $"model-write-{Guid.NewGuid():N}";
    private ClayDraftRegistration? _journal;
    private ClayDraftOperation? _writeAttempt;
    private IReadOnlyList<ClayDraftRecoveryCandidate> _journalCandidates = [];
    private KnownModelWrite? _knownWrite;
    private string? _notSentAttempt;
    private string? _journalNotice;
    private bool _journalReady;
    private bool _journalBusy;
    private bool _confirmedRecorded;
    private bool _lateInput;
    private int _journalEmployeeId;

    [Inject] private ClayDraftService Drafts { get; set; } = null!;

    private bool WriteBlocked => !_journalReady || _journalBusy || InputBlocked || _writeAttempt is not null ||
        _knownWrite is not null || _notSentAttempt is not null || _journalCandidates.Count > 0;

    private async Task InitializeJournalAsync(int id, int initialType, int version)
    {
        if (CurrentUser is not { } actor || !_canEdit)
            return;
        var entityKey = id > 0 ? id.ToString(CultureInfo.InvariantCulture) : $"new:{initialType.ToString(CultureInfo.InvariantCulture)}";
        ClayDraftRegistration? registration = null;
        try
        {
            registration = await Drafts.RegisterHandleAsync(JournalForm, entityKey,
                actor.Id.ToString(CultureInfo.InvariantCulture), 1, $".{_journalRoot}", _pageCts.Token);
            if (_disposed || _loadVersion != version)
                return;
            _journal = registration;
            registration = null;
            _journalEmployeeId = actor.Id;
            RestoreJournal();
            await RestoreEditorInputAsync(_journal, version);
            await ScanJournalAsync(_journal);
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
        if ((_writeAttempt is null || _confirmedRecorded) &&
            restored.TypedScopes?.TryGetValue(JournalReceiptScope, out var json) == true &&
            JsonSerializer.Deserialize<KnownModelWrite>(json) is { Id: > 0 } receipt &&
            (_writeAttempt is null || receipt.AttemptId == _writeAttempt.AttemptId))
            _knownWrite = receipt;
        else if (_confirmedRecorded && _writeAttempt is { Kind: ModelWriteKind } confirmed)
        {
            using var intent = JsonDocument.Parse(confirmed.Data);
            if (intent.RootElement.TryGetProperty("Id", out var idProperty) && idProperty.TryGetInt32(out var id) && id > 0 &&
                intent.RootElement.TryGetProperty("Fingerprint", out var fingerprintProperty) &&
                fingerprintProperty.GetString() is { Length: > 0 } fingerprint)
                _knownWrite = new KnownModelWrite(confirmed.AttemptId, id, fingerprint);
        }
    }

    private async Task ScanJournalAsync(ClayDraftRegistration registration)
    {
        _journalReady = false;
        var candidates = await registration.ListRecoveryCandidatesAsync(_pageCts.Token)
            ?? throw new InvalidOperationException("Журнал не подтвердил поиск сохранений.");
        var health = await registration.GetHealthAsync(_pageCts.Token);
        if (!ReferenceEquals(_journal, registration))
            return;
        _journalCandidates = candidates;
        _journalReady = health is { Healthy: true, PendingWrites: 0 } &&
            (!health.HasOperation || _writeAttempt is not null);
        UpdateJournalNotice();
    }

    private async Task PrepareJournalAsync(ClayDraftRegistration registration, int id, DateTime? expectedChangedAt,
        string fingerprint, string input, int employeeId, CancellationToken ct)
    {
        var version = _loadVersion;
        var route = (Id, CodeType, LegacyCodeType);
        bool Current() => IsEditorContextCurrent(registration, version) && route == (Id, CodeType, LegacyCodeType) &&
            CurrentUser?.Id == employeeId;
        await ScanJournalAsync(registration);
        if (WriteBlocked || !Current() || employeeId != _journalEmployeeId)
            throw new InvalidOperationException("Журнал текущей модели не готов. Запись не отправлена.");
        await registration.EnsureCommandAllowedAsync(ct);
        await EnsureEditorRawReadyAsync(registration, ct);
        await registration.OpenScopeAsync(JournalReceiptScope, ct);
        if (!Current() || _model is not { } model || EditableFingerprint(model) != fingerprint)
            throw new InvalidOperationException("Ввод изменился до отправки. Проверьте данные и повторите сохранение.");
        var typedRevision = await SaveEditorScopeAsync(registration, input, ct);
        var attemptId = Guid.NewGuid().ToString("N");
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
            if (_knownWrite is not null)
                await FinishKnownJournalAsync(registration);
            await ScanJournalAsync(registration);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            _journalReady = false;
            UpdateJournalNotice();
        }
        finally { _journalBusy = false; }
    }

    private async Task AdoptJournalAsync(ClayDraftRecoveryCandidate candidate)
    {
        if (_journalBusy || _loading || _saving || _disposed || !candidate.CanRecover ||
            !_journalCandidates.Contains(candidate) || IsModified || _writeAttempt is not null ||
            _knownWrite is not null || _journal is not { } registration)
            return;
        var version = _loadVersion;
        _journalBusy = true;
        try
        {
            if (await registration.RecoverListedDraftAsync(candidate.DraftId, _pageCts.Token))
            {
                if (!IsEditorContextCurrent(registration, version))
                    return;
                RestoreJournal();
                if (IsModified)
                {
                    _adoptionConflict = true;
                    _inputConflict = true;
                    _inputNotice = "Ввод появился при открытии черновика. Оба снимка оставлены для сверки.";
                    await ScanJournalAsync(registration);
                    return;
                }
                await RestoreEditorInputAsync(registration, _loadVersion);
                await ScanJournalAsync(registration);
            }
            else
                _journalNotice = "Сохранённая попытка занята или недоступна. Запись заблокирована.";
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
        if (_model is { } model && IsModified && EditableFingerprint(model) != receipt.Fingerprint)
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
            var id = _knownWrite?.Id ?? Id;
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
        _journalCandidates = [];
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
                : "Сохранение подтверждено. Можно завершить журнал или перечитать сохранённую модель."
            : _notSentAttempt is not null
                ? "Запись не отправлена. Проверьте состояние журнала."
                : _writeAttempt?.Status == ClayDraftOperationStatus.Confirmed
                    ? "Сохранение подтверждено, но идентификатор результата не восстановлен. Повтор заблокирован."
                    : _writeAttempt is not null
                        ? "Результат сохранения модели неизвестен. Повтор заблокирован."
                        : _journalCandidates.Count > 0
                            ? "Найдена сохранённая попытка. Новая запись заблокирована."
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
        ex is JSException or JSDisconnectedException or InvalidOperationException or OperationCanceledException or JsonException;

    private sealed record KnownModelWrite(string AttemptId, int Id, string Fingerprint);
}
