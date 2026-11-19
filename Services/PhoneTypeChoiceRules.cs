namespace Kesco.App.Web.Inventory.Services;

/// <summary>Правила временных значений radio при смене типа номера.</summary>
public static class PhoneTypeChoiceRules
{
    /// <summary>Признак блокировки выбора услуг связи.</summary>
    public static bool ServicesDisabled(int? phoneTypeId) => phoneTypeId is <= 0
        or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Короткий_номер
        or (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Короткий_номер_мобильного;

    /// <summary>Возвращает отображаемое значение услуг, сохраняя выбор пользователя для обычных типов.</summary>
    public static int? ResolveServices(int? phoneTypeId, int? userChoice) => phoneTypeId switch
    {
        <= 0 => 0,
        (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Короткий_номер or
            (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.Короткий_номер_мобильного => 4,
        _ => userChoice,
    };

    /// <summary>Для новой PBX-группы выбирает скрытие из справочника, не имитируя выбор для остальных типов.</summary>
    public static int? ResolveDirectory(int? phoneTypeId, int? userChoice, bool isNew) =>
        isNew && phoneTypeId == (int)Clayzor.Lib.Entities.ТипыТелефонныхНомеров.ГруппаИлиФиктивный ? 0 : userChoice;

    /// <summary>При выборе основного номера включает отображение только из пустого или скрытого режима.</summary>
    public static int? ResolveDirectoryAfterDefault(bool isDefault, int? current) =>
        isDefault && current is null or 0 ? 1 : current;
}
