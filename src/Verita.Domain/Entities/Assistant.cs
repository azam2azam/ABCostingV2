namespace Verita.Domain.Entities;

/// <summary>
/// A conversation with the rule-based assistant. It belongs to one user; nobody else reads it.
/// <see cref="ContextJson"/> holds what follow-up questions build on: the last intent, its
/// parameters and the hospital and period it ran against.
/// </summary>
public class AssistantThread
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Title { get; set; } = "";
    public string? ContextJson { get; set; }
    public bool IsPinned { get; set; }
    public bool IsArchived { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// One turn. A user turn records how it was understood (intent, parameters, turn type, score);
/// the assistant turn after it records the answer and the run it came from, so an old answer is
/// shown as it was and labelled with its run rather than silently re-queried.
/// </summary>
public class AssistantMessage
{
    public Guid Id { get; set; }
    public Guid ThreadId { get; set; }
    /// <summary>The user turn this one answers, or the earlier reply a follow-up builds on.</summary>
    public Guid? ParentMessageId { get; set; }
    public string Role { get; set; } = "user";
    public string Text { get; set; } = "";
    public string? Intent { get; set; }
    /// <summary>NEW, REFINE, DRILLDOWN, CORRECTION, REPHRASE, SHIFT, CLARIFY or META.</summary>
    public string? TurnType { get; set; }
    public string? ParamsJson { get; set; }
    /// <summary>The reply as rendered: text, blocks, assumptions, links and suggestions.</summary>
    public string? DetailJson { get; set; }
    /// <summary>The permission the answer needed; checked again whenever the thread is opened.</summary>
    public string? Permission { get; set; }
    public Guid? RunId { get; set; }
    public string? RunCode { get; set; }
    public bool Matched { get; set; }
    [Share] public decimal Score { get; set; }
    /// <summary>1 helpful, -1 not helpful, null not rated. A correction or rephrase sets -1 on the reply it replaces.</summary>
    public int? Feedback { get; set; }
    public string? FeedbackReason { get; set; }
    public DateTime CreatedAt { get; set; }
}
