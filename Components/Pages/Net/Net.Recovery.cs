using System.Globalization;
using System.Text.Json;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;

namespace Kesco.App.Web.Inventory.Components.Pages.Net;

public partial class Net
{
    private NetReadOnlyRecovery? _readOnlyRecovery;
    private string? _readOnlyRecoveryNotice;

    private string RecoveryOwner(int version) => $"{_operationRoot}-recovery-{version}";

    private bool IsReadOnlyRecoveryCurrent(int id, int version) =>
        IsCurrent(version, id) && _model is null && _operationContext is null;

    private bool IsReadOnlyRecoveryCurrent(NetReadOnlyRecovery recovery) =>
        ReferenceEquals(_readOnlyRecovery, recovery) && IsReadOnlyRecoveryCurrent(recovery.PortId, recovery.Version);

    private async Task LoadReadOnlyRecoveryAsync(int id, int version, Employee actor)
    {
        if (actor.Id <= 0 || !IsReadOnlyRecoveryCurrent(id, version))
            return;

        ClayDraftRegistration? registration = null;
        try
        {
            registration = await Drafts.RegisterHandleAsync(OperationFormKey, id.ToString(CultureInfo.InvariantCulture),
                actor.Id.ToString(CultureInfo.InvariantCulture), 1,
                $"[data-net-recovery='{RecoveryOwner(version)}']", _pageCts.Token);
            if (!IsReadOnlyRecoveryCurrent(id, version) || !registration.Restore.HasDraft)
                return;

            var state = await registration.ReadEditorScopeAsync(NetInputScope, _pageCts.Token);
            if (!IsReadOnlyRecoveryCurrent(id, version))
                return;

            NetDraft? input = null;
            if (registration.Restore.TypedScopes?.TryGetValue(NetInputScope, out var json) == true)
            {
                var stored = JsonSerializer.Deserialize<NetEditorInput>(json);
                if (stored?.PortId != id || stored.Current is null)
                    throw new InvalidOperationException("Сохранённый ввод принадлежит другому порту.");
                input = stored.Current;
            }

            if (input is null && !state.HasRaw && !state.HasOperation && registration.Restore.Operation is null)
                return;

            _readOnlyRecovery = new NetReadOnlyRecovery(id, version, registration, input,
                state.HasOperation || registration.Restore.Operation is not null, state.HasRaw);
            registration = null;
        }
        catch (Exception error) when (IsJournalError(error))
        {
            if (IsReadOnlyRecoveryCurrent(id, version))
                _readOnlyRecoveryNotice = "Не удалось открыть локальный черновик порта.";
        }
        finally
        {
            if (registration is not null)
                await ReleaseReadOnlyRecoveryAsync(registration);
        }
    }

    private async Task RestoreReadOnlyNetRawAsync(NetReadOnlyRecovery recovery)
    {
        if (_loading || !IsReadOnlyRecoveryCurrent(recovery))
            return;

        recovery.RestoreRaw = false;
        try
        {
            var applied = await recovery.Registration.ApplyRawWithResultAsync(NetInputScope, _pageCts.Token);
            if (IsReadOnlyRecoveryCurrent(recovery))
                recovery.RawPending = applied.Pending.Count > 0;
        }
        catch (Exception error) when (IsJournalError(error))
        {
            if (IsReadOnlyRecoveryCurrent(recovery))
                recovery.RawPending = true;
        }
        if (IsReadOnlyRecoveryCurrent(recovery))
            StateHasChanged();
    }

    private async Task ResetReadOnlyRecoveryAsync()
    {
        var recovery = _readOnlyRecovery;
        _readOnlyRecovery = null;
        _readOnlyRecoveryNotice = null;
        if (recovery is not null)
            await ReleaseReadOnlyRecoveryAsync(recovery.Registration);
    }

    private static async Task ReleaseReadOnlyRecoveryAsync(ClayDraftRegistration registration)
    {
        try { await registration.DisposeAsync(); }
        catch (Exception error) when (IsJournalError(error)) { }
    }

    private static IEnumerable<(string Key, string Label, string? Value, int Rows)> ReadOnlyNetFields(NetDraft? fields)
    {
        yield return ("net.connected", "Подключено", fields?.Connected, 4);
        yield return ("net.description", "Примечание", fields?.Description, 4);
        yield return ("net.socket", "Код розетки", fields?.SocketId?.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("net.employee", "Код сотрудника", fields?.EmployeeId?.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("net.customer", "Код заказчика", fields?.CustomerId.ToString(CultureInfo.CurrentCulture), 1);
        yield return ("net.return-date", "Срок возврата", fields?.ReturnDate?.ToString("dd.MM.yyyy", CultureInfo.CurrentCulture), 1);
        yield return ("net.switch", "Переключить на порт", fields?.SwitchToId is > 0 ? fields.SwitchToId.Value.ToString(CultureInfo.CurrentCulture) : null, 1);
        yield return ("net.exchange", "Обменять с портом", fields?.ExchangeWithId is > 0 ? fields.ExchangeWithId.Value.ToString(CultureInfo.CurrentCulture) : null, 1);
    }

    private sealed record NetReadOnlyRecovery(int PortId, int Version, ClayDraftRegistration Registration,
        NetDraft? Input, bool HasOperation, bool HasRaw)
    {
        internal bool RestoreRaw { get; set; } = HasRaw;
        internal bool RawPending { get; set; }
    }
}
