using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Components.Form;
using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class ModelEquipmentForm
{
    private const string EditorScope = "model-input";
    private static readonly ClayToolbarButtonOptions RetryInputOptions = new() { Text = "Повторить восстановление ввода" };
    private static readonly ClayToolbarButtonOptions RetryInputStorageOptions = new() { Text = "Повторить сохранение черновика" };
    private ModelEditorOriginal? _editorOriginal;
    private string? _scheduledInput;
    private string? _inputNotice;
    private bool _inputConflict;
    private bool _adoptionConflict;
    private bool _restoreRawRequested;
    private bool _restoringRaw;
    private bool _rawPending;
    private bool _inputSaveFailed;

    private bool InputBlocked => _inputConflict || _restoreRawRequested || _restoringRaw || _rawPending || _inputSaveFailed;

    private void CaptureEditorOriginal(EquipmentModel model)
    {
        _editorOriginal = GetEditorOriginal(model);
        _scheduledInput = null;
    }

    private static ModelEditorOriginal GetEditorOriginal(EquipmentModel model) =>
        new(model.Id, model.ChangedAt, ModelEditorFields.From(model),
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(model.PhoneTemplate ?? string.Empty))));

    private async Task RestoreEditorInputAsync(ClayDraftRegistration registration, int version)
    {
        if (!registration.Restore.HasDraft || _model is not { } fresh || _editorOriginal is null)
            return;
        var originalLiveFields = ModelEditorFields.From(fresh);
        var state = await registration.ReadEditorScopeAsync(EditorScope, _pageCts.Token);
        if (!IsEditorContextCurrent(registration, version))
            return;

        if (registration.Restore.TypedScopes?.TryGetValue(EditorScope, out var json) == true)
        {
            var draft = JsonSerializer.Deserialize<ModelEditorDraft>(json);
            if (draft is null || draft.Original.Id != fresh.Id)
                throw new InvalidOperationException("Исходная модель черновика не совпадает с открытой.");

            var typeId = draft.Current.EquipmentTypeId;
            var type = typeId > 0 && typeId != _selectedType?.Id
                ? await EquipmentTypeSource.GetByIdAsync(typeId, _pageCts.Token)
                : typeId > 0 ? _selectedType : null;
            if (!IsEditorContextCurrent(registration, version))
                return;

            if (ModelEditorFields.From(fresh) != originalLiveFields)
            {
                _adoptionConflict = true;
                _inputConflict = true;
                _inputNotice = "Ввод изменился при восстановлении. Открытые и сохранённые значения оставлены для сверки.";
                return;
            }
            _inputConflict = draft.Original != _editorOriginal;
            _editorOriginal = draft.Original;
            if (_inputConflict)
            {
                _writeBaseline = null;
                _inputNotice = "Сохранён исходный снимок черновика. Модель изменилась после его создания. Ввод восстановлен, запись требует сверки.";
            }
            draft.Current.ApplyTo(fresh);
            _selectedType = type;
            _equipmentTypeId = typeId > 0 ? typeId : null;
            UpdateTypeState(false);
            _scheduledInput = json;
        }
        else if (state.HasRaw)
        {
            _inputConflict = true;
            _editorOriginal = null;
            _writeBaseline = null;
            _inputNotice = "Восстановлен черновик ввода без исходной версии. Запись требует сверки.";
        }

        _restoreRawRequested = state.HasRaw;
    }

    private async Task RestoreRawInputAsync()
    {
        if (_restoringRaw || _journal is not { } registration || _model is not { } model ||
            _disposed || _loading || _saving || _journalBusy || !_canEdit)
            return;
        var version = _loadVersion;
        var retry = _rawPending;
        var route = (Id, CodeType, LegacyCodeType);
        var opened = ModelEditorFields.From(model);
        bool Current() => IsEditorContextCurrent(registration, version) && route == (Id, CodeType, LegacyCodeType) &&
            CurrentUser?.Id == _journalEmployeeId;
        _restoreRawRequested = false;
        _restoringRaw = true;
        StateHasChanged();
        try
        {
            if (retry)
            {
                var actor = await Employee.GetCurrentWithRolesAsync(Db, await ReadPrincipalAsync(), _pageCts.Token);
                if (!Current()) return;
                if (actor?.Id != _journalEmployeeId || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
                {
                    _inputNotice = "Не удалось подтвердить сотрудника для восстановления. Ввод сохранён.";
                    return;
                }
                if (opened != ModelEditorFields.From(model))
                {
                    _inputNotice = "Ввод изменился. Повторите восстановление оставшихся полей.";
                    return;
                }
            }
            var result = retry
                ? await registration.ApplyPendingRawAsync(EditorScope, _pageCts.Token)
                : await registration.ApplyRawWithResultAsync(EditorScope, _pageCts.Token);
            if (!Current()) return;
            var readiness = await registration.GetRestoreReadinessAsync(_pageCts.Token);
            if (!Current()) return;
            _rawPending = result is null || result.Pending.Count > 0 || !EditorRawReady(readiness);
            if (_rawPending)
                _inputNotice = "Часть ввода ещё не восстановлена. Запись заблокирована.";
            else if (!_inputSaveFailed)
                _inputNotice = _inputConflict ? "Ввод восстановлен. Запись требует сверки исходной версии." : null;
        }
        catch (Exception ex) when (IsJournalError(ex) || ex is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (!Current())
                return;
            _rawPending = true;
            _inputNotice = "Не удалось восстановить черновик ввода.";
        }
        finally
        {
            if (IsEditorContextCurrent(registration, version))
            {
                _restoringRaw = false;
                StateHasChanged();
            }
        }
    }

    private static bool EditorRawReady(ClayDraftRestoreReadiness readiness) =>
        !readiness.PendingScopes.Contains(EditorScope) && !readiness.FailedScopes.Contains(EditorScope);

    private async Task EnsureEditorRawReadyAsync(ClayDraftRegistration registration, CancellationToken token)
    {
        var version = _loadVersion;
        bool ready;
        try { ready = EditorRawReady(await registration.GetRestoreReadinessAsync(token)); }
        catch (Exception error) when (IsJournalError(error)) { ready = false; }
        if (!IsEditorContextCurrent(registration, version))
            throw new InvalidOperationException("Контекст модели изменился.");
        if (ready) return;
        _rawPending = true;
        _inputNotice = "Сначала полностью восстановите ввод модели. Запись не отправлена.";
        throw new InvalidOperationException(_inputNotice);
    }

    private async Task PersistEditedInputAsync()
    {
        if (_disposed || _loading || _journalBusy || !_canEdit || _restoringRaw || _restoreRawRequested || _rawPending ||
            _journal is not { } registration || _model is not { } model || _editorOriginal is null ||
            (!IsModified && _scheduledInput is null) || _inputSaveFailed || _adoptionConflict)
            return;

        var json = SerializeEditorDraft(model);
        if (_scheduledInput == json)
            return;
        var version = _loadVersion;
        _scheduledInput = json;
        try
        {
            await SaveEditorScopeAsync(registration, json, _pageCts.Token);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsEditorContextCurrent(registration, version) && _scheduledInput == json)
            {
                _inputSaveFailed = true;
                _inputNotice = "Не удалось сохранить черновик ввода. Запись заблокирована.";
                StateHasChanged();
            }
        }
    }

    private Task RetryEditorRawAsync() => _rawPending ? RestoreRawInputAsync() : Task.CompletedTask;

    private async Task RetryEditorInputAsync()
    {
        if (_saving || _loading || _journalBusy || _restoringRaw || _disposed || _adoptionConflict || _journal is not { } registration)
            return;
        if (_rawPending || !_inputSaveFailed)
            return;
        try
        {
            // Передаём только собственную доказанную метку «не отправлено», если она есть.
            var status = await registration.RetryStorageAsync(_notSentAttempt, _pageCts.Token);
            if (!ReferenceEquals(_journal, registration) || _disposed)
                return;
            _writeAttempt = status;
            _notSentAttempt = null;
            _inputSaveFailed = false;
            _scheduledInput = null;
            if (!_inputConflict)
                _inputNotice = null;
            await PersistEditedInputAsync();
            await ScanJournalAsync(registration);
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            _inputSaveFailed = true;
            _inputNotice = "Черновик ввода пока недоступен. Запись заблокирована.";
        }
    }

    private async Task<bool> ProtectEditorRefreshAsync()
    {
        if (_journal is not { } registration)
            return false;
        var version = _loadVersion;
        try
        {
            var scope = await registration.ReadEditorScopeAsync(EditorScope, _pageCts.Token);
            if (!IsEditorContextCurrent(registration, version))
                return true;
            if (!IsModified && !InputBlocked && !scope.HasRaw)
                return false;

            await PersistEditedInputAsync();
            if (!IsEditorContextCurrent(registration, version))
                return true;
            if (!_inputSaveFailed)
                _inputNotice = "Локальный ввод оставлен в открытой форме. Обновление отложено до сверки черновика.";
        }
        catch (Exception ex) when (IsJournalError(ex))
        {
            if (IsEditorContextCurrent(registration, version))
                _inputNotice = "Не удалось проверить состояние черновика. Открытый ввод оставлен без изменений.";
        }
        return true;
    }

    private string SerializeEditorDraft(EquipmentModel model) =>
        JsonSerializer.Serialize(new ModelEditorDraft(_editorOriginal!, ModelEditorFields.From(model)));

    private static async Task<long> SaveEditorScopeAsync(ClayDraftRegistration registration, string json, CancellationToken ct)
    {
        var revision = await registration.SaveTypedScopeAsync(EditorScope, json, ct);
        return revision > 0 ? revision : throw new InvalidOperationException("Черновик модели не подтверждён.");
    }

    private bool IsEditorContextCurrent(ClayDraftRegistration registration, int version) =>
        !_disposed && version == _loadVersion && ReferenceEquals(_journal, registration);

    private async Task<bool> HasLaterEditorInputAsync(ClayDraftRegistration registration, string fingerprint)
    {
        var state = await registration.ReadEditorScopeAsync(EditorScope, _pageCts.Token);
        return state.HasRaw || _model is { } live && EditableFingerprint(live) != fingerprint;
    }

    private void ResetEditorInput()
    {
        _scheduledInput = null;
        _inputNotice = null;
        _inputConflict = false;
        _adoptionConflict = false;
        _restoreRawRequested = false;
        _restoringRaw = false;
        _rawPending = false;
        _inputSaveFailed = false;
    }

    private sealed record ModelEditorOriginal(int Id, DateTime? ChangedAt, ModelEditorFields Fields, string TemplateFingerprint);
    private sealed record ModelEditorDraft(ModelEditorOriginal Original, ModelEditorFields Current);

    private sealed record ModelEditorFields(
        int EquipmentTypeId, int? ResourceId, string Name, string PartNumber, string Cpu, double MonitorSize,
        int MemoryTypeId, int MemoryBanks, bool IsMultimedia, int NetworkInterfaces, string NetworkName,
        string Driver, string LifeTime, string Description)
    {
        internal static ModelEditorFields From(EquipmentModel model) =>
            new(model.EquipmentTypeId, model.ResourceId, model.Name, model.PartNumber, model.Cpu, model.MonitorSize,
                model.MemoryTypeId, model.MemoryBanks, model.IsMultimedia, model.NetworkInterfaces,
                model.NetworkName, model.Driver, model.LifeTime, model.Description);

        internal void ApplyTo(EquipmentModel model)
        {
            model.EquipmentTypeId = EquipmentTypeId;
            model.ResourceId = ResourceId;
            model.Name = Name;
            model.PartNumber = PartNumber;
            model.Cpu = Cpu;
            model.MonitorSize = MonitorSize;
            model.MemoryTypeId = MemoryTypeId;
            model.MemoryBanks = MemoryBanks;
            model.IsMultimedia = IsMultimedia;
            model.NetworkInterfaces = NetworkInterfaces;
            model.NetworkName = NetworkName;
            model.Driver = Driver;
            model.LifeTime = LifeTime;
            model.Description = Description;
        }
    }
}
