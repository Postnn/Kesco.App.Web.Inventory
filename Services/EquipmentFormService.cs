using System.Data;
using Clayzor.Lib.DALC;
using Clayzor.Lib.Entities;
using Clayzor.Lib.Entities.Inventory;
using Dapper;
using Microsoft.AspNetCore.Components.Authorization;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Операции чтения и сохранения основной записи оборудования.</summary>
public class EquipmentFormService
{
    private readonly DbManager _db;
    private readonly ICurrentUserPrincipalProvider? _principalProvider;
    private readonly Dictionary<int, (string Original, string Current)> _confirmedPhoneDiffs = new();

    /// <summary>Создаёт сервис без UI-авторизации для тестов и небезопасных для ролей вызовов.</summary>
    public EquipmentFormService(DbManager db) : this(db, (ICurrentUserPrincipalProvider?)null) { }

    /// <summary>Создаёт сервис с подключением и серверной Windows-идентичностью текущего circuit.</summary>
    public EquipmentFormService(DbManager db, AuthenticationStateProvider? authenticationStateProvider)
        : this(db, authenticationStateProvider is null ? null : new CircuitCurrentUserPrincipalProvider(authenticationStateProvider)) { }

    /// <summary>Создаёт сервис с явным источником доверенной идентичности HTTP-запроса или circuit.</summary>
    public EquipmentFormService(DbManager db, ICurrentUserPrincipalProvider? principalProvider)
    {
        _db = db;
        _principalProvider = principalProvider;
    }

    /// <summary>Читает независимые секции формы одним SQL batch на соединении текущего circuit.</summary>
    public virtual async Task<EquipmentFormReadResult> GetFormDataAsync(EquipmentFormReadRequest request)
    {
        if (request.IncludeAccounts)
            await EnsureAccountAccessAsync(false);

        var statements = new List<string>();
        void Add(string sql) => statements.Add(sql.TrimEnd().TrimEnd(';') + ";");

        if (request.IncludePhones) Add(SQLQueries.SELECT_ТелефонныеНомераОборудования);
        if (request.IncludeHotelPhoneCount) Add(SQLQueries.SELECT_UniquePhoneNumberInHotel);
        Add(SQLQueries.SELECT_ОборудованиеРасположение);
        Add(SQLQueries.SELECT_ОборудованиеСотрудники);
        if (request.IncludeAccounts) Add(SQLQueries.SELECT_ОборудованиеУчётныеЗаписи);
        if (request.IncludeComputer)
        {
            Add(SQLQueries.SELECT_Компьютеры_КодОборудования);
            Add(SQLQueries.SELECT_СтандартныеКонфигурации);
        }
        if (request.IncludeSim)
        {
            Add(SQLQueries.SELECT_SIMКарты_КодОборудования);
            Add(SQLQueries.SELECT_SIMКартыДопУслугиСотОператора_КодОборудования);
            Add(SQLQueries.SELECT_ТипыДопУслугСотОператора);
        }
        if (request.IncludeLicense)
        {
            Add(SQLQueries.SELECT_Soft_КодОборудования);
            Add(SQLQueries.SELECT_ТипыЛицензирования);
        }
        if (request.IncludeComputer) Add(SQLQueries.SELECT_ПО_КодОборудования);
        Add(SQLQueries.SELECT_ОборудованиеБухУчета_КодОборудования);
        Add(SQLQueries.SELECT_Routers_КодОборудования);
        Add(SQLQueries.SELECT_КодОборудования_RouterInventory);
        Add(SQLQueries.SELECT_ID_vwПодключенияUPS);
        Add(SQLQueries.SELECT_ID_РеальноеРасположениеОборудования_ПоследнееРасположение);
        Add(SQLQueries.SELECT_ID_ConnectedWorkNet);
        if (request.IncludeNetworkName && !string.IsNullOrWhiteSpace(request.NetworkName))
            Add(SQLQueries.SELECT_NAME_vwЛокальнаяСеть);
        if (request.IncludeNetworkName) Add(SQLQueries.SELECT_ОборудованиеСетевыеИмена);

        var parameters = new
        {
            EquipmentId = request.EquipmentId,
            ServerEquipmentId = request.EquipmentId,
            ShowHistory = request.ShowServiceHistory ? 1 : 0,
            ShowRequired = request.ShowCompanyRequiredServices ? 1 : 0,
            СетевоеИмя = request.NetworkName ?? string.Empty
        };

        return await _db.RunAsync(async connection =>
        {
            using var reader = await connection.QueryMultipleAsync(string.Join("\n", statements), parameters);
            var result = new EquipmentFormReadResult();
            if (request.IncludePhones)
                result.Phones = (await reader.ReadAsync<EquipmentPhoneLink>()).ToList();
            if (request.IncludeHotelPhoneCount)
                result.HotelPhoneCount = await reader.ReadFirstOrDefaultAsync<int>();
            result.Locations = (await reader.ReadAsync<EquipmentLocationPeriod>()).ToList();
            result.Responsibles = (await reader.ReadAsync<EquipmentResponsiblePeriod>()).ToList();
            if (request.IncludeAccounts)
                result.Accounts = (await reader.ReadAsync<EquipmentAccount>()).ToList();
            if (request.IncludeComputer)
            {
                result.ComputerHistory = (await reader.ReadAsync<EquipmentComputerSnapshot>()).ToList();
                foreach (var snapshot in result.ComputerHistory)
                    snapshot.ParseDetails();
                result.Configurations = (await reader.ReadAsync<StandardConfiguration>()).ToList();
            }
            if (request.IncludeSim)
            {
                result.SimCards = (await reader.ReadAsync<EquipmentSimCard>()).ToList();
                result.SimServices = (await reader.ReadAsync<EquipmentSimService>()).ToList();
                result.SimServiceTypes = (await reader.ReadAsync<SimServiceType>()).ToList();
            }
            if (request.IncludeLicense)
            {
                result.Licenses = (await reader.ReadAsync<EquipmentLicense>()).ToList();
                result.LicenseTypes = (await reader.ReadAsync<LicenseType>()).ToList();
            }
            if (request.IncludeComputer)
                result.SoftwareInstallations = (await reader.ReadAsync<EquipmentSoftwareInstallation>()).ToList();
            result.Accounting = (await reader.ReadAsync<EquipmentAccounting>()).ToList();
            result.RouterInfo = await reader.ReadFirstOrDefaultAsync<EquipmentRouterInfo>();
            result.RouterInventory = (await reader.ReadAsync<EquipmentRouterInventoryItem>()).ToList();
            result.UpsLinks = (await reader.ReadAsync<UpsServerLink>()).ToList();
            result.LastInventory = await reader.ReadFirstOrDefaultAsync<EquipmentActualInventory>();
            result.NetworkAttachmentCount = await reader.ReadFirstOrDefaultAsync<int>();
            if (request.IncludeNetworkName && !string.IsNullOrWhiteSpace(request.NetworkName))
                result.NetworkPorts = (await reader.ReadAsync<EquipmentNetworkPortLink>()).ToList();
            if (request.IncludeNetworkName)
                result.NetworkNameHistory = (await reader.ReadAsync<EquipmentNetworkNameHistory>()).ToList();
            return result;
        });
    }

