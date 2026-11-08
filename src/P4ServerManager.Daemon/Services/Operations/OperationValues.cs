using System.Diagnostics.CodeAnalysis;

namespace P4ServerManager.Daemon.Services.Operations;

/// <summary>
/// Key of a value in <see cref="OperationValues"/>. Declared once, as a static field next to the step that produces the
/// value: two keys with the same name are still two keys.
/// </summary>
/// <param name="name">Name of the value, for the error messages.</param>
public sealed class OperationValueKey<T>(string name)
{
    public string Name { get; } = name;

    public override string ToString()
    {
        return Name;
    }
}

/// <summary>
/// Values a step produces for the steps after it, such as the build it resolved. They live in memory for one run only
/// and are never saved, so that a step shared between operations does not need a property on every context.
/// </summary>
public sealed class OperationValues
{
    private readonly Dictionary<object, object?> values = [];

    public void Set<T>(OperationValueKey<T> key, T value)
    {
        values[key] = value;
    }

    public bool TryGet<T>(OperationValueKey<T> key, [MaybeNullWhen(false)] out T value)
    {
        if (values.TryGetValue(key, out object? stored))
        {
            value = (T)stored!;
            return true;
        }
        value = default;
        return false;
    }

    /// <exception cref="InvalidOperationException">
    /// No step set the value: the plan lacks the step that produces it, or runs it later.
    /// </exception>
    public T GetRequired<T>(OperationValueKey<T> key)
    {
        return TryGet(key, out T? value)
            ? value
            : throw new InvalidOperationException($"No step of the operation set '{key.Name}'.");
    }
}
