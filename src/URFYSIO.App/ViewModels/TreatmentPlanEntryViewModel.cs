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
    private readonly IPhotoPickerService _photoPicker;
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

    /// <summary>True while a comment is being posted — drives the spinner and blocks double-submits.</summary>
    [ObservableProperty] private bool _isPostingComment;

    /// <summary>
    /// The photo staged for the next comment, if any. Held until the post succeeds so a
    /// failed upload doesn't make the user pick it again.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedPhoto))]
    [NotifyPropertyChangedFor(nameof(SelectedPhotoPreview))]
    [NotifyPropertyChangedFor(nameof(CanSubmitComment))]
    private PickedPhoto? _selectedPhoto;

    public bool HasSelectedPhoto => SelectedPhoto is not null;

    /// <summary>Thumbnail for the staged photo. Rebuilt from the in-memory bytes on each read so the source is never a spent stream.</summary>
    public ImageSource? SelectedPhotoPreview => SelectedPhoto is null
        ? null
        : ImageSource.FromStream(() => SelectedPhoto.OpenStream());

    /// <summary>True when the device has a camera, so the UI can hide "Take photo" rather than offer a dead option.</summary>
    public bool IsCaptureSupported => _photoPicker.IsCaptureSupported;

    // Drives whether the inline "Add comment" Post button is enabled. A photo on its own is
    // a valid comment ("here's how it looks today"), so either text or a photo will do —
    // the API applies the same rule.
    public bool CanSubmitComment =>
        (!string.IsNullOrWhiteSpace(NewCommentText) || HasSelectedPhoto)
        && !IsCommentTooLong
        && !IsPostingComment;

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

    public TreatmentPlanEntryViewModel(
        TreatmentPlanEntryDto dto, IApiService api, IPhotoPickerService photoPicker)
    {
        Dto = dto;
        _api = api;
        _photoPicker = photoPicker;
        Comments.CollectionChanged += (_, _) => OnPropertyChanged(nameof(CommentCountLabel));
    }

    partial void OnIsPostingCommentChanged(bool value) => OnPropertyChanged(nameof(CanSubmitComment));

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

    /// <summary>
    /// Offers the camera / gallery choice, then stages the chosen photo. Split from
    /// <see cref="TakePhotoAsync"/> and <see cref="ChoosePhotoAsync"/> so the actual
    /// picking logic stays reachable (and testable) without the Shell action sheet.
    /// </summary>
    [RelayCommand]
    private async Task AddPhotoAsync()
    {
        // With no camera there is only one option — go straight to the gallery rather
        // than showing a one-item menu.
        if (!_photoPicker.IsCaptureSupported)
        {
            await ChoosePhotoAsync();
            return;
        }

        var choice = await Shell.Current.DisplayActionSheetAsync(
            "Add photo", "Cancel", null, "Take photo", "Choose from gallery");

        switch (choice)
        {
            case "Take photo": await TakePhotoAsync(); break;
            case "Choose from gallery": await ChoosePhotoAsync(); break;
        }
    }

    [RelayCommand]
    private async Task TakePhotoAsync() => Apply(await _photoPicker.CapturePhotoAsync());

    [RelayCommand]
    private async Task ChoosePhotoAsync() => Apply(await _photoPicker.PickPhotoAsync());

    /// <summary>
    /// Applies a pick result. A plain cancel leaves everything untouched and shows nothing;
    /// only a real failure (permission denied, unreadable file) produces a message.
    /// </summary>
    private void Apply(PhotoPickResult result)
    {
        if (result.Photo is not null)
        {
            SelectedPhoto = result.Photo;
            CommentsError = null;
            // Reveal the composer, otherwise the thumbnail lands in a collapsed panel.
            IsCommentsExpanded = true;
        }
        else if (result.Error is not null)
        {
            CommentsError = result.Error;
        }
    }

    [RelayCommand]
    private void RemovePhoto()
    {
        SelectedPhoto = null;
        CommentsError = null;
    }

    /// <summary>
    /// Opens an already-posted comment photo full-screen. The URL is the SAS minted when
    /// the thread was loaded; if it has since expired the viewer shows a "reopen the
    /// comments" message rather than a blank screen.
    /// </summary>
    [RelayCommand]
    private async Task ViewPhotoAsync(string? photoUrl)
    {
        if (string.IsNullOrWhiteSpace(photoUrl)) return;

        try
        {
            await Shell.Current.Navigation.PushModalAsync(new Views.PhotoViewerPage(photoUrl));
        }
        catch (Exception ex)
        {
            CommentsError = $"Could not open the photo: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task AddCommentAsync()
    {
        if (IsPostingComment) return;

        var text = NewCommentText?.Trim() ?? string.Empty;
        var photo = SelectedPhoto;

        // A photo alone is a valid comment; only reject when there's nothing at all.
        if (string.IsNullOrEmpty(text) && photo is null) return;

        // Client-side length guard — instant feedback, no wasted round-trip.
        if (text.Length > MaxCommentLength)
        {
            CommentsError = $"Comment cannot exceed {MaxCommentLength} characters " +
                            $"(currently {text.Length}).";
            return;
        }

        try
        {
            IsPostingComment = true;
            CommentsError = null;

            // PickedPhoto holds bytes, so this stream is fresh on every attempt — a retry
            // after a failed upload works without re-picking the photo.
            using var photoStream = photo?.OpenStream();
            var (added, error) = await _api.AddEntryCommentAsync(Id, text, photoStream, photo?.ContentType);

            if (added is not null)
            {
                Comments.Add(added);
                // Only clear the draft once the post actually succeeded.
                NewCommentText = string.Empty;
                SelectedPhoto = null;
                HasLoadedComments = true;
                // Make sure the thread is visible after posting — otherwise the user
                // would post and see nothing because the panel is still collapsed.
                IsCommentsExpanded = true;
            }
            else
            {
                // Surface the API's actual ProblemDetails message (validation reason,
                // forbidden, etc.) rather than a generic fallback. The typed text and the
                // staged photo are both left in place so the user can simply retry.
                CommentsError = string.IsNullOrWhiteSpace(error)
                    ? "Failed to add comment."
                    : error;
            }
        }
        catch (Exception ex)
        {
            CommentsError = $"Failed to add comment: {ex.Message}";
        }
        finally
        {
            IsPostingComment = false;
        }
    }
}