    /// <summary>Загружает основную запись оборудования для редактирования.</summary>
    public virtual async Task<Equipment?> GetAsync(int id) =>
        await _db.RunDbAsync(async connection =>
            (await connection.QueryAsync<Equipment>(SQLQueries.SELECT_ID_Оборудование, new { Id = id }))
                .SingleOrDefault());

    /// <summary>Проверяет роль 43 и сохраняет 15 основных полей существующего оборудования.</summary>
    public virtual async Task UpdateAsync(Equipment equipment)
    {
        await EnsureInventoryEditAsync();
        await ValidateEquipmentForSaveAsync(equipment);
        await equipment.UpdateAsync(_db);
    }

    /// <summary>Загружает телефонные связи оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentPhoneLink>> GetPhonesAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentPhoneLink>(
            SQLQueries.SELECT_ТелефонныеНомераОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Возвращает количество номеров, если текущее расположение является гостиничным номером.</summary>
    public virtual async Task<int> GetHotelPhoneCountAsync(int equipmentId) =>
        await ExecuteRequiredScalarAsync<int>(SQLQueries.SELECT_UniquePhoneNumberInHotel,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text);

    /// <summary>Определяет доступное оборудование подключения аналогового номера.</summary>
    public virtual async Task<int?> GetConnectionEquipmentIdAsync(int phoneId, int? employeeId, int locationId) =>
        (await _db.QueryRequiredAsync<int?>(SQLQueries.SELECT_ТелефонныйНомерОборудованиеПодключения,
            new { PhoneId = phoneId, EmployeeId = employeeId, LocationId = locationId })).SingleOrDefault();

