﻿using Avalonia.Controls;
using AvaloniaEdit.Document;
using Microsoft.CodeAnalysis.Text;
using RoslynPad.Editor;
using RoslynPad.Build;
using RoslynPad.UI;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Folding;
using RoslynPad.Folding;

namespace RoslynPad;

partial class DocumentView : UserControl, IDisposable
{
    private readonly RoslynCodeEditor _editor;
    private OpenDocumentViewModel? _viewModel;
    private readonly DispatcherTimer _foldingUpdateTimer;
    private readonly FoldingManager _foldingManager;
    private readonly BraceFoldingStrategy _foldingStrategy;

    public DocumentView()
    {
        InitializeComponent();

        _editor = this.FindControl<RoslynCodeEditor>("Editor") ?? throw new InvalidOperationException("Missing Editor");

        _foldingStrategy = new BraceFoldingStrategy();
        _foldingManager = FoldingManager.Install(_editor.TextArea);

        _foldingUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _foldingUpdateTimer.Tick += (sender, e) 
            => _foldingStrategy.UpdateFoldings(_foldingManager, _editor.Document);
        
        _foldingUpdateTimer.Start();

        DataContextChanged += OnDataContextChanged;
    }


    public OpenDocumentViewModel ViewModel => _viewModel.NotNull();

    private async void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (DataContext is not OpenDocumentViewModel viewModel) return;
        _viewModel = viewModel;

        viewModel.NuGet.PackageInstalled += NuGetOnPackageInstalled;

        viewModel.EditorFocus += (o, e) => _editor.Focus();

        viewModel.MainViewModel.EditorFontSizeChanged += size => _editor.FontSize = size;
        viewModel.MainViewModel.ThemeChanged += OnThemeChanged;
        _editor.FontSize = viewModel.MainViewModel.EditorFontSize;
        _editor.FontFamily = new FontFamily(viewModel.MainViewModel.EditorFontFamily);

        var documentText = await viewModel.LoadTextAsync().ConfigureAwait(true);

        var documentId = await _editor.InitializeAsync(viewModel.MainViewModel.RoslynHost,
            new ThemeClassificationColors(viewModel.MainViewModel.Theme),
            viewModel.WorkingDirectory, documentText, viewModel.SourceCodeKind).ConfigureAwait(true);

        viewModel.Initialize(documentId, OnError,
            () => new TextSpan(_editor.SelectionStart, _editor.SelectionLength),
            this);
        

        _editor.Document.TextChanged += (o, e) => 
        {
            viewModel.OnTextChanged();
            _foldingStrategy.UpdateFoldings(_foldingManager, _editor.Document);
        };
    }
    private void OnThemeChanged(object? sender, EventArgs e)
    {
        Editor.ClassificationHighlightColors = new ThemeClassificationColors(ViewModel.MainViewModel.Theme);
        _foldingStrategy.UpdateFoldings(_foldingManager, _editor.Document);
    }

    private void NuGetOnPackageInstalled(PackageData package)
    {
        _ = this.GetDispatcher().InvokeAsync(() =>
        {
            var text = $"#r \"nuget: {package.Id}, {package.Version}\"{Environment.NewLine}";
            _editor.Document.Insert(0, text, AnchorMovementType.Default);
        });
    }

    private void OnError(ExceptionResultObject? e)
    {
    }

    public void Dispose()
    {
    }
}
