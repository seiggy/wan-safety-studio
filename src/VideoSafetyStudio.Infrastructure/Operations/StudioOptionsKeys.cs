using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using VideoSafetyStudio.Application.Operations;

namespace VideoSafetyStudio.Infrastructure.Operations;

/// <summary>
/// The configuration binder silently ignores a misspelled key, which for a safety ceiling (for example <c>MaxJobMinute</c>) would quietly keep a default.
/// This reports every key under <c>Studio</c> that no option has, so a typo stops the host at start with the full path.
/// </summary>
public static class StudioOptionsKeys
{
    public static IReadOnlyList<string> Unknown(IConfigurationSection section) => [.. Walk(section, typeof(StudioOptions))];

    private static IEnumerable<string> Walk(IConfigurationSection section, Type type)
    {
        foreach (var child in section.GetChildren())
        {
            var property = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .FirstOrDefault(item => item.CanWrite && string.Equals(item.Name, child.Key, StringComparison.OrdinalIgnoreCase));
            if (property is null)
            {
                yield return child.Path;
                continue;
            }

            var kind = property.PropertyType;
            if (kind.IsGenericType && kind.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                // Dictionary keys are free (region names, DNS zone names, tag names); only typed values are checked.
                var value = kind.GetGenericArguments()[1];
                if (value == typeof(string)) continue;
                foreach (var entry in child.GetChildren()) foreach (var unknown in Walk(entry, value)) yield return unknown;
            }
            else if (!kind.IsArray && kind.Namespace == typeof(StudioOptions).Namespace && !typeof(IEnumerable).IsAssignableFrom(kind))
            {
                foreach (var unknown in Walk(child, kind)) yield return unknown;
            }
        }
    }
}