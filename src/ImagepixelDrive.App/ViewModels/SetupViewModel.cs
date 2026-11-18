using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ImagepixelDrive.Core;
using ImagepixelDrive.Core.Rclone;

namespace ImagepixelDrive.App.ViewModels;

/// <summary>
/// First start. The customer says where the drive is stored and gives its password. A
/// place that holds a drive already is opened; an empty one gets a new drive.
/// </summary>
public partial class SetupViewModel : ViewModelBase
{
    private const int MinimumPasswordLength = 8;

    private readonly DriveManager _manager;
    private readonly MainViewModel _main;
    private string _region = ImagepixelStorage.DefaultRegion;
    private string? _prefix;
    private string? _apiUrl;
    private string? _apiToken;

    public SetupViewModel(DriveManager manager, MainViewModel main)
    {
        _manager = manager;
        _main = main;
    }

    /// <summary>Where the drive is stored. Imagepixel Storage is the recommended choice and preselected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsImagepixel), nameof(IsOwnPlace))]
    public partial StorageKind Kind { get; set; } = StorageKind.ImagepixelStorage;

    public bool IsImagepixel => Kind == StorageKind.ImagepixelStorage;

    public bool IsOwnPlace => Kind == StorageKind.LocalFolder;

    /// <summary>Cloud storage found on this computer, offered as a place with one click.</summary>
    public IReadOnlyList<CloudFolder> CloudFolders => _main.CloudFolders;

    public bool HasCloudFolders => CloudFolders.Count > 0;

    /// <summary>
    /// All the customer enters for Imagepixel Storage: the setup code carries the
    /// bucket and both keys.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeProblem))]
    public partial string SetupCodeText { get; set; } = "";

    /// <summary>Whether what was entered is a setup code of Imagepixel. The storage cannot be reached without one.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCodeProblem))]
    public partial bool CodeAccepted { get; set; }

    /// <summary>Something was entered, but it is not a setup code.</summary>
    public bool HasCodeProblem => !CodeAccepted && !string.IsNullOrWhiteSpace(SetupCodeText);

    // what the setup code said; none of it is shown or typed
    internal string Bucket { get; private set; } = "";

    internal string AccessKeyId { get; private set; } = "";

    internal string SecretAccessKey { get; private set; } = "";

    /// <summary>The folder the customer chose for a drive at a place of their own.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DriveFolder), nameof(DriveFolderHint), nameof(HasDriveFolderHint), nameof(CloudHint), nameof(HasCloudHint))]
    public partial string LocalPath { get; set; } = "";

    /// <summary>
    /// The folder the drive really lies in: a folder of its own inside the chosen one,
    /// unless the chosen one is a drive already. Null while nothing usable is entered.
    /// </summary>
    public string? DriveFolder
    {
        get
        {
            try
            {
                return string.IsNullOrWhiteSpace(LocalPath) ? null : DriveManager.ResolveDriveFolder(LocalPath);
            }
            catch (Exception e) when (e is ArgumentException or NotSupportedException or System.IO.IOException)
            {
                // what has been typed so far is not a path yet
                return null;
            }
        }
    }

    /// <summary>Tells where the drive ends up when that is not the chosen folder itself.</summary>
    public string? DriveFolderHint =>
        DriveFolder is { } folder && !string.Equals(folder, LocalPath.Trim().TrimEnd('\\', '/'), FolderRules.PathComparison)
            ? Texts.DriveFolder(folder)
            : null;

    public bool HasDriveFolderHint => DriveFolderHint is not null;

    /// <summary>Explains what it means to put the drive into the folder of a cloud service.</summary>
    public string? CloudHint =>
        DriveFolder is { } folder && CloudFolderDetector.Containing(folder, CloudFolders) is { } cloud
            ? Texts.CloudPlace(cloud.Name)
            : null;

    public bool HasCloudHint => CloudHint is not null;

    /// <summary>Shows the password while typing. It cannot be reset later, so typos matter.</summary>
    [ObservableProperty]
    public partial bool ShowPassword { get; set; }

