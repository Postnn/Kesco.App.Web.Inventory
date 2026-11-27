using System.Data;
using System.Data.Common;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    private sealed record CompoundPeriodContext(EquipmentLocationPeriod? Relocation, EquipmentResponsiblePeriod? Next);

    /// <summary>Обновляет последнее расположение.</summary>
    public virtual Task UpdateLocationAsync(EquipmentLocationPeriod location, int employeeId, CancellationToken ct)
    {
        var submitted = new EquipmentLocationPeriod
        {
            EquipmentId = location.EquipmentId, FromUtc = location.FromUtc, LocationId = location.LocationId,
            ChangedAtUtc = location.ChangedAtUtc, ReturnDate = location.ReturnDate, Notes = location.Notes,
        };
        return RunPeriodWriteAsync(submitted.EquipmentId, submitted.FromUtc, employeeId,
            submitted.LocationId <= 0 || submitted.ChangedAtUtc == default
                ? "Не удалось подтвердить исходное расположение." : null,
            (connection, transaction, start, _, token) => submitted.UpdateEnlistedAsync(connection, transaction, start, token), ct);
    }

    /// <summary>Выдаёт оборудование ответственному.</summary>
    public virtual Task IssueResponsibleAsync(EquipmentResponsiblePeriod responsible, int employeeId, CancellationToken ct) =>
        SaveResponsibleAsync(responsible, insert: true, employeeId, ct);

    /// <summary>Изменяет срок возврата и примечания последнего периода ответственности.</summary>
    public virtual Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible, int employeeId, CancellationToken ct) =>
        SaveResponsibleAsync(responsible, insert: false, employeeId, ct);

    private Task SaveResponsibleAsync(EquipmentResponsiblePeriod source, bool insert, int employeeId,
        CancellationToken ct)
    {
        var submitted = new EquipmentResponsiblePeriod
        {
            EquipmentId = source.EquipmentId, FromUtc = source.FromUtc, ToUtc = source.ToUtc, EmployeeId = source.EmployeeId,
            ChangedAtUtc = source.ChangedAtUtc, ReturnDate = source.ReturnDate, Notes = source.Notes,
        };
        var invalid = submitted.EmployeeId <= 0 || !insert && submitted.ChangedAtUtc == default;
        return RunPeriodWriteAsync(submitted.EquipmentId, submitted.FromUtc, employeeId,
            invalid ? "Не удалось подтвердить исходный период ответственности." : null,
            (connection, transaction, start, _, token) => insert
                ? submitted.IssueEnlistedAsync(connection, transaction, start, token)
                : submitted.UpdateEnlistedAsync(connection, transaction, start, token), ct);
    }

    private async Task RunPeriodWriteAsync(int equipmentId, DateTime fromUtc, int employeeId,
        string? validation, Func<DbConnection, DbTransaction, DateTime, IReadOnlyList<int>, CancellationToken, Task<int>> write,
        CancellationToken ct, CompoundPeriodContext? compound = null)
    {
        if (equipmentId <= 0 || fromUtc == default)
            throw new EquipmentPeriodWriteRejectedException("Не удалось подтвердить оборудование и начало периода.");
        if (validation is not null) throw new EquipmentPeriodWriteRejectedException(validation);
        var start = EquipmentPeriodWrite.NormalizeStartUtc(fromUtc);
        var principal = await GetWindowsPrincipalAsync();
        await RequireMainActorAsync(principal, employeeId, ct);
        int[] simIds = [];
        if (compound is not null)
        {
            if (compound.Relocation is { } relocation)
                relocation.FromUtc = EquipmentPeriodWrite.NormalizeStartUtc(relocation.FromUtc);
            if (compound.Next is { } next)
                next.FromUtc = EquipmentPeriodWrite.NormalizeStartUtc(next.FromUtc);
            if (compound.Relocation is { } move)
                simIds = await _db.RunDbAsync(connection => EquipmentLocationEnlistedSql.ReadPlanAsync(connection, null, move, ct));
        }

        await _db.RunDbAsync(async connection =>
        {
            await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.Serializable, ct);
            try { AssertMainActor(await Employee.GetCurrentWithRolesAsync(connection, transaction, principal, ct), employeeId); }
            catch (EquipmentMainWriteRejectedException error) { throw new EquipmentPeriodWriteRejectedException(error.Message); }
            if (compound?.Relocation is { } move)
            {
                var current = await EquipmentLocationEnlistedSql.ReadPlanAsync(connection, transaction, move, ct);
                if (!current.SequenceEqual(simIds))
                    throw new EquipmentPeriodWriteRejectedException("Состав связанных SIM изменился. Обновите карточку.");
            }
            var resultId = await write(connection, transaction, start, simIds, ct);
            if (resultId != equipmentId)
                throw new InvalidOperationException("База не подтвердила изменение периода оборудования.");
            await transaction.CommitAsync(CancellationToken.None);
            return true;
        });
    }
}
