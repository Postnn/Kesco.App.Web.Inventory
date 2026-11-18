using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Операции чтения и сохранения основной записи оборудования.</summary>
public class EquipmentFormService
{
    private readonly DbManager _db;

    /// <summary>Создаёт сервис с подключением текущего Blazor circuit.</summary>
    public EquipmentFormService(DbManager db) => _db = db;

    /// <summary>Загружает основную запись оборудования для редактирования.</summary>
    public virtual async Task<Equipment?> GetAsync(int id) =>
        (await _db.QueryAsync<Equipment>(SQLQueries.SELECT_ID_Оборудование, new { Id = id }))
            .SingleOrDefault();

    /// <summary>Сохраняет 15 основных полей существующего оборудования.</summary>
    public virtual Task UpdateAsync(Equipment equipment) => equipment.UpdateAsync(_db);

    /// <summary>Загружает телефонные связи оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentPhoneLink>> GetPhonesAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentPhoneLink>(
            SQLQueries.SELECT_ТелефонныеНомераОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Определяет доступное оборудование подключения аналогового номера.</summary>
    public virtual async Task<int?> GetConnectionEquipmentIdAsync(int phoneId, int? employeeId, int locationId) =>
        (await _db.QueryAsync<int?>(SQLQueries.SELECT_ТелефонныйНомерОборудованиеПодключения,
            new { PhoneId = phoneId, EmployeeId = employeeId, LocationId = locationId })).SingleOrDefault();

    /// <summary>Загружает историю расположений оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentLocationPeriod>> GetLocationsAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentLocationPeriod>(
            SQLQueries.SELECT_ОборудованиеРасположение, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет наличие действующего ответственного.</summary>
    public virtual async Task<bool> HasCurrentResponsibleAsync(int equipmentId) =>
        await _db.ExecuteScalarAsync<int>(
            SQLQueries.SELECT_ОборудованиеЕстьТекущийОтветственный,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text) > 0;

    /// <summary>Добавляет расположение и при необходимости ответственного.</summary>
    public virtual Task AddLocationAsync(
        EquipmentLocationPeriod location,
        EquipmentResponsiblePeriod? responsible = null) =>
        location.InsertAsync(_db, responsible);

    /// <summary>Обновляет срок возврата и примечания расположения.</summary>
    public virtual Task UpdateLocationAsync(EquipmentLocationPeriod location) => location.UpdateAsync(_db);

    /// <summary>Загружает историю ответственных сотрудников.</summary>
    public virtual async Task<IReadOnlyList<EquipmentResponsiblePeriod>> GetResponsiblesAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentResponsiblePeriod>(
            SQLQueries.SELECT_ОборудованиеСотрудники, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Добавляет нового ответственного.</summary>
    public virtual Task IssueResponsibleAsync(EquipmentResponsiblePeriod responsible) => responsible.InsertAsync(_db);

    /// <summary>Обновляет срок возврата, примечания и окончание ответственного.</summary>
    public virtual Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible) => responsible.UpdateAsync(_db);

    /// <summary>Изымает оборудование и атомарно сохраняет связанные записи.</summary>
    public virtual Task WithdrawResponsibleAsync(
        EquipmentResponsiblePeriod current,
        EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location) =>
        current.WithdrawAsync(_db, next, location);

