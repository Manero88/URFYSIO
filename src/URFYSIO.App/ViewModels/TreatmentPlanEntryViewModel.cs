using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using URFYSIO.App.Services;
using URFYSIO.Shared.DTOs.TreatmentPlans;

namespace URFYSIO.App.ViewModels;

/// <summary>
/// Per-entry view-model. Wraps the immutable <see cref="TreatmentPlanEntryDto"/>
/// and adds the *mutable* state that lives on the page: the comment thread,
/// whether the thread is currently expanded, and the in-progress draft text.
/// We need a wrapper because we can't extend the shared DTO and because every
/// entry on the page needs its own independent expansion + draft state.
///
/// Comments are lazy-loaded: the first time the thread is expanded we hit
/// <see cref="IApiService.GetEntryCommentsAsync"/>; after that the local
/// collection is the source of truth (subsequent adds append in place, no
/// full re-fetch).
/// </summary>
public partial class TreatmentPlanEntryViewModel : ObservableObject
{
    // Must match the server-side cap (TreatmentPlanService.AddCommentAsync /
    // the TreatmentPlanEntryComment.Text column). Kept here so the UI can validate
    // and show a counter without a round-trip.
    public const int MaxCommentLength = 1000;

    private readonly IApiService _api;
    public TreatmentPlanEntryDto Dto { get; }

    // Proxied entry properties — XAML binds to these by name. We don't surface the
    // whole DTO because that would let templates accidentally rely on raw DTO mutation
    // (binding two-way to .IsCompleted, etc.).
    public Guid Id => Dto.Id;
    public Guid TreatmentPlanId => Dto.TreatmentPlanId;
    public string Title => Dto.Title;
    public string? Description => Dto.Description;
    public int OrderIndex => Dto.OrderIndex;
    public bool IsCompleted => Dto.IsCompleted;

    public ObservableCollection<TreatmentPlanEntryCommentDto> Comments { get; } = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ChevronGlyph))]
    private bool _isCommentsExpanded;

    [ObservableProperty] private bool _isLoadingComments;
    [ObservableProperty] private bool _hasLoadedComments;
    [ObservableProperty] private string _newCommentText = string.Empty;
    [ObservableProperty] private string? _commentsError;

    // Drives whether the inline "Add comment" Post button is enabled: must be
    // non-empty AND within the length limit.
    public bool CanSubmitComment =>
        !string.IsNullOrWhiteSpace(NewCommentText) && !IsCommentTooLong;

    public bool IsCommentTooLong => (NewCommentText?.Length ?? 0) > MaxCommentLength;

    // "X / 1000" counter shown under the composer. Turns red (via IsCommentTooLong)
    // once the draft exceeds the limit.
    public string CommentCharCountLabel => $"{NewCommentText?.Length ?? 0} / {MaxCommentLength}";

    // Header label for the toggle: shows the count once we know it, otherwise a
    // neutral CTA so the affordance is obvious even on entries with zero comments.
    public string CommentCountLabel => HasLoadedComments
        ? Comments.Count switch
        {
            0 => "Add comment",
            1 => "1 comment",
            _ => $"{Comments.Count} comments"
        }
        : "View comments";

    public string ChevronGlyph => IsCommentsExpanded ? "▾" : "▸";

    public TreatmentPlanEntryViewModel(TreatmentPlanEntryDto dto, IApiService api)
    {
        Dto = dto;
        _api = api;
        Comments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CommentCountLabel));
    }

    partial void OnNewCommentTextChanged(string value)
    {
        OnPropertyChanged(nameof(CanSubmitComment));
        OnPropertyChanged(nameof(IsCommentTooLong));
        OnPropertyChanged(nameof(CommentCharCountLabel));
    }

    partial void OnHasLoadedCommentsChanged(bool value) => OnPropertyChanged(nameof(CommentCountLabel));

    [RelayCommand]
    private async Task ToggleCommentsAsync()
    {
        IsCommentsExpanded = !IsCommentsExpanded;
        if (IsCommentsExpanded && !HasLoadedComments)
            await LoadCommentsAsync();
    }

    private async Task LoadCommentsAsync()
    {
        try
        {
            IsLoadingComments = true;
            CommentsError = null;
            var list = await _api.GetEntryCommentsAsync(Id);
            Comments.Clear();
            foreach (var c in list)
                Comments.Add(c);
            HasLoadedComments = true;
        }
        catch (Exception ex)
        {
            CommentsError = $"Failed to load comments: {ex.Message}";
        }
        finally
        {
            IsLoadingComments = false;
        }
    }

    [RelayCommand]
    private async Task AddCommentAsync()
    {
        var text = NewCommentText?.Trim();
        if (string.IsNullOrEmpty(text)) return;

        // Client-side length guard — instant feedback, no wasted round-trip.
        if (text.Length > MaxCommentLength)
        {
            CommentsError = $"Comment cannot exceed {MaxCommentLength} characters " +
                            $"(currently {text.Length}).";
            return;
        }

        try
        {
            CommentsError = null;
            var (added, error) = await _api.AddEntryCommentAsync(Id, text);
            if (added is not null)
            {
                Comments.Add(added);
                NewCommentText = string.Empty;
                HasLoadedComments = true;
                // Make sure the thread is visible after posting — otherwise the user
                // would post and see nothing because the panel is still collapsed.
                IsCommentsExpanded = true;
            }
            else
            {
                // Surface the API's actual ProblemDetails message (validation reason,
                // forbidden, etc.) rather than a generic fallback.
                CommentsError = string.IsNullOrWhiteSpace(error)
                    ? "Failed to add comment."
                    : error;
            }
        }
        catch (Exception ex)
        {
            CommentsError = $"Failed to add comment: {ex.Message}";
        }
    }
}
