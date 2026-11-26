using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment;

public partial class ModelEquipmentForm
{
    private readonly string _readOnlyRecoveryRoot = $"model-recovery-{Guid.NewGuid():N}";
    private LocalModelRecovery? _localRecovery;

    private string ReadOnlyRecoveryClass(int version) => $"{_readOnlyRecoveryRoot}-{version}";

    private bool IsReadOnlyRecoveryCurrent(int id, int version) =>
        !_disposed && Id == id && version == _loadVersion && !_canEdit;

    private bool IsReadOnlyRecoveryCurrent(LocalModelRecovery recovery) =>
        ReferenceEquals(_localRecovery, recovery) && IsReadOnlyRecoveryCurrent(recovery.Id, recovery.Version);

    private async Task LoadReadOnlyRecoveryAsync(int id, int version)
    {
        ClayDraftRegistration? registration = null;
        try
        {
            var actor = await Employee.GetCurrentAsync(Db, await ReadPrincipalAsync(), _pageCts.Token);
            if (actor is not { Id: > 0 } || !IsReadOnlyRecoveryCurrent(id, version))
                return;

            registration = await Drafts.RegisterHandleAsync(JournalForm, id.ToString(CultureInfo.InvariantCulture),
                actor.Id.ToString(CultureInfo.InvariantCulture), 1, $".{ReadOnlyRecoveryClass(version)}", _pageCts.Token);
            if (!IsReadOnlyRecoveryCurrent(id, version) || !registration.Restore.HasDraft)
                return;

            var state = await registration.ReadEditorScopeAsync(EditorScope, _pageCts.Token);
            if (!IsReadOnlyRecoveryCurrent(id, version))
                return;

            ModelEditorFields? fields = null;
            if (registration.Restore.TypedScopes?.TryGetValue(EditorScope, out var json) == true)
            {
                var draft = JsonSerializer.Deserialize<ModelEditorDraft>(json);
                if (draft?.Original is null || draft.Original.Id != id)
                    throw new InvalidOperationException("Исходная модель локального черновика не совпадает с открытой.");
                fields = draft.Current;
            }

            if (fields is null && !state.HasRaw && registration.Restore.Operation is null)
                return;

            _writeBaseline = null;
            _localRecovery = new LocalModelRecovery(id, version, actor.Id, registration, fields,
                registration.Restore.Operation is not null, state.HasRaw);
            registration = null;
        }
        catch (Exception error) when (IsJournalError(error) || error is Microsoft.Data.SqlClient.SqlException)
        {
            if (IsReadOnlyRecoveryCurrent(id, version))
                _inputNotice = "Не удалось открыть локальный черновик модели. Запись недоступна.";
        }
        finally
        {
            if (registration is not null)
                await registration.DisposeAsync();
        }
    }

    private async Task RestoreReadOnlyRawAsync(LocalModelRecovery recovery)
    {
        if (_loading || recovery.ApplyingRaw || !IsReadOnlyRecoveryCurrent(recovery))
            return;

        var retry = recovery.RawPending;
        recovery.RestoreRaw = false;
        recovery.ApplyingRaw = true;
        try
        {
            if (retry)
            {
                var employee = await Employee.GetCurrentAsync(Db, await ReadPrincipalAsync(), _pageCts.Token);
                if (!IsReadOnlyRecoveryCurrent(recovery) || employee?.Id != recovery.ActorId) return;
            }
            var result = retry
                ? await recovery.Registration.ApplyPendingRawAsync(EditorScope, _pageCts.Token)
                : await recovery.Registration.ApplyRawWithResultAsync(EditorScope, _pageCts.Token);
            if (!IsReadOnlyRecoveryCurrent(recovery)) return;
            var readiness = await recovery.Registration.GetRestoreReadinessAsync(_pageCts.Token);
            if (IsReadOnlyRecoveryCurrent(recovery))
                recovery.RawPending = result.Pending.Count != 0 || !EditorRawReady(readiness);
        }
        catch (Exception error) when (IsJournalError(error) || error is Microsoft.Data.SqlClient.SqlException or UnauthorizedAccessException)
        {
            if (IsReadOnlyRecoveryCurrent(recovery)) recovery.RawPending = true;
        }
        finally
        {
            recovery.ApplyingRaw = false;
            if (IsReadOnlyRecoveryCurrent(recovery)) StateHasChanged();
        }
    }

    private async Task ResetReadOnlyRecoveryAsync()
    {
        var recovery = _localRecovery;
        _localRecovery = null;
        if (recovery is not null)
            await recovery.Registration.DisposeAsync();
    }

    private static IEnumerable<(string Key, string Label, string? Value, int Rows)> ReadOnlyRecoveryFields(ModelEditorFields? fields)
    {
        yield return ("model.type", "Код типа оборудования", fields?.EquipmentTypeId.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("model.resource", "Код ресурса", fields?.ResourceId?.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("model.name", "Модель", fields?.Name, 1);
        yield return ("model.pn", "Номер производителя", fields?.PartNumber, 1);
        yield return ("model.cpu", "CPU", fields?.Cpu, 1);
        yield return ("model.monitor", "Размер монитора", fields?.MonitorSize.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("model.memory-type", "Код типа памяти", fields?.MemoryTypeId.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("model.memory-banks", "Банки памяти", fields?.MemoryBanks.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("model.net", "Сетевые интерфейсы", fields?.NetworkInterfaces.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("model.net-name", "Сетевое имя", fields?.NetworkName, 1);
        yield return ("model.lifetime", "Срок службы", fields?.LifeTime, 1);
        yield return ("model.driver", "Драйвер", fields?.Driver, 3);
        yield return ("model.notes", "Примечание", fields?.Description, 2);
    }

    private sealed record LocalModelRecovery(
        int Id, int Version, int ActorId, ClayDraftRegistration Registration, ModelEditorFields? Fields, bool HasOperation, bool HasRaw)
    {
        internal bool RestoreRaw { get; set; } = HasRaw;
        internal bool RawPending { get; set; }
        internal bool ApplyingRaw { get; set; }
    }
}
