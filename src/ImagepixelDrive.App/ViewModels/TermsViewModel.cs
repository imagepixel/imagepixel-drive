using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ImagepixelDrive.App.ViewModels;

/// <summary>
/// The very first page: the notes on use and liability in short, which the user
/// confirms once before anything else. The full text is opened from here.
/// </summary>
public partial class TermsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;

    public TermsViewModel(MainViewModel main)
    {
        _main = main;
    }

    /// <summary>Whether the user ticked that they read the notes and agree.</summary>
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ContinueCommand))]
    public partial bool Confirmed { get; set; }

    [RelayCommand]
    private void OpenTerms() => _main.OpenInFileManager(AboutViewModel.TermsUrl);

    [RelayCommand(CanExecute = nameof(Confirmed))]
    private Task ContinueAsync() => _main.AcceptTermsAsync();
}
