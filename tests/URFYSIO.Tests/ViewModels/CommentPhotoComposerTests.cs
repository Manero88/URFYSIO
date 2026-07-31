#if INCLUDE_MAUI_TESTS
using Moq;
using URFYSIO.App.Services;
using URFYSIO.App.ViewModels;
using URFYSIO.Shared.DTOs.TreatmentPlans;

namespace URFYSIO.Tests.ViewModels;

/// <summary>
/// The comment composer's photo behaviour. The rules that matter to a patient: a photo
/// alone is a valid comment, a denied camera permission still leaves them able to type,
/// and a failed upload never costs them the text they wrote.
/// </summary>
public class CommentPhotoComposerTests
{
    private readonly Mock<IApiService> _api = new();
    private readonly Mock<IPhotoPickerService> _picker = new();

    private static readonly Guid EntryId = Guid.NewGuid();

    private static PickedPhoto SamplePhoto() => new("photo.jpg", "image/jpeg", [1, 2, 3, 4]);

    private TreatmentPlanEntryViewModel CreateViewModel()
    {
        _picker.SetupGet(p => p.IsCaptureSupported).Returns(true);
        var dto = new TreatmentPlanEntryDto { Id = EntryId, Title = "Squats" };
        return new TreatmentPlanEntryViewModel(dto, _api.Object, _picker.Object);
    }

    private static TreatmentPlanEntryCommentDto PostedComment(string text = "ok") =>
        new() { Id = Guid.NewGuid(), TreatmentPlanEntryId = EntryId, Text = text };

    // ---- Staging a photo ----