    /// <summary>Загружает другое оборудование той же legacy-группы на выбранном рабочем месте без изменения данных.</summary>
    public virtual async Task<IReadOnlyList<EquipmentWorkplaceAdvisoryItem>> GetWorkplaceAdvisoryAsync(
        int equipmentId, int locationId) =>
        (await _db.QueryRequiredAsync<EquipmentWorkplaceAdvisoryItem>(
            SQLQueries.SELECT_ПредупреждениеОборудованиеНаРабочемМесте,
            new { Id = locationId, IT = 1, EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает историю расположений оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentLocationPeriod>> GetLocationsAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentLocationPeriod>(
            SQLQueries.SELECT_ОборудованиеРасположение, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет наличие действующего ответственного.</summary>
    public virtual async Task<bool> HasCurrentResponsibleAsync(int equipmentId) =>
        await ExecuteRequiredScalarAsync<int>(
            SQLQueries.SELECT_ОборудованиеЕстьТекущийОтветственный,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text) > 0;

    /// <summary>Проверяет роль 43 и добавляет расположение и при необходимости ответственного.</summary>
    public virtual async Task AddLocationAsync(
        EquipmentLocationPeriod location,
        EquipmentResponsiblePeriod? responsible = null)
    {
        await EnsureInventoryEditAsync();
        await location.InsertAsync(_db, responsible);
    }

    /// <summary>Проверяет роль 43 и обновляет срок возврата и примечания расположения.</summary>
    public virtual async Task UpdateLocationAsync(EquipmentLocationPeriod location)
    {
        await EnsureInventoryEditAsync();
        await location.UpdateAsync(_db);
    }

    /// <summary>Загружает историю ответственных сотрудников.</summary>
    public virtual async Task<IReadOnlyList<EquipmentResponsiblePeriod>> GetResponsiblesAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentResponsiblePeriod>(
            SQLQueries.SELECT_ОборудованиеСотрудники, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет роль 43 и добавляет нового ответственного.</summary>
    public virtual async Task IssueResponsibleAsync(EquipmentResponsiblePeriod responsible)
    {
        await EnsureInventoryEditAsync();
        await responsible.InsertAsync(_db);
    }

    /// <summary>Проверяет роль 43 и обновляет срок возврата, примечания и окончание ответственного.</summary>
    public virtual async Task UpdateResponsibleAsync(EquipmentResponsiblePeriod responsible)
    {
        await EnsureInventoryEditAsync();
        await responsible.UpdateAsync(_db);
    }

    /// <summary>Проверяет роль 43 и атомарно сохраняет изъятие и связанные записи.</summary>
    public virtual Task WithdrawResponsibleAsync(
        EquipmentResponsiblePeriod current,
        EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location) =>
        WithdrawResponsibleAsync(current, next, location, null);

    /// <summary>Проверяет срок возврата по локальному календарному дню начала следующего периода.</summary>
    public virtual async Task WithdrawResponsibleAsync(
        EquipmentResponsiblePeriod current,
        EquipmentResponsiblePeriod? next,
        EquipmentLocationPeriod? location,
        DateTime? nextFromLocalDate)
    {
        await EnsureInventoryEditAsync();
        var responsibles = await GetResponsiblesAsync(current.EquipmentId);
        var active = responsibles.Where(item => item.ToUtc is null).Take(2).ToList();
        if (active.Count != 1 || active[0].FromUtc != current.FromUtc || active[0].EmployeeId != current.EmployeeId)
            throw new InvalidOperationException(
                "Действующий ответственный изменился. Обновите данные и повторите операцию.");
        if (next is not null && next.EmployeeId == active[0].EmployeeId)
            throw new InvalidOperationException("Следующий ответственный должен отличаться от действующего.");
        if (next?.ReturnDate is not null)
        {
            if (nextFromLocalDate is null)
                throw new InvalidOperationException(
                    "Локальная календарная дата начала следующего периода не задана. Запись отменена.");
            if (next.ReturnDate.Value.Date < nextFromLocalDate.Value.Date)
                throw new InvalidOperationException("Срок возврата следующего ответственного не может быть раньше его выдачи.");
        }
        await current.WithdrawAsync(_db, next, location);
    }

    /// <summary>Загружает учётные записи без паролей после проверки роли 102.</summary>
    public virtual async Task<IReadOnlyList<EquipmentAccount>> GetAccountsAsync(int equipmentId)
    {
        await EnsureAccountAccessAsync(false);
        return (await _db.QueryRequiredAsync<EquipmentAccount>(
            SQLQueries.SELECT_ОборудованиеУчётныеЗаписи, new { EquipmentId = equipmentId })).ToList();
    }

    /// <summary>Загружает пароль одной записи после серверной проверки роли 102.</summary>
    public virtual async Task<string> GetAccountPasswordAsync(int equipmentId, string login)
    {
        await EnsureAccountAccessAsync(false);
        return await _db.RunDbAsync(async connection =>
        {
            var rows = (await connection.QueryAsync<string>(
                SQLQueries.SELECT_ОборудованиеУчётнаяЗаписьПароль,
                new { EquipmentId = equipmentId, Login = login }, commandType: CommandType.Text)).ToList();
            if (rows.Count == 0)
                throw new InvalidOperationException("Учётная запись не найдена или была удалена. Обновите список.");
            if (rows.Count > 1)
                throw new InvalidOperationException("Не удалось однозначно определить учётную запись. Обновите список.");
            return rows[0] ?? string.Empty;
        });
    }

    /// <summary>Добавляет учётную запись после проверки ролей 102 и 43 и фактического типа.</summary>
    public virtual async Task AddAccountAsync(EquipmentAccount account)
    {
        await EnsureAccountAccessAsync(true);
        if ((await GetRequiredChildWriteTypeAsync(account.EquipmentId)).MacAddressCount == 0)
            throw new InvalidOperationException("Учётные записи недоступны для фактического типа оборудования.");
        await account.InsertAsync(_db);
    }

    /// <summary>Обновляет учётную запись после проверки ролей 102 и 43 и фактического типа.</summary>
    public virtual async Task UpdateAccountAsync(EquipmentAccount account)
    {
        await EnsureAccountAccessAsync(true);
        if ((await GetRequiredChildWriteTypeAsync(account.EquipmentId)).MacAddressCount == 0)
            throw new InvalidOperationException("Учётные записи недоступны для фактического типа оборудования.");
        await account.UpdateAsync(_db);
    }

    /// <summary>Удаляет учётную запись после проверки ролей 102 и 43 и фактического типа.</summary>
    public virtual async Task DeleteAccountAsync(EquipmentAccount account)
    {
        await EnsureAccountAccessAsync(true);
        if ((await GetRequiredChildWriteTypeAsync(account.EquipmentId)).MacAddressCount == 0)
            throw new InvalidOperationException("Учётные записи недоступны для фактического типа оборудования.");
        await account.DeleteAsync(_db);
    }

    /// <summary>Загружает сведения SIM без PIN/PUK.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSimCard>> GetSimCardsAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentSimCard>(
            SQLQueries.SELECT_SIMКарты_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Строит URL детализации только для доступной SIM и актуального ответственного.</summary>
    public virtual async Task<string?> GetSimDetailUrlAsync(int equipmentId, string baseUri)
    {
        await EnsureTrustedIdentityAsync();
        var responsibles = (await _db.QueryRequiredAsync<EquipmentResponsiblePeriod>(
            SQLQueries.SELECT_ОборудованиеСотрудники, new { EquipmentId = equipmentId })).ToList();
        var sims = (await _db.QueryRequiredAsync<EquipmentSimCard>(
            SQLQueries.SELECT_SIMКарта_Детализация, new { EquipmentId = equipmentId })).ToList();
        if (sims.Count != 1)
            return null;
        var sim = sims[0];
        if (sim.ExecutorPersonId != (int)Лица.Билайн)
            return null;
        if (sim.IsSubordinate != 1)
        {
            var tarifficationAvailable = await ExecuteRequiredScalarAsync<int>(
                SQLQueries.SELECT_SIMДетализация_ЕстьТарификация, null, CommandType.Text);
            if (tarifficationAvailable != 1)
                return null;
            var availableByEmployee = await ExecuteRequiredScalarAsync<int>(
                SQLQueries.SELECT_SIMДетализация_Подчинённый, new { ContractId = sim.ContractId }, CommandType.Text);
            if (availableByEmployee != 1)
                return null;
        }
        var responsible = responsibles.Count == 1
            ? responsibles[0]
            : responsibles.SingleOrDefault(item => item.ToUtc is null);
        return responsible is null
            ? null
            : sim.BuildAccountingPhoneUri(baseUri, responsible.EmployeeName);
    }

    /// <summary>Загружает PIN/PUK после серверной проверки роли.</summary>
    public virtual async Task<EquipmentSimSecrets> GetSimSecretsAsync(int equipmentId)
    {
        if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
            throw new UnauthorizedAccessException("Недостаточно прав для просмотра кодов SIM.");
        return await _db.RunDbAsync(async connection =>
        {
            var secrets = (await connection.QueryAsync<EquipmentSimSecrets>(
                SQLQueries.SELECT_SIMКарта_Секреты, new { EquipmentId = equipmentId })).SingleOrDefault();
            if (secrets is null)
                throw new InvalidOperationException("Сведения SIM не найдены или были удалены. Обновите список.");
            return secrets;
        });
    }

    /// <summary>Ищет другую активную SIM с тем же номером.</summary>
    public virtual async Task<int?> GetDuplicateSimIdAsync(int equipmentId, string phoneNumber) =>
        await ExecuteRequiredScalarAsync<int?>(SQLQueries.SELECT_SimКарты_Другая_С_Тем_Же_Номером,
            new { EquipmentId = equipmentId, PhoneNumber = phoneNumber }, commandType: CommandType.Text);

    /// <summary>Проверяет роль 42 и фактический тип, атомарно списывает подтверждённый дубликат и сохраняет SIM.</summary>
    public virtual async Task SaveSimAsync(EquipmentSimCard sim, int? duplicateEquipmentId = null)
    {
        if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения SIM.");
        if ((await GetRequiredChildWriteTypeAsync(sim.EquipmentId)).HasSimCharacteristics == 0)
            throw new InvalidOperationException("Сведения SIM недоступны для фактического типа оборудования.");
        var validation = sim.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await sim.SaveAsync(_db, duplicateEquipmentId);
    }

    /// <summary>Проверяет роль 42 и фактический тип, затем удаляет только сведения SIM.</summary>
    public virtual async Task DeleteSimAsync(int equipmentId)
    {
        if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
            throw new UnauthorizedAccessException("Недостаточно прав для удаления SIM.");
        if ((await GetRequiredChildWriteTypeAsync(equipmentId)).HasSimCharacteristics == 0)
            throw new InvalidOperationException("Сведения SIM недоступны для фактического типа оборудования.");
        await _db.ExecuteAsync(
            SQLQueries.DELETE_ID_SIMКарты, new { EquipmentId = equipmentId }, commandType: CommandType.Text);
    }

    /// <summary>Возвращает лицо сотрудника для заполнения ПДн.</summary>
    public virtual async Task<int?> GetEmployeePersonIdAsync(int employeeId) =>
        await ExecuteRequiredScalarAsync<int?>(SQLQueries.SELECT_ЛицоСотрудника,
            new { EmployeeId = employeeId }, commandType: CommandType.Text);

    /// <summary>Проверяет выбранное лицо ПДн без изменения выбора и без блокировки сохранения.</summary>
    public virtual async Task<(bool IsEligible, string Message)> GetPersonalDataAdvisoryAsync(
        int equipmentId, int? contractId, int? personId, string technicalTariffs)
    {
        if (personId is null)
            return (true, string.Empty);
        if (contractId is null)
            return (false, "Сначала выберите договор связи; соответствие лица ПДн не проверено.");
        var eligibility = await ExecuteRequiredScalarAsync<int>(
            SQLQueries.SELECT_ЛицоПерсональныхДанныхСоответствие,
            new { EquipmentId = equipmentId, ContractId = contractId, PersonId = personId, TechnicalTariffs = technicalTariffs },
            commandType: CommandType.Text);
        return eligibility switch
        {
            1 => (true, string.Empty),
            -1 => (false, "Договор связи не найден. Сохранённое лицо ПДн не удалено; проверьте выбор."),
            _ => (false, "Лицо ПДн не соответствует договору, заказчику или активному техническому тарифу."),
        };
    }

    /// <summary>Загружает дополнительные услуги текущей SIM.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSimService>> GetSimServicesAsync(
        int equipmentId, bool showHistory, bool showRequired) =>
        (await _db.QueryRequiredAsync<EquipmentSimService>(SQLQueries.SELECT_SIMКартыДопУслугиСотОператора_КодОборудования,
            new { EquipmentId = equipmentId, ShowHistory = showHistory ? 1 : 0, ShowRequired = showRequired ? 1 : 0 })).ToList();

    /// <summary>Загружает типы дополнительных услуг.</summary>
    public virtual async Task<IReadOnlyList<SimServiceType>> GetSimServiceTypesAsync() =>
        (await _db.QueryRequiredAsync<SimServiceType>(SQLQueries.SELECT_ТипыДопУслугСотОператора)).ToList();

    /// <summary>Сохраняет услугу только после передачи календарной даты через согласованный контракт.</summary>
    public virtual Task SaveSimServiceAsync(EquipmentSimService service) =>
        SaveSimServiceAsync(service, null);

    /// <summary>Проверяет роль 42, фактический тип и даты услуги по календарному дню клиента.</summary>
    public virtual async Task SaveSimServiceAsync(EquipmentSimService service, DateTime? businessToday)
    {
        if (!await HasRoleAsync(Роли.Администратор_тарифов_связи))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения дополнительной услуги SIM.");
        if ((await GetRequiredChildWriteTypeAsync(service.EquipmentId)).HasSimCharacteristics == 0)
            throw new InvalidOperationException("Дополнительные услуги SIM недоступны для фактического типа оборудования.");
        if (businessToday is null)
            throw new InvalidOperationException("Календарная дата клиента не задана. Сохранение дополнительной услуги SIM отменено.");
        var validation = service.Validate(businessToday.Value);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await service.SaveAsync(_db);
    }

    /// <summary>Возвращает конфигурацию IP-телефона после проверки роли 102.</summary>
    public virtual async Task<string> GetPhoneConfigAsync(string mac)
    {
        if (!await HasRoleAsync(Роли.Дежурный_инженер))
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
        await ExecuteRequiredScalarAsync<int>(SQLQueries.SELECT_ID_ConnectedWorkNet,
            new { EquipmentId = equipmentId }, commandType: CommandType.Text);

    /// <summary>Проверяет компьютерный тип и роль 103, затем подключает оборудование к рабочей сети.</summary>
    public virtual async Task AttachNetworkAsync(int equipmentId)
    {
        await EnsureNetworkAccessAsync(equipmentId);
        await _db.ExecuteAsync(
            SQLQueries.INSERT_Net, new { КодОборудования = equipmentId }, commandType: CommandType.Text);
    }

    /// <summary>Проверяет компьютерный тип и роль 103, затем отключает оборудование от рабочей сети.</summary>
    public virtual async Task DetachNetworkAsync(int equipmentId)
    {
        await EnsureNetworkAccessAsync(equipmentId);
        await _db.ExecuteAsync(
            SQLQueries.UPDATE_Net, new { КодОборудования = equipmentId }, commandType: CommandType.Text);
    }

    /// <summary>Загружает UPS, подключённые к серверу.</summary>
    public virtual async Task<IReadOnlyList<UpsServerLink>> GetUpsLinksAsync(int serverEquipmentId) =>
        (await _db.QueryRequiredAsync<UpsServerLink>(
            SQLQueries.SELECT_ID_vwПодключенияUPS, new { ServerEquipmentId = serverEquipmentId })).ToList();

    /// <summary>Проверяет роль по владельцу и быстро подключает UPS к серверу.</summary>
    public virtual async Task AddUpsLinkAsync(int upsEquipmentId, int serverEquipmentId)
    {
        await EnsureUpsAccessAsync(serverEquipmentId);
        await _db.ExecuteAsync(SQLQueries.INSERT_СерверПодключенияUPSОборудование,
            new { КодОборудованияUPS = upsEquipmentId, КодОборудованияСервера = serverEquipmentId },
            commandType: CommandType.Text);
    }

    /// <summary>Проверяет роль по владельцу и удаляет связь UPS и сервера.</summary>
    public virtual async Task DeleteUpsLinkAsync(int upsEquipmentId, int serverEquipmentId)
    {
        await EnsureUpsAccessAsync(serverEquipmentId);
        await _db.ExecuteAsync(SQLQueries.DELETE_СерверПодключенияUPS,
            new { КодОборудованияUPS = upsEquipmentId, КодОборудованияСервера = serverEquipmentId },
            commandType: CommandType.Text);
    }

    /// <summary>Загружает серверы выбранного UPS.</summary>
    public virtual async Task<IReadOnlyList<UpsServerLink>> GetUpsServersAsync(int upsEquipmentId) =>
        (await _db.QueryRequiredAsync<UpsServerLink>(
            SQLQueries.SELECT_ID_СервераПодключенияUPS, new { UpsEquipmentId = upsEquipmentId })).ToList();

    /// <summary>Загружает тесты выбранного UPS.</summary>
    public virtual async Task<IReadOnlyList<UpsTest>> GetUpsTestsAsync(int upsEquipmentId) =>
        (await _db.QueryRequiredAsync<UpsTest>(
            SQLQueries.SELECT_ID_ТестыПодключенияUPS, new { UpsEquipmentId = upsEquipmentId })).ToList();

    /// <summary>Проверяет актуальные права на исходное оборудование и каждый изменяемый сервер.</summary>
    public virtual async Task SaveUpsDetailsAsync(
        int upsEquipmentId,
        int sourceServerEquipmentId,
        IReadOnlyCollection<UpsServerLink> originalServers,
        IReadOnlyCollection<UpsServerLink> currentServers,
        IReadOnlyCollection<UpsTest> originalTests,
        IReadOnlyCollection<UpsTest> currentTests)
    {
        if (upsEquipmentId <= 0 || sourceServerEquipmentId <= 0)
            throw new InvalidOperationException("Подключение UPS отсутствует в БД.");
        await EnsureTrustedIdentityAsync();
        await UpsServerLink.SaveDetailsAsync(
            _db, upsEquipmentId, sourceServerEquipmentId, originalServers, currentServers, originalTests, currentTests);
    }

    /// <summary>Загружает последнюю инвентаризацию оборудования.</summary>
    public virtual async Task<EquipmentActualInventory?> GetLastInventoryAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentActualInventory>(
            SQLQueries.SELECT_ID_РеальноеРасположениеОборудования_ПоследнееРасположение,
            new { EquipmentId = equipmentId })).SingleOrDefault();

    /// <summary>Загружает основные сведения маршрутизатора.</summary>
    public virtual async Task<EquipmentRouterInfo?> GetRouterInfoAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentRouterInfo>(
            SQLQueries.SELECT_Routers_КодОборудования, new { EquipmentId = equipmentId })).SingleOrDefault();

    /// <summary>Загружает инвентаризацию маршрутизатора.</summary>
    public virtual async Task<IReadOnlyList<EquipmentRouterInventoryItem>> GetRouterInventoryAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentRouterInventoryItem>(
            SQLQueries.SELECT_КодОборудования_RouterInventory, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет роли 66/68 и ведение бухгалтерского учёта владельцем.</summary>
    public virtual async Task<bool> CanUseAccountingAsync(int? personId)
    {
        if (personId is null or <= 0)
            return false;
        var hasRole = await HasRoleForPersonAsync(Роли.Бухгалтер, personId) ||
                      await HasRoleForPersonAsync(Роли.Главный_бухгалтер_1С, personId);
        if (!hasRole)
            return false;
        return await ExecuteRequiredScalarAsync<int>(SQLQueries.SELECT_ЛицоВедетБухгалтерскийУчет,
            new { PersonId = personId.Value }, commandType: CommandType.Text) == 1;
    }

    /// <summary>Загружает историю бухгалтерского учёта.</summary>
    public virtual async Task<IReadOnlyList<EquipmentAccounting>> GetAccountingAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentAccounting>(
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

    /// <summary>Атомарно удаляет подтверждённую запись, если она всё ещё последняя.</summary>
    public virtual async Task DeleteAccountingAsync(int equipmentId, int accountingId)
    {
        await EnsureTrustedIdentityAsync();
        await EquipmentAccounting.DeleteLatestAsync(_db, equipmentId, accountingId);
    }

    /// <summary>Загружает установленное ПО оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentSoftwareInstallation>> GetSoftwareInstallationsAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentSoftwareInstallation>(
            SQLQueries.SELECT_ПО_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Проверяет роль 43 и фактический тип, валидацию и сохраняет установку ПО.</summary>
    public virtual async Task SaveSoftwareInstallationAsync(EquipmentSoftwareInstallation installation)
    {
        await EnsureInventoryEditAsync();
        if ((await GetRequiredChildWriteTypeAsync(installation.EquipmentId)).HasComputerCharacteristics == 0)
            throw new InvalidOperationException("Установка ПО недоступна для фактического типа оборудования.");
        var validation = installation.Validate();
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await installation.SaveAsync(_db);
    }

    /// <summary>Загружает лицензию оборудования.</summary>
    public virtual async Task<IReadOnlyList<EquipmentLicense>> GetLicensesAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentLicense>(
            SQLQueries.SELECT_Soft_КодОборудования, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает типы лицензирования.</summary>
    public virtual async Task<IReadOnlyList<LicenseType>> GetLicenseTypesAsync() =>
        (await _db.QueryRequiredAsync<LicenseType>(SQLQueries.SELECT_ТипыЛицензирования)).ToList();

    /// <summary>Проверяет роль 44 по владельцу/арендатору и сохраняет лицензию.</summary>
    public virtual async Task SaveLicenseAsync(EquipmentLicense license, LicenseType? type)
    {
        await EnsureLicenseAccessAsync(license.EquipmentId);
        var validation = license.Validate(type);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await license.SaveAsync(_db);
    }

    /// <summary>Проверяет роль текущего пользователя по конкретному лицу через Employee.</summary>
    public virtual async Task<bool> HasRoleForPersonAsync(int roleId, int? personId) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), (Роли)roleId, personId);

    /// <summary>Загружает историю сетевых имён от новых записей к старым.</summary>
    public virtual async Task<IReadOnlyList<EquipmentNetworkNameHistory>> GetNetworkNameHistoryAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentNetworkNameHistory>(
            SQLQueries.SELECT_ОборудованиеСетевыеИмена, new { EquipmentId = equipmentId })).ToList();

    /// <summary>Загружает историю дневных конфигураций компьютера.</summary>
    public virtual async Task<IReadOnlyList<EquipmentComputerSnapshot>> GetComputerHistoryAsync(int equipmentId)
    {
        var history = (await _db.QueryRequiredAsync<EquipmentComputerSnapshot>(
            SQLQueries.SELECT_Компьютеры_КодОборудования, new { EquipmentId = equipmentId })).ToList();
        foreach (var snapshot in history)
            snapshot.ParseDetails();
        return history;
    }

    /// <summary>Загружает стандартные конфигурации.</summary>
    public virtual async Task<IReadOnlyList<StandardConfiguration>> GetStandardConfigurationsAsync() =>
        (await _db.QueryRequiredAsync<StandardConfiguration>(SQLQueries.SELECT_СтандартныеКонфигурации)).ToList();

    /// <summary>Проверяет роль 43, фактическую модель компьютера и сохраняет дневной снимок.</summary>
    public virtual async Task SaveComputerAsync(EquipmentComputerSnapshot snapshot, DateTime? today = null)
    {
        await EnsureInventoryEditAsync();
        var context = (await _db.QueryRequiredAsync<ComputerModelContext>(
            SQLQueries.SELECT_КомпьютерКонтекстСохранения, new { snapshot.EquipmentId })).SingleOrDefault()
            ?? throw new InvalidOperationException("Оборудование или его модель не найдены. Обновите данные.");
        if (context.HasComputerCharacteristics == 0)
            throw new InvalidOperationException("Конфигурация компьютера недоступна для фактического типа оборудования.");
        var validation = snapshot.ValidateConfiguration(today ?? DateTime.Today, context.MemoryBanks);
        if (validation is not null)
            throw new InvalidOperationException(validation);
        await snapshot.SaveAsync(_db);
    }

    /// <summary>Проверяет роль 43 и атомарно сохраняет основную запись и телефонный diff.</summary>
    public virtual async Task UpdateWithPhonesAsync(
        Equipment equipment,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        await EnsureInventoryEditAsync();
        await ValidateEquipmentForSaveAsync(equipment);
        await equipment.UpdateWithPhonesAsync(_db, original, current);
        _confirmedPhoneDiffs[equipment.Id] = (PhoneSyncFingerprint(original), PhoneSyncFingerprint(current));
    }

    /// <summary>Настраивает сохранённый IP-телефон по актуальным связям без повторного SQL UPDATE.</summary>
    public virtual async Task<IReadOnlyList<PhoneSyncResult>> ConfigurePhoneEquipmentAsync(int equipmentId)
    {
        var equipment = await GetAsync(equipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        if (!await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_оборудования, equipment.OwnerId))
            throw new UnauthorizedAccessException("Недостаточно прав для настройки оборудования владельца.");
        if (equipment.EquipmentTypeId != (int)ТипыОборудования.IP_телефон)
            throw new InvalidOperationException("Команда доступна только для сохранённого IP-телефона.");
        var current = await GetPhonesAsync(equipmentId);
        return await SynchronizePhoneOperationsAsync(equipmentId, [], current);
    }

    /// <summary>Синхронизирует изменённые номера и оборудование с подтверждёнными АТС.</summary>
    public virtual async Task<IReadOnlyList<string>> SynchronizePhonesAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        var results = await SynchronizePhoneOperationsAsync(equipmentId, original, current);
        return results.Where(result => !result.Success).Select(result => result.Error!).ToList();
    }

    /// <summary>Планирует и выполняет синхронизацию, возвращая результат каждой отдельной операции.</summary>
    public virtual async Task<IReadOnlyList<PhoneSyncResult>> SynchronizePhoneOperationsAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        await EnsureInventoryEditAsync();
        await EnsurePhoneSyncContextCurrentAsync(equipmentId, original, current);
        EnsureConfirmedDeletionContext(equipmentId, original, current);
        var results = new List<PhoneSyncResult>();
        var currentIds = current.Select(item => item.PhoneId).ToHashSet();
        var originalIds = original.Select(item => item.PhoneId).ToHashSet();
        var originalById = original.ToDictionary(item => item.PhoneId);
        var added = current.Where(item => !originalIds.Contains(item.PhoneId)).ToList();
        var deleted = original.Where(item => !currentIds.Contains(item.PhoneId)).ToList();
        var changed = current.Where(item => originalById.TryGetValue(item.PhoneId, out var source) &&
            HasSyncChanges(source, item)).ToList();
        if (added.Count == 0 && deleted.Count == 0 && changed.Count == 0)
            return results;
        var phones = deleted.Select(item => (item.PhoneId, Deleted: true))
            .Concat(added.Concat(changed).Select(item => (item.PhoneId, Deleted: false)))
            .OrderBy(item => item.PhoneId);
        var changedIds = changed.Select(item => item.PhoneId).ToHashSet();
        var equipmentIds = deleted.Concat(original.Where(item => changedIds.Contains(item.PhoneId)))
            .Select(item => item.ConnectionEquipmentId)
            .Concat(added.Concat(changed).Select(item => item.ConnectionEquipmentId))
            .Where(id => id is > 0)
            .Select(id => id!.Value)
            .Distinct()
            .ToList();
        if (equipmentIds.Count == 0)
            equipmentIds.Add(equipmentId);
        var operations = new List<PhoneSyncOperation>();
        var needsCcm = false;
        var hasXorcom = false;
        foreach (var phone in phones)
        {
            var operation = new PhoneSyncOperation(PhoneSyncKind.Xorcom, phone.PhoneId.ToString(), phone.Deleted);
            var lookup = (await _db.QueryRequiredAsync<PhoneNumberLookup>(
                SQLQueries.SELECT_ТелефонныйНомерПоИдентификатору, new { Id = phone.PhoneId })).SingleOrDefault();
            if (lookup is null)
            {
                results.Add(new PhoneSyncResult(operation, $"Телефонный номер #{phone.PhoneId} не найден для синхронизации."));
                continue;
            }
            needsCcm |= lookup.StationId == 0;
            if (string.IsNullOrWhiteSpace(lookup.XorcomName))
                continue;
            hasXorcom = true;
            operations.Add(operation);
        }
        if (needsCcm && !hasXorcom)
            operations.AddRange(equipmentIds.Select(id => new PhoneSyncOperation(PhoneSyncKind.Ccm, id.ToString())));
        EnsureOperationsBelongToDiff(equipmentId, original, current, operations);
        results.AddRange(await ExecutePhoneSyncOperationsCoreAsync(operations));
        return results;
    }

    /// <summary>Повторяет только неуспешные операции после свежей проверки прав и актуального контекста.</summary>
    public virtual async Task<IReadOnlyList<PhoneSyncResult>> ExecutePhoneSyncOperationsAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current,
        IEnumerable<PhoneSyncOperation> operations)
    {
        await EnsureInventoryEditAsync();
        await EnsurePhoneSyncContextCurrentAsync(equipmentId, original, current);
        EnsureConfirmedDeletionContext(equipmentId, original, current);
        EnsureOperationsBelongToDiff(equipmentId, original, current, operations);
        return await ExecutePhoneSyncOperationsCoreAsync(operations);
    }

