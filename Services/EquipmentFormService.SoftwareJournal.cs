using System.Data.Common;
using System.Globalization;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal const string InsertSoftwareCommand = "equipment-software.insert";
    internal const string UpdateSoftwareCommand = "equipment-software.update";

    /// <summary>Перечитывает исходные данные установки ПО после проверки сотрудника и фактического типа.</summary>
    public virtual async Task<EquipmentSoftwareInstallation?> ReadSoftwareEditorResetAsync(int equipmentId, int installationId,
        int employeeId, CancellationToken ct)
    {
        await RequireMainActorAsync(await GetWindowsPrincipalAsync(), employeeId, ct);
        var type = (await _db.QueryRequiredAsync<EquipmentType>(SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей,
            new { EquipmentId = equipmentId }, ct)).SingleOrDefault();
        if (type is not { HasComputerCharacteristics: > 0 }) return null;
        if (installationId == 0) return new EquipmentSoftwareInstallation { EquipmentId = equipmentId };

        var rows = await _db.QueryRequiredAsync<EquipmentSoftwareInstallation>(SQLQueries.SELECT_ПО_КодОборудования,
            new { EquipmentId = equipmentId }, ct);
        return rows.SingleOrDefault(row => row.EquipmentId == equipmentId && row.Id == installationId);
    }

    /// <summary>Записывает установку ПО и подтверждение её точного ключа в одной транзакции.</summary>
    public virtual async Task SaveSoftwareInstallationAsync(EquipmentSoftwareInstallation installation, int employeeId,
        Guid attemptId, CancellationToken ct)
    {
        var submitting = false;
        try
        {
            if (attemptId == Guid.Empty || installation.EquipmentId <= 0 || installation.Id < 0)
                throw new EquipmentSoftwareWriteRejectedException("Не удалось подтвердить контекст установки ПО.");
            if (installation.Validate() is { } validation)
                throw new EquipmentSoftwareWriteRejectedException(validation);

            var principal = await GetWindowsPrincipalAsync();
            var actor = await RequireMainActorAsync(principal, employeeId, ct);
            var attempt = SoftwareAttempt(actor, installation.EquipmentId, installation.Id, attemptId);
            submitting = true;
            var receipt = await InventoryOperationJournal.RunSqlAsync<EquipmentSoftwareWriteRejectedException>(_db, attempt,
                async (connection, transaction, token) =>
                {
                    var fresh = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, token);
                    if (fresh?.Id != employeeId || !fresh.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
                        throw new EquipmentSoftwareWriteRejectedException("Недостаточно прав для изменения установленного ПО.");

                    var type = await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
                        SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, new { installation.EquipmentId },
                        transaction, cancellationToken: token));
                    if (type is not { HasComputerCharacteristics: > 0 })
                        throw new EquipmentSoftwareWriteRejectedException("Установка ПО недоступна для фактического типа оборудования.");

                    var softwareId = await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
                        SQLQueries.SELECT_ПрограммноеОборудованиеПоИдентификатору, new { Id = installation.SoftwareId },
                        transaction, cancellationToken: token));
                    if (softwareId is not > 0 || softwareId != installation.SoftwareId)
                        throw new EquipmentSoftwareWriteRejectedException("Выбранное программное обеспечение недоступно.");

                    return await installation.SaveEnlistedAsync(connection, transaction, token);
                }, ct);
            if (receipt.State == InventoryOperationState.Rejected)
                throw new EquipmentSoftwareWriteRejectedException("Предыдущая попытка изменения установки ПО отклонена.");
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId is not > 0 ||
                installation.Id > 0 && receipt.ResultEntityId != installation.Id)
                throw new InventoryOperationPendingException();
        }
        catch (Exception error) when (error is EquipmentSoftwareWriteRejectedException or EquipmentMainWriteRejectedException)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.Rejected, error.Message, error);
        }
        catch (InventoryOperationPendingException error) when (error.BlockedBeforeReservation)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent, error.Message, error);
        }
        catch (Exception error) when (error is DbException or TimeoutException or InvalidOperationException or OperationCanceledException)
        {
            throw new EquipmentOperationException(submitting ? EquipmentWritePhase.Unknown : EquipmentWritePhase.NotSent,
                error.Message, error);
        }
    }

    /// <summary>Читает подтверждение собственной попытки по ключу оборудования и исходной установки.</summary>
    public virtual async Task<InventoryOperationReceipt?> ReadSoftwareWriteReceiptAsync(int equipmentId, int installationId,
        int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (equipmentId <= 0 || installationId < 0 || attemptId == Guid.Empty)
            throw new UnauthorizedAccessException("Контекст установки ПО недоступен для сверки.");
        var actor = await RequireMainActorAsync(await GetWindowsPrincipalAsync(), employeeId, ct);
        if (await ReadMainEquipmentAsync(equipmentId, ct) is null)
            return null;
        return await InventoryOperationJournal.ReadAttemptAsync(_db,
            SoftwareAttempt(actor, equipmentId, installationId, attemptId), ct);
    }

    private static InventoryOperationJournal.Attempt SoftwareAttempt(Employee actor, int equipmentId, int installationId,
        Guid attemptId) => InventoryOperationJournal.ForEmployee(actor, attemptId, MainEquipmentScope(equipmentId),
            installationId == 0 ? InsertSoftwareCommand : UpdateSoftwareCommand,
            installationId == 0 ? [] : ["equipment/software/" + installationId.ToString(CultureInfo.InvariantCulture)]);
}
