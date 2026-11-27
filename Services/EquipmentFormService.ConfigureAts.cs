using System.Globalization;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Открывает или продолжает собственный серверный план настройки сохранённого телефона.</summary>
    public virtual async Task<PhoneSyncAttempt> ConfigurePhoneEquipmentAttemptAsync(int equipmentId,
        Func<PhoneSyncAttempt, Task>? checkpoint = null)
    {
        var principal = await GetWindowsPrincipalAsync();
        var actor = await RequirePhoneSyncActorAsync(principal);
        var active = await EquipmentAtsWorkflow.ReadConfigureAsync(_db, principal, actor.Id, equipmentId, null, default);
        if (active is not null)
            return await DispatchConfigureAsync(principal, active, checkpoint, default);
        var created = await EquipmentAtsWorkflow.OpenConfigureAsync(_db, principal, actor.Id, equipmentId, Guid.NewGuid(), default);
        return await DispatchConfigureAsync(principal, created, checkpoint, default);
    }

    /// <summary>Подтверждает план настройки с идентификатором, выделенным текущей формой до отправки.</summary>
    public virtual async Task<PhoneSyncAttempt> ConfigurePhoneEquipmentAttemptAsync(int equipmentId,
        int employeeId, Guid attemptId, CancellationToken ct)
    {
        var principal = await GetWindowsPrincipalAsync();
        var snapshot = await EquipmentAtsWorkflow.OpenConfigureAsync(_db, principal, employeeId, equipmentId, attemptId, ct);
        return await DispatchConfigureAsync(principal, snapshot, null, ct);
    }

    /// <summary>Читает собственную активную настройку или результат точной попытки без отправки команд.</summary>
    public virtual async Task<PhoneSyncAttempt?> ReadConfigurePhoneAttemptAsync(int equipmentId, int employeeId,
        Guid? attemptId, CancellationToken ct)
    {
        var snapshot = await EquipmentAtsWorkflow.ReadConfigureAsync(_db, await GetWindowsPrincipalAsync(), employeeId,
            equipmentId, attemptId, ct);
        return snapshot is null ? null : ConfigureAttempt(snapshot);
    }

    private async Task<PhoneSyncAttempt> RetryConfigurePhoneAsync(PhoneSyncAttempt attempt,
        Func<PhoneSyncAttempt, Task>? checkpoint)
    {
        var principal = await GetWindowsPrincipalAsync();
        var actor = await RequirePhoneSyncActorAsync(principal);
        var snapshot = await FindConfigureAttemptAsync(principal, actor.Id, attempt);
        return await DispatchConfigureAsync(principal, snapshot, checkpoint, default);
    }

    private async Task<PhoneSyncReconciliation> ReconcileConfigurePhoneAsync(PhoneSyncAttempt attempt)
    {
        var principal = await GetWindowsPrincipalAsync();
        var actor = await RequirePhoneSyncActorAsync(principal);
        var snapshot = await FindConfigureAttemptAsync(principal, actor.Id, attempt);
        var equipment = await GetAsync(snapshot.Header.EquipmentId);
        var sameEquipment = equipment?.Id == snapshot.Header.EquipmentId && equipment.OwnerId == snapshot.Header.OwnerId &&
            equipment.ChangedAtUtc == snapshot.Header.ExpectedChangedAtUtc && equipment.EquipmentTypeId == (int)ТипыОборудования.IP_телефон;
        var phones = sameEquipment ? await GetPhonesAsync(snapshot.Header.EquipmentId) : [];
        // Подтверждение внешнего результата всегда берётся из журнала, даже если данные карточки совпали.
        var matches = sameEquipment && await EquipmentAtsWorkflow.MatchesConfigureContextAsync(_db, principal, actor.Id,
            snapshot.Header.EquipmentId, snapshot.Header.AttemptId, default);
        return new PhoneSyncReconciliation(ConfigureAttempt(snapshot), equipment, phones, matches);
    }

    private async Task<EquipmentAtsSnapshot> FindConfigureAttemptAsync(string? principal, int employeeId, PhoneSyncAttempt attempt)
    {
        if (!Guid.TryParseExact(attempt.AttemptId, "N", out var attemptId) || attemptId == Guid.Empty)
            throw new UnauthorizedAccessException("Не удалось определить серверную попытку настройки.");
        return await EquipmentAtsWorkflow.ReadConfigureAsync(_db, principal, employeeId,
            attempt.Intent.EquipmentId, attemptId, default) ??
            throw new UnauthorizedAccessException("Серверная попытка настройки недоступна.");
    }

    private async Task<Employee> RequirePhoneSyncActorAsync(string? principal) =>
        await Employee.GetCurrentAsync(_db, principal) ??
        throw new UnauthorizedAccessException("Сотрудник для синхронизации с АТС не подтверждён.");

    private Task<PhoneSyncAttempt> DispatchConfigureAsync(string? principal, EquipmentAtsSnapshot snapshot,
        Func<PhoneSyncAttempt, Task>? checkpoint, CancellationToken ct) =>
        DispatchEquipmentAtsAsync(principal, snapshot.Header, snapshot.Steps, ConfigureAttempt(snapshot), checkpoint, ct);

    private async Task<PhoneSyncAttempt> DispatchEquipmentAtsAsync(string? principal, EquipmentAtsHeader header,
        IReadOnlyList<EquipmentAtsStep> steps, PhoneSyncAttempt attempt, Func<PhoneSyncAttempt, Task>? checkpoint,
        CancellationToken ct)
    {
        if (attempt.Abandoned || attempt.Operations.Any(result => result.IsUnknown))
            return attempt;

        var provider = new PhoneFormService(_db);
        var results = attempt.Operations.ToArray();
        for (var i = 0; i < steps.Count; i++)
        {
            var step = steps[i];
            if (step.State == EquipmentAtsStep.Confirmed) continue;
            ct.ThrowIfCancellationRequested();
            if (checkpoint is not null)
            {
                var before = results.ToArray();
                before[i] = before[i] with { Phase = PhoneSyncPhase.Unknown };
                await checkpoint(attempt with { Operations = before });
            }
            try
            {
                results[i] = await provider.SynchronizeEquipmentWorkflowStepAsync(principal, header, step, ct);
            }
            catch (EquipmentAtsContextChangedException error)
            {
                var abandoned = header.Origin == (byte)EquipmentPhoneSyncOrigin.EquipmentSave
                    ? await EquipmentAtsWorkflow.TryAbandonSavedAsync(_db, principal, header.EmployeeId, header.EquipmentId, header.AttemptId, ct)
                    : await EquipmentAtsWorkflow.TryAbandonUndispatchedConfigureAsync(_db, principal,
                        header.EmployeeId, header.EquipmentId, header.AttemptId, ct);
                results[i] = results[i] with { Phase = PhoneSyncPhase.Rejected, Error = error.Message };
                return attempt with { Operations = results, Abandoned = abandoned };
            }

            if (checkpoint is not null)
                await checkpoint(attempt with { Operations = results.ToArray() });
            if (results[i].Phase != PhoneSyncPhase.Succeeded) break;
        }
        return attempt with { Operations = results };
    }

    private static PhoneSyncAttempt ConfigureAttempt(EquipmentAtsSnapshot snapshot)
    {
        var header = snapshot.Header;
        var intent = new EquipmentPhoneSyncIntent(header.AttemptId.ToString("N"), header.EquipmentId,
            EquipmentPhoneSyncOrigin.ExplicitConfigure, header.ExpectedChangedAtUtc, header.OwnerId, (int)ТипыОборудования.IP_телефон, [], []);
        return ToPhoneSyncAttempt(intent, header, snapshot.Steps);
    }

    private static PhoneSyncAttempt ToPhoneSyncAttempt(EquipmentPhoneSyncIntent intent, EquipmentAtsHeader header,
        IReadOnlyList<EquipmentAtsStep> steps)
    {
        var operations = steps.Select(step => new PhoneSyncOperationResult(intent.AttemptId,
            new PhoneSyncOperation((PhoneSyncKind)step.Kind, step.TargetId.ToString(CultureInfo.InvariantCulture), step.Deleting),
            step.State == EquipmentAtsStep.Confirmed ? PhoneSyncPhase.Succeeded :
            step.State == EquipmentAtsStep.Dispatching ? PhoneSyncPhase.Unknown : PhoneSyncPhase.NotSent)).ToArray();
        return new PhoneSyncAttempt(intent, operations) { Abandoned = header.State == EquipmentAtsWorkflow.Abandoned };
    }
}