    private async Task EnsurePhoneSyncContextCurrentAsync(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        if (await GetAsync(equipmentId) is null)
            throw new InvalidOperationException("Оборудование не найдено. Повтор синхронизации отменён.");
        var actual = (await GetPhonesAsync(equipmentId)).ToDictionary(item => item.PhoneId);
        var currentIds = current.Select(item => item.PhoneId).ToHashSet();
        if (original.Any(item => !currentIds.Contains(item.PhoneId) && actual.ContainsKey(item.PhoneId)))
            throw new InvalidOperationException("Удалённая телефонная связь была создана повторно. Повтор синхронизации отменён.");
        foreach (var deleted in original.Where(item => !currentIds.Contains(item.PhoneId) && !actual.ContainsKey(item.PhoneId)))
        {
            var assignments = await _db.QueryRequiredAsync<int>(
                SQLQueries.SELECT_ТелефонныйНомерАктуальныеПривязки, new { PhoneId = deleted.PhoneId });
            if (assignments.Any())
                throw new InvalidOperationException("Удалённый номер уже привязан к другому оборудованию. Повтор синхронизации отменён.");
        }
        foreach (var expected in current)
        {
            if (!actual.TryGetValue(expected.PhoneId, out var persisted) || HasSyncChanges(expected, persisted))
                throw new InvalidOperationException("Телефонные связи изменились в другой сессии. Повтор синхронизации отменён.");
        }
    }

