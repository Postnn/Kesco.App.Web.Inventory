using System.Globalization;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

public partial class EquipmentFormService
{
    /// <summary>Настраивает сохранённый телефон по существующим связям.</summary>
    public virtual Task<PhoneSyncAttempt> ConfigurePhoneEquipmentAttemptAsync(int equipmentId,
        Func<PhoneSyncAttempt, Task>? checkpoint = null) =>
        ConfigureCurrentPhoneAsync(equipmentId, null, Guid.NewGuid(), checkpoint, default);

    /// <summary>Настраивает телефон в контексте текущего сотрудника и открытого редактора.</summary>
    public virtual Task<PhoneSyncAttempt> ConfigurePhoneEquipmentAttemptAsync(int equipmentId,
        int employeeId, Guid attemptId, CancellationToken ct) =>
        ConfigureCurrentPhoneAsync(equipmentId, employeeId, attemptId, null, ct);

    private async Task<PhoneSyncAttempt> ConfigureCurrentPhoneAsync(int equipmentId, int? employeeId, Guid attemptId,
        Func<PhoneSyncAttempt, Task>? checkpoint, CancellationToken ct)
    {
        var equipment = await ReadMainEquipmentAsync(equipmentId, ct) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        await EnsureConfigureActorAsync(equipment, employeeId, ct);
        var phones = await GetPhonesAsync(equipmentId);
        ct.ThrowIfCancellationRequested();
        if (_phoneSyncAttempts.Values.Any(attempt => attempt.Intent.EquipmentId == equipmentId &&
            attempt.Intent.Origin == EquipmentPhoneSyncOrigin.ExplicitConfigure &&
            (attempt.PlanningError is not null || attempt.Operations.Any(step => step.IsUnknown || step.CanRetry))))
            throw new InvalidOperationException("Сначала сверьте предыдущую попытку настройки.");

        var intent = CreatePhoneIntent(equipmentId, EquipmentPhoneSyncOrigin.ExplicitConfigure,
            equipment.ChangedAtUtc, equipment.OwnerId, equipment.EquipmentTypeId, [], phones)
            with { AttemptId = attemptId.ToString("N") };
        _confirmedPhoneIntents[intent.AttemptId] = intent;
        return await PlanPhoneSyncAttemptAsync(intent, previous: null, checkpoint);
    }

    private async Task EnsureConfigureActorAsync(Equipment equipment, int? employeeId, CancellationToken ct)
    {
        if (equipment.EquipmentTypeId != (int)ТипыОборудования.IP_телефон)
            throw new InvalidOperationException("Команда доступна только для сохранённого IP-телефона.");
        if (employeeId is null)
        {
            if (!await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_оборудования, equipment.OwnerId))
                throw new UnauthorizedAccessException("Недостаточно прав для настройки оборудования владельца.");
            return;
        }

        var actor = await Employee.GetCurrentWithRolesAsync(_db, await GetWindowsPrincipalAsync(), ct);
        if (actor is null || actor.Id != employeeId ||
            !actor.HasRole(Роли.Специалист_по_инвентаризации_оборудования, EmployeeRoleScope.PersonOrGlobal, equipment.OwnerId))
            throw new UnauthorizedAccessException("Недостаточно прав для настройки оборудования владельца.");
    }

}
