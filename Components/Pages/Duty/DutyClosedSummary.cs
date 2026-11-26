using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Components.Pages.Duty;

/// <summary>Отображаемый итог закрытого дежурства.</summary>
internal sealed record DutyClosedSummary(DutyConfirmation Confirmation, int? ReplacementId,
    int? EmployerId, DateTime? TimeOff, decimal? Payment)
{
    internal static DutyClosedSummary FromRow(DutyRow row)
    {
        if (row.ActualId is not { } actual)
            return new DutyClosedSummary(DutyConfirmation.Nobody, null, null, null, null);

        var timeOff = row.TimeOff == DateTime.MinValue ? null : row.TimeOff;
        return actual == row.PlannedId
            ? new DutyClosedSummary(DutyConfirmation.Planned, null, null, timeOff, row.Payment)
            : new DutyClosedSummary(DutyConfirmation.Other, actual, row.EmployerId is > 0 ? row.EmployerId : null, timeOff, row.Payment);
    }
}