    private static void EnsureOperationsBelongToDiff(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current,
        IEnumerable<PhoneSyncOperation> operations)
    {
        var originalById = original.ToDictionary(item => item.PhoneId);
        var currentById = current.ToDictionary(item => item.PhoneId);
        var affectedIds = originalById.Keys.Concat(currentById.Keys).Where(phoneId =>
        {
            var hasOriginal = originalById.TryGetValue(phoneId, out var before);
            var hasCurrent = currentById.TryGetValue(phoneId, out var after);
            return hasOriginal != hasCurrent || hasOriginal && hasCurrent && HasSyncChanges(before!, after!);
        }).ToHashSet();
        var affected = original.Concat(current).Where(item => affectedIds.Contains(item.PhoneId)).ToList();
        var equipmentIds = affected.Select(item => item.ConnectionEquipmentId).Where(id => id is > 0)
            .Select(id => id!.Value).Append(equipmentId).ToHashSet();
        foreach (var operation in operations.Distinct())
        {
            if (operation.Kind == PhoneSyncKind.Ccm &&
                int.TryParse(operation.Key, out var targetId) && equipmentIds.Contains(targetId))
                continue;
            if (operation.Kind == PhoneSyncKind.Xorcom && int.TryParse(operation.Key, out var phoneId) &&
                affectedIds.Contains(phoneId) &&
                operation.Deleting == (originalById.ContainsKey(phoneId) && !currentById.ContainsKey(phoneId)))
                continue;
            throw new UnauthorizedAccessException("Операция АТС не соответствует подтверждённому изменению оборудования.");
        }
    }

