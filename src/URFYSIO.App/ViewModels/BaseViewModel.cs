using CommunityToolkit.Mvvm.ComponentModel;

namespace URFYSIO.App.ViewModels;

public partial class BaseViewModel : ObservableObject
{
    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private string? _successMessage;

    protected bool SetBusy()
    {
        if (IsBusy) return false;
        IsBusy = true;
        ErrorMessage = null;
        SuccessMessage = null;
        return true;
    }

    protected void ClearBusy() => IsBusy = false;

    protected void SetSuccess(string message)
    {
        ErrorMessage = null;
        SuccessMessage = message;
    }

    protected void SetError(string message)
    {
        SuccessMessage = null;
        ErrorMessage = message;
    }
}
