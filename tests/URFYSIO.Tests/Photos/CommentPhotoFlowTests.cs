using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using SkiaSharp;
using URFYSIO.API.Controllers;
using URFYSIO.Core.Entities;
using URFYSIO.Core.Enums;
using URFYSIO.Core.Exceptions;
using URFYSIO.Infrastructure.Data;
using URFYSIO.Infrastructure.Services;
using URFYSIO.Shared.DTOs.TreatmentPlans;
using URFYSIO.Shared.Photos;

namespace URFYSIO.Tests.Photos;

/// <summary>
/// End-to-end behaviour of "comment with a photo" (User Story 5): the blob name is what
/// gets persisted, a fresh SAS URL is what comes back, and the private container is never
/// exposed as a durable link.
/// </summary>
public class CommentPhotoFlowTests
{
    private static AppDbContext GetDbContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        var db = new AppDbContext(options);
        db.Database.EnsureCreated();
        return db;
    }

    private static byte[] MakeJpeg(int width = 800, int height = 600)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap)) canvas.Clear(SKColors.SeaGreen);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Jpeg, 90);
        return data.ToArray();
    }

    private static IFormFile FormFileFor(byte[] bytes, string contentType = "image/jpeg", string name = "photo.jpg") =>
        new FormFile(new MemoryStream(bytes), 0, bytes.Length, "photo", name)
        {
            Headers = new HeaderDictionary(),
            ContentType = contentType
        };

    private static ControllerContext ContextFor(Guid localUserId, string role)
    {
        var http = new DefaultHttpContext();
        http.Items["LocalUserId"] = localUserId;
        http.User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.Role, role)],
            authenticationType: "Test", nameType: ClaimTypes.NameIdentifier, roleType: ClaimTypes.Role));
        return new ControllerContext { HttpContext = http };
    }

    /// <summary>Seeds a client with a plan and one entry, which is the shape every test here needs.</summary>
    private static (User Client, TreatmentPlanEntry Entry) SeedPlanWithEntry(AppDbContext db)
    {
        var clientUser = new User
        {
            Id = Guid.NewGuid(), FirstName = "Cli", LastName = "Ent",
            Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "", Role = UserRole.Client
        };
        var clientProfile = new ClientProfile { Id = Guid.NewGuid(), UserId = clientUser.Id, User = clientUser };

        var physioUser = new User
        {
            Id = Guid.NewGuid(), FirstName = "Phys", LastName = "Io",
            Email = $"{Guid.NewGuid()}@test.com", PasswordHash = "", Role = UserRole.Physiotherapist
        };
        var physioProfile = new PhysiotherapistProfile { Id = Guid.NewGuid(), UserId = physioUser.Id, User = physioUser };

        var plan = new TreatmentPlan
        {
            Id = Guid.NewGuid(), ClientProfileId = clientProfile.Id, PhysiotherapistProfileId = physioProfile.Id,
            Title = "Knee rehab", CreatedAt = DateTime.UtcNow
        };
        var entry = new TreatmentPlanEntry
        {
            Id = Guid.NewGuid(), TreatmentPlanId = plan.Id, Title = "Squats", OrderIndex = 0
        };

        db.Users.AddRange(clientUser, physioUser);
        db.ClientProfiles.Add(clientProfile);
        db.PhysiotherapistProfiles.Add(physioProfile);
        db.TreatmentPlans.Add(plan);
        db.TreatmentPlanEntries.Add(entry);
        db.SaveChanges();

        return (clientUser, entry);
    }

    private static TreatmentPlansController CreateController(
        AppDbContext db, FakeBlobStorageService blobs, Guid userId, string role)
    {
        var controller = new TreatmentPlansController(
            new TreatmentPlanService(db),
            new UserService(db),
            blobs,
            Mock.Of<ILogger<TreatmentPlansController>>())
        {
            ControllerContext = ContextFor(userId, role)
        };
        return controller;
    }

    // ---- Storing the blob name ----

    [Fact]
    public async Task AddComment_WithPhoto_StoresBlobNameAndReturnsSasUrl()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        var result = await controller.AddEntryComment(entry.Id, "Knee looks swollen today", FormFileFor(MakeJpeg()));

        var created = Assert.IsType<CreatedResult>(result);
        var dto = Assert.IsType<TreatmentPlanEntryCommentDto>(created.Value);

        // The URL is minted per response, and it is a SAS — never a bare container URL.
        Assert.NotNull(dto.PhotoUrl);
        Assert.Contains("sig=", dto.PhotoUrl);
        Assert.True(dto.HasPhoto);

        // What's persisted is the opaque blob name, not the URL.
        var stored = await db.TreatmentPlanEntryComments.SingleAsync();
        Assert.Equal(blobs.Uploaded.Single(), stored.PhotoBlobName);
        Assert.DoesNotContain("http", stored.PhotoBlobName!);
    }

    [Fact]
    public async Task AddComment_WithoutPhoto_StoresNoBlobAndReturnsNoUrl()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        var result = await controller.AddEntryComment(entry.Id, "Feeling better", photo: null);

        var created = Assert.IsType<CreatedResult>(result);
        var dto = Assert.IsType<TreatmentPlanEntryCommentDto>(created.Value);

        Assert.Null(dto.PhotoUrl);
        Assert.False(dto.HasPhoto);
        Assert.Empty(blobs.Uploaded);
        Assert.Null((await db.TreatmentPlanEntryComments.SingleAsync()).PhotoBlobName);
    }

    [Fact]
    public async Task AddComment_PhotoOnlyWithNoText_IsAccepted()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        // "Here's how it looks" is a complete contribution on its own — the photo IS the feedback.
        var result = await controller.AddEntryComment(entry.Id, text: null, FormFileFor(MakeJpeg()));

        var created = Assert.IsType<CreatedResult>(result);
        var dto = Assert.IsType<TreatmentPlanEntryCommentDto>(created.Value);
        Assert.True(dto.HasPhoto);
        Assert.Equal(string.Empty, dto.Text);
    }

    [Fact]
    public async Task AddComment_WithNeitherTextNorPhoto_IsRejected()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var controller = CreateController(db, new FakeBlobStorageService(), client.Id, "Client");

        var ex = await Assert.ThrowsAsync<DomainException>(
            () => controller.AddEntryComment(entry.Id, text: "  ", photo: null));

        Assert.Equal(400, ex.StatusCode);
    }

    [Fact]
    public async Task AddComment_PhysioCanAlsoAttachAPhoto()
    {
        using var db = GetDbContext();
        var (_, entry) = SeedPlanWithEntry(db);
        var physio = await db.Users.FirstAsync(u => u.Role == UserRole.Physiotherapist);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, physio.Id, "Physiotherapist");

        var result = await controller.AddEntryComment(entry.Id, "Compare with last week", FormFileFor(MakeJpeg()));

        Assert.IsType<CreatedResult>(result);
        Assert.Single(blobs.Uploaded);
    }

    // ---- Validation ----

    [Fact]
    public async Task AddComment_RejectsNonImageAndUploadsNothing()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        var pdf = Encoding.UTF8.GetBytes("%PDF-1.7 fake");
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => controller.AddEntryComment(entry.Id, "notes", FormFileFor(pdf, "application/pdf", "notes.pdf")));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("JPEG", ex.Message);
        // Rejected before touching storage — a bad upload must cost nothing.
        Assert.Empty(blobs.Uploaded);
        Assert.Empty(db.TreatmentPlanEntryComments);
    }

    [Fact]
    public async Task AddComment_RejectsOversizedPhotoAndUploadsNothing()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        var tooBig = new byte[PhotoRules.MaxBytes + 1];
        var ex = await Assert.ThrowsAsync<DomainException>(
            () => controller.AddEntryComment(entry.Id, "big", FormFileFor(tooBig)));

        Assert.Equal(400, ex.StatusCode);
        Assert.Contains("too large", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(blobs.Uploaded);
    }

    [Fact]
    public async Task AddComment_RejectsAMislabelledNonImage()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var controller = CreateController(db, new FakeBlobStorageService(), client.Id, "Client");

        // Content-Type says JPEG but the bytes aren't. Only decoding catches this, which is
        // why the header alone is never treated as proof.
        var notAnImage = Encoding.UTF8.GetBytes("definitely not an image");

        await Assert.ThrowsAsync<DomainException>(
            () => controller.AddEntryComment(entry.Id, "sneaky", FormFileFor(notAnImage)));
    }

    // ---- Reading a thread ----

    [Fact]
    public async Task GetComments_MintsAFreshSasUrlForEachPhoto()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        await controller.AddEntryComment(entry.Id, "one", FormFileFor(MakeJpeg()));
        await controller.AddEntryComment(entry.Id, "two", photo: null);
        blobs.SasRequested.Clear();

        var result = await controller.GetEntryComments(entry.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dtos = Assert.IsAssignableFrom<IEnumerable<TreatmentPlanEntryCommentDto>>(ok.Value).ToList();

        Assert.Equal(2, dtos.Count);
        Assert.Single(dtos, d => d.HasPhoto);
        Assert.Single(dtos, d => !d.HasPhoto);
        // Regenerated on read rather than served from the database.
        Assert.Single(blobs.SasRequested);
    }

    [Fact]
    public async Task GetComments_StillReturnsTheThreadWhenSasGenerationFails()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");
        await controller.AddEntryComment(entry.Id, "Swelling is down", FormFileFor(MakeJpeg()));

        // Storage goes bad after the fact.
        blobs.SasException = new InvalidOperationException("storage unavailable");

        var result = await controller.GetEntryComments(entry.Id);

        var ok = Assert.IsType<OkObjectResult>(result);
        var dto = Assert.IsAssignableFrom<IEnumerable<TreatmentPlanEntryCommentDto>>(ok.Value).Single();

        // The text is the more important half — one unreadable blob must not blank the conversation.
        Assert.Equal("Swelling is down", dto.Text);
        Assert.Null(dto.PhotoUrl);
        Assert.False(dto.HasPhoto);
    }

    // ---- Cleanup ----

    [Fact]
    public async Task AddComment_DeletesTheUploadedBlobIfSavingTheCommentFails()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        // Over-length text: the photo uploads successfully, then the insert rejects it.
        // (A missing entry wouldn't exercise this — the controller checks that before
        // touching storage, so nothing would have been uploaded to orphan.)
        var tooLongText = new string('a', 1001);

        await Assert.ThrowsAsync<DomainException>(
            () => controller.AddEntryComment(entry.Id, tooLongText, FormFileFor(MakeJpeg())));

        // Nothing references the blob any more, so it must not be left behind.
        Assert.Single(blobs.Uploaded);
        Assert.Equal(blobs.Uploaded.Single(), blobs.Deleted.Single());
        Assert.Empty(blobs.Blobs);
        Assert.Empty(db.TreatmentPlanEntryComments);
    }

    [Fact]
    public async Task AddComment_ReturnsNotFoundForAnUnknownEntryWithoutUploading()
    {
        using var db = GetDbContext();
        var (client, _) = SeedPlanWithEntry(db);
        var blobs = new FakeBlobStorageService();
        var controller = CreateController(db, blobs, client.Id, "Client");

        var result = await controller.AddEntryComment(Guid.NewGuid(), "orphan", FormFileFor(MakeJpeg()));

        Assert.IsType<NotFoundResult>(result);
        // The existence check runs first, so a bad entry id never costs an upload.
        Assert.Empty(blobs.Uploaded);
    }

    [Fact]
    public async Task DeleteEntry_RemovesItsCommentPhotos()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var physio = await db.Users.FirstAsync(u => u.Role == UserRole.Physiotherapist);
        var blobs = new FakeBlobStorageService();

        var clientController = CreateController(db, blobs, client.Id, "Client");
        await clientController.AddEntryComment(entry.Id, "photo one", FormFileFor(MakeJpeg()));
        await clientController.AddEntryComment(entry.Id, "photo two", FormFileFor(MakeJpeg()));

        var physioController = CreateController(db, blobs, physio.Id, "Physiotherapist");
        var result = await physioController.DeleteEntry(entry.Id);

        Assert.IsType<NoContentResult>(result);
        // Comments cascade with the entry, so their photos must go too rather than linger.
        Assert.Equal(2, blobs.Deleted.Count);
        Assert.Empty(blobs.Blobs);
    }

    [Fact]
    public async Task DeleteEntry_SucceedsEvenIfBlobDeletionFails()
    {
        using var db = GetDbContext();
        var (client, entry) = SeedPlanWithEntry(db);
        var physio = await db.Users.FirstAsync(u => u.Role == UserRole.Physiotherapist);
        var blobs = new FakeBlobStorageService();

        await CreateController(db, blobs, client.Id, "Client")
            .AddEntryComment(entry.Id, "photo", FormFileFor(MakeJpeg()));

        blobs.DeleteException = new InvalidOperationException("storage unavailable");

        var result = await CreateController(db, blobs, physio.Id, "Physiotherapist").DeleteEntry(entry.Id);

        // The entry is already gone from the database; a storage hiccup must not report
        // failure to the physio for work that actually succeeded.
        Assert.IsType<NoContentResult>(result);
        Assert.Empty(await db.TreatmentPlanEntries.ToListAsync());
    }
}