    private static bool HasSyncChanges(EquipmentPhoneLink source, EquipmentPhoneLink target) =>
        source.ConnectionEquipmentId != target.ConnectionEquipmentId ||
        source.AutoDialNumber != target.AutoDialNumber ||
        source.AutoDialDelay != target.AutoDialDelay ||
        source.IsDefault != target.IsDefault;

    private static string PhoneSyncFingerprint(IEnumerable<EquipmentPhoneLink> links) =>
        string.Join(";", links.OrderBy(item => item.PhoneId).Select(item =>
            $"{item.PhoneId}:{item.ConnectionEquipmentId}:{item.AutoDialNumber}:{item.AutoDialDelay}:{item.IsDefault}"));

    private void EnsureConfirmedDeletionContext(
        int equipmentId,
        IReadOnlyCollection<EquipmentPhoneLink> original,
        IReadOnlyCollection<EquipmentPhoneLink> current)
    {
        var currentIds = current.Select(item => item.PhoneId).ToHashSet();
        if (!original.Any(item => !currentIds.Contains(item.PhoneId)))
            return;
        if (!_confirmedPhoneDiffs.TryGetValue(equipmentId, out var confirmed) ||
            confirmed.Original != PhoneSyncFingerprint(original) || confirmed.Current != PhoneSyncFingerprint(current))
            throw new UnauthorizedAccessException(
                "Удаление номера допускается только для diff, подтверждённого сохранением в текущем серверном контексте.");
    }