    [Fact]
    public async Task TakePhoto_StagesThePhotoAndExpandsTheThread()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.CapturePhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));

        await vm.TakePhotoCommand.ExecuteAsync(null);

        Assert.True(vm.HasSelectedPhoto);
        Assert.NotNull(vm.SelectedPhotoPreview);
        // Otherwise the thumbnail would land in a collapsed panel the user can't see.
        Assert.True(vm.IsCommentsExpanded);
        Assert.Null(vm.CommentsError);
    }

    [Fact]
    public async Task ChoosePhoto_StagesFromTheGallery()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));

        await vm.ChoosePhotoCommand.ExecuteAsync(null);

        Assert.True(vm.HasSelectedPhoto);
    }

    [Fact]
    public async Task Cancelling_ThePicker_ChangesNothingAndShowsNoError()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Cancelled());

        await vm.ChoosePhotoCommand.ExecuteAsync(null);

        Assert.False(vm.HasSelectedPhoto);
        // Backing out of the picker is not an error and must not look like one.
        Assert.Null(vm.CommentsError);
    }

    [Fact]
    public async Task PermissionDenied_ShowsTheReasonButLeavesTextCommentingUsable()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.CapturePhotoAsync())
               .ReturnsAsync(PhotoPickResult.Failed("Camera permission was not granted."));
        vm.NewCommentText = "Knee feels better";

        await vm.TakePhotoCommand.ExecuteAsync(null);

        Assert.False(vm.HasSelectedPhoto);
        Assert.Contains("permission", vm.CommentsError!, StringComparison.OrdinalIgnoreCase);
        // The whole point: no photo, but the comment can still be posted.
        Assert.Equal("Knee feels better", vm.NewCommentText);
        Assert.True(vm.CanSubmitComment);
    }

    [Fact]
    public async Task RemovePhoto_ClearsTheStagedPhoto()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));
        await vm.ChoosePhotoCommand.ExecuteAsync(null);

        vm.RemovePhotoCommand.Execute(null);

        Assert.False(vm.HasSelectedPhoto);
        Assert.Null(vm.SelectedPhotoPreview);
    }

    // ---- Submit rules ----

    [Fact]
    public void CanSubmit_IsFalseWithNeitherTextNorPhoto()
    {
        var vm = CreateViewModel();
        Assert.False(vm.CanSubmitComment);
    }

    [Fact]
    public async Task CanSubmit_IsTrueWithAPhotoAndNoText()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));
        await vm.ChoosePhotoCommand.ExecuteAsync(null);

        // The photo IS the feedback — requiring a caption would be busywork.
        Assert.True(vm.CanSubmitComment);
    }

    // ---- Posting ----

    [Fact]
    public async Task AddComment_SendsThePhotoAndClearsItOnSuccess()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));
        await vm.ChoosePhotoCommand.ExecuteAsync(null);
        vm.NewCommentText = "Swelling down";

        _api.Setup(a => a.AddEntryCommentAsync(EntryId, "Swelling down", It.IsAny<Stream>(), "image/jpeg"))
            .ReturnsAsync((PostedComment("Swelling down"), null));

        await vm.AddCommentCommand.ExecuteAsync(null);

        _api.Verify(a => a.AddEntryCommentAsync(EntryId, "Swelling down", It.IsAny<Stream>(), "image/jpeg"), Times.Once);
        Assert.Empty(vm.NewCommentText);
        Assert.False(vm.HasSelectedPhoto);
        Assert.Single(vm.Comments);
    }

    [Fact]
    public async Task AddComment_WithoutAPhoto_SendsNullStream()
    {
        var vm = CreateViewModel();
        vm.NewCommentText = "Text only";
        _api.Setup(a => a.AddEntryCommentAsync(EntryId, "Text only", null, null))
            .ReturnsAsync((PostedComment("Text only"), null));

        await vm.AddCommentCommand.ExecuteAsync(null);

        _api.Verify(a => a.AddEntryCommentAsync(EntryId, "Text only", null, null), Times.Once);
    }

    [Fact]
    public async Task AddComment_OnFailure_KeepsBothTheTextAndThePhoto()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));
        await vm.ChoosePhotoCommand.ExecuteAsync(null);
        vm.NewCommentText = "Worth keeping";

        _api.Setup(a => a.AddEntryCommentAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync((null, "Upload failed. Please try again."));

        await vm.AddCommentCommand.ExecuteAsync(null);

        // Losing a typed comment because an upload failed would be the worst outcome here.
        Assert.Equal("Worth keeping", vm.NewCommentText);
        Assert.True(vm.HasSelectedPhoto);
        Assert.Equal("Upload failed. Please try again.", vm.CommentsError);
        Assert.Empty(vm.Comments);
    }

    [Fact]
    public async Task AddComment_CanBeRetriedAfterAFailure()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));
        await vm.ChoosePhotoCommand.ExecuteAsync(null);
        vm.NewCommentText = "Retry me";

        _api.SetupSequence(a => a.AddEntryCommentAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync((null, "Network error"))
            .ReturnsAsync((PostedComment("Retry me"), null));

        await vm.AddCommentCommand.ExecuteAsync(null);
        await vm.AddCommentCommand.ExecuteAsync(null);

        // PickedPhoto holds bytes, so the second attempt gets a fresh stream rather than
        // a spent one — the retry works without re-picking the photo.
        Assert.Single(vm.Comments);
        Assert.Empty(vm.NewCommentText);
        Assert.False(vm.HasSelectedPhoto);
    }

    [Fact]
    public async Task AddComment_SurfacesTheApiValidationMessage()
    {
        var vm = CreateViewModel();
        _picker.Setup(p => p.PickPhotoAsync()).ReturnsAsync(PhotoPickResult.Success(SamplePhoto()));
        await vm.ChoosePhotoCommand.ExecuteAsync(null);

        _api.Setup(a => a.AddEntryCommentAsync(It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()))
            .ReturnsAsync((null, "Only JPEG and PNG images can be attached."));

        await vm.AddCommentCommand.ExecuteAsync(null);

        Assert.Equal("Only JPEG and PNG images can be attached.", vm.CommentsError);
    }

    [Fact]
    public async Task AddComment_DoesNothingWhenThereIsNeitherTextNorPhoto()
    {
        var vm = CreateViewModel();

        await vm.AddCommentCommand.ExecuteAsync(null);

        _api.Verify(a => a.AddEntryCommentAsync(
            It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<Stream>(), It.IsAny<string>()), Times.Never);
    }
}
#endif
