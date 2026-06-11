using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;

namespace URFYSIO.App.ViewModels;

/// <summary>
/// One header-row in the grouped treatment-plans list: everything a physio has for
/// a given client, collapsed by default so the page stays scannable when there are
/// many clients.
///
/// Extends <see cref="ObservableCollection{T}"/> so MAUI's <c>IsGrouped="True"</c>
/// CollectionView treats the instance as both the group metadata (ClientName,
/// IsExpanded, Count) and the group's items — no separate "Plans" property needed.
/// The view binds the *outer* CollectionView.ItemsSource to a collection of these,
/// and the inner panel to <c>{Binding .}</c> to iterate the contained plans.
///
/// IsExpanded is toggled from the header via a command on the host VM; we mutate
/// the in-place group rather than rebuilding because rebuilding would lose scroll
/// position and flicker.
///
/// Items are <see cref="TreatmentPlanWithEntriesViewModel"/> rather than raw DTOs
/// so each entry inside a plan can carry its own comment-thread state.
/// </summary>
public partial class ClientTreatmentPlansGroup : ObservableCollection<TreatmentPlanWithEntriesViewModel>
{
    public Guid ClientProfileId { get; }
    public string ClientName { get; }

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(IsExpanded)));
            OnPropertyChanged(new System.ComponentModel.PropertyChangedEventArgs(nameof(ChevronGlyph)));
        }
    }

    // ▸ when collapsed, ▾ when expanded. Kept as a plain string so the XAML doesn't
    // need a bool-to-glyph converter.
    public string ChevronGlyph => IsExpanded ? "▾" : "▸";

    public ClientTreatmentPlansGroup(
        Guid clientProfileId,
        string clientName,
        IEnumerable<TreatmentPlanWithEntriesViewModel> plans,
        bool isExpanded = false)
        : base(plans)
    {
        ClientProfileId = clientProfileId;
        ClientName = clientName;
        _isExpanded = isExpanded;
    }
}
