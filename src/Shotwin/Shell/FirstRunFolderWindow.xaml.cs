using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using Shotwin.Services;

namespace Shotwin.Shell;

/// <summary>
/// Asked once, on the first capture: where should shots go?
///
/// Choosing up front is what keeps Controlled Folder Access from ever becoming the
/// user's problem — a folder they picked is a folder they can write to, and the answer
/// arrives when it is obviously relevant rather than buried in a settings page they
/// have no reason to open yet.
/// </summary>
public partial class FirstRunFolderWindow : Window
{
    public string ChosenFolder { get; private set; } = FolderAccess.FallbackFolder;

    public FirstRunFolderWindow()
    {
        InitializeComponent();

        FlowDirection = Localisation.FlowDirection;

        // Suggest Pictures, because that is where people look for screenshots; the
        // write probe below is what decides whether it can actually be used.
        FolderBox.Text = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Shotwin");

        BrowseButton.Click += (_, _) => Browse();
        UseButton.Click += (_, _) => Accept(FolderBox.Text);
        DefaultButton.Click += (_, _) => Accept(FolderAccess.FallbackFolder);

        FolderBox.TextChanged += (_, _) => Problem.Visibility = Visibility.Collapsed;

        // Dragging is the only window chrome this dialog has.
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed) DragMove();
        };

        Loaded += (_, _) => { Activate(); FolderBox.Focus(); };
        SourceInitialized += (_, _) => Interop.WindowCorners.ApplyNative(this);
    }

    private void Browse()
    {
        var dialog = new OpenFolderDialog
        {
            Title = Localisation.Get("SettingsChooseFolderTitle"),
            InitialDirectory = Directory.Exists(FolderBox.Text)
                ? FolderBox.Text
                : Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };

        if (dialog.ShowDialog(this) == true)
            FolderBox.Text = dialog.FolderName;
    }

    private void Accept(string folder)
    {
        folder = folder?.Trim() ?? string.Empty;

        if (folder.Length == 0)
        {
            Warn(Localisation.Get("SettingsFirstRunPickFolder"));
            return;
        }

        if (!FolderAccess.IsWritable(folder))
        {
            Warn(Localisation.Get(FolderAccess.IsProtectedByDefault(folder)
                ? "SettingsFirstRunProtected"
                : "SettingsFirstRunUnwritable"));
            return;
        }

        ChosenFolder = folder;
        DialogResult = true;
        Close();
    }

    private void Warn(string message)
    {
        Problem.Text = message;
        Problem.Visibility = Visibility.Visible;
    }
}
