using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using NuGet.Packaging;
using RoslynPad.Build;
using RoslynPad.Roslyn;
using RoslynPad.Utilities;

namespace RoslynPad.UI
{
    public class MainViewModelBase : NotificationObject
    {
        private static readonly Version s_currentVersion = new(16, 0);
        private static readonly string s_currentVersionVariant = "";

        private readonly IServiceProvider _serviceProvider;
        private readonly ITelemetryProvider _telemetryProvider;
        private readonly ICommandProvider _commands;
        private readonly DocumentFileWatcher _documentFileWatcher;


        private OpenDocumentViewModel? _currentOpenDocument;
        private bool _hasUpdate;
        private double _editorFontSize;
        private string? _searchText;
        private bool _isWithinSearchResults;
        private bool _isInitialized;
        private DocumentViewModel _documentRoot;
        private DocumentWatcher _documentWatcher;

        public IApplicationSettingsValues Settings { get; }

        public DocumentViewModel DocumentRoot
        {
            get => _documentRoot;
            private set => SetProperty(ref _documentRoot, value);
        }

        public RoslynHost RoslynHost { get; private set; }

        public bool IsInitialized
        {
            get => _isInitialized;
            private set
            {
                SetProperty(ref _isInitialized, value);
                OnPropertyChanged(nameof(HasNoOpenDocuments));
            }
        }

#pragma warning disable CS8618 // Non-nullable field is uninitialized.
        public MainViewModelBase(IServiceProvider serviceProvider, ITelemetryProvider telemetryProvider, ICommandProvider commands, IApplicationSettings settings, NuGetViewModel nugetViewModel, DocumentFileWatcher documentFileWatcher)
#pragma warning restore CS8618 // Non-nullable field is uninitialized.
        {
            _serviceProvider = serviceProvider;
            _telemetryProvider = telemetryProvider;
            _commands = commands;
            _documentFileWatcher = documentFileWatcher;

            settings.LoadDefault();
            Settings = settings.Values;

            _telemetryProvider.Initialize(s_currentVersion.ToString(), settings);
            _telemetryProvider.LastErrorChanged += () =>
            {
                OnPropertyChanged(nameof(LastError));
                OnPropertyChanged(nameof(HasError));
            };

            NuGet = nugetViewModel;

            NewDocumentCommand = commands.Create<SourceCodeKind>(CreateNewDocument);
            OpenFileCommand = commands.CreateAsync(OpenFile);
            CloseCurrentDocumentCommand = commands.CreateAsync(CloseCurrentDocument);
            CloseDocumentCommand = commands.CreateAsync<OpenDocumentViewModel>(CloseDocument);
            ClearErrorCommand = commands.Create(() => _telemetryProvider.ClearLastError());
            ReportProblemCommand = commands.Create(ReportProblem);
            EditUserDocumentPathCommand = commands.Create(EditUserDocumentPath);
            ToggleOptimizationCommand = commands.Create(() => Settings.OptimizeCompilation = !Settings.OptimizeCompilation);

            _editorFontSize = Settings.EditorFontSize;

            DocumentRoot = CreateDocumentRoot();

            OpenDocuments = new ObservableCollection<OpenDocumentViewModel>();
            OpenDocuments.CollectionChanged += (sender, args) => OnPropertyChanged(nameof(HasNoOpenDocuments));
        }

        public async Task Initialize()
        {
            if (IsInitialized) return;

            try
            {
                await InitializeInternal().ConfigureAwait(true);

                IsInitialized = true;
            }
            catch (Exception e)
            {
                _telemetryProvider.ReportError(e);
            }
        }

        protected virtual ImmutableArray<Assembly> CompositionAssemblies => ImmutableArray.Create(
            typeof(MainViewModelBase).Assembly);

        private async Task InitializeInternal()
        {
            RoslynHost = await Task.Run(() => new RoslynHost(CompositionAssemblies,
                RoslynHostReferences.NamespaceDefault.With(imports: new[] { "RoslynPad.Runtime" }),
                disabledDiagnostics: ImmutableArray.Create("CS1701", "CS1702", "CS7011", "CS8097")))
                .ConfigureAwait(true);

            OpenDocumentFromCommandLine();
            await OpenAutoSavedDocuments().ConfigureAwait(true);

            if (HasCachedUpdate())
            {
                HasUpdate = true;
            }
            else
            {
                var task = Task.Run(CheckForUpdates);
            }
        }

