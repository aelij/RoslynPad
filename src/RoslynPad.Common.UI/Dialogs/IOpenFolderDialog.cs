namespace RoslynPad.UI;

public interface IOpenFolderDialog
{
    string InitialDirectory { get; set; }

    Task<string?> ShowAsync();
}
