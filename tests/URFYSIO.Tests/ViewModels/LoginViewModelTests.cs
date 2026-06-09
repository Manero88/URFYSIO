#if INCLUDE_MAUI_TESTS
using Moq;
using URFYSIO.App.Services;
using URFYSIO.App.ViewModels;

namespace URFYSIO.Tests.ViewModels;

public class LoginViewModelTests
{
    private readonly Mock<IAuthService> _authServiceMock = new();

    private LoginViewModel CreateViewModel() => new(_authServiceMock.Object);

    // Helper: shorthand for setting up the password-grant method.
    private void SetupLogin(bool success, string? error = null) =>
        _authServiceMock
            .Setup(a => a.LoginWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((success, error));

    [Fact]
    public async Task LoginCommand_SuccessfulLogin_CallsAuthService()
    {
        // Arrange
        var vm = CreateViewModel();
        SetupLogin(success: true);
        _authServiceMock.Setup(a => a.CurrentRole).Returns("Client");

        // Act — Shell.Current is null in test context; navigation will throw NRE,
        // which the VM's catch block converts to an error message.
        await vm.LoginCommand.ExecuteAsync(null);

        // Assert — the password-grant method was called and IsBusy was cleared.
        _authServiceMock.Verify(
            a => a.LoginWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoginCommand_FailedLogin_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        SetupLogin(success: false, error: "Invalid email or password.");

        // Act
        await vm.LoginCommand.ExecuteAsync(null);

        // Assert
        Assert.Equal("Invalid email or password.", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoginCommand_FailedLoginWithNullError_SetsDefaultMessage()
    {
        // Arrange — Auth0 returned failure without an explicit error string.
        var vm = CreateViewModel();
        SetupLogin(success: false, error: null);

        // Act
        await vm.LoginCommand.ExecuteAsync(null);

        // Assert — VM falls back to "Login failed." when no error description is provided.
        Assert.Equal("Login failed.", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoginCommand_WhileBusy_DoesNotExecuteTwice()
    {
        // Arrange
        var vm = CreateViewModel();
        var tcs = new TaskCompletionSource<(bool, string?)>();
        _authServiceMock
            .Setup(a => a.LoginWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()))
            .Returns(tcs.Task);

        // Act — start first call (will hang on tcs)
        var firstCall = vm.LoginCommand.ExecuteAsync(null);

        // Second call should be no-op due to IsBusy guard
        _authServiceMock
            .Setup(a => a.LoginWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ReturnsAsync((false, "second"));
        await vm.LoginCommand.ExecuteAsync(null);

        // Complete the first call
        tcs.SetResult((false, null));
        await firstCall;

        // Assert — LoginWithPasswordAsync called only once (the second was blocked by SetBusy)
        _authServiceMock.Verify(
            a => a.LoginWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
    }

    [Fact]
    public async Task LoginCommand_ExceptionThrown_SetsErrorMessage()
    {
        // Arrange
        var vm = CreateViewModel();
        _authServiceMock
            .Setup(a => a.LoginWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()))
            .ThrowsAsync(new Exception("Network error"));

        // Act
        await vm.LoginCommand.ExecuteAsync(null);

        // Assert
        Assert.Contains("Network error", vm.ErrorMessage);
        Assert.False(vm.IsBusy);
    }

    [Fact]
    public async Task LoginCommand_AdminRole_AttemptsNavigation()
    {
        // Arrange
        var vm = CreateViewModel();
        SetupLogin(success: true);
        _authServiceMock.Setup(a => a.CurrentRole).Returns("Admin");

        // Act — Shell.Current is null so navigation throws NRE,
        // caught by the VM's catch block.
        await vm.LoginCommand.ExecuteAsync(null);

        // Assert — the auth call was made; IsBusy properly cleared.
        _authServiceMock.Verify(
            a => a.LoginWithPasswordAsync(It.IsAny<string>(), It.IsAny<string>()),
            Times.Once);
        Assert.False(vm.IsBusy);
    }
}
#endif