        private void OpenDocumentFromCommandLine()
        {
            string[] args = Environment.GetCommandLineArgs();

            if (args.Length > 1)
            {
                string filePath = args[1];

                if (File.Exists(filePath))
                {
                    var document = DocumentViewModel.FromPath(filePath);
                    OpenDocument(document);
                }
            }
        }

        private async Task OpenAutoSavedDocuments()
        {
            var documents = await Task.Run(() => LoadAutoSavedDocuments(DocumentRoot.Path)).ConfigureAwait(true);

            OpenDocuments.AddRange(documents);

            if (OpenDocuments.Count == 0)
            {
                CreateNewDocument();
            }
            else
            {
                CurrentOpenDocument = OpenDocuments[0];
            }
        }

        private IEnumerable<OpenDocumentViewModel> LoadAutoSavedDocuments(string root)
        {
            return IOUtilities.EnumerateFilesRecursive(root, $"*{DocumentViewModel.AutoSaveSuffix}.*")
                .Select(d => DocumentViewModel.FromPath(d))
                .Where(d => IsRelevantDocument(d))
                .Select(d => GetOpenDocumentViewModel(d));
        }

        private OpenDocumentViewModel GetOpenDocumentViewModel(DocumentViewModel? documentViewModel = null)
        {
            var d = _serviceProvider.GetRequiredService<OpenDocumentViewModel>();
            d.SetDocument(documentViewModel);
            return d;
        }

        public string WindowTitle
        {
            get
            {
                var currentVersion = s_currentVersion.Minor <= 0 && s_currentVersion.Build <= 0
                    ? s_currentVersion.Major.ToString()
                    : s_currentVersion.ToString();
                var title = "RoslynPad " + currentVersion;
                if (!string.IsNullOrEmpty(s_currentVersionVariant))
                {
                    title += "-" + s_currentVersionVariant;
                }
                return title;
            }
        }

        private static void ReportProblem()
        {
            _ = Task.Run(() => Process.Start(
                new ProcessStartInfo
                {
                    FileName = "https://github.com/aelij/RoslynPad/issues",
                    UseShellExecute = true,
                }));
        }

        public bool HasUpdate
        {
            get => _hasUpdate; private set => SetProperty(ref _hasUpdate, value);
        }

        private bool HasCachedUpdate()
        {
            return Version.TryParse(Settings.LatestVersion, out var latestVersion) &&
                   latestVersion > s_currentVersion;
        }

        private async Task CheckForUpdates()
        {
            string latestVersionString;
            using (var client = new System.Net.Http.HttpClient())
            {
                try
                {
                    latestVersionString = await client.GetStringAsync("https://roslynpad.net/latest").ConfigureAwait(false);
                }
                catch
                {
                    return;
                }
            }

            if (Version.TryParse(latestVersionString, out var latestVersion))
            {
                if (latestVersion > s_currentVersion)
                {
                    HasUpdate = true;
                }
                Settings.LatestVersion = latestVersionString;
            }
        }

        private DocumentViewModel CreateDocumentRoot()
        {
            _documentWatcher?.Dispose();
            var root = DocumentViewModel.CreateRoot(Settings.EffectiveDocumentPath);
            _documentWatcher = new DocumentWatcher(_documentFileWatcher, root);
            return root;
        }

        public void EditUserDocumentPath()
        {
            var dialog = _serviceProvider.GetRequiredService<IFolderBrowserDialog>();
            dialog.ShowEditBox = true;
            dialog.SelectedPath = Settings.EffectiveDocumentPath;

            if (dialog.Show() == true)
            {
                string documentPath = dialog.SelectedPath;
                if (!DocumentRoot.Path.Equals(documentPath, StringComparison.OrdinalIgnoreCase))
                {
                    Settings.DocumentPath = documentPath;

                    DocumentRoot = CreateDocumentRoot();
                }
            }
        }

        public NuGetViewModel NuGet { get; }

        public ObservableCollection<OpenDocumentViewModel> OpenDocuments { get; }

        public OpenDocumentViewModel? CurrentOpenDocument
        {
            get => _currentOpenDocument;
            set
            {
                if (value == null) return; // prevent binding from clearing the value
                SetProperty(ref _currentOpenDocument, value);
            }
        }

