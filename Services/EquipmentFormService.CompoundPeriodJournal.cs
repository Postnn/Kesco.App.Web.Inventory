using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Записывает расположение, перемещение связанных SIM и при необходимости выдачу в одной транзакции.</summary>
    public virtual Task AddLocationAsync(EquipmentLocationPeriod location, EquipmentResponsiblePeriod? responsible,
        bool expectedActiveResponsible, int employeeId, Guid attemptId, CancellationToken ct)
    {
        var move = CopyLocationPeriod(location);
        var issue = responsible is null ? null : CopyResponsiblePeriod(responsible);
        var validation = ValidatePeriodRequest(() => ValidateLocationAddition(move, issue));
        if (issue is not null) issue.FromUtc = move.FromUtc;
        return RunPeriodJournalWriteAsync(move.EquipmentId, move.FromUtc, AddLocationPeriodCommand, employeeId,
            attemptId, validation, (connection, transaction, _, simIds, token) =>
            {
                var command = EquipmentLocationSaveBuilder.BuildAddEnlisted(move, issue, expectedActiveResponsible, simIds);
                return EquipmentPeriodJournalWrite.ExecuteAsync(connection, transaction, command.Sql, command.Parameters,
                    move.EquipmentId, token, AddLocationRefusal);
            }, ct, new CompoundPeriodContext(move, issue));
    }

    /// <summary>Атомарно закрывает ответственность, фиксирует перенос SIM и выдачу следующему ответственному.</summary>
    public virtual Task WithdrawResponsibleAsync(EquipmentResponsiblePeriod current, EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location, DateTime? nextFromLocalDate, int employeeId, Guid attemptId, CancellationToken ct)
    {
        var closing = CopyResponsiblePeriod(current);
        var issue = next is null ? null : CopyResponsiblePeriod(next);
        var move = location is null ? null : CopyLocationPeriod(location);
        var validation = ValidatePeriodRequest(() =>
        {
            ValidateWithdrawal(closing, issue, move);
            if (closing.ChangedAtUtc == default)
                throw new EquipmentPeriodWriteRejectedException("Не удалось подтвердить исходную версию ответственного.");
        });
        if (issue?.EmployeeId == closing.EmployeeId)
            validation ??= "Следующий ответственный должен отличаться от действующего.";
        if (issue?.ReturnDate is { } due)
        {
            if (nextFromLocalDate is null)
                validation ??= "Локальная календарная дата начала следующего периода не задана. Запись отменена.";
            else if (due.Date < nextFromLocalDate.Value.Date)
                validation ??= "Срок возврата следующего ответственного не может быть раньше его выдачи.";
        }

        return RunPeriodJournalWriteAsync(closing.EquipmentId, closing.FromUtc, WithdrawResponsiblePeriodCommand,
            employeeId, attemptId, validation, (connection, transaction, start, simIds, token) =>
            {
                closing.FromUtc = start;
                var command = EquipmentResponsibleSaveBuilder.BuildWithdrawEnlisted(closing, issue, move, simIds);
                return EquipmentPeriodJournalWrite.ExecuteAsync(connection, transaction, command.Sql, command.Parameters,
                    closing.EquipmentId, token, WithdrawRefusal);
            }, ct, new CompoundPeriodContext(move, issue));
    }

    private static string? AddLocationRefusal(int status) => status switch
    {
        3 => "База не подтвердила выдачу ответственному.",
        4 => "Действующий ответственный изменился. Обновите данные перед повтором.",
        5 => "Для расположения «На руках» укажите ответственного сотрудника.",
        6 => "У оборудования уже есть действующий ответственный. Обновите данные.",
        7 => "Выдача на указанную дату и время уже существует. Обновите данные.",
        _ => RelocationRefusal(status),
    };

    private static string? WithdrawRefusal(int status) => status switch
    {
        1 => "Действующий ответственный изменился. Обновите данные перед повтором.",
        2 => "Для изъятия с расположения «На руках» укажите новое расположение.",
        3 => "Период следующего ответственного изменился. Обновите данные.",
        4 => "База не подтвердила изъятие или следующую выдачу.",
        _ => RelocationRefusal(status),
    };

    private static string? RelocationRefusal(int status) => status switch
    {
        10 => "Период расположения изменился. Обновите данные перед повтором.",
        11 => "Состав или период связанных SIM изменился. Обновите данные.",
        12 => "База не подтвердила все сохранённые расположения.",
        _ => null,
    };

    private static string? ValidatePeriodRequest(Action validate)
    {
        try { validate(); return null; }
        catch (InvalidOperationException error) { return error.Message; }
    }

    private static EquipmentLocationPeriod CopyLocationPeriod(EquipmentLocationPeriod value) => new()
    {
        EquipmentId = value.EquipmentId, LocationId = value.LocationId, FromUtc = value.FromUtc,
        ReturnDate = value.ReturnDate, Notes = value.Notes,
    };

    private static EquipmentResponsiblePeriod CopyResponsiblePeriod(EquipmentResponsiblePeriod value) => new()
    {
        EquipmentId = value.EquipmentId, EmployeeId = value.EmployeeId, FromUtc = value.FromUtc, ToUtc = value.ToUtc,
        ChangedAtUtc = value.ChangedAtUtc, ReturnDate = value.ReturnDate, Notes = value.Notes,
    };
}
