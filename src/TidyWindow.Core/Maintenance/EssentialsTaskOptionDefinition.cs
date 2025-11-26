using System;

namespace TidyWindow.Core.Maintenance;

/// <summary>
/// Describes a toggleable option that can be surfaced for an essentials automation script.
/// </summary>
public sealed record EssentialsTaskOptionDefinition
{
    public EssentialsTaskOptionDefinition(
        string id,
        string label,
        string parameterName,
        bool defaultValue = true,
        EssentialsTaskOptionMode mode = EssentialsTaskOptionMode.EmitWhenTrue,
        string? description = null)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            throw new ArgumentException("Option id must be provided.", nameof(id));
        }

        if (string.IsNullOrWhiteSpace(label))
        {
            throw new ArgumentException("Option label must be provided.", nameof(label));
        }

        if (string.IsNullOrWhiteSpace(parameterName))
        {
            throw new ArgumentException("Parameter name must be provided.", nameof(parameterName));
        }

        Id = id;
        Label = label;
        ParameterName = parameterName;
        DefaultValue = defaultValue;
        Mode = mode;
        Description = string.IsNullOrWhiteSpace(description) ? null : description;
    }

    public string Id { get; }

    public string Label { get; }

    public string ParameterName { get; }

    public bool DefaultValue { get; }

    public EssentialsTaskOptionMode Mode { get; }

    public string? Description { get; }
}

public enum EssentialsTaskOptionMode
{
    /// <summary>
    /// Emits the corresponding PowerShell switch only when the option is enabled.
    /// </summary>
    EmitWhenTrue,

    /// <summary>
    /// Emits the corresponding PowerShell switch only when the option is disabled.
    /// Useful for scripts that expose "Skip*" switches.
    /// </summary>
    EmitWhenFalse
}
