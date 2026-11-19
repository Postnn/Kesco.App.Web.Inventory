namespace Kesco.App.Web.Inventory.Services;

/// <summary>Правила временных значений radio при смене типа номера.</summary>
public static class PhoneTypeChoiceRules
{
    /// <summary>Признак блокировки выбора услуг связи.</summary>
    public static bool ServicesDisabled(int? phoneTypeId) => phoneTypeId is <= 0 or 100 or 101;

    /// <summary>Возвращает отображаемое значение услуг, сохраняя выбор пользователя для обычных типов.</summary>
    public static int ResolveServices(int? phoneTypeId, int userChoice) => phoneTypeId switch
    {
        <= 0 => 0,
        100 or 101 => 4,
        _ => userChoice,
    };
}
