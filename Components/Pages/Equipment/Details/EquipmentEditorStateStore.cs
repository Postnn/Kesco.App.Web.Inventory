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
        "Dialog", "Password", "Secrets", "Configurations", "Types",
    };

    internal static string Capture(ComponentBase component)
    {
        var state = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var property in component.GetType().GetProperties())
        {
            if (!IsStateProperty(property) || Excluded.Contains(property.Name) || IsSecret(property.Name) ||
            typeof(Delegate).IsAssignableFrom(property.PropertyType))
                continue;
            state[property.Name] = property.GetValue(component);
        }
        if (component is EquipmentComputerDialog computer)
        {
            state["$ram"] = computer.Ram.ToArray();
            state["$hd"] = computer.Hd.ToArray();
            state["$ssd"] = computer.Ssd.ToArray();
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
            RestoreComponents(state, "$ram", computer.Ram);
            RestoreComponents(state, "$hd", computer.Hd);
            RestoreComponents(state, "$ssd", computer.Ssd);
        }
    }

    private static void RestoreComponents(
        IReadOnlyDictionary<string, JsonElement> state,
        string key,
        ICollection<decimal?> target)
    {
        if (!state.TryGetValue(key, out var element))
            return;
        target.Clear();
        foreach (var value in element.Deserialize<decimal?[]>() ?? [])
            target.Add(value);
    }

    private static bool IsStateProperty(System.Reflection.PropertyInfo property) =>
        property.CanRead && property.SetMethod?.IsPublic == true && property.GetIndexParameters().Length == 0 &&
        property.GetCustomAttributes(typeof(CascadingParameterAttribute), true).Length == 0;

    private static bool IsSecret(string name) =>
        name.Contains("password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("pin", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("puk", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("secret", StringComparison.OrdinalIgnoreCase);
}