    private async Task<IReadOnlyList<PhoneSyncResult>> ExecutePhoneSyncOperationsCoreAsync(
        IEnumerable<PhoneSyncOperation> operations)
    {
        var results = new List<PhoneSyncResult>();
        var service = new PhoneFormService(_db);
        foreach (var operation in operations.Distinct())
        {
            var phoneId = operation.Kind == PhoneSyncKind.Xorcom ? int.Parse(operation.Key) : 0;
            results.AddRange(await service.SynchronizeAsync(phoneId, [operation]));
        }
        return results;
    }

    private sealed class ComputerModelContext
    {
        /// <summary>Фактическая модель оборудования.</summary>
        public int ModelId { get; set; }
        /// <summary>Фактический тип модели.</summary>
        public int EquipmentTypeId { get; set; }
        /// <summary>Признак характеристик компьютера.</summary>
        public byte HasComputerCharacteristics { get; set; }
        /// <summary>Число банков памяти фактической модели.</summary>
        public int? MemoryBanks { get; set; }
    }

    private async Task ValidateEquipmentForSaveAsync(Equipment equipment)
    {
        var errors = new List<string>();
        if (equipment.EquipmentTypeId is null || equipment.ModelId is null)
            errors.Add("Выберите тип и модель оборудования.");
        if (equipment.OwnerId is null)
            errors.Add("Укажите владельца оборудования.");
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        var type = (await _db.QueryRequiredAsync<EquipmentType>(
            SQLQueries.SELECT_МодельОборудованияТипДляСохранения,
            new { ModelId = equipment.ModelId.GetValueOrDefault() })).SingleOrDefault()
            ?? throw new InvalidOperationException("Модель оборудования не найдена.");
        if (type.Id != equipment.EquipmentTypeId)
            throw new InvalidOperationException("Выбранная модель не соответствует типу оборудования. Обновите модель и повторите операцию.");

        var hasWriteOff = equipment.WriteOffAtUtc is not null && equipment.WriteOffAtUtc != DateTime.MinValue;
        if (equipment.DocumentWriteOffId is not null && !hasWriteOff)
            errors.Add("Укажите дату списания.");
        if (type.Id != (int)ТипыОборудования.SIM_карта &&
            hasWriteOff && equipment.DocumentWriteOffId is null)
            errors.Add("Укажите документ списания.");
        if (type.IsSerialNumberRequired > 0 && string.IsNullOrWhiteSpace(equipment.SerialNumber))
            errors.Add("Укажите серийный номер.");
        if (type.MacAddressCount > 0 &&
            (!Equipment.IsValidMacAddress(equipment.MacAddress) ||
             !Equipment.IsValidMacAddress(equipment.MacAddress2) ||
             !Equipment.IsValidMacAddress(equipment.MacAddressIlo)))
            errors.Add("MAC-адрес должен содержать 12 hex-символов; допустимы разделители пробел, двоеточие, дефис и точка.");
        if (errors.Count > 0)
            throw new InvalidOperationException(string.Join(" ", errors));

        if (type.MacAddressCount > 0)
        {
            equipment.MacAddress = Equipment.NormalizeMacAddress(equipment.MacAddress);
            equipment.MacAddress2 = Equipment.NormalizeMacAddress(equipment.MacAddress2);
            equipment.MacAddressIlo = Equipment.NormalizeMacAddress(equipment.MacAddressIlo);
        }
    }