    /// <summary>Загружает учётные записи без паролей.</summary>
    public virtual async Task<IReadOnlyList<EquipmentAccount>> GetAccountsAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentAccount>(
            SQLQueries.SELECT_ОборудованиеУчётныеЗаписи, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает пароль одной записи после серверной проверки роли.</summary>
    public virtual async Task<string> GetAccountPasswordAsync(int equipmentId, string login)
    {
        if (!await HasRoleAsync(43))
            throw new UnauthorizedAccessException("Недостаточно прав для просмотра пароля.");
        return await _db.ExecuteScalarAsync<string>(
            SQLQueries.SELECT_ОборудованиеУчётнаяЗаписьПароль,
            new { EquipmentId = equipmentId, Login = login }, commandType: CommandType.Text) ?? string.Empty;
    }

    /// <summary>Добавляет учётную запись.</summary>
    public virtual Task AddAccountAsync(EquipmentAccount account) => account.InsertAsync(_db);

    /// <summary>Обновляет учётную запись.</summary>
    public virtual Task UpdateAccountAsync(EquipmentAccount account) => account.UpdateAsync(_db);

    /// <summary>Удаляет учётную запись.</summary>
    public virtual Task DeleteAccountAsync(EquipmentAccount account) => account.DeleteAsync(_db);

    /// <summary>Загружает сведения SIM без PIN/PUK.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSimCard>> GetSimCardsAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentSimCard>(
            SQLQueries.SELECT_SIMКарты_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает PIN/PUK после серверной проверки роли.</summary>
    public virtual async Task<EquipmentSimSecrets> GetSimSecretsAsync(int equipmentId)
    {
        if (!await HasRoleAsync(42))
            throw new UnauthorizedAccessException("Недостаточно прав для просмотра кодов SIM.");
        return (await _db.QueryAsync<EquipmentSimSecrets>(
            SQLQueries.SELECT_SIMКарта_Секреты, new { EquipmentId = equipmentId })).SingleOrDefault()
            ?? new EquipmentSimSecrets();
    }

    /// <summary>Ищет другую активную SIM с тем же номером.</summary>
    public virtual async Task<int?> GetDuplicateSimIdAsync(int equipmentId, string phoneNumber) =>
        await _db.ExecuteScalarAsync<int?>(SQLQueries.SELECT_SimКарты_Другая_С_Тем_Же_Номером,
            new { EquipmentId = equipmentId, PhoneNumber = phoneNumber }, commandType: CommandType.Text);

    /// <summary>Атомарно списывает подтверждённый дубликат и сохраняет SIM.</summary>
    public virtual Task SaveSimAsync(EquipmentSimCard sim, int? duplicateEquipmentId = null)
    {
        var validation = sim.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        return sim.SaveAsync(_db, duplicateEquipmentId);
    }

    /// <summary>Удаляет только сведения SIM.</summary>
    public virtual Task DeleteSimAsync(int equipmentId) => _db.ExecuteAsync(
        SQLQueries.DELETE_ID_SIMКарты, new { EquipmentId = equipmentId }, commandType: CommandType.Text);

    /// <summary>Возвращает лицо сотрудника для заполнения ПДн.</summary>
    public virtual async Task<int?> GetEmployeePersonIdAsync(int employeeId) =>
        await _db.ExecuteScalarAsync<int?>(SQLQueries.SELECT_ЛицоСотрудника,
            new { EmployeeId = employeeId }, commandType: CommandType.Text);

    /// <summary>Загружает дополнительные услуги текущей SIM.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
        int equipmentId, bool showHistory, bool showRequired) =>
        (await _db.QueryAsync<EquipmentSimService>(SQLQueries.SELECT_SIMКартыДопУслугиСотОператора_КодОборудования,
            new { EquipmentId = equipmentId, ShowHistory = showHistory ? 1 : 0, ShowRequired = showRequired ? 1 : 0 })).ToList();

    /// <summary>Загружает типы дополнительных услуг.</summary>
    public virtual async Task<IReadOnlyList<SimServiceType>> GetSimServiceTypesAsync() =>
        (await _db.QueryAsync<SimServiceType>(SQLQueries.SELECT_ТипыДопУслугСотОператора)).ToList();

    /// <summary>Проверяет и сохраняет дополнительную услугу SIM.</summary>
    public virtual Task SaveSimServiceAsync(EquipmentSimService service)
    {
        var validation = service.Validate(DateTime.Today);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        return service.SaveAsync(_db);
    }

    /// <summary>Загружает лицензию оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentLicense>> GetLicensesAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentLicense>(
            SQLQueries.SELECT_Soft_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает типы лицензирования.</summary>
    public virtual async Task<IReadOnlyList<LicenseType>> GetLicenseTypesAsync() =>
        (await _db.QueryAsync<LicenseType>(SQLQueries.SELECT_ТипыЛицензирования)).ToList();

    /// <summary>Проверяет и сохраняет лицензию.</summary>
    public virtual Task SaveLicenseAsync(EquipmentLicense license, LicenseType? type)
    {
        var validation = license.Validate(type);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        return license.SaveAsync(_db);
    }

    /// <summary>Проверяет роль текущего пользователя по конкретному лицу.</summary>
    public virtual async Task<bool> HasRoleForPersonAsync(int roleId, int? personId) =>
        await _db.ExecuteScalarAsync<int>(SQLQueries.SELECT_ТекущийПользовательИмеетРольПоЛицу,
            new { RoleId = roleId, PersonId = personId }, commandType: CommandType.Text) == 1;

