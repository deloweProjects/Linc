using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace Linc.Desktop.Services;

/// <summary>What a live activity is about. The order is the default priority: a call beats everything.</summary>
public enum LiveActivityKind { Call, Link, Share, Mirror, Battery, Media }

/// <summary>
/// A button on an expanded activity. <see cref="Danger"/> draws it red (Decline, Stop); with a
/// <see cref="Glyph"/> the button is that icon and <see cref="Label"/> becomes its tooltip.
/// </summary>
public sealed record LiveAction(string Label, Action Invoke, bool Danger = false, string? Glyph = null);

/// <summary>
/// One thing happening right now, shown by the Linc Island (<c>Views.DynamicIsland</c>). Posting
/// an activity with an <see cref="Id"/> already shown replaces it, which is how progress moves.
/// <see cref="Progress"/> is 0–100, or null for no bar. <see cref="AutoExpand"/> opens the island
/// by itself when this activity reaches the top (a ringing call).
/// </summary>
public sealed record LiveActivity(
    string Id,
    LiveActivityKind Kind,
    string Glyph,
    string Title,
    string? Subtitle = null,
    string? Trailing = null,
    double? Progress = null,
    IReadOnlyList<LiveAction>? Actions = null,
    bool AutoExpand = false,
    ImageSource? Art = null)
{
    public int Priority => (int)Kind;
}

/// <summary>
/// The Linc Island's feed: features post what is happening now and end it when it stops; the
/// island shows the most important one and morphs as the list changes. Any thread may call it;
/// <see cref="Changed"/> is always raised on the UI thread.
/// </summary>
public interface ILiveActivityService
{
    /// <summary>Must be called once from the UI thread, before anything is posted.</summary>
    void Start();

    /// <summary>Everything live, most important first (lowest <see cref="LiveActivity.Priority"/>, then newest).</summary>
    IReadOnlyList<LiveActivity> Activities { get; }

    /// <summary>Raised on the UI thread after any post or end.</summary>
    event Action? Changed;

    /// <summary>A button asked to open one of the app's pages ("Share", "Home"…), by its Nav tag.</summary>
    event Action<string>? NavigateRequested;

    void Post(LiveActivity activity);
    void End(string id);

    /// <summary>Ends <paramref name="id"/> after <paramref name="delay"/> unless it is posted again in between.</summary>
    void EndAfter(string id, TimeSpan delay);

    void RequestNavigate(string pageTag);
}

public sealed class LiveActivityService : ILiveActivityService
{
    private readonly List<(LiveActivity Activity, long Seq)> _items = [];
    private readonly Dictionary<string, long> _endTokens = [];
    private DispatcherQueue? _dispatcher;
    private long _seq;

    public event Action? Changed;
    public event Action<string>? NavigateRequested;

    public IReadOnlyList<LiveActivity> Activities { get; private set; } = [];

    public void Start() => _dispatcher ??= DispatcherQueue.GetForCurrentThread();

    public void Post(LiveActivity activity) => OnUi(() =>
    {
        _endTokens.Remove(activity.Id);
        var index = _items.FindIndex(i => i.Activity.Id == activity.Id);
        if (index >= 0)
        {
            _items[index] = (activity, _items[index].Seq); // an update keeps its place
        }
        else
        {
            _items.Add((activity, ++_seq));
        }
        Publish();
    });

    public void End(string id) => OnUi(() =>
    {
        _endTokens.Remove(id);
        if (_items.RemoveAll(i => i.Activity.Id == id) > 0)
        {
            Publish();
        }
    });

    public void EndAfter(string id, TimeSpan delay) => OnUi(() =>
    {
        var token = ++_seq;
        _endTokens[id] = token;
        var timer = _dispatcher!.CreateTimer();
        timer.Interval = delay;
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            // Posted again since (a new share with the same name, the call ringing again): keep it.
            if (_endTokens.TryGetValue(id, out var current) && current == token)
            {
                End(id);
            }
        };
        timer.Start();
    });

    public void RequestNavigate(string pageTag) => OnUi(() => NavigateRequested?.Invoke(pageTag));

    private void Publish()
    {
        Activities = Order(_items);
        Changed?.Invoke();
    }

    /// <summary>Most important first; among equals the newest, so a second share shows over the first.</summary>
    internal static IReadOnlyList<LiveActivity> Order(IEnumerable<(LiveActivity Activity, long Seq)> items) =>
        items.OrderBy(i => i.Activity.Priority).ThenByDescending(i => i.Seq).Select(i => i.Activity).ToList();

    private void OnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}
