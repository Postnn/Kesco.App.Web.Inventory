using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using System.Globalization;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal const string InsertSimCommand = "equipment-sim.insert";
    internal const string UpdateSimCommand = "equipment-sim.update";
    internal const string DeleteSimCommand = "equipment-sim.delete";
    internal const string InsertSimServiceCommand = "equipment-sim-service.insert";
    internal const string UpdateSimServiceCommand = "equipment-sim-service.update";

    /// <summary>Сохраняет SIM и подтверждённое списание дубликата в защищённой транзакции.</summary>
    public virtual Task SaveSimAsync(EquipmentSimCard sim, EquipmentSimWriteMode mode, string? expectedVersion,
        int? duplicateEquipmentId, int employeeId, Guid attemptId, CancellationToken ct)
    {
        var submitted = new EquipmentSimCard
        {
            EquipmentId = sim.EquipmentId, PhoneNumber = sim.PhoneNumber, ActivatedAtUtc = sim.ActivatedAtUtc,
            ContractId = sim.ContractId, PersonalDataPersonId = sim.PersonalDataPersonId,
            Secrets = new EquipmentSimSecrets { Pin = sim.Secrets.Pin, Pin2 = sim.Secrets.Pin2, Puk = sim.Secrets.Puk, Puk2 = sim.Secrets.Puk2 },
        };
        var validation = mode is not (EquipmentSimWriteMode.Add or EquipmentSimWriteMode.Edit) ||
            mode == EquipmentSimWriteMode.Add && expectedVersion is not null ||
            mode == EquipmentSimWriteMode.Edit && string.IsNullOrWhiteSpace(expectedVersion)
                ? "Исходное состояние SIM не подтверждено." : submitted.Validate();
        if (duplicateEquipmentId is <= 0 || duplicateEquipmentId == submitted.EquipmentId)
            validation = "Контекст дубликата SIM изменился.";

        return RunSimJournalWriteAsync(submitted.EquipmentId, submitted.EquipmentId,
            mode == EquipmentSimWriteMode.Add ? InsertSimCommand : UpdateSimCommand,
            DuplicateScopes(duplicateEquipmentId), employeeId, attemptId, validation,
            async (connection, transaction, token) =>
            {
                var duplicates = (await connection.QueryAsync<int>(new CommandDefinition(SQLQueries.SELECT_АктивныеДубликатыSIM,
                    new { submitted.EquipmentId, submitted.PhoneNumber }, transaction, cancellationToken: token))).ToArray();
                if (duplicateEquipmentId is int duplicateId)
                {
                    if (duplicates is not [var exact] || exact != duplicateId)
                        throw new EquipmentSimWriteRejectedException("Активный дубликат SIM изменился. Подтвердите актуальную операцию.");
                    await RequireSimEquipmentTypeAsync(connection, transaction, duplicateId, token);
                }
                else if (duplicates.Length != 0)
                    throw new EquipmentSimWriteRejectedException("Найден активный дубликат SIM. Сверьте данные перед записью.");

                return await submitted.SaveEnlistedAsync(connection, transaction, mode, expectedVersion, duplicateEquipmentId, token);
            }, ct);
    }

    /// <summary>Удаляет проверенную исходную SIM и подтверждает отсутствие её строки.</summary>
    public virtual Task DeleteSimAsync(int equipmentId, string? expectedVersion, int employeeId, Guid attemptId, CancellationToken ct) =>
        RunSimJournalWriteAsync(equipmentId, equipmentId, DeleteSimCommand, [], employeeId, attemptId,
            string.IsNullOrWhiteSpace(expectedVersion) ? "Исходное состояние SIM не подтверждено." : null,
            (connection, transaction, token) => EquipmentSimCard.DeleteEnlistedAsync(
                connection, transaction, equipmentId, expectedVersion, token), ct);

    /// <summary>Фиксирует запись дополнительной услуги и точный ключ в серверном журнале.</summary>
    public virtual Task SaveSimServiceAsync(EquipmentSimService service, DateTime? businessToday, int employeeId,
        Guid attemptId, CancellationToken ct)
    {
        var submitted = new EquipmentSimService
        {
            Id = service.Id, EquipmentId = service.EquipmentId, PhoneNumber = service.PhoneNumber, ChangedAt = service.ChangedAt,
            ServiceTypeId = service.ServiceTypeId, ServiceName = service.ServiceName, DateFrom = service.DateFrom, DateTo = service.DateTo,
            ActivationCost = service.ActivationCost, DailyCost = service.DailyCost, MonthlyCost = service.MonthlyCost,
        };
        var validation = submitted.Id < 0 ? "Контекст дополнительной услуги SIM изменился." : businessToday is { } day
            ? submitted.Validate(day) : "Календарная дата клиента не задана. Сохранение дополнительной услуги SIM отменено.";
        return RunSimJournalWriteAsync(submitted.EquipmentId, submitted.Id == 0 ? null : submitted.Id,
            submitted.Id == 0 ? InsertSimServiceCommand : UpdateSimServiceCommand, SimServiceScopes(submitted.Id),
            employeeId, attemptId, validation, submitted.SaveEnlistedAsync, ct);
    }

    /// <summary>Сверяет исход точной попытки изменения SIM без чтения PIN или PUK.</summary>
    public virtual Task<InventoryOperationReceipt?> ReadSimWriteReceiptAsync(int equipmentId, int? duplicateEquipmentId,
        string commandKind, int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (commandKind is not (InsertSimCommand or UpdateSimCommand or DeleteSimCommand) ||
            duplicateEquipmentId is <= 0 || duplicateEquipmentId == equipmentId ||
            commandKind == DeleteSimCommand && duplicateEquipmentId is not null)
            throw new UnauthorizedAccessException("Контекст SIM недоступен для сверки.");
        return ReadSimJournalReceiptAsync(equipmentId, commandKind, DuplicateScopes(duplicateEquipmentId), employeeId, attemptId, ct);
    }

    /// <summary>Читает результат собственной попытки изменения дополнительной услуги SIM.</summary>
    public virtual Task<InventoryOperationReceipt?> ReadSimServiceWriteReceiptAsync(int equipmentId, int serviceId,
        int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (serviceId < 0) throw new UnauthorizedAccessException("Контекст дополнительной услуги SIM недоступен для сверки.");
        return ReadSimJournalReceiptAsync(equipmentId, serviceId == 0 ? InsertSimServiceCommand : UpdateSimServiceCommand,
            SimServiceScopes(serviceId), employeeId, attemptId, ct);
    }

    private async Task RunSimJournalWriteAsync(int equipmentId, int? resultId, string commandKind, IReadOnlyCollection<string> scopes,
        int employeeId, Guid attemptId, string? validation, Func<DbConnection, DbTransaction, CancellationToken, Task<int>> write,
        CancellationToken ct)
    {
        var submitting = false;
        try
        {
            if (equipmentId <= 0 || attemptId == Guid.Empty)
                throw new EquipmentSimWriteRejectedException("Контекст SIM изменился.");
            if (validation is not null) throw new EquipmentSimWriteRejectedException(validation);
            var principal = await GetWindowsPrincipalAsync();
            var actor = await _db.RunDbAsync(connection => RequireSimJournalAccessAsync(connection, null, principal, employeeId, equipmentId, ct));
            var attempt = SimJournalAttempt(actor, equipmentId, commandKind, scopes, attemptId);
            submitting = true;
            var receipt = await InventoryOperationJournal.RunProtectedSqlAsync<EquipmentSimWriteRejectedException>(_db, attempt,
                async (connection, transaction, token) => await write(connection, transaction, token),
                static () => new EquipmentSimStorageException(true, true), ct,
                prepare: (connection, transaction, token) =>
                    RequireSimJournalAccessAsync(connection, transaction, principal, employeeId, equipmentId, token));
            if (receipt.State == InventoryOperationState.Rejected)
                throw new EquipmentSimWriteRejectedException("Предыдущая попытка изменения SIM отклонена.");
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId is not > 0 ||
                resultId is int expected && receipt.ResultEntityId != expected)
                throw new InventoryOperationPendingException();
        }
        catch (EquipmentSimWriteRejectedException error)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.Rejected, error.Message, error);
        }
        catch (Exception error) when (error is InventoryOperationPreparationException or
            InventoryOperationPendingException { BlockedBeforeReservation: true })
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent, error.Message, error);
        }
        catch (Exception error) when (error is DbException or TimeoutException or InvalidOperationException or
            OperationCanceledException or ArgumentException or SqlTypeException)
        {
            var safe = new EquipmentSimStorageException(submitting, submitting);
            throw new EquipmentOperationException(submitting ? EquipmentWritePhase.Unknown : EquipmentWritePhase.NotSent, safe.Message, safe);
        }
    }

    private async Task<InventoryOperationReceipt?> ReadSimJournalReceiptAsync(int equipmentId, string commandKind,
        IReadOnlyCollection<string> scopes, int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (equipmentId <= 0 || attemptId == Guid.Empty)
            throw new UnauthorizedAccessException("Контекст SIM недоступен для сверки.");
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var actor = await RequireSimJournalAccessAsync(connection, transaction, principal, employeeId, equipmentId, ct);
            var receipt = await InventoryOperationJournal.ReadAttemptInTransactionAsync(connection, transaction,
                SimJournalAttempt(actor, equipmentId, commandKind, scopes, attemptId), ct);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        });
    }

    private static string[] DuplicateScopes(int? duplicateId) => duplicateId is int id ? [MainEquipmentScope(id)] : [];
    private static string[] SimServiceScopes(int id) => id > 0 ? ["equipment/sim-service/" + id.ToString(CultureInfo.InvariantCulture)] : [];

    /// <summary>Перечитывает услугу в рамках исходной привязки SIM к оборудованию.</summary>
    public virtual async Task<EquipmentSimService?> ReadSimServiceEditorResetAsync(int equipmentId, int serviceId,
        string openingPhone, DateTime businessDay, int employeeId, CancellationToken ct)
    {
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await RequireSimJournalAccessAsync(connection, null, principal, employeeId, equipmentId, ct);
            var phone = await connection.QuerySingleOrDefaultAsync<string>(new CommandDefinition(
                SQLQueries.SELECT_SimPhoneForServiceReset, new { EquipmentId = equipmentId }, cancellationToken: ct));
            if (string.IsNullOrEmpty(phone) || !string.Equals(phone, openingPhone, StringComparison.Ordinal))
                return null;
            if (serviceId == 0)
                return new EquipmentSimService { EquipmentId = equipmentId, PhoneNumber = phone };

            var rows = await connection.QueryAsync<EquipmentSimService>(new CommandDefinition(
                SQLQueries.SELECT_SIMКартыДопУслугиСотОператора_КодОборудования,
                new { EquipmentId = equipmentId, ShowHistory = 1, ShowRequired = 1, BusinessToday = businessDay.Date },
                cancellationToken: ct));
            var row = rows.SingleOrDefault(item => item.Id == serviceId && item.PhoneNumber == phone);
            if (row is not null) row.EquipmentId = equipmentId;
            return row;
        });
    }

    private static InventoryOperationJournal.Attempt SimJournalAttempt(Employee actor, int equipmentId, string kind,
        IReadOnlyCollection<string> scopes, Guid id) => InventoryOperationJournal.ForEmployee(actor, id, MainEquipmentScope(equipmentId), kind, scopes);

    /// <summary>Перечитывает сохранённую SIM и условия договора после свежей проверки права записи.</summary>
    public virtual async Task<(EquipmentSimCard Snapshot, int CustomerType)?> ReadSimEditorResetAsync(
        int equipmentId, bool expectedExisting, int employeeId, CancellationToken ct)
    {
        var principal = await GetWindowsPrincipalAsync();
        try
        {
            return await _db.RunDbAsync<(EquipmentSimCard Snapshot, int CustomerType)?>(async connection =>
            {
                try
                {
                    await RequireSimJournalAccessAsync(connection, null, principal, employeeId, equipmentId, ct);

                    if (!expectedExisting)
                    {
                        var occupied = await HasChildPresenceRowAsync(connection,
                            EquipmentChildPresenceQueries.SimPresenceRow, new { EquipmentId = equipmentId }, ct);
                        return occupied ? null : (new EquipmentSimCard { EquipmentId = equipmentId }, 0);
                    }

                    var row = await EquipmentSimCard.ReadEditorSnapshotAsync(connection, equipmentId, ct);
                    if (row?.EquipmentId != equipmentId || string.IsNullOrEmpty(row.EditorVersion))
                        return null;

                    var customerType = 0;
                    if (row.ContractId is int contractId)
                    {
                        var contract = await connection.QuerySingleOrDefaultAsync<MobileContractLookup>(new CommandDefinition(
                            SQLQueries.SELECT_ДоговорМобильнойСвязиПоИдентификатору,
                            new { Id = contractId }, cancellationToken: ct));
                        if (contract?.Id != contractId) return null;
                        customerType = contract.CustomerType;
                    }
                    return (row, customerType);
                }
                catch (DbException)
                {
                    throw new EquipmentSimStorageException(false, false);
                }
            });
        }
        catch (DbException)
        {
            throw new EquipmentSimStorageException(false, false);
        }
    }

    private static async Task<Employee> RequireSimJournalAccessAsync(DbConnection connection, DbTransaction? transaction,
        string? principal, int employeeId, int equipmentId, CancellationToken ct)
    {
        var actor = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
        if (actor?.Id != employeeId || !actor.HasRole(Роли.Администратор_тарифов_связи))
            throw new EquipmentSimWriteRejectedException("Недостаточно прав для изменения SIM.");
        await RequireSimEquipmentTypeAsync(connection, transaction, equipmentId, ct);
        return actor;
    }

    private static async Task RequireSimEquipmentTypeAsync(DbConnection connection, DbTransaction? transaction,
        int equipmentId, CancellationToken ct)
    {
        var type = await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
            SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, new { EquipmentId = equipmentId }, transaction,
            cancellationToken: ct));
        if (type is not { HasSimCharacteristics: > 0 })
            throw new EquipmentSimWriteRejectedException("Сведения SIM недоступны для фактического типа оборудования.");
    }
}
