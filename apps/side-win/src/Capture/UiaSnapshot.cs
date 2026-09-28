using System.Diagnostics;

namespace Side.Win.Capture;

/// <summary>
/// Breadth-first text extraction from a UIA tree (port of AXText.extract + AXSnapshot).
/// Only Text-control names are collected (AXStaticText analogue); password fields are skipped and
/// blocked text fields are not descended into.
/// </summary>
internal static class UiaSnapshot
{
    public const int MaxNodes = 400;
    public const int MaxCharacters = 12_000;
    public const int MaxChildrenPerNode = 100;
    /// <summary>Hard wall-clock budget so a hung target cannot stall the capture thread.</summary>
    public static readonly TimeSpan Budget = TimeSpan.FromSeconds(2.5);

    public static List<IUIAutomationElement> Children(IUIAutomationTreeWalker walker, IUIAutomationElement element, int max)
    {
        var list = new List<IUIAutomationElement>();
        var child = Uia.Try(() => walker.GetFirstChildElement(element));
        while (child is not null && list.Count < max)
        {
            list.Add(child);
            var current = child;
            child = Uia.Try(() => walker.GetNextSiblingElement(current));
        }
        return list;
    }

    public static string Extract(IUIAutomationElement root)
    {
        var automation = Uia.Client;
        if (automation is null) return "";
        var walker = automation.get_ControlViewWalker();
        var clock = Stopwatch.StartNew();
        var queue = new List<IUIAutomationElement> { root };
        var next = 0;
        var parts = new List<string>();
        var seen = new HashSet<string>();
        var characters = 0;
        while (next < queue.Count && next < MaxNodes && characters < MaxCharacters && clock.Elapsed < Budget)
        {
            var node = queue[next++];
            if (Uia.IsPassword(node)) continue;
            var type = Uia.ControlType(node);
            if (type == Uia.ControlTypeText && Uia.Name(node) is { } value)
            {
                var trimmed = value.Trim();
                if (trimmed.Length > 0 && seen.Add(trimmed))
                {
                    var separator = parts.Count == 0 ? 0 : 1;
                    var remaining = MaxCharacters - characters - separator;
                    if (remaining <= 0) break;
                    var excerpt = trimmed.Length > remaining ? trimmed[..remaining] : trimmed;
                    parts.Add(excerpt);
                    characters += separator + excerpt.Length;
                }
            }
            if ((type == Uia.ControlTypeEdit || type == Uia.ControlTypeDocument) && Uia.IsTextField(node) &&
                FieldLabels.BlockedRule(Uia.Metadata(node)) is not null) continue;
            queue.AddRange(Children(walker, node, MaxChildrenPerNode));
        }
        return string.Join("\n", parts);
    }

    /// <summary>First element of the given control type found breadth-first, optionally skipping Document subtrees.</summary>
    public static IUIAutomationElement? FindFirst(
        IUIAutomationElement root, Func<IUIAutomationElement, bool> match, bool skipDocuments, int maxNodes = 300)
    {
        var automation = Uia.Client;
        if (automation is null) return null;
        var walker = automation.get_ControlViewWalker();
        var clock = Stopwatch.StartNew();
        var queue = new List<IUIAutomationElement> { root };
        var next = 0;
        while (next < queue.Count && next < maxNodes && clock.Elapsed < Budget)
        {
            var node = queue[next++];
            if (next > 1 && match(node)) return node;
            if (skipDocuments && Uia.ControlType(node) == Uia.ControlTypeDocument) continue;
            queue.AddRange(Children(walker, node, MaxChildrenPerNode));
        }
        return null;
    }
}
