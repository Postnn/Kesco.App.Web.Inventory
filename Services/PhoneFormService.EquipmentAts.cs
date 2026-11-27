using System.Data.Common;
using System.Globalization;
using Clayzor.Lib.Entities.Inventory;
using Dapper;

namespace Kesco.App.Web.Inventory.Services;

public partial class PhoneFormService
{
    internal virtual async Task<PhoneSyncOperationResult> SynchronizeEquipmentWorkflowStepAsync(
        string? principal, EquipmentAtsHeader header, EquipmentAtsStep step, CancellationToken ct)
    {
        var attemptId = header.AttemptId.ToString("N");
        var operation = new PhoneSyncOperation((PhoneSyncKind)step.Kind,
            step.TargetId.ToString(CultureInfo.InvariantCulture), step.Deleting);
        PhoneSyncOperationResult Result(PhoneSyncPhase phase, string? message = null) =>
            new(attemptId, operation, phase, message);
        const string unknown = "Исход команды АТС не подтверждён. Повтор заблокирован.";
        var saved = header.Origin == (byte)EquipmentPhoneSyncOrigin.EquipmentSave;

        if (header.AttemptId == Guid.Empty || header.EmployeeId <= 0 || header.EquipmentId <= 0 ||
            !saved && header.Origin != (byte)EquipmentPhoneSyncOrigin.ExplicitConfigure || string.IsNullOrWhiteSpace(principal) ||
            step.StepIndex < 0 || step.TargetId <= 0 || step.Deleting && (!saved || step.Kind != EquipmentAtsStep.Xorcom) ||
            step.Kind is not (EquipmentAtsStep.Xorcom or EquipmentAtsStep.Ccm))
            return Result(PhoneSyncPhase.Rejected, "Не удалось подтвердить контекст команды АТС.");

        string procedure;
        Action<DynamicParameters> fill;
        try
        {
            ct.ThrowIfCancellationRequested();
            var phoneId = step.Kind == EquipmentAtsStep.Xorcom ? step.TargetId : 0;
            (procedure, fill) = await PreparePhoneSyncAsync(phoneId, operation, ct, requireExplicitDelete: true);
        }
        catch (Exception error) when (error is DbException or TimeoutException or OperationCanceledException)
        {
            return Result(PhoneSyncPhase.NotSent, "Не удалось подготовить команду АТС. Команда не отправлена.");
        }
        catch (Exception error) when (error is InvalidOperationException or UnauthorizedAccessException or ArgumentException)
        {
            return Result(PhoneSyncPhase.Rejected, "Не удалось проверить цель или возможность команды АТС.");
        }

        bool shouldDispatch;
        try
        {
            // Сверка владельца, цели и очередности и переход в Dispatching подтверждаются одной SQL-транзакцией.
            shouldDispatch = saved
                ? await EquipmentAtsWorkflow.BeginSavedStepAsync(_db, principal, header.EmployeeId,
                    header.EquipmentId, header.AttemptId, step, ct)
                : await EquipmentAtsWorkflow.BeginConfigureStepAsync(_db, principal, header.EmployeeId,
                    header.EquipmentId, header.AttemptId, step, ct);
        }
        catch (EquipmentAtsContextChangedException)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            return Result(PhoneSyncPhase.Rejected, "Сотрудник или цель команды АТС не подтверждены.");
        }
        catch (EquipmentAtsPlanningUnavailableException error)
        {
            return Result(PhoneSyncPhase.NotSent, error.Message);
        }
        catch (Exception)
        {
            return Result(PhoneSyncPhase.Unknown, unknown);
        }

        if (!shouldDispatch)
            return Result(PhoneSyncPhase.Succeeded);

        try
        {
            await ExecuteReturnCodeAsync(procedure, new DynamicParameters(), fill, () => ct.ThrowIfCancellationRequested());
            if (saved)
                await EquipmentAtsWorkflow.ConfirmSavedStepAsync(_db, principal, header.EmployeeId,
                    header.EquipmentId, header.AttemptId, step, CancellationToken.None);
            else
                await EquipmentAtsWorkflow.ConfirmConfigureStepAsync(_db, principal, header.EmployeeId,
                    header.EquipmentId, header.AttemptId, step, CancellationToken.None);
            return Result(PhoneSyncPhase.Succeeded);
        }
        catch (Exception)
        {
            // Любое отсутствие доказанного успеха после Dispatching требует сверки, включая ненулевой код АТС.
            return Result(PhoneSyncPhase.Unknown, unknown);
        }
    }
}
