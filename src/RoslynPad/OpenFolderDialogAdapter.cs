using System.Composition;
using System.Windows.Forms;
using RoslynPad.UI;

namespace RoslynPad;

[Export(typeof(IOpenFolderDialog))]
internal class OpenFolderDialogAdapter : IOpenFolderDialog, IDisposable
{
    private readonly FolderBrowserDialog _dialog;

    public OpenFolderDialogAdapter()
    {
        _dialog = new FolderBrowserDialog();
    }

    public string InitialDirectory
    {
        get => _dialog.SelectedPath;
        set => _dialog.SelectedPath = value;
    }

    public Task<string?> ShowAsync()
    {
        var result = _dialog.ShowDialog();

        return Task.FromResult(result == DialogResult.OK ? _dialog.SelectedPath : null);
    }

    public void Dispose()
    {
        _dialog.Dispose();
    }
}