        private void ClearCurrentOpenDocument()
        {
            if (_currentOpenDocument == null) return;
            _currentOpenDocument = null;
            OnPropertyChanged(nameof(CurrentOpenDocument));
        }

        public string NewCSDocumentGesture => KeybindHelper.GetSequenceStr(KEY_BIND.NewCSDocument);
        public string NewCSXScriptGesture => KeybindHelper.GetSequenceStr(KEY_BIND.NewCSXScript);
        public IDelegateCommand<SourceCodeKind> NewDocumentCommand { get; }
        public string OpenFileGesture => KeybindHelper.GetSequenceStr(KEY_BIND.OpenFile);
        public IDelegateCommand OpenFileCommand { get; }

        public IDelegateCommand EditUserDocumentPathCommand { get; }
        public string CloseCurrentDocumentGesture => KeybindHelper.GetSequenceStr(KEY_BIND.CloseCurrentFile);

        public IDelegateCommand CloseCurrentDocumentCommand { get; }

        public IDelegateCommand<OpenDocumentViewModel> CloseDocumentCommand { get; }
        public string ToggleOptimizationGesture => KeybindHelper.GetSequenceStr(KEY_BIND.ToggleDebugMode);
        public IDelegateCommand ToggleOptimizationCommand { get; }

        public void OpenDocument(DocumentViewModel document)
        {
            if (document.IsFolder) return;

            var openDocument = OpenDocuments.FirstOrDefault(x => x.Document?.Path != null && string.Equals(x.Document.Path, document.Path, StringComparison.Ordinal));
            if (openDocument == null)
            {
                openDocument = GetOpenDocumentViewModel(document);
                OpenDocuments.Add(openDocument);
            }

            CurrentOpenDocument = openDocument;
        }

        public async Task OpenFile()
        {
            if (!IsInitialized) return;

            var dialog = _serviceProvider.GetRequiredService<IOpenFileDialog>();
            dialog.Filter = new FileDialogFilter("C# Files", "cs", "csx");
            var fileNames = await dialog.ShowAsync().ConfigureAwait(true);
            if (fileNames == null)
            {
                return;
            }

            // make sure we use the normalized path, in case the user used the wrong capitalization on Windows
            var filePath = IOUtilities.NormalizeFilePath(fileNames.First());
            var document = DocumentViewModel.FromPath(filePath);
            if (!document.IsAutoSave)
            {
                var autoSavePath = document.GetAutoSavePath();
                if (File.Exists(autoSavePath))
                {
                    document = DocumentViewModel.FromPath(autoSavePath);
                }
            }

            OpenDocument(document);
        }

        public void CreateNewDocument(SourceCodeKind kind = SourceCodeKind.Regular)
        {
            var openDocument = GetOpenDocumentViewModel();
            openDocument.SourceCodeKind = kind;
            OpenDocuments.Add(openDocument);
            CurrentOpenDocument = openDocument;
        }

        public async Task CloseDocument(OpenDocumentViewModel? document)
        {
            if (document == null)
            {
                return;
            }

            var result = await document.SaveAsync(promptSave: true).ConfigureAwait(true);
            if (result == SaveResult.Cancel)
            {
                return;
            }

            if (document.DocumentId != null)
            {
                RoslynHost.CloseDocument(document.DocumentId);
            }

            OpenDocuments.Remove(document);
            document.Close();
        }

        public async Task AutoSaveOpenDocuments()
        {
            foreach (var document in OpenDocuments)
            {
                await document.AutoSaveAsync().ConfigureAwait(false);
            }
        }

        private async Task CloseCurrentDocument()
        {
            if (CurrentOpenDocument == null) return;
            await CloseDocument(CurrentOpenDocument).ConfigureAwait(false);
            if (!OpenDocuments.Any())
            {
                ClearCurrentOpenDocument();
            }
        }

        public async Task CloseAllDocuments()
        {
            // can't modify the collection while enumerating it.
            var openDocs = new ObservableCollection<OpenDocumentViewModel>(OpenDocuments);
            foreach (var document in openDocs)
            {
                await CloseDocument(document).ConfigureAwait(false);
            }
        }

