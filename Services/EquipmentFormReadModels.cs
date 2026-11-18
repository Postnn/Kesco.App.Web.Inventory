using Clayzor.Lib.Entities.Inventory;

namespace Kesco.App.Web.Inventory.Services;

/// <summary>Параметры пакетного чтения табличных секций формы оборудования.</summary>
public sealed class EquipmentFormReadRequest
{
    /// <summary>Идентификатор оборудования.</summary>
    public int EquipmentId { get; set; }

    /// <summary>Читать телефонные связи.</summary>
    public bool IncludePhones { get; set; }

    /// <summary>Читать количество телефонов гостиничного номера.</summary>
    public bool IncludeHotelPhoneCount { get; set; }

    /// <summary>Читать учётные записи после проверки роли.</summary>
    public bool IncludeAccounts { get; set; }

    /// <summary>Читать секции компьютера.</summary>
    public bool IncludeComputer { get; set; }

    /// <summary>Читать секции SIM.</summary>
    public bool IncludeSim { get; set; }

    /// <summary>Читать лицензию и типы лицензирования.</summary>
    public bool IncludeLicense { get; set; }

    /// <summary>Читать историю сетевых имён.</summary>
    public bool IncludeNetworkName { get; set; }

    /// <summary>Включать завершённые услуги SIM.</summary>
    public bool ShowServiceHistory { get; set; }

    /// <summary>Включать обязательные услуги организации.</summary>
    public bool ShowCompanyRequiredServices { get; set; }
}

/// <summary>Результат пакетного чтения табличных секций формы оборудования.</summary>
public sealed class EquipmentFormReadResult
{
    /// <summary>Телефонные связи.</summary>
    public IReadOnlyList<EquipmentPhoneLink> Phones { get; set; } = [];

    /// <summary>Количество телефонов гостиничного номера.</summary>
    public int HotelPhoneCount { get; set; }

    /// <summary>История расположений.</summary>
    public IReadOnlyList<EquipmentLocationPeriod> Locations { get; set; } = [];

    /// <summary>История ответственных.</summary>
    public IReadOnlyList<EquipmentResponsiblePeriod> Responsibles { get; set; } = [];

    /// <summary>Учётные записи без паролей.</summary>
    public IReadOnlyList<EquipmentAccount> Accounts { get; set; } = [];

    /// <summary>История конфигураций компьютера.</summary>
    public IReadOnlyList<EquipmentComputerSnapshot> ComputerHistory { get; set; } = [];

    /// <summary>Стандартные конфигурации.</summary>
    public IReadOnlyList<StandardConfiguration> Configurations { get; set; } = [];

    /// <summary>Сведения SIM без секретов.</summary>
    public IReadOnlyList<EquipmentSimCard> SimCards { get; set; } = [];

    /// <summary>Дополнительные услуги SIM.</summary>
    public IReadOnlyList<EquipmentSimService> SimServices { get; set; } = [];

    /// <summary>Типы дополнительных услуг SIM.</summary>
    public IReadOnlyList<SimServiceType> SimServiceTypes { get; set; } = [];

    /// <summary>Лицензии.</summary>
    public IReadOnlyList<EquipmentLicense> Licenses { get; set; } = [];

    /// <summary>Типы лицензирования.</summary>
    public IReadOnlyList<LicenseType> LicenseTypes { get; set; } = [];

    /// <summary>Установленное ПО.</summary>
    public IReadOnlyList<EquipmentSoftwareInstallation> SoftwareInstallations { get; set; } = [];

    /// <summary>История бухгалтерского учёта.</summary>
    public IReadOnlyList<EquipmentAccounting> Accounting { get; set; } = [];

    /// <summary>Сведения маршрутизатора.</summary>
    public EquipmentRouterInfo? RouterInfo { get; set; }

    /// <summary>Инвентаризация маршрутизатора.</summary>
    public IReadOnlyList<EquipmentRouterInventoryItem> RouterInventory { get; set; } = [];

    /// <summary>Подключения UPS.</summary>
    public IReadOnlyList<UpsServerLink> UpsLinks { get; set; } = [];

    /// <summary>Последняя инвентаризация.</summary>
    public EquipmentActualInventory? LastInventory { get; set; }

    /// <summary>Количество подключений рабочей сети.</summary>
    public int NetworkAttachmentCount { get; set; }

    /// <summary>История сетевых имён.</summary>
    public IReadOnlyList<EquipmentNetworkNameHistory> NetworkNameHistory { get; set; } = [];
}
