namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Options that control how <see cref="JavaPropertiesWriter"/> formats its output.
/// </summary>
public struct JavaPropertiesWriterOptions
{
    /// <summary>
    /// The character written between a key and its value. Must be <c>'='</c> or <c>':'</c>.
    /// Defaults to <c>'='</c>.
    /// </summary>
    public char KeyValueSeparator { get; set; }

    /// <summary>The line terminator written after each property or comment. Defaults to <c>"\n"</c>.</summary>
    public string NewLine { get; set; }

    /// <summary>
    /// Initializes a new <see cref="JavaPropertiesWriterOptions"/> with default settings.
    /// </summary>
    public JavaPropertiesWriterOptions()
    {
        KeyValueSeparator = '=';
        NewLine = "\n";
    }
}
