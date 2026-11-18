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

    /// <summary>Возвращает конфигурацию IP-телефона после проверки роли 102.</summary>
    public virtual async Task<string> GetPhoneConfigAsync(string mac)
    {
        if (!await HasRoleAsync(102))
            throw new UnauthorizedAccessException("Недостаточно прав для получения конфигурации телефона.");
        var parameters = new Dapper.DynamicParameters();
        parameters.Add("@MACадрес", mac, System.Data.DbType.String, size: 12);
        parameters.Add("@КонфигурацияIpТелефона", dbType: System.Data.DbType.String,
            direction: System.Data.ParameterDirection.Output, size: -1);
        await _db.ExecuteAsync("dbo.sp_КонфигурацияIpТелефона", parameters);
        return parameters.Get<string>("@КонфигурацияIpТелефона") ?? string.Empty;
    }

    /// <summary>Возвращает счётчик подключений рабочей сети.</summary>
    public virtual async Task<int> GetNetworkAttachmentCountAsync(int equipmentId) =>
        await _db.ExecuteScalarAsync<int>(SQLQueries.SELECT_ID_ConnectedWorkNet,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text);

    /// <summary>Подключает оборудование к рабочей сети.</summary>
    public virtual Task AttachNetworkAsync(int equipmentId) => _db.ExecuteAsync(
        SQLQueries.INSERT_Net, new { КодОборудования = equipmentId }, commandType: CommandType.Text);

    /// <summary>Отключает оборудование от рабочей сети.</summary>
    public virtual Task DetachNetworkAsync(int equipmentId) => _db.ExecuteAsync(
        SQLQueries.UPDATE_Net, new { КодОборудования = equipmentId }, commandType: CommandType.Text);

    /// <summary>Загружает UPS, подключённые к серверу.</summary>
    public virtual async Task<IReadOnlyList<UpsServerLink>> GetUpsLinksAsync(int serverEquipmentId) =>
        (await _db.QueryAsync<UpsServerLink>(
            SQLQueries.SELECT_ID_vwПодключенияUPS, new { ServerEquipmentId = serverEquipmentId })).ToList();

    /// <summary>Быстро подключает UPS к серверу.</summary>
    public virtual Task AddUpsLinkAsync(int upsEquipmentId, int serverEquipmentId) =>
        _db.ExecuteAsync(SQLQueries.INSERT_СерверПодключенияUPSОборудование,
            new { КодОборудованияUPS = upsEquipmentId, КодОборудованияСервера = serverEquipmentId },
            commandType: CommandType.Text);

    /// <summary>Удаляет связь UPS и сервера.</summary>
    public virtual Task DeleteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId) =>
        _db.ExecuteAsync(SQLQueries.DELETE_СерверПодключенияUPS,
            new { КодОборудованияUPS = upsEquipmentId, КодОборудованияСервера = serverEquipmentId },
            commandType: CommandType.Text);

    /// <summary>Загружает серверы выбранного UPS.</summary>
    public virtual async Task<IReadOnlyList<UpsServerLink>> GetUpsServersAsync(int upsEquipmentId) =>
        (await _db.QueryAsync<UpsServerLink>(
            SQLQueries.SELECT_ID_СервераПодключенияUPS, new { UpsEquipmentId = upsEquipmentId })).ToList();

    /// <summary>Загружает тесты выбранного UPS.</summary>
    public virtual async Task<IReadOnlyList<UpsTest>> GetUpsTestsAsync(int upsEquipmentId) =>
        (await _db.QueryAsync<UpsTest>(
            SQLQueries.SELECT_ID_ТестыПодключенияUPS, new { UpsEquipmentId = upsEquipmentId })).ToList();

    /// <summary>Сохраняет diff серверов и тестов одной транзакцией.</summary>
    public virtual Task SaveUpsDetailsAsync(
        int upsEquipmentId,
        IReadOnlyCollection<UpsServerLink> originalServers,
        IReadOnlyCollection<UpsServerLink> currentServers,
        IReadOnlyCollection<UpsTest> originalTests,
        IReadOnlyCollection<UpsTest> currentTests)
    {
        if (upsEquipmentId <= 0)
            throw new InvalidOperationException("Подключение UPS отсутствует в БД.");
        return UpsServerLink.SaveDetailsAsync(
            _db, upsEquipmentId, originalServers, currentServers, originalTests, currentTests);
    }

    /// <summary>Загружает последнюю инвентаризацию оборудования.</summary>
    public virtual async Task<EquipmentActualInventory?> GetLastInventoryAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentActualInventory>(
            SQLQueries.SELECT_ID_РеальноеРасположениеОборудования_ПоследнееРасположение,
            new { EquipmentId = equipmentId })).SingleOrDefault();

    /// <summary>Загружает основные сведения маршрутизатора.</summary>
    public virtual async Task<EquipmentRouterInfo?> GetRouterInfoAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentRouterInfo>(
            SQLQueries.SELECT_Routers_КодОборудования, new { EquipmentId = equipmentId })).SingleOrDefault();

    /// <summary>Загружает инвентаризацию маршрутизатора.</summary>
    public virtual async Task<IReadOnlyList<EquipmentRouterInventoryItem>> GetRouterInventoryAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentRouterInventoryItem>(
            SQLQueries.SELECT_КодОборудования_RouterInventory, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет роли 66/68 и ведение бухгалтерского учёта владельцем.</summary>
    public virtual async Task<bool> CanUseAccountingAsync(int? personId)
    {
        if (personId is null or <= 0)
            return false;
        var hasRole = await HasRoleForPersonAsync(66, personId) ||
                      await HasRoleForPersonAsync(68, personId);
        if (!hasRole)
            return false;
        return await _db.ExecuteScalarAsync<int>(SQLQueries.SELECT_ЛицоВедетБухгалтерскийУчет,
            new { PersonId = personId.Value }, commandType: CommandType.Text) == 1;
    }

    /// <summary>Загружает историю бухгалтерского учёта.</summary>
    public virtual async Task<IReadOnlyList<EquipmentAccounting>> GetAccountingAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentAccounting>(
            SQLQueries.SELECT_ОборудованиеБухУчета_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет права и сохраняет запись бухгалтерского учёта.</summary>
    public virtual async Task SaveAccountingAsync(EquipmentAccounting accounting)
    {
        if (!await CanUseAccountingAsync(accounting.OwnerId))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения бухгалтерского учёта.");
        var validation = accounting.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await accounting.SaveAsync(_db);
    }

    /// <summary>Проверяет права и удаляет только последнюю запись бухгалтерского учёта.</summary>
    public virtual async Task DeleteAccountingAsync(int equipmentId, int? ownerId)
    {
        if (!await CanUseAccountingAsync(ownerId))
            throw new UnauthorizedAccessException("Недостаточно прав для удаления бухгалтерского учёта.");
        await _db.ExecuteAsync(SQLQueries.DELETE_КодОборудования_ОборудованиеБухУчета,
            new { КодОборудования = equipmentId }, commandType: CommandType.Text);
    }

    /// <summary>Загружает установленное ПО оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSoftwareInstallation>> GetSoftwareInstallationsAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentSoftwareInstallation>(
            SQLQueries.SELECT_ПО_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет и сохраняет установку ПО.</summary>
    public virtual Task SaveSoftwareInstallationAsync(EquipmentSoftwareInstallation installation)
    {
        var validation = installation.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        return installation.SaveAsync(_db);
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

    /// <summary>Загружает историю сетевых имён от новых записей к старым.</summary>
    public virtual async Task<IReadOnlyList<EquipmentNetworkNameHistory>> GetNetworkNameHistoryAsync(int equipmentId) =>
        (await _db.QueryAsync<EquipmentNetworkNameHistory>(
            SQLQueries.SELECT_ОборудованиеСетевыеИмена, new { EquipmentId = equipmentId })).ToList();

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
            .Distinct()
            .ToList();
        if (equipmentIds.Count == 0)
            equipmentIds.Add(equipmentId);
        var needsCcm = false;
        var hasXorcom = false;
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
            hasXorcom = true;
            var parameters = new Dapper.DynamicParameters();
            parameters.Add("@КодТелефонногоНомера", phone.PhoneId, DbType.Int32);
            var hasDeletedParameter = await _db.ExecuteScalarAsync<int>(
                SQLQueries.SELECT_ПроцедураИмеетПараметр,
                new { ProcedureName = SQLQueries.SP_PhoneSync_Xorcom, ParameterName = "@Удаление" },
                commandType: CommandType.Text) > 0;
            if (hasDeletedParameter)
                parameters.Add("@Удаление", phone.Deleted ? 1 : 0, DbType.Int32);
            parameters.Add("@RETURN_VALUE", value: 0, dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
            await _db.ExecuteAsync(SQLQueries.SP_PhoneSync_Xorcom, parameters);
            var returnCode = parameters.Get<int>("@RETURN_VALUE");
            if (returnCode != 0)
                errors.Add($"Xorcom: номер #{phone.PhoneId}, код ошибки {returnCode}.");
        }
        if (needsCcm && !hasXorcom)
        {
            foreach (var id in equipmentIds)
            {
                var parameters = new Dapper.DynamicParameters();
                parameters.Add("@КодОборудования", id, DbType.Int32);
                parameters.Add("@RETURN_VALUE", value: 0, dbType: DbType.Int32, direction: ParameterDirection.ReturnValue);
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
