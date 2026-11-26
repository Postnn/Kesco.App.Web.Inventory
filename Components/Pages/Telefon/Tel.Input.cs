using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;
using Kesco.App.Web.Inventory.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Telefon;

public partial class Tel
{
    private const string TelInputScope = "tel-input";
    private static readonly ClayToolbarButtonOptions RestoreTelInputOptions = new() { Text = "Повторить восстановление ввода" };
    private static readonly ClayToolbarButtonOptions RetryTelStorageOptions = new() { Text = "Повторить сохранение черновика" };
    private ClayDraftRestore? _appliedTelRestore;
    private string? _scheduledTelInput;
    private string? _telInputNotice;
    private bool _telInputConflict;
    private bool _telOriginalMissing;
    private bool _telRawRequested;
    private object? _telRawInFlight;
    private bool _telRestoringRaw => _telRawInFlight is not null;
    private bool _telRawPending;
    private bool _telInputSaveFailed;
    private string? _telAdoptingFingerprint;

    private bool TelInputBlocked => _telInputConflict || _telRawRequested || _telRestoringRaw || _telRawPending || _telInputSaveFailed;

    private async Task<TelEditorDraft?> ReadTelInputAsync(int id, int copyId, int version)
    {
        if (_telJournal is not { } registration || _telJournalKey != JournalKey(id, copyId) ||
            ReferenceEquals(_appliedTelRestore, registration.Restore))
            return null;
        if (registration.Restore.CancelledScopes?.Contains(TelInputScope) == true)
        {
            await registration.OpenScopeAsync(TelInputScope, _pageCts.Token);
            if (IsTelInputCurrent(registration, version))
                _appliedTelRestore = registration.Restore;
            return null;
        }
        if (!registration.Restore.HasDraft) return null;
        var scope = await registration.ReadEditorScopeAsync(TelInputScope, _pageCts.Token);
        if (!IsTelInputCurrent(registration, version))
            return null;
        _telRawRequested = scope.HasRaw;
        if (registration.Restore.TypedScopes?.TryGetValue(TelInputScope, out var json) != true)
        {
            if (scope.HasRaw)
            {
                _telOriginalMissing = _telInputConflict = true;
                _telInputNotice = "Сырой ввод сохранён без исходного снимка. Запись требует сверки.";
            }
            return null;
        }

        var draft = JsonSerializer.Deserialize<TelEditorDraft>(json!);
        if (draft is not { Version: 1 } || draft.Id != id || draft.CopyId != copyId ||
            draft.Original.Id != (id > 0 ? id : 0) || draft.Current.Id != (id > 0 ? id : 0))
            throw new InvalidOperationException("Черновик относится к другой исходной записи или маршруту.");
        _scheduledTelInput = json;
        return draft;
    }

    private void CompareTelDraftOrigin(TelEditorDraft draft, Phone fresh, PhoneRelationsSnapshot freshRelations)
    {
        // Полный номер вычисляется на форме и на сервере. Версию не вычисляем и не обновляем из fresh.
        var comparable = ClonePhone(fresh);
        comparable.FullNumber = draft.Original.FullNumber;
        _telInputConflict = draft.Id > 0 &&
            (draft.Original.ChangedAtUtc != fresh.ChangedAtUtc ||
             !PhoneSaveBuilder.MainEquals(draft.Original, comparable) ||
             draft.OriginalRelations.Canonical() != TelDraftRelations.From(freshRelations).Canonical());
        if (_telInputConflict)
            _telInputNotice = "Исходная версия номера или состав связей изменились. Ввод восстановлен, запись требует сверки.";
    }

