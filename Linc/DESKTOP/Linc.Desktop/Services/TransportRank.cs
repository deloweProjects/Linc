namespace Linc.Desktop.Services;

/// <summary>One connect candidate for <see cref="TransportRank.Choose"/> (M13f §2).</summary>
/// <param name="Name">Which transport this is (the supervisor names them by <see cref="LinkTransport"/>).</param>
/// <param name="Rank">Higher wins, once liveness has been accounted for.</param>
/// <param name="Answered">Has this transport actually answered? A candidate that has not loses its rank.</param>
public sealed record TransportCandidate(string Name, int Rank, bool Answered);

/// <summary>
/// The ranking rule (M13f §2): a transport that has not answered must lose its rank. Pure —
/// plain value logic, no sockets and no WinUI — so tools\hotspotsim compiles it verbatim and
/// proves the rule, and so the supervisor and the harness share one decision instead of two.
/// </summary>
public static class TransportRank
{
    /// <summary>
    /// The highest-ranked candidate that has <i>answered</i>, or null when none has. A stale
    /// entry — one that has not answered — is never chosen, whatever its rank.
    /// </summary>
    public static string? Choose(IReadOnlyList<TransportCandidate> candidates)
    {
        TransportCandidate? best = null;
        foreach (var candidate in candidates)
        {
            if (!candidate.Answered)
            {
                continue;
            }
            if (best is null || candidate.Rank > best.Rank)
            {
                best = candidate;
            }
        }
        return best?.Name;
    }

    /// <summary>
    /// The highest-ranked answering candidate, unless the user has picked a transport by hand and
    /// that one is answering — then theirs wins whatever the ranking says. Falls back to
    /// <see cref="Choose"/> when the hand-picked transport is absent or has stopped answering, and
    /// reports that it did so, because an override that silently evaporates is exactly the
    /// "it says wireless but it isn't" failure this exists to end (M15a Part C).
    /// </summary>
    /// <param name="candidates">The same candidates <see cref="Choose"/> takes.</param>
    /// <param name="manualOverride">The transport the user asked for, or null for automatic.</param>
    public static TransportChoice ChooseWithOverride(
        IReadOnlyList<TransportCandidate> candidates, string? manualOverride)
    {
        var automatic = Choose(candidates);
        if (string.IsNullOrWhiteSpace(manualOverride))
        {
            return new TransportChoice(automatic, ByHand: false, OverrideDropped: false);
        }

        var wanted = manualOverride!.Trim();
        foreach (var candidate in candidates)
        {
            if (!string.Equals(candidate.Name, wanted, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            // A hand-picked transport still has to be alive. Honouring one that has not answered
            // would reinstate the very bug M13f fixed for the automatic path.
            return candidate.Answered
                ? new TransportChoice(candidate.Name, ByHand: true, OverrideDropped: false)
                : new TransportChoice(automatic, ByHand: false, OverrideDropped: true);
        }
        return new TransportChoice(automatic, ByHand: false, OverrideDropped: true);
    }
}

/// <summary>The outcome of <see cref="TransportRank.ChooseWithOverride"/>.</summary>
/// <param name="Name">The chosen transport, or null when nothing is answering.</param>
/// <param name="ByHand">True when <paramref name="Name"/> is the user's hand-picked transport.</param>
/// <param name="OverrideDropped">
/// True when a hand-picked transport was asked for but could not be honoured — the caller must
/// say so in the caption and clear the override, never fail quietly.
/// </param>
public sealed record TransportChoice(string? Name, bool ByHand, bool OverrideDropped);
