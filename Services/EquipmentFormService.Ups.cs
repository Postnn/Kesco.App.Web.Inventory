using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Подключает ИБП к серверу.</summary>
    public virtual Task AddUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, CancellationToken ct) =>
        WriteUpsLinkAsync(upsEquipmentId, serverEquipmentId, employeeId,
            SQLQueries.INSERT_СерверПодключенияUPSОборудованиеEnlisted, ct);

    /// <summary>Удаляет существующую связь ИБП.</summary>
    public virtual Task DeleteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId, CancellationToken ct) =>
        WriteUpsLinkAsync(upsEquipmentId, serverEquipmentId, employeeId,
            SQLQueries.DELETE_СерверПодключенияUPSEnlisted, ct);

    private Task WriteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId, int employeeId,
        string sql, CancellationToken ct)
    {
        var parameters = new DynamicParameters();
        parameters.Add("@КодОборудованияUPS", upsEquipmentId, DbType.Int32);
        parameters.Add("@КодОборудованияСервера", serverEquipmentId, DbType.Int32);
        return RunUpsWriteAsync(upsEquipmentId, serverEquipmentId, employeeId,
            new EquipmentPhoneSaveCommand(sql, parameters), [], saveDetails: false, ct);
    }

    /// <summary>Сохраняет все изменённые связи и тесты ИБП в одной транзакции.</summary>
    public virtual Task SaveUpsDetailsAsync(int upsEquipmentId, int sourceServerEquipmentId,
        IReadOnlyCollection<UpsServerLink> originalServers, IReadOnlyCollection<UpsServerLink> currentServers,
        IReadOnlyCollection<UpsTest> originalTests, IReadOnlyCollection<UpsTest> currentTests,
        int employeeId, CancellationToken ct)
    {
        var command = UpsSaveBuilder.BuildEnlisted(upsEquipmentId, sourceServerEquipmentId,
            originalServers, currentServers, originalTests, currentTests, out var affected);
        return RunUpsWriteAsync(upsEquipmentId, sourceServerEquipmentId, employeeId,
            command, affected, saveDetails: true, ct);
    }

    private async Task RunUpsWriteAsync(int upsEquipmentId, int serverEquipmentId, int employeeId,
        EquipmentPhoneSaveCommand command, IReadOnlyCollection<int> affected, bool saveDetails, CancellationToken ct)
    {
        if (!ValidUpsContext(upsEquipmentId, serverEquipmentId) || affected.Contains(upsEquipmentId))
            throw new UpsWriteRejectedException("Не удалось подтвердить оборудование и ИБП.");
        var principal = await GetWindowsPrincipalAsync();
        await _db.RunDbAsync(connection => RequireUpsAccessAsync(connection, null, principal,
            employeeId, upsEquipmentId, serverEquipmentId, ct));
        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            await RequireUpsAccessAsync(connection, transaction, principal, employeeId, upsEquipmentId, serverEquipmentId, ct);
            var proof = await connection.QuerySingleOrDefaultAsync<UpsWriteProof>(new CommandDefinition(command.Sql, command.Parameters,
                transaction, commandType: CommandType.Text, cancellationToken: ct));
            var rejection = proof?.Status switch
            {
                1 or 3 when saveDetails => "Недостаточно прав для изменения затронутых подключений UPS.",
                4 => "Признак выключения доступен только для компьютеров. Обновите данные.",
                1 or 2 => "Связь или тест UPS изменились. Обновите данные.",
                3 or 5 => "База не подтвердила изменение UPS.",
                _ => null,
            };
            if (rejection is not null) throw new UpsWriteRejectedException(rejection);
            if (proof is not { Status: 0 } || proof.ConfirmedEquipmentId != serverEquipmentId)
                throw new InvalidOperationException("База не подтвердила точный результат операции UPS.");
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        });
    }

    private static bool ValidUpsContext(int upsId, int serverId) =>
        upsId > 0 && serverId > 0 && upsId != serverId;

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

    private static async Task<Employee> RequireUpsAccessAsync(DbConnection connection, DbTransaction? transaction,
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
                await RequireUpsAccessAsync(connection, transaction, principal, employeeId, upsId, serverId, ct);
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
