using System.Runtime.CompilerServices;

namespace VamSys.UiTests.Driver;

/// <summary>
/// A window test: explicit, because it starts the QA package and takes over the desktop (focus and keyboard), so it runs
/// only when asked for (<c>-- --explicit only</c>), never in check-all.
/// </summary>
public sealed class WindowFactAttribute : FactAttribute
{
    public WindowFactAttribute([CallerFilePath] string? sourceFilePath = null, [CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber) => Explicit = true;
}