    private async Task EnsureInventoryEditAsync()
    {
        if (!await HasRoleAsync(Роли.Специалист_по_инвентаризации_оборудования))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения оборудования.");
    }

    private async Task EnsureAccountAccessAsync(bool write)
    {
        if (!await HasGlobalRoleAsync(Роли.Дежурный_инженер) ||
            write && !await HasRoleAsync(Роли.Специалист_по_инвентаризации_оборудования))
            throw new UnauthorizedAccessException("Недостаточно прав для работы с учётными записями оборудования.");
    }

    private async Task<EquipmentType> GetRequiredChildWriteTypeAsync(int equipmentId) =>
        (await _db.QueryRequiredAsync<EquipmentType>(
            SQLQueries.SELECT_ОборудованиеТипДляДочернихЗаписей, new { EquipmentId = equipmentId })).SingleOrDefault()
        ?? throw new InvalidOperationException("Оборудование или его модель не найдены. Обновите данные.");

    private async Task<EquipmentType> GetRequiredTypeAsync(Equipment equipment)
    {
        if (equipment.EquipmentTypeId is null)
            throw new InvalidOperationException("Для оборудования не указан тип.");
        return (await _db.QueryRequiredAsync<EquipmentType>(SQLQueries.SELECT_ТипОборудованияПоИдентификатору,
            new { Id = equipment.EquipmentTypeId.Value })).SingleOrDefault()
            ?? throw new InvalidOperationException("Тип оборудования не найден.");
    }

    private async Task EnsureNetworkAccessAsync(int equipmentId)
    {
        var equipment = await GetAsync(equipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        var type = await GetRequiredTypeAsync(equipment);
        if (type.HasComputerCharacteristics == 0 ||
            !await HasGlobalRoleAsync(Роли.Исполнитель_заявок_службы_поддержки_пользователей))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения подключения рабочей сети.");
    }

    private async Task EnsureUpsAccessAsync(int serverEquipmentId)
    {
        var equipment = await GetAsync(serverEquipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        var type = await GetRequiredTypeAsync(equipment);
        if (equipment.EquipmentTypeId is not < 100 || type.HasSimCharacteristics != 0 ||
            !await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_оборудования, equipment.OwnerId))
            throw new UnauthorizedAccessException("Недостаточно прав для изменения подключений UPS.");
    }

    private async Task EnsureLicenseAccessAsync(int equipmentId)
    {
        var equipment = await GetAsync(equipmentId) ??
                        throw new InvalidOperationException("Оборудование не найдено.");
        var type = await GetRequiredTypeAsync(equipment);
        var hasRole = await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_по, equipment.OwnerId) ||
                      equipment.TenantId is not null && equipment.TenantId != equipment.OwnerId &&
                      await HasRoleForPersonAsync(Роли.Специалист_по_инвентаризации_по, equipment.TenantId);
        if (type.HasSoftwareCharacteristics == 0 || !hasRole)
            throw new UnauthorizedAccessException("Недостаточно прав для изменения лицензии.");
    }

    private async Task<T?> ExecuteRequiredScalarAsync<T>(string sql, object? parameters, CommandType commandType) =>
        (await _db.QueryRequiredAsync<T>(sql, parameters)).SingleOrDefault();

    /// <summary>Получает текущего сотрудника после подтверждения Windows/SQL principal.</summary>
    public virtual async Task<Employee?> GetCurrentUserAsync() =>
        await Employee.GetCurrentAsync(_db, await GetWindowsPrincipalAsync());

    /// <summary>Получает текущего сотрудника со снимком effective roles для отображения формы.</summary>
    public virtual async Task<Employee?> GetCurrentUserWithRolesAsync() =>
        await Employee.GetCurrentWithRolesAsync(_db, await GetWindowsPrincipalAsync());

    private async Task EnsureTrustedIdentityAsync()
    {
        if (await GetCurrentUserAsync() is null)
            throw new UnauthorizedAccessException("Не удалось подтвердить текущего сотрудника.");
    }

    private Task<string?> GetWindowsPrincipalAsync() =>
        _principalProvider?.GetWindowsPrincipalAsync() ?? Task.FromResult<string?>(null);

    /// <summary>Проверяет роль текущего пользователя с учётом замещений через Employee.</summary>
    public virtual async Task<bool> HasRoleAsync(Роли role) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), role);

    /// <summary>Проверяет роль по фактическому лицу; при отсутствии лица допускается только global.</summary>
    public virtual async Task<bool> HasRoleForPersonAsync(Роли role, int? personId) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), role,
            EmployeeRoleScope.PersonOrGlobal, personId);

    private async Task<bool> HasGlobalRoleAsync(Роли role) =>
        await Employee.HasRoleAsync(_db, await GetWindowsPrincipalAsync(), role, EmployeeRoleScope.Global);
}
