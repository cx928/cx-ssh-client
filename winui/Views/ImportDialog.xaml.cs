using Microsoft.UI.Xaml.Controls;

namespace CxSshClient.Views;

public enum ImportMode { Encrypted, Csv }

public sealed partial class ImportDialog : ContentDialog
{
    public ImportMode Mode { get; private set; } = ImportMode.Csv;
    public string Passphrase => PassBox.Password;

    public ImportDialog()
    {
        InitializeComponent();
    }

    private void Mode_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ModeButtons is null || PassBox is null) return;
        Mode = ModeButtons.SelectedIndex == 0 ? ImportMode.Encrypted : ImportMode.Csv;
        PassBox.IsEnabled = Mode == ImportMode.Encrypted;
    }
}