    private async Task ApplyTelRawAsync()
    {
        if (_telRestoringRaw || IsTelJournalInteractionBlocked || !_canEdit || _telJournal is not { } registration)
            return;
        var version = _operationVersion;
        var route = (Id, CopyId);
        var actorId = _telJournalActor;
        var token = _pageCts.Token;
        var retry = _telRawPending;
        var input = CurrentTelInputFingerprint();
        var attempt = new object();
        bool Current() => IsTelInputCurrent(registration, version) && route == (Id, CopyId) &&
            actorId == CurrentUser?.Id && ReferenceEquals(_telRawInFlight, attempt);

        _telRawRequested = false;
        _telRawInFlight = attempt;
        StateHasChanged();
        try
        {
            var actor = await Service.GetCurrentUserAsync(await ReadPrincipalAsync(), token);
            if (!Current())
                return;
            if (actor is null || actor.Id != actorId || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            {
                KeepTelRawPending("Не удалось подтвердить сотрудника для восстановления. Ввод сохранён.");
                return;
            }
            if (input != CurrentTelInputFingerprint())
            {
                KeepTelRawPending("Ввод изменился перед восстановлением. Повторите восстановление оставшихся полей.");
                return;
            }

            var result = retry
                ? await registration.ApplyPendingRawAsync(TelInputScope, token)
                : await registration.ApplyRawWithResultAsync(TelInputScope, token);
            if (!Current())
                return;
            var readiness = await registration.GetRestoreReadinessAsync(token);
            if (!Current())
                return;

            _telRawPending = result is null || result.Pending.Count != 0 || !TelScopeRestored(readiness);
            _telInputNotice = _telRawPending ? "Часть ввода ещё не восстановлена. Запись заблокирована."
                : _telInputSaveFailed ? "Не удалось сохранить черновик номера. Повторите сохранение."
                : _telInputConflict ? "Ввод восстановлен. Запись требует сверки исходной версии." : null;
        }
        catch (Exception error) when (IsTelJournalError(error) || error is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (Current())
                KeepTelRawPending("Не удалось восстановить сырой ввод. Запись заблокирована.");
        }
        finally
        {
            if (ReferenceEquals(_telRawInFlight, attempt))
            {
                _telRawInFlight = null;
                if (!_disposed) StateHasChanged();
            }
        }
    }

    private static bool TelScopeRestored(ClayDraftRestoreReadiness readiness) =>
        !readiness.PendingScopes.Contains(TelInputScope) && !readiness.FailedScopes.Contains(TelInputScope);

    private void KeepTelRawPending(string notice)
    {
        _telRawPending = true;
        _telInputNotice = notice;
    }

    private async Task EnsureTelInputRestoredAsync(ClayDraftRegistration registration, int version)
    {
        bool restored;
        try
        {
            restored = TelScopeRestored(await registration.GetRestoreReadinessAsync(_pageCts.Token));
        }
        catch (Exception error) when (IsTelJournalError(error))
        {
            restored = false;
        }
        if (!IsTelInputCurrent(registration, version))
            throw new InvalidOperationException("Контекст номера изменился.");
        if (restored)
            return;

        KeepTelRawPending("Сначала восстановите ввод номера. Запись не отправлена.");
        throw new InvalidOperationException(_telInputNotice);
    }

    private bool RejectUnacceptedParallel()
    {
        if (string.IsNullOrEmpty(_parallelQuickNumber))
            return false;
        _validationMessage = "Добавьте введённый параллельный номер или очистите поле перед сохранением.";
        return true;
    }

    private async Task PersistTelInputAsync()
    {
        if (_disposed || _loading || !_loaded || !_canEdit || _telEditorTransition || _telJournalBusy || _telChildOpen || _telRestoringRaw ||
            _telRawRequested || _telRawPending || _telInputSaveFailed || _telOriginalMissing ||
            _telJournal is not { } registration || _telJournalKey != JournalKey(Id, CopyId) || _model is null ||
            (!HasUnsavedDraft && string.IsNullOrEmpty(_parallelQuickNumber) && _scheduledTelInput is null))
            return;
        var version = _operationVersion;
        var json = SerializeTelInput();
        if (_scheduledTelInput == json)
            return;
        _scheduledTelInput = json;
        try { await PersistTelInputScopeAsync(registration, json); }
        catch (Exception ex) when (IsTelJournalError(ex))
        {
            if (IsTelInputCurrent(registration, version) && _scheduledTelInput == json)
            {
                _telInputSaveFailed = true;
                _telInputNotice = "Не удалось сохранить черновик номера. Запись заблокирована.";
                StateHasChanged();
            }
        }
    }

    private Task RetryTelRawAsync() => _telRawPending ? ApplyTelRawAsync() : Task.CompletedTask;

    private async Task RetryTelInputAsync()
    {
        if (IsTelJournalInteractionBlocked || _telRestoringRaw || _telRawPending ||
            !_telInputSaveFailed || _telJournal is not { } registration)
            return;
        try
        {
            var status = await registration.RetryStorageAsync(_notSentAttemptId, _pageCts.Token);
            if (_disposed || !ReferenceEquals(registration, _telJournal))
                return;
            _telMarker = status;
            _notSentAttemptId = null;
            _telInputSaveFailed = false;
            _scheduledTelInput = null;
            if (!_telInputConflict)
                _telInputNotice = null;
            await PersistTelInputAsync();
            await ScanTelJournalAsync(registration);
        }
        catch (Exception ex) when (IsTelJournalError(ex))
        {
            _telInputSaveFailed = true;
            _telInputNotice = "Черновик номера пока недоступен. Запись заблокирована.";
        }
    }

    private async Task<long> PersistTelInputScopeAsync(ClayDraftRegistration registration, string json)
    {
        var revision = await registration.SaveTypedScopeAsync(TelInputScope, json, _pageCts.Token);
        return revision > 0 ? revision : throw new InvalidOperationException("Черновик номера не подтверждён.");
    }

    private string SerializeTelInput(PhoneRelationsSnapshot? relations = null) => JsonSerializer.Serialize(new TelEditorDraft(
        1, Id, CopyId, ClonePhone(_originalPhone), TelDraftRelations.From(_originalRelations),
        ClonePhone(_model!), TelDraftRelations.From(relations ?? _relations),
        _hasReturnDate, _servicesUserChoice, _directoryUserChoice, _parallelQuickNumber));

    private bool IsTelInputCurrent(ClayDraftRegistration registration, int version) =>
        IsTelWriteCurrent(version) && ReferenceEquals(_telJournal, registration);

    private async Task<bool> HasRemainingTelRawAsync()
    {
        var registration = _telJournal;
        return registration is not null && (await registration.ReadEditorScopeAsync(TelInputScope, _pageCts.Token)).HasRaw;
    }

    private void ResetTelInput()
    {
        _appliedTelRestore = null;
        _scheduledTelInput = null;
        _telInputNotice = null;
        _telInputConflict = false;
        _telOriginalMissing = false;
        _telRawRequested = false;
        _telRawInFlight = null;
        _telRawPending = false;
        _telInputSaveFailed = false;
        _telAdoptingFingerprint = null;
    }

    private sealed record TelEditorDraft(int Version, int Id, int CopyId, Phone Original, TelDraftRelations OriginalRelations,
        Phone Current, TelDraftRelations CurrentRelations, bool HasReturnDate, int? ServicesChoice, int? DirectoryChoice, string QuickParallel);

    private sealed record TelDraftRelations(int[] Group, TelParallelDraft[] Parallel, TelEquipmentDraft[] Equipment)
    {
        internal static TelDraftRelations From(PhoneRelationsSnapshot relations) =>
            new(relations.GroupMembers.Select(x => x.PhoneId).ToArray(),
                relations.ParallelCalls.Select(x => new TelParallelDraft(x.CallNumber, x.DelayBeforeCall,
                    x.DelayTooQuickResponse, x.NotUsed)).ToArray(),
                relations.Equipment.Select(x => new TelEquipmentDraft(x.EquipmentId, x.ConnectionEquipmentId,
                    x.LocationId, x.PhoneTypeId, x.ReturnDate, x.Notes)).ToArray());

        internal string Canonical() => JsonSerializer.Serialize(new
        {
            Group = Group.Order(),
            Parallel = Parallel.OrderBy(x => x.CallNumber, StringComparer.Ordinal),
            Equipment = Equipment.OrderBy(x => x.EquipmentId).ThenBy(x => x.ConnectionEquipmentId)
                .Select(x => new { x.EquipmentId, x.ConnectionEquipmentId, x.ReturnDate, x.Notes }),
        });

        internal PhoneRelationsSnapshot ToSnapshot(int phoneId, PhoneRelationsSnapshot? visible = null) => new()
        {
            GroupMembers = Group.Select(id => visible?.GroupMembers.FirstOrDefault(item => item.PhoneId == id)?.Clone() ??
                new PhoneGroupMember { PhoneId = id, GroupPhoneId = phoneId, Name = $"#{id}" }).ToList(),
            ParallelCalls = Parallel.Select(x => new PhoneParallelCall
            {
                PhoneId = phoneId, CallNumber = x.CallNumber, DelayBeforeCall = x.Before, DelayTooQuickResponse = x.Quick, NotUsed = x.Disabled,
            }).ToList(),
            Equipment = Equipment.Select(x =>
            {
                var key = new PhoneEquipmentKey(x.EquipmentId, x.ConnectionEquipmentId);
                var item = visible?.Equipment.FirstOrDefault(value => value.Key == key)?.Clone() ??
                    new PhoneEquipmentLink
                    {
                        EquipmentId = x.EquipmentId, ConnectionEquipmentId = x.ConnectionEquipmentId,
                        LocationId = x.LocationId, PhoneTypeId = x.PhoneTypeId, Model = $"#{x.EquipmentId}",
                    };
                item.ReturnDate = x.ReturnDate;
                item.Notes = x.Notes;
                return item;
            }).ToList(),
        };
    }

    private sealed record TelParallelDraft(string CallNumber, int Before, int Quick, bool Disabled);
    private sealed record TelEquipmentDraft(int EquipmentId, int? ConnectionEquipmentId, int LocationId, int PhoneTypeId,
        DateTime? ReturnDate, string Notes);
}