        public async Task OnExit()
        {
            await AutoSaveOpenDocuments().ConfigureAwait(false);
            IOUtilities.PerformIO(() => Directory.Delete(Path.Combine(Path.GetTempPath(), "RoslynPad"), recursive: true));
        }

        public Exception? LastError
        {
            get
            {
                var exception = _telemetryProvider.LastError;
                var aggregateException = exception as AggregateException;
                return aggregateException?.Flatten() ?? exception;
            }
        }

        public bool HasError => LastError != null;

        public IDelegateCommand ClearErrorCommand { get; }

        public bool SendTelemetry
        {
            get => Settings.SendErrors; set
            {
                Settings.SendErrors = value;
                OnPropertyChanged(nameof(SendTelemetry));
            }
        }

        public bool HasNoOpenDocuments => IsInitialized && OpenDocuments.Count == 0;

        public IDelegateCommand ReportProblemCommand { get; }

        public double MinimumEditorFontSize => 8;
        public double MaximumEditorFontSize => 72;

        public double EditorFontSize
        {
            get => _editorFontSize; set
            {
                if (value < MinimumEditorFontSize || value > MaximumEditorFontSize) return;

                if (SetProperty(ref _editorFontSize, value))
                {
                    Settings.EditorFontSize = value;
                    EditorFontSizeChanged?.Invoke(value);
                }
            }
        }

        public event Action<double> EditorFontSizeChanged;

        public DocumentViewModel AddDocument(string documentName)
        {
            return DocumentRoot.CreateNew(documentName);
        }

        public string? SearchText
        {
            get => _searchText;
            set
            {
                if (SetProperty(ref _searchText, value) && Settings.SearchWhileTyping)
                {
                    SearchCommand.Execute();
                }
                OnPropertyChanged(nameof(CanClearSearch));
            }
        }

        public bool IsWithinSearchResults
        {
            get => _isWithinSearchResults;
            private set
            {
                SetProperty(ref _isWithinSearchResults, value);
                OnPropertyChanged(nameof(CanClearSearch));
            }
        }

        public bool CanClearSearch => IsWithinSearchResults || !string.IsNullOrEmpty(SearchText);

        public IDelegateCommand SearchCommand => _commands.CreateAsync(Search);

        #region Search

        private async Task Search()
        {
            if (string.IsNullOrWhiteSpace(SearchText))
            {
                ClearSearch();
                return;
            }

            if (!SearchFileContents)
            {
                IsWithinSearchResults = true;

                foreach (var document in GetAllDocumentsForSearch(DocumentRoot))
                {
                    document.IsSearchMatch = SearchDocumentName(document);
                }

                return;
            }

            Regex? regex = null;
            if (SearchUsingRegex)
            {
                regex = CreateSearchRegex();

                if (regex == null)
                {
                    return;
                }
            }

            IsWithinSearchResults = true;

            foreach (var document in GetAllDocumentsForSearch(DocumentRoot))
            {
                if (SearchDocumentName(document))
                {
                    document.IsSearchMatch = true;
                }
                else
                {
                    await SearchInFile(document, regex).ConfigureAwait(false);
                }
            }

            bool SearchDocumentName(DocumentViewModel document)
            {
                return document.Name.Contains(SearchText, StringComparison.OrdinalIgnoreCase);
            }

            Regex? CreateSearchRegex()
            {
                try
                {
                    var regex = new Regex(SearchText, RegexOptions.Multiline | RegexOptions.Singleline | RegexOptions.IgnoreCase, TimeSpan.FromSeconds(5));

                    ClearError(nameof(SearchText), "Regex");

                    return regex;
                }
                catch (ArgumentException)
                {
                    SetError(nameof(SearchText), "Regex", "Invalid regular expression");

                    return null;
                }
            }

            async Task SearchInFile(DocumentViewModel document, Regex? regex)
            {
                // a regex can span many lines so we need to load the entire file;
                // otherwise, search line-by-line

                if (regex != null)
                {
                    var documentText = await IOUtilities.ReadAllTextAsync(document.Path).ConfigureAwait(false);
                    try
                    {
                        document.IsSearchMatch = regex.IsMatch(documentText);
                    }
                    catch (RegexMatchTimeoutException)
                    {
                        document.IsSearchMatch = false;
                    }
                }
                else
                {
                    // need IAsyncEnumerable here, but for now just push it to the thread-pool
                    await Task.Run(() =>
                    {
                        var lines = IOUtilities.ReadLines(document.Path);
                        document.IsSearchMatch = lines.Any(line =>
                            line.Contains(SearchText, StringComparison.OrdinalIgnoreCase));
                    }).ConfigureAwait(false);
                }
            }
        }

