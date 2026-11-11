namespace Verita.Application.Assistant;

/// <summary>
/// Picks the intent for a question the rules could not settle. It sees the question text and the
/// intent list only: no hospital data, no answers. Returns null when it cannot or should not route.
/// </summary>
public interface IIntentRouter
{
    /// <summary>False when routing is switched off or has no API key; callers then skip it entirely, including any lookup.</summary>
    bool IsEnabled { get; }

    Task<string?> RouteAsync(string question, IReadOnlyList<IntentDef> intents, Guid userId, CancellationToken ct);
}