    /// <summary>Загружает историю дневных конфигураций компьютера.</summary>
    public virtual async Task<IReadOnlyList<EquipmentComputerSnapshot>> GetComputerHistoryAsync(int equipmentId)
    {
        var history = (await _db.QueryAsync<EquipmentComputerSnapshot>(
            SQLQueries.SELECT_Компьютеры_КодОборудования, new { EquipmentId = equipmentId })).ToList();
        foreach (var snapshot in history)
            snapshot.ParseDetails();
        return history;
    }

    /// <summary>Загружает стандартные конфигурации.</summary>
    public virtual async Task<IReadOnlyList<StandardConfiguration>> GetStandardConfigurationsAsync() =>
        (await _db.QueryAsync<StandardConfiguration>(SQLQueries.SELECT_СтандартныеКонфигурации)).ToList();

    /// <summary>Добавляет или обновляет дневной снимок компьютера.</summary>
    public virtual Task SaveComputerAsync(EquipmentComputerSnapshot snapshot) => snapshot.SaveAsync(_db);

    /// <summary>Атомарно сохраняет основную запись и телефонный diff.</summary>
    public virtual Task UpdateWithPhonesAsync(
        Equipment equipment,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current) =>
        equipment.UpdateWithPhonesAsync(_db, original, current);

    /// <summary>Синхронизирует изменённые номера и оборудование с подтверждёнными АТС.</summary>
    public virtual async Task<IReadOnlyList<string>> SynchronizePhonesAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        var errors = new List<string>();
        var currentIds = current.Select(item => item.PhoneId).ToHashSet();
        var phones = original.Select(item => (item.PhoneId, Deleted: !currentIds.Contains(item.PhoneId)))
            .Concat(current.Select(item => (item.PhoneId, Deleted: false)))
            .GroupBy(item => item.PhoneId)
            .Select(group => (PhoneId: group.Key, Deleted: group.Last().Deleted))
            .OrderBy(item => item.PhoneId);
        var equipmentIds = original.Select(item => item.ConnectionEquipmentId)
            .Concat(current.Select(item => item.ConnectionEquipmentId))
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Append(equipmentId)
            .Distinct()
            .ToList();
        var needsCcm = false;
        foreach (var phone in phones)
        {
            var lookup = (await _db.QueryAsync<PhoneNumberLookup>(
                SQLQueries.SELECT_ТелефонныйНомерПоИдентификатору, new { Id = phone.PhoneId })).SingleOrDefault();
            if (lookup is null)
            {
                errors.Add($"Телефонный номер #{phone.PhoneId} не найден для синхронизации.");
                continue;
            }
            needsCcm |= lookup.StationId == 0;
            if (string.IsNullOrWhiteSpace(lookup.XorcomName))
                continue;
            var parameters = new Dapper.DynamicParameters();
            parameters.Add("@КодТелефонногоНомера", phone.PhoneId, DbType.Int32);
            var hasDeletedParameter = await _db.ExecuteScalarAsync<int>(
                SQLQueries.SELECT_ПроцедураИмеетПараметр,
                new { ProcedureName = SQLQueries.SP_PhoneSync_Xorcom, ParameterName = "@Удаление" },
                commandType: CommandType.Text) > 0;
            if (hasDeletedParameter)
                parameters.Add("@Удаление", phone.Deleted ? 1 : 0, DbType.Int32);
            parameters.Add("@RETURN_VALUE", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
            await _db.ExecuteAsync(SQLQueries.SP_PhoneSync_Xorcom, parameters);
            var returnCode = parameters.Get<int>("@RETURN_VALUE");
            if (returnCode != 0)
                errors.Add($"Xorcom: номер #{phone.PhoneId}, код ошибки {returnCode}.");
        }
        if (needsCcm)
        {
            foreach (var id in equipmentIds)
            {
                var parameters = new Dapper.DynamicParameters();
                parameters.Add("@КодОборудования", id, DbType.Int32);
                parameters.Add("@RETURN_VALUE", dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
                await _db.ExecuteAsync(SQLQueries.SP_PhoneSync_Ccm, parameters);
                var returnCode = parameters.Get<int>("@RETURN_VALUE");
                if (returnCode != 0)
                    errors.Add($"CCM: оборудование #{id}, код ошибки {returnCode}.");
            }
        }
        return errors;
    }

    /// <summary>Проверяет роль текущего пользователя с учётом замещений.</summary>
    public virtual async Task<bool> HasRoleAsync(int roleId) =>
        await _db.ExecuteScalarAsync<int>(
            SQLQueries.SELECT_ТекущийПользовательИмеетРоль,
            new { RoleId = roleId },
            commandType: CommandType.Text) == 1;
}