        private static IEnumerable<DocumentViewModel> GetAllDocumentsForSearch(DocumentViewModel root)
        {
            foreach (var document in root.Children)
            {
                if (document.IsFolder)
                {
                    foreach (var childDocument in GetAllDocumentsForSearch(document))
                    {
                        yield return childDocument;
                    }

                    // TODO: I'm lazy :)
                    document.IsSearchMatch = document.Children.Any(c => c.IsSearchMatch);
                }
                else
                {
                    yield return document;
                }
            }
        }

        public bool SearchFileContents
        {
            get => Settings.SearchFileContents;
            set
            {
                Settings.SearchFileContents = value;
                if (!value)
                {
                    SearchUsingRegex = false;
                }
                OnPropertyChanged();
            }
        }

        public bool SearchUsingRegex
        {
            get => Settings.SearchUsingRegex;
            set
            {
                Settings.SearchUsingRegex = value;
                if (value)
                {
                    SearchFileContents = true;
                }
                OnPropertyChanged();
            }
        }

        public IDelegateCommand ClearSearchCommand => _commands.Create(ClearSearch);

        private void ClearSearch()
        {
            SearchText = null;
            IsWithinSearchResults = false;
            ClearErrors(nameof(SearchText));

            foreach (var document in GetAllDocumentsForSearch(DocumentRoot))
            {
                document.IsSearchMatch = true;
            }
        }

        #endregion

        #region Document Watcher

        private class DocumentWatcher : IDisposable
        {
            private static readonly char[] s_pathSeparators = { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar };

            private readonly DocumentViewModel _documentRoot;
            private readonly IDisposable _subscription;

            public DocumentWatcher(DocumentFileWatcher watcher, DocumentViewModel documentRoot)
            {
                _documentRoot = documentRoot;
                watcher.Path = documentRoot.Path;
                _subscription = watcher.Subscribe(OnDocumentFileChanged);
            }

            public void Dispose() => _subscription.Dispose();

            private void OnDocumentFileChanged(DocumentFileChanged data)
            {
                var pathParts = data.Path.Substring(_documentRoot.Path.Length)
                    .Split(s_pathSeparators, StringSplitOptions.RemoveEmptyEntries);

                DocumentViewModel? current = _documentRoot;

                for (var index = 0; index < pathParts.Length; index++)
                {
                    if (!current.IsChildrenInitialized)
                    {
                        break;
                    }

                    var part = pathParts[index];
                    var isLast = index == pathParts.Length - 1;

                    var parent = current;
                    current = current.InternalChildren[part];

                    // the current part is not in the tree
                    if (current == null)
                    {
                        if (data.Type != DocumentFileChangeType.Deleted)
                        {
                            var currentPath = isLast && data.Type == DocumentFileChangeType.Renamed
                                ? data.NewPath
                                : Path.Combine(_documentRoot.Path, Path.Combine(pathParts.Take(index + 1).ToArray()));

                            var newDocument = DocumentViewModel.FromPath(currentPath!);
                            if (!newDocument.IsAutoSave &&
                                IsRelevantDocument(newDocument))
                            {
                                parent.AddChild(newDocument);
                            }
                        }

                        break;
                    }

                    // it's the last part - the actual file
                    if (isLast)
                    {
                        switch (data.Type)
                        {
                            case DocumentFileChangeType.Renamed:
                                if (data.NewPath != null)
                                {
                                    current.ChangePath(data.NewPath);
                                    // move it to the correct place
                                    parent.InternalChildren.Remove(current);
                                    if (IsRelevantDocument(current))
                                    {
                                        parent.AddChild(current);
                                    }
                                }
                                break;
                            case DocumentFileChangeType.Deleted:
                                parent.InternalChildren.Remove(current);
                                break;
                        }
                    }
                }
            }
        }

        private static bool IsRelevantDocument(DocumentViewModel document)
        {
            return document.IsFolder ||
                DocumentViewModel.RelevantFileExtensions.Contains(Path.GetExtension(document.Name));
        }

        #endregion
    }
}
