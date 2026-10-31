using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using TidyWindow.Next.Core;

namespace TidyWindow.Next.Desktop;

public sealed class AddedBucketRow(AddedBucket bucket)
{
    public string Name => bucket.Name;
    public string Detail => bucket.Manifests == 0 ? "No apps found" : bucket.Manifests == 1 ? "1 app" : $"{bucket.Manifests:N0} apps";
}

public sealed class BucketRow(BucketOffer offer) : ObservableState
{
    public BucketOffer Offer { get; } = offer;
    public string Name => Offer.Name;
    public string Description => Offer.Description;
    public string Badge => Offer.Official ? "Official" : "Community";
    public string Repository => Offer.Repository;
    public string InstallLabel => $"Add the {Name} bucket";

    private bool adding, enabled = true;
    public bool Adding { get => adding; set { if (Set(ref adding, value)) { Changed(nameof(Action)); Changed(nameof(CanAdd)); } } }
    public string Action => Adding ? "Adding…" : "Add";
    /// <summary>Set by the panel: false while anything else is working, or when Scoop could not clone the bucket anyway.</summary>
    public bool Enabled { get => enabled; set { if (Set(ref enabled, value)) Changed(nameof(CanAdd)); } }
    public bool CanAdd => Enabled && !Adding;
}

/// <summary>What the expandable part of the Scoop row shows: the buckets already added, and the ones that can be.</summary>
public sealed class BucketsViewModel : ObservableState
{
    public ObservableCollection<AddedBucketRow> Added { get; } = [];
    public ObservableCollection<BucketRow> Available { get; } = [];

    private bool loading, gitMissing, locked, loaded;
    private string message = "", detail = "";
    private InfoBarSeverity severity = InfoBarSeverity.Informational;

    public bool Loaded => loaded;
    public bool Loading { get => loading; set { if (Set(ref loading, value)) Changed(nameof(LoadingVisibility)); } }
    public Visibility LoadingVisibility => Loading ? Visibility.Visible : Visibility.Collapsed;
    public string AddedTitle => Added.Count == 0 ? "Added buckets" : $"Added buckets ({Added.Count})";
    public string AvailableTitle => "Add more";
    public Visibility AddedVisibility => Added.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility AvailableVisibility => Available.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NothingToAddVisibility => loaded && Available.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public Visibility GitVisibility => gitMissing ? Visibility.Visible : Visibility.Collapsed;
    public string GitNote => ScoopBuckets.GitNeeded;

    public string Message { get => message; private set { if (Set(ref message, value)) { Changed(nameof(MessageVisibility)); } } }
    public Visibility MessageVisibility => Message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public string Detail { get => detail; private set { if (Set(ref detail, value)) Changed(nameof(DetailVisibility)); } }
    public Visibility DetailVisibility => Detail.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    public InfoBarSeverity MessageSeverity => severity;

    /// <summary>Informational while something is happening, then Success or Error once it has an outcome.</summary>
    public void Show(string text, InfoBarSeverity kind, string more = "")
    {
        severity = kind; Changed(nameof(MessageSeverity));
        Message = text; Detail = more;
    }
    public void ClearMessage() => Show("", InfoBarSeverity.Informational);

    /// <summary>True while another operation must not be disturbed.</summary>
    public bool Locked { get => locked; set { if (Set(ref locked, value)) RefreshEnabled(); } }

    public void Apply(BucketOverview overview)
    {
        gitMissing = overview.GitMissing;
        Added.Clear(); foreach (var bucket in overview.Added) Added.Add(new(bucket));
        // Keep a row that is mid-add where it is, so the button does not jump under the pointer.
        var adding = Available.Where(x => x.Adding).Select(x => x.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        Available.Clear(); foreach (var offer in overview.Available) Available.Add(new(offer) { Adding = adding.Contains(offer.Name) });
        loaded = true;
        RefreshEnabled();
        foreach (var name in new[] { nameof(AddedTitle), nameof(AddedVisibility), nameof(AvailableVisibility), nameof(NothingToAddVisibility), nameof(GitVisibility), nameof(Loaded) }) Changed(name);
    }

    private void RefreshEnabled() { foreach (var row in Available) row.Enabled = !locked && !gitMissing; }
}
