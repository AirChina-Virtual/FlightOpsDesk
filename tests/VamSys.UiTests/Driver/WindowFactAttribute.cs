using System.Runtime.CompilerServices;

namespace VamSys.UiTests.Driver;

/// <summary>
/// A window test: explicit, because it starts the program and takes over the desktop (focus, keyboard and mouse), so it
/// runs only when asked for (<c>-- --explicit only</c>), never in check-all.
/// </summary>
public sealed class WindowFactAttribute : FactAttribute
{
    public WindowFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Explicit = true;
}

/// <summary>A window test with data rows; explicit for the same reason as <see cref="WindowFactAttribute"/>.</summary>
public sealed class WindowTheoryAttribute : TheoryAttribute
{
    public WindowTheoryAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Explicit = true;
}
