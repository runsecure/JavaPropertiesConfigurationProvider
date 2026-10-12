namespace Microsoft.Extensions.Configuration.JavaProperties;

/// <summary>
/// Identifies the kind of token most recently produced by <see cref="JavaPropertiesReader"/>.
/// </summary>
public enum JavaPropertiesTokenType : byte
{
    /// <summary>No token has been read yet.</summary>
    None,

    /// <summary>The key portion of a key/value property.</summary>
    PropertyName,

    /// <summary>The value portion of a key/value property.</summary>
    PropertyValue,

    /// <summary>A comment line (starting with '#' or '!').</summary>
    Comment,

    /// <summary>A logical end-of-line marker.</summary>
    EndOfLine,

    /// <summary>The end of the input data has been reached.</summary>
    EndOfFile
}
