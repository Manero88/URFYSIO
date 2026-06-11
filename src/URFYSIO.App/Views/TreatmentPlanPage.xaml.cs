using URFYSIO.App.ViewModels;

namespace URFYSIO.App.Views;

public partial class TreatmentPlanPage : ContentPage
{
    private readonly TreatmentPlanViewModel _vm;

    public TreatmentPlanPage(TreatmentPlanViewModel vm)
    {
        InitializeComponent();
        BindingContext = _vm = vm;
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        await _vm.LoadPlansCommand.ExecuteAsync(null);
    }
}
