using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Clayzor.Lib.Web.Controls.Services;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Переносит сведения о прежних попытках Equipment после проверки прав и серверных подтверждений.</summary>
    public virtual async Task ImportEquipmentLegacyEvidenceAsync(int equipmentId, int employeeId,
        IReadOnlyList<ClayLegacyDraftOperationEvidence> evidence, CancellationToken ct)
    {
        if (equipmentId <= 0 || employeeId <= 0) throw InvalidEquipmentLegacyIntent();
        if (evidence.Count == 0) return;
        var intents = evidence.Select(item => ParseEquipmentLegacyIntent(item, equipmentId)).ToArray();
        if (intents.Select(item => item.AttemptId).Distinct().Count() != intents.Length)
            throw InvalidEquipmentLegacyIntent();

        var principal = await GetWindowsPrincipalAsync();
        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var actor = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
            if (actor?.Id != employeeId)
                throw new UnauthorizedAccessException("Сотрудник для сверки операций оборудования не подтверждён.");

            var equipment = await ReadLegacyEquipmentAsync(connection, transaction, equipmentId, ct);
            if (equipment?.Id != equipmentId) throw InvalidEquipmentLegacyIntent();
            var type = intents.Any(item => NeedsLegacyEquipmentType(item.Family)) && equipment.EquipmentTypeId is > 0
                ? await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
                    SQLQueries.SELECT_ТипОборудованияПоИдентификатору, new { Id = equipment.EquipmentTypeId.Value },
                    transaction, cancellationToken: ct)) : null;

            foreach (var intent in intents)
            {
                if (!CanImportEquipmentLegacyIntent(intent, actor, equipment, type))
                    throw new UnauthorizedAccessException("Недостаточно прав для сверки прежней операции оборудования.");
                if (intent.Family == EquipmentLegacyFamily.Ups && intent.RelatedId is int upsId)
                {
                    var ups = await ReadLegacyEquipmentAsync(connection, transaction, upsId, ct);
                    if (ups?.Id != upsId || ups.EquipmentTypeId != (int)ТипыОборудования.ИБП)
                        throw InvalidEquipmentLegacyIntent();
                }

                var attempt = BuildLegacyEquipmentAttempt(actor, equipmentId, intent);
                var receipt = intent.Kind is AddLocationPeriodCommand or WithdrawResponsiblePeriodCommand or SaveUpsDetailsCommand
                    ? await InventoryOperationJournal.ReadAttemptContainingScopesAsync(connection, transaction, attempt, ct)
                    : await InventoryOperationJournal.ReadAttemptInTransactionAsync(connection, transaction, attempt, ct);
                if (receipt is not null)
                {
                    if (receipt.State is InventoryOperationState.Reserved or InventoryOperationState.Dispatching or
                        InventoryOperationState.Unknown or InventoryOperationState.Rejected)
                        continue;
                    if (receipt.State != InventoryOperationState.Confirmed || !HasLegacyEquipmentResult(intent, equipmentId, receipt))
                        throw InvalidEquipmentLegacyIntent();
                    if (intent.Kind is InsertAccountingCommand or UpdateAccountingCommand or DeleteAccountingCommand)
                        await RequireAccountingReceiptOwnersAsync(connection, transaction, actor, intent.Kind, intent.AttemptId, ct);
                    if (intent.Kind != EquipmentAtsWorkflow.SaveCommand ||
                        await HasSavedPhoneWorkflowAsync(connection, transaction, actor.Id, equipmentId, intent.AttemptId, ct))
                        continue;
                }

                if (intent.Family == EquipmentLegacyFamily.Accounting && (equipment.OwnerId is not > 0 ||
                    !await HasAccountingOwnerAccessAsync(connection, transaction, actor, equipment.OwnerId.Value, ct)))
                    throw new UnauthorizedAccessException("Недостаточно прав по владельцу бухгалтерского учёта.");

                // Без подходящего серверного результата известна только область исходной карточки.
                await InventoryOperationJournal.ImportLegacyAttemptAsync(connection, transaction, attempt with { OtherScopes = [] },
                    ct, retainEvenIfJournalExists: receipt is not null);
            }
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        });
    }

    private static Task<Equipment?> ReadLegacyEquipmentAsync(DbConnection connection, DbTransaction transaction,
        int equipmentId, CancellationToken ct) => connection.QuerySingleOrDefaultAsync<Equipment>(new CommandDefinition(
        SQLQueries.SELECT_ID_Оборудование, new { Id = equipmentId }, transaction, cancellationToken: ct));

    private static bool NeedsLegacyEquipmentType(EquipmentLegacyFamily family) => family is EquipmentLegacyFamily.Network or
        EquipmentLegacyFamily.Software or EquipmentLegacyFamily.Sim or EquipmentLegacyFamily.SimService or
        EquipmentLegacyFamily.License or EquipmentLegacyFamily.Computer or EquipmentLegacyFamily.Account or EquipmentLegacyFamily.Ups;

    private static bool CanImportEquipmentLegacyIntent(EquipmentLegacyIntent intent, Employee actor, Equipment equipment, EquipmentType? type)
    {
        var inventory = actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования);
        var ownerInventory = actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования,
            EmployeeRoleScope.PersonOrGlobal, equipment.OwnerId);
        var ipPhone = equipment.EquipmentTypeId == (int)ТипыОборудования.IP_телефон;
        return intent.Family switch
        {
            EquipmentLegacyFamily.Main or EquipmentLegacyFamily.Period => inventory,
            EquipmentLegacyFamily.Network => type is { HasComputerCharacteristics: > 0 } &&
                actor.HasRole(Роли.Исполнитель_заявок_службы_поддержки_пользователей, EmployeeRoleScope.Global),
            EquipmentLegacyFamily.Accounting => actor.HasRole(Роли.Бухгалтер) || actor.HasRole(Роли.Главный_бухгалтер_1С),
            EquipmentLegacyFamily.Software or EquipmentLegacyFamily.Computer => inventory && type is { HasComputerCharacteristics: > 0 },
            EquipmentLegacyFamily.Sim or EquipmentLegacyFamily.SimService => type is { HasSimCharacteristics: > 0 } &&
                actor.HasRole(Роли.Администратор_тарифов_связи),
            EquipmentLegacyFamily.License => type is { HasSoftwareCharacteristics: > 0 } &&
                (actor.HasRole(Роли.Специалист_по_инвентаризации_по, EmployeeRoleScope.PersonOrGlobal, equipment.OwnerId) ||
                 equipment.TenantId is > 0 && equipment.TenantId != equipment.OwnerId &&
                 actor.HasRole(Роли.Специалист_по_инвентаризации_по, EmployeeRoleScope.PersonOrGlobal, equipment.TenantId)),
            EquipmentLegacyFamily.Account => type is { MacAddressCount: > 0 } && inventory &&
                actor.HasRole(Роли.Дежурный_инженер, EmployeeRoleScope.Global),
            EquipmentLegacyFamily.Ups => type is { HasSimCharacteristics: 0 } && equipment.EquipmentTypeId is > 0 and < 100 && ownerInventory,
            EquipmentLegacyFamily.Configure => intent.Kind == "configure-phone-equipment" ? ipPhone && ownerInventory :
                ipPhone ? ownerInventory : inventory,
            _ => false,
        };
    }

    private static InventoryOperationJournal.Attempt BuildLegacyEquipmentAttempt(Employee actor, int id, EquipmentLegacyIntent intent)
    {
        var attempt = intent.AttemptId;
        return intent.Kind switch
        {
            "equipment-save" => MainEquipmentAttempt(actor, id, attempt, false, []),
            EquipmentAtsWorkflow.SaveCommand => MainEquipmentAttempt(actor, id, attempt, true),
            AttachNetworkCommand or DetachNetworkCommand => NetworkAttempt(actor, id, attempt, intent.Kind == AttachNetworkCommand),
            InsertAccountingCommand or UpdateAccountingCommand or DeleteAccountingCommand =>
                AccountingAttempt(actor, id, intent.RelatedId!.Value, intent.Kind, attempt),
            InsertSoftwareCommand or UpdateSoftwareCommand => SoftwareAttempt(actor, id, intent.RelatedId!.Value, attempt),
            InsertSimCommand or UpdateSimCommand or DeleteSimCommand => SimJournalAttempt(actor, id,
                intent.Kind, DuplicateScopes(intent.RelatedId), attempt),
            InsertSimServiceCommand or UpdateSimServiceCommand => SimJournalAttempt(actor, id,
                intent.Kind, SimServiceScopes(intent.RelatedId!.Value), attempt),
            InsertLicenseCommand or UpdateLicenseCommand => LicenseAttempt(actor, id, intent.Existing!.Value, attempt),
            InsertComputerCommand or UpdateComputerCommand => ComputerJournalAttempt(actor, id, intent.When!.Value, intent.Existing!.Value, attempt),
            InsertAccountCommand or UpdateAccountCommand or DeleteAccountCommand => AccountAttempt(actor, id, intent.Kind, intent.Binding!, attempt),
            AddLocationPeriodCommand or UpdateLocationPeriodCommand or IssueResponsiblePeriodCommand or UpdateResponsiblePeriodCommand or
                WithdrawResponsiblePeriodCommand => PeriodJournalAttempt(actor, id, intent.When!.Value, intent.Kind, attempt),
            AddUpsLinkCommand or DeleteUpsLinkCommand or SaveUpsDetailsCommand => UpsJournalAttempt(actor, intent.RelatedId!.Value, id,
                intent.Kind, attempt, []),
            _ => InventoryOperationJournal.ForEmployee(actor, attempt, MainEquipmentScope(id), intent.Kind, []),
        };
    }

    private static bool HasLegacyEquipmentResult(EquipmentLegacyIntent intent, int equipmentId, InventoryOperationReceipt receipt) =>
        receipt.ResultEntityId is > 0 && (intent.Family is EquipmentLegacyFamily.Accounting or EquipmentLegacyFamily.Software or
            EquipmentLegacyFamily.SimService ? intent.RelatedId is not > 0 || receipt.ResultEntityId == intent.RelatedId :
            receipt.ResultEntityId == equipmentId);

    private static async Task<bool> HasSavedPhoneWorkflowAsync(DbConnection connection, DbTransaction transaction, int employeeId,
        int equipmentId, Guid attemptId, CancellationToken ct)
    {
        var workflow = await connection.QuerySingleOrDefaultAsync<EquipmentAtsHeader>(new CommandDefinition(
            SQLQueries.SELECT_EquipmentAtsWorkflowForUpdate, new { AttemptId = attemptId }, transaction, cancellationToken: ct));
        if (workflow is null) return false;
        if (workflow.AttemptId != attemptId || workflow.EmployeeId != employeeId || workflow.EquipmentId != equipmentId ||
            workflow.Origin != (byte)EquipmentPhoneSyncOrigin.EquipmentSave || workflow.State > EquipmentAtsWorkflow.Abandoned)
            throw InvalidEquipmentLegacyIntent();
        return true;
    }
}