    [ObservableProperty]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial string PasswordRepeat { get; set; } = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; set; }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    public partial bool IsBusy { get; set; }

    public bool CanEdit => !IsBusy;

    [ObservableProperty]
    public partial string BusyText { get; set; } = "";

    partial void OnSetupCodeTextChanged(string value)
    {
        if (SetupCode.TryDecode(value, out var code))
        {
            Bucket = code!.Bucket;
            AccessKeyId = code.AccessKeyId;
            SecretAccessKey = code.SecretAccessKey;
            _region = code.Region;
            _prefix = code.Prefix;
            _apiUrl = code.ApiUrl;
            _apiToken = code.ApiToken;
            CodeAccepted = true;
            ErrorMessage = null;
        }
        else
        {
            // nothing of a code entered before stays behind
            Bucket = "";
            AccessKeyId = "";
            SecretAccessKey = "";
            _region = ImagepixelStorage.DefaultRegion;
            _prefix = null;
            _apiUrl = null;
            _apiToken = null;
            CodeAccepted = false;
        }
    }

    [RelayCommand]
    private void ChooseKind(string kind)
    {
        Kind = Enum.Parse<StorageKind>(kind);
        ErrorMessage = null;
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        if (await _main.PickFolder("Ordner oder Festplatte für das Laufwerk auswählen", null) is { } path)
        {
            LocalPath = path;
            ErrorMessage = null;
        }
    }

    [RelayCommand]
    private void ChooseCloudFolder(CloudFolder folder)
    {
        LocalPath = folder.Path;
        ErrorMessage = null;
    }

    [RelayCommand]
    private void ShowAbout() => _main.ShowAbout();

    [RelayCommand]
    private async Task ConnectAsync()
    {
        ErrorMessage = Validate();
        if (HasError)
        {
            return;
        }

        var storage = new StorageSettings
        {
            Kind = Kind,

            // what a setup code said belongs to Imagepixel Storage only
            Bucket = IsImagepixel ? Bucket.Trim() : "",
            Region = _region,
            Prefix = _prefix ?? ImagepixelStorage.DefaultPrefix,
            AccessKeyId = IsImagepixel ? AccessKeyId.Trim() : "",
            LocalPath = IsOwnPlace ? DriveFolder! : "",
            ApiUrl = IsImagepixel ? _apiUrl ?? "" : "",
            Endpoint = Endpoint,
        };
        var secret = IsImagepixel ? SecretAccessKey.Trim() : null;
        var apiToken = IsImagepixel ? _apiToken : null;

        IsBusy = true;
        BusyText = "Verbindung wird geprüft …";
        try
        {
            var state = await _manager.ConnectAsync(storage, Password, secret, createIfMissing: false, apiToken: apiToken);
            if (state == DriveState.Empty)
            {
                // A new drive is about to be created. Its password cannot be recovered,
                // so it has to be long enough and typed twice.
                if (Password.Length < MinimumPasswordLength)
                {
                    ErrorMessage = $"An diesem Ort liegt noch kein Laufwerk. Für ein neues Laufwerk braucht das Passwort mindestens {MinimumPasswordLength} Zeichen.";
                    return;
                }

                if (Password != PasswordRepeat)
                {
                    ErrorMessage = "An diesem Ort liegt noch kein Laufwerk. Bitte wiederhole das Passwort, damit es neu angelegt werden kann.";
                    return;
                }

                BusyText = "Laufwerk wird angelegt …";
                state = await _manager.ConnectAsync(storage, Password, secret, createIfMissing: true, apiToken: apiToken);
            }

            switch (state)
            {
                case DriveState.WrongPassword:
                    ErrorMessage = "An diesem Ort liegt bereits ein Laufwerk, aber das Passwort passt nicht dazu.";
                    return;
                case DriveState.Unreachable:
                    ErrorMessage = IsImagepixel
                        ? Texts.StorageUnreachable
                        : "Der Ordner ist nicht erreichbar. Ist die Festplatte angeschlossen?";
                    return;
                case DriveState.Empty:
                    ErrorMessage = "Das Laufwerk konnte nicht angelegt werden.";
                    return;
            }

            BusyText = "Laufwerk wird geöffnet …";
            await _main.ShowDashboardAsync();
        }
        catch (Exception e)
        {
            ErrorMessage = Texts.Error(e);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The address of another storage service. Empty in normal use; set by tests.</summary>
    internal string Endpoint { get; set; } = "";

    private string? Validate()
    {
        if (IsImagepixel)
        {
            if (!CodeAccepted)
            {
                return HasCodeProblem ? Texts.SetupCodeProblem : Texts.SetupCodeMissing;
            }
        }
        else if (DriveFolder is null)
        {
            return "Bitte wähle einen Ordner oder eine Festplatte für das Laufwerk aus.";
        }

        // an existing drive may have any password, so only an empty one is rejected here
        return Password.Length == 0 ? "Bitte gib ein Passwort ein." : null;
    }
}
