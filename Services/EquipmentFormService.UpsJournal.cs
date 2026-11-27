using System.Data;
using System.Data.Common;
using System.Data.SqlTypes;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    internal const string AddUpsLinkCommand = "equipment-ups.link.insert";
    internal const string DeleteUpsLinkCommand = "equipment-ups.link.delete";
    internal const string SaveUpsDetailsCommand = "equipment-ups.details.save";

    /// <summary>Подключает ИБП и фиксирует подтверждение точной пары в одной транзакции.</summary>
    public virtual Task AddUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, Guid attemptId, CancellationToken ct) =>
        WriteUpsLinkAsync(upsEquipmentId, serverEquipmentId, employeeId, attemptId, AddUpsLinkCommand,
            SQLQueries.INSERT_СерверПодключенияUPSОборудованиеEnlisted, ct);

    /// <summary>Удаляет существующую связь ИБП и подтверждает отсутствие точной пары.</summary>
    public virtual Task DeleteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, Guid attemptId, CancellationToken ct) =>
        WriteUpsLinkAsync(upsEquipmentId, serverEquipmentId, employeeId, attemptId, DeleteUpsLinkCommand,
            SQLQueries.DELETE_СерверПодключенияUPSEnlisted, ct);

    private Task WriteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, Guid attemptId,
        string kind, string sql, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@КодОборудованияUPS", upsEquipmentId, DbType.Int32);
        parameters.Add("@КодОборудованияСервера", serverEquipmentId, DbType.Int32);
        return RunUpsJournalWriteAsync(upsEquipmentId, serverEquipmentId, employeeId, attemptId, kind,
            new EquipmentPhoneSaveCommand(sql, parameters), [], ct);
    }

    /// <summary>Фиксирует все изменённые связи и тесты ИБП в одной транзакции с подтверждением.</summary>
    public virtual Task SaveUpsDetailsAsync(int upsEquipmentId, int sourceServerEquipmentId,
        IReadOnlyCollection<UpsServerLink> originalServers, IReadOnlyCollection<UpsServerLink> currentServers,
        IReadOnlyCollection<UpsTest> originalTests, IReadOnlyCollection<UpsTest> currentTests,
        int employeeId, Guid attemptId, CancellationToken ct)
    {
        EquipmentPhoneSaveCommand command;
        int[] affected;
        try
        {
            command = UpsSaveBuilder.BuildEnlisted(upsEquipmentId, sourceServerEquipmentId,
                originalServers, currentServers, originalTests, currentTests, out affected);
        }
        catch (InvalidOperationException error)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.Rejected, error.Message, error);
        }
        return RunUpsJournalWriteAsync(upsEquipmentId, sourceServerEquipmentId, employeeId, attemptId,
            SaveUpsDetailsCommand, command, affected, ct);
    }

    private async Task RunUpsJournalWriteAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, Guid attemptId,
        string kind, EquipmentPhoneSaveCommand command, IReadOnlyCollection<int> affected, CancellationToken ct)
    {
        var submitting = false;
        try
        {
            if (!ValidUpsJournalContext(upsEquipmentId, serverEquipmentId, attemptId) || affected.Contains(upsEquipmentId))
                throw new UpsWriteRejectedException("Не удалось подтвердить оборудование и ИБП.");
            var principal = await GetWindowsPrincipalAsync();
            var actor = await _db.RunDbAsync(connection => RequireUpsJournalAccessAsync(connection, null, principal,
                employeeId, upsEquipmentId, serverEquipmentId, ct));
            var attempt = UpsJournalAttempt(actor, upsEquipmentId, serverEquipmentId, kind, attemptId, affected);
            submitting = true;
            var receipt = await InventoryOperationJournal.RunSqlAsync<UpsWriteRejectedException>(_db, attempt,
                async (connection, transaction, token) =>
                {
                    await RequireUpsJournalAccessAsync(connection, transaction, principal, employeeId, upsEquipmentId, serverEquipmentId, token);
                    var proof = await connection.QuerySingleOrDefaultAsync<UpsWriteProof>(new CommandDefinition(command.Sql, command.Parameters,
                        transaction, commandType: CommandType.Text, cancellationToken: token));
                    var rejection = proof?.Status switch
                    {
                        1 or 3 when kind == SaveUpsDetailsCommand => "Недостаточно прав для изменения затронутых подключений UPS.",
                        4 => "Признак выключения доступен только для компьютеров. Обновите данные.",
                        1 or 2 => "Связь или тест UPS изменились. Обновите данные.",
                        3 or 5 => "База не подтвердила изменение UPS.",
                        _ => null,
                    };
                    if (rejection is not null) throw new UpsWriteRejectedException(rejection);
                    if (proof is not { Status: 0 } || proof.ConfirmedEquipmentId != serverEquipmentId)
                        throw new InvalidOperationException("База не подтвердила точный результат операции UPS.");
                    return serverEquipmentId;
                }, ct);
            if (receipt.State == InventoryOperationState.Rejected)
                throw new UpsWriteRejectedException("Предыдущая попытка изменения UPS отклонена.");
            if (receipt.State != InventoryOperationState.Confirmed || receipt.ResultEntityId != serverEquipmentId)
                throw new InventoryOperationPendingException();
        }
        catch (UpsWriteRejectedException error)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.Rejected, error.Message, error);
        }
        catch (InventoryOperationPendingException error) when (error.BlockedBeforeReservation)
        {
            throw new EquipmentOperationException(EquipmentWritePhase.NotSent, error.Message, error);
        }
        catch (Exception error) when (error is DbException or TimeoutException or InvalidOperationException or
            OperationCanceledException or SqlTypeException)
        {
            throw new EquipmentOperationException(submitting ? EquipmentWritePhase.Unknown : EquipmentWritePhase.NotSent,
                error.Message, error);
        }
    }

    /// <summary>Читает подтверждение исходной операции UPS с действующими правами на сервер.</summary>
    public virtual async Task<InventoryOperationReceipt?> ReadUpsWriteReceiptAsync(int upsEquipmentId, int serverEquipmentId,
        string commandKind, int employeeId, Guid attemptId, CancellationToken ct)
    {
        if (!ValidUpsJournalContext(upsEquipmentId, serverEquipmentId, attemptId) ||
            commandKind is not (AddUpsLinkCommand or DeleteUpsLinkCommand or SaveUpsDetailsCommand))
            throw new UnauthorizedAccessException("Контекст подключения UPS недоступен для сверки.");
        var principal = await GetWindowsPrincipalAsync();
        return await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            var actor = await RequireUpsJournalAccessAsync(connection, transaction, principal, employeeId, upsEquipmentId, serverEquipmentId, ct);
            var attempt = UpsJournalAttempt(actor, upsEquipmentId, serverEquipmentId, commandKind, attemptId, []);
            var receipt = commandKind == SaveUpsDetailsCommand
                ? await InventoryOperationJournal.ReadAttemptContainingScopesAsync(connection, transaction, attempt, ct)
                : await InventoryOperationJournal.ReadAttemptInTransactionAsync(connection, transaction, attempt, ct);
            await transaction.CommitAsync(CancellationToken.None);
            return receipt;
        });
    }

    private static bool ValidUpsJournalContext(int upsId, int serverId, Guid id) =>
        upsId > 0 && serverId > 0 && upsId != serverId && id != Guid.Empty;

    private static InventoryOperationJournal.Attempt UpsJournalAttempt(Employee actor, int upsId, int serverId, string kind,
        Guid id, IReadOnlyCollection<int> affected) => InventoryOperationJournal.ForEmployee(actor, id, MainEquipmentScope(serverId),
        kind, affected.Append(upsId).Select(MainEquipmentScope).ToArray());

    /// <summary>Проверяет действующие права на исходный сервер перед сбросом выбора нового ИБП.</summary>
    public virtual async Task<bool> CanResetUpsLinkEditorAsync(int serverId, int employeeId, CancellationToken ct)
    {
        if (serverId <= 0 || employeeId <= 0) return false;
        var principal = await GetWindowsPrincipalAsync();
        try
        {
            await _db.RunDbAsync(connection => RequireUpsSourceAccessAsync(connection, null, principal, employeeId, serverId, ct));
            return true;
        }
        catch (UpsWriteRejectedException)
        {
            return false;
        }
    }

    private static async Task<Employee> RequireUpsJournalAccessAsync(DbConnection connection, DbTransaction? transaction,
        string? principal, int employeeId, int upsId, int serverId, CancellationToken ct)
    {
        var actor = await RequireUpsSourceAccessAsync(connection, transaction, principal, employeeId, serverId, ct);
        var ups = await connection.QuerySingleOrDefaultAsync<Equipment>(new CommandDefinition(
            SQLQueries.SELECT_ID_Оборудование, new { Id = upsId }, transaction, cancellationToken: ct));
        if (ups?.Id != upsId || ups.EquipmentTypeId != (int)ТипыОборудования.ИБП)
            throw new UpsWriteRejectedException("Выбранный ИБП недоступен или его тип изменился.");
        return actor;
    }

    private static async Task<Employee> RequireUpsSourceAccessAsync(DbConnection connection, DbTransaction? transaction,
        string? principal, int employeeId, int serverId, CancellationToken ct)
    {
        var actor = await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct);
        if (actor?.Id != employeeId || !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования))
            throw new UpsWriteRejectedException("Недостаточно прав для изменения подключений UPS.");
        var server = await connection.QuerySingleOrDefaultAsync<Equipment>(new CommandDefinition(
            SQLQueries.SELECT_ID_Оборудование, new { Id = serverId }, transaction, cancellationToken: ct));
        if (server?.Id != serverId || server.EquipmentTypeId is not (> 0 and < 100) ||
            !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования, EmployeeRoleScope.PersonOrGlobal, server.OwnerId))
            throw new UpsWriteRejectedException("Недостаточно прав по владельцу сервера или его тип изменился.");
        var type = await connection.QuerySingleOrDefaultAsync<EquipmentType>(new CommandDefinition(
            SQLQueries.SELECT_ТипОборудованияПоИдентификатору, new { Id = server.EquipmentTypeId.Value }, transaction,
            cancellationToken: ct));
        if (type is not { HasSimCharacteristics: 0 })
            throw new UpsWriteRejectedException("Подключение UPS недоступно для фактического типа сервера.");
        return actor;
    }

    /// <summary>Согласованный снимок связей и тестов для сброса редактора ИБП.</summary>
    public sealed record UpsEditorReset(IReadOnlyList<UpsServerLink> Servers, IReadOnlyList<UpsTest> Tests);

    /// <summary>Читает свежие списки после проверки точной исходной связи и сотрудника.</summary>
    public virtual async Task<UpsEditorReset?> ReadUpsEditorResetAsync(int upsId, int serverId, int employeeId, CancellationToken ct)
    {
        if (upsId <= 0 || serverId <= 0 || upsId == serverId || employeeId <= 0)
            return null;

        var principal = await GetWindowsPrincipalAsync();
        try
        {
            return await _db.RunDbAsync<UpsEditorReset?>(async connection =>
            {
                await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
                await RequireUpsJournalAccessAsync(connection, transaction, principal, employeeId, upsId, serverId, ct);
                var servers = (await connection.QueryAsync<UpsServerLink>(new CommandDefinition(
                    SQLQueries.SELECT_ID_СервераПодключенияUPS, new { UpsEquipmentId = upsId }, transaction,
                    cancellationToken: ct))).ToList();
                if (servers.Any(row => row.UpsEquipmentId != upsId || row.ServerEquipmentId <= 0) ||
                    servers.GroupBy(row => row.ServerEquipmentId).Any(group => group.Count() != 1) ||
                    servers.Count(row => row.ServerEquipmentId == serverId) != 1)
                    return null;
                var tests = (await connection.QueryAsync<UpsTest>(new CommandDefinition(
                    SQLQueries.SELECT_ID_ТестыПодключенияUPS, new { UpsEquipmentId = upsId }, transaction,
                    cancellationToken: ct))).ToList();
                if (tests.Any(row => row.UpsEquipmentId != upsId || row.Id <= 0) ||
                    tests.GroupBy(row => row.Id).Any(group => group.Count() != 1))
                    return null;
                await transaction.CommitAsync(ct);
                return new UpsEditorReset(servers, tests);
            });
        }
        catch (UpsWriteRejectedException)
        {
            return null;
        }
    }

    private sealed class UpsWriteProof
    {
        /// <summary>Именованный статус изменения UPS.</summary>
        public int? Status { get; set; }
        /// <summary>Код сервера подтверждённой операции.</summary>
        public int? ConfirmedEquipmentId { get; set; }
    }

    private sealed class UpsWriteRejectedException(string message) : InvalidOperationException(message);
}
