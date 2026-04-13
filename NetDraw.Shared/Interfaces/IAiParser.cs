using NetDraw.Shared.Models;

namespace NetDraw.Shared.Interfaces;

/// <summary>
/// Parses a natural-language AI command into a list of drawing actions.
/// </summary>
public interface IAiParser
{
    /// <summary>
    /// Converts a free-text <paramref name="command"/> into concrete
    /// <see cref="DrawActionBase"/> instances that can be applied to the canvas.
    /// </summary>
    Task<List<DrawActionBase>> ParseAsync(string command);
}
