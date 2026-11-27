using System.Text.Json;
using Microsoft.AspNetCore.Components;

namespace Kesco.App.Web.Inventory.Components.Pages.Equipment.Details;

/// <summary>Typed snapshot выбранных значений и исходных версий дочернего редактора.</summary>
internal static class EquipmentEditorStateStore
{
    private static readonly HashSet<string> Excluded = new(StringComparer.Ordinal)
    {
        nameof(EquipmentEditorDialogBase<object>.Save),
        nameof(EquipmentEditorDialogBase<object>.EditorContext),
        nameof(EquipmentEditorDialogBase<object>.PresenceTarget),
        "Dialog", "Password", "Secrets", "Configurations", "Types", "AvailableServers",
        "Today", "Now", "FreshOriginal", "FreshOriginalActive",
    };

    internal static string Capture(ComponentBase component)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in component.GetType().GetProperties())
        {
            if (!IsStateProperty(property) || Excluded.Contains(property.Name) || IsSecret(property.Name))
                continue;
            state[property.Name] = property.GetValue(component);
        }
        if (component is EquipmentComputerDialog computer)
        {
            state["$ram"] = computer.Ram.ToArray();
            state["$ramSlots"] = computer.RamSlots.ToArray();
            state["$ramDeleted"] = computer.DeletedRamSlots.ToArray();
            state["$hd"] = computer.Hd.ToArray();
            state["$hdSlots"] = computer.HdSlots.ToArray();
            state["$hdDeleted"] = computer.DeletedHdSlots.ToArray();
            state["$ssd"] = computer.Ssd.ToArray();
            state["$ssdSlots"] = computer.SsdSlots.ToArray();
            state["$ssdDeleted"] = computer.DeletedSsdSlots.ToArray();
        }
        return JsonSerializer.Serialize(state);
    }

    internal static void Restore(ComponentBase component, string json)
    {
        var state = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ??
                    throw new InvalidOperationException("Состояние дочернего редактора повреждено.");
        var properties = component.GetType().GetProperties().ToDictionary(property => property.Name, StringComparer.Ordinal);
        foreach (var (name, element) in state)
        {
            if (name.StartsWith('$') || Excluded.Contains(name) || IsSecret(name))
                continue;
            if (!properties.TryGetValue(name, out var property) || !IsStateProperty(property))
                continue;
            property.SetValue(component, element.Deserialize(property.PropertyType));
        }
        if (component is EquipmentComputerDialog computer)
        {
            RestoreComponents(state, "$ram", "$ramSlots", "$ramDeleted", computer.Ram, computer.RamSlots, computer.DeletedRamSlots);
            RestoreComponents(state, "$hd", "$hdSlots", "$hdDeleted", computer.Hd, computer.HdSlots, computer.DeletedHdSlots);
            RestoreComponents(state, "$ssd", "$ssdSlots", "$ssdDeleted", computer.Ssd, computer.SsdSlots, computer.DeletedSsdSlots);
        }
    }

    private static void RestoreComponents(
        IReadOnlyDictionary<string, JsonElement> state,
        string valuesKey,
        string slotsKey,
        string deletedKey,
        ICollection<decimal?> target,
        IList<int> slots,
        ISet<int> deleted)
    {
        if (!state.TryGetValue(valuesKey, out var element))
            return;
        target.Clear();
        var values = element.Deserialize<decimal?[]>() ?? [];
        foreach (var value in values)
            target.Add(value);
        slots.Clear();
        var restoredSlots = state.TryGetValue(slotsKey, out var slotsElement)
            ? slotsElement.Deserialize<int[]>() ?? []
            : [];
        for (var index = 0; index < values.Length; index++)
            slots.Add(restoredSlots.Length == values.Length ? restoredSlots[index] : index);
        deleted.Clear();
        if (state.TryGetValue(deletedKey, out var deletedElement))
            deleted.UnionWith(deletedElement.Deserialize<int[]>() ?? []);
        deleted.ExceptWith(slots);
    }

    private static bool IsStateProperty(System.Reflection.PropertyInfo property) =>
        property.CanRead && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0 &&
        !typeof(Delegate).IsAssignableFrom(property.PropertyType) &&
        property.GetCustomAttributes(typeof(CascadingParameterAttribute), true).Length == 0;

    private static bool IsSecret(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("puk", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase);
}
