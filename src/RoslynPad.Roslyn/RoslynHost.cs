﻿using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition.Convention;
using System.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Host;
using Microsoft.CodeAnalysis.Host.Mef;
using Microsoft.CodeAnalysis.Shared.Utilities;
using Microsoft.CodeAnalysis.Text;
using RoslynPad.Roslyn.Diagnostics;

namespace RoslynPad.Roslyn
{
    public sealed class RoslynHost
    {
        #region Fields

        private static readonly ImmutableArray<Type> _defaultReferenceAssemblyTypes = new[] {
            typeof(object),
            typeof(Thread),
            typeof(Task),
            typeof(List<>),
            typeof(Regex),
            typeof(StringBuilder),
            typeof(Uri),
            typeof(Enumerable),
            typeof(IEnumerable),
            typeof(Path),
            typeof(Assembly),
            Type.GetType("RoslynPad.Runtime.ObjectExtensions, RoslynPad.Common", throwOnError: true),
        }.ToImmutableArray();

        private static readonly ImmutableArray<Assembly> _defaultReferenceAssemblies =
            _defaultReferenceAssemblyTypes.Select(x => x.Assembly).Distinct().Concat(new[]
            {
                Assembly.Load("System.Runtime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"),
                typeof(Microsoft.CSharp.RuntimeBinder.Binder).Assembly,
            })
            .ToImmutableArray();

        internal static readonly ImmutableArray<string> PreprocessorSymbols = ImmutableArray.CreateRange(new[] { "__DEMO__", "__DEMO_EXPERIMENTAL__", "TRACE", "DEBUG" });

        private readonly NuGetConfiguration _nuGetConfiguration;
        private readonly ConcurrentDictionary<DocumentId, RoslynWorkspace> _workspaces;
        private readonly ConcurrentDictionary<DocumentId, Action<DiagnosticsUpdatedArgs>> _diagnosticsUpdatedNotifiers;
        private readonly CSharpParseOptions _parseOptions;
        private readonly DocumentationProviderServiceFactory.DocumentationProviderService _documentationProviderService;
        private readonly string _referenceAssembliesPath;
        private readonly CompositionHost _compositionContext;
        private readonly MefHostServices _host;

        private int _documentNumber;

        public ImmutableArray<MetadataReference> DefaultReferences { get; }

        public ImmutableArray<string> DefaultImports { get; }

        #endregion

        #region Constructors

        public RoslynHost(NuGetConfiguration nuGetConfiguration = null, IEnumerable<Assembly> additionalAssemblies = null)
        {
            _nuGetConfiguration = nuGetConfiguration;

            _workspaces = new ConcurrentDictionary<DocumentId, RoslynWorkspace>();
            _diagnosticsUpdatedNotifiers = new ConcurrentDictionary<DocumentId, Action<DiagnosticsUpdatedArgs>>();

            var assemblies = new[]
            {
                Assembly.Load("Microsoft.CodeAnalysis"),
                Assembly.Load("Microsoft.CodeAnalysis.CSharp"),
                Assembly.Load("Microsoft.CodeAnalysis.Features"),
                Assembly.Load("Microsoft.CodeAnalysis.CSharp.Features"),
                typeof(RoslynHost).Assembly,
            };

            if (additionalAssemblies != null)
            {
                assemblies = assemblies.Concat(additionalAssemblies).ToArray();
            }

            var partTypes = MefHostServices.DefaultAssemblies.Concat(assemblies)
                    .Distinct()
                    .SelectMany(x => x.GetTypes())
                    .Concat(new[] { typeof(DocumentationProviderServiceFactory) })
                    .ToArray();

            _compositionContext = new ContainerConfiguration()
                .WithParts(partTypes)
                .WithDefaultConventions(new AttributeFilterProvider())
                .CreateContainer();

            _host = MefHostServices.Create(_compositionContext);

            _parseOptions = new CSharpParseOptions(kind: SourceCodeKind.Script, preprocessorSymbols: PreprocessorSymbols);

            _referenceAssembliesPath = GetReferenceAssembliesPath();
            _documentationProviderService = new DocumentationProviderServiceFactory.DocumentationProviderService();

            DefaultReferences = GetMetadataReferences();

            DefaultImports = _defaultReferenceAssemblyTypes.Select(x => x.Namespace).Distinct().ToImmutableArray();

            GetService<IDiagnosticService>().DiagnosticsUpdated += OnDiagnosticsUpdated;
        }

        private ImmutableArray<MetadataReference> GetMetadataReferences()
        {
            // allow facade assemblies to take precedence
            var dictionary = _defaultReferenceAssemblies.Select(x => x.Location)
                .ToImmutableDictionary(Path.GetFileNameWithoutExtension)
                .SetItems(TryGetFacadeAssemblies()
                    .ToImmutableDictionary(Path.GetFileNameWithoutExtension));

            var metadataReferences = dictionary.Values
                .Select(CreateMetadataReference)
                .ToImmutableArray();

            return metadataReferences;
        }

        internal MetadataReference CreateMetadataReference(string location)
        {
            return MetadataReference.CreateFromFile(location, documentation: GetDocumentationProvider(location));
        }

        private void OnDiagnosticsUpdated(object sender, DiagnosticsUpdatedArgs diagnosticsUpdatedArgs)
        {
            var documentId = diagnosticsUpdatedArgs?.DocumentId;
            if (documentId == null) return;

            OnOpenedDocumentSyntaxChanged(GetDocument(documentId));

            Action<DiagnosticsUpdatedArgs> notifier;
            if (_diagnosticsUpdatedNotifiers.TryGetValue(documentId, out notifier))
            {
                notifier(diagnosticsUpdatedArgs);
            }
        }

        private class AttributeFilterProvider : AttributedModelProvider
        {
            public override IEnumerable<Attribute> GetCustomAttributes(Type reflectedType, MemberInfo member)
            {
                var customAttributes = member.GetCustomAttributes().Where(x => !(x is ExtensionOrderAttribute)).ToArray();
                return customAttributes;
            }

            public override IEnumerable<Attribute> GetCustomAttributes(Type reflectedType, ParameterInfo member)
            {
                var customAttributes = member.GetCustomAttributes().Where(x => !(x is ExtensionOrderAttribute)).ToArray();
                return customAttributes;
            }
        }

        private async void OnOpenedDocumentSyntaxChanged(Document document)
        {
            RoslynWorkspace workspace;
            if (_workspaces.TryGetValue(document.Id, out workspace))
            {
                await workspace.ProcessReferenceDirectives(document).ConfigureAwait(false);
            }
        }

        public TService GetService<TService>()
        {
            return _compositionContext.GetExport<TService>();
        }

        #endregion

        #region Reference Resolution

        internal void AddMetadataReference(ProjectId projectId, AssemblyIdentity assemblyIdentity)
        {
            // TODO
        }

        public bool HasReference(DocumentId documentId, string text)
        {
            RoslynWorkspace workspace;
            if (_workspaces.TryGetValue(documentId, out workspace) && workspace.HasReference(text))
            {
                return true;
            }
            return _defaultReferenceAssemblies.Any(x => x.GetName().Name == text);
        }

        private static MetadataReferenceResolver CreateMetadataReferenceResolver(Workspace workspace, string workingDirectory)
        {
            var resolver = Activator.CreateInstance(
                // can't access this type due to a name collision with Scripting assembly
                // can't use extern alias because of project.json
                // ReSharper disable once AssignNullToNotNullAttribute
                Type.GetType("Microsoft.CodeAnalysis.RelativePathResolver, Microsoft.CodeAnalysis.Workspaces"),
                ImmutableArray<string>.Empty,
                workingDirectory);
            return (MetadataReferenceResolver)Activator.CreateInstance(typeof(WorkspaceMetadataFileReferenceResolver),
                workspace.Services.GetService<IMetadataService>(),
                resolver);
        }

        #endregion

        #region Documentation

        private static string GetReferenceAssembliesPath()
        {
            var programFiles =
                Environment.GetFolderPath(Environment.Is64BitOperatingSystem
                    ? Environment.SpecialFolder.ProgramFilesX86
                    : Environment.SpecialFolder.ProgramFiles);
            var path = Path.Combine(programFiles, @"Reference Assemblies\Microsoft\Framework\.NETFramework");
            if (Directory.Exists(path))
            {
                var directories = IOUtilities.PerformIO(() => Directory.GetDirectories(path), Array.Empty<string>())
                    .Select(x => new { path = x, version = GetFxVersionFromPath(x) })
                    .OrderByDescending(x => x.version);

                var directory = directories
                                    .FirstOrDefault(x => File.Exists(Path.Combine(x.path, "System.dll")) &&
                                                         File.Exists(Path.Combine(x.path, "System.xml"))) ??
                                directories
                                    .FirstOrDefault(x => File.Exists(Path.Combine(x.path, "System.dll")));

                return directory?.path;
            }
            return null;
        }

        private static Version GetFxVersionFromPath(string path)
        {
            var name = Path.GetFileName(path);
            if (name?.StartsWith("v", StringComparison.OrdinalIgnoreCase) == true)
            {
                if (Version.TryParse(name.Substring(1), out var version))
                {
                    return version;
                }
            }

            return new Version(0, 0, 0);
        }

        private IEnumerable<string> TryGetFacadeAssemblies()
        {
            if (_referenceAssembliesPath != null)
            {
                var facadesPath = Path.Combine(_referenceAssembliesPath, "Facades");
                if (Directory.Exists(facadesPath))
                {
                    return Directory.EnumerateFiles(facadesPath, "*.dll");
                }
            }

            return Array.Empty<string>();
        }

        private DocumentationProvider GetDocumentationProvider(string location)
        {
            if (File.Exists(Path.ChangeExtension(location, "xml")))
            {
                return _documentationProviderService.GetDocumentationProvider(location);
            }
            if (_referenceAssembliesPath != null)
            {
                // ReSharper disable once AssignNullToNotNullAttribute
                var referenceLocation = Path.Combine(_referenceAssembliesPath, Path.GetFileName(location));
                if (File.Exists(Path.ChangeExtension(referenceLocation, "xml")))
                {
                    return _documentationProviderService.GetDocumentationProvider(referenceLocation);
                }
            }
            return null;
        }

        #endregion

        #region Documents

        public void CloseDocument(DocumentId documentId)
        {
            RoslynWorkspace workspace;
            if (_workspaces.TryGetValue(documentId, out workspace))
            {
                DiagnosticProvider.Disable(workspace);
                workspace.Dispose();
                _workspaces.TryRemove(documentId, out workspace);
            }
            Action<DiagnosticsUpdatedArgs> notifier;
            _diagnosticsUpdatedNotifiers.TryRemove(documentId, out notifier);
        }

        public Document GetDocument(DocumentId documentId)
        {
            RoslynWorkspace workspace;
            return _workspaces.TryGetValue(documentId, out workspace) ? workspace.CurrentSolution.GetDocument(documentId) : null;
        }

        public DocumentId AddDocument(SourceTextContainer sourceTextContainer, string workingDirectory, Action<DiagnosticsUpdatedArgs> onDiagnosticsUpdated, Action<SourceText> onTextUpdated)
        {
            if (sourceTextContainer == null) throw new ArgumentNullException(nameof(sourceTextContainer));

            var workspace = new RoslynWorkspace(_host, _nuGetConfiguration, this);
            if (onTextUpdated != null)
            {
                workspace.ApplyingTextChange += (d, s) => onTextUpdated(s);
            }

            DiagnosticProvider.Enable(workspace, DiagnosticProvider.Options.Semantic);

            var currentSolution = workspace.CurrentSolution;
            var project = CreateSubmissionProject(currentSolution, CreateCompilationOptions(workspace, workingDirectory));
            var currentDocument = SetSubmissionDocument(workspace, sourceTextContainer, project);

            _workspaces.TryAdd(currentDocument.Id, workspace);

            if (onDiagnosticsUpdated != null)
            {
                _diagnosticsUpdatedNotifiers.TryAdd(currentDocument.Id, onDiagnosticsUpdated);
            }

            return currentDocument.Id;
        }

        public void UpdateDocument(Document document)
        {
            RoslynWorkspace workspace;
            if (!_workspaces.TryGetValue(document.Id, out workspace))
            {
                return;
            }

            workspace.TryApplyChanges(document.Project.Solution);
        }

        public ImmutableArray<string> GetReferencesDirectives(DocumentId documentId)
        {
            RoslynWorkspace workspace;
            if (_workspaces.TryGetValue(documentId, out workspace))
            {
                return workspace.ReferencesDirectives;
            }
            return ImmutableArray<string>.Empty;
        }

        private CSharpCompilationOptions CreateCompilationOptions(Workspace workspace, string workingDirectory)
        {
            var metadataReferenceResolver = CreateMetadataReferenceResolver(workspace, workingDirectory);
            var compilationOptions = new CSharpCompilationOptions(OutputKind.NetModule,
                usings: DefaultImports,
                allowUnsafe: true,
                sourceReferenceResolver: new SourceFileResolver(ImmutableArray<string>.Empty, workingDirectory),
                metadataReferenceResolver: metadataReferenceResolver);
            return compilationOptions;
        }

        private static Document SetSubmissionDocument(RoslynWorkspace workspace, SourceTextContainer textContainer, Project project)
        {
            var id = DocumentId.CreateNewId(project.Id);
            var solution = project.Solution.AddDocument(id, project.Name, textContainer.CurrentText);
            workspace.SetCurrentSolution(solution);
            workspace.OpenDocument(id, textContainer);
            return solution.GetDocument(id);
        }

        private Project CreateSubmissionProject(Solution solution, CSharpCompilationOptions compilationOptions)
        {
            var name = "Program" + _documentNumber++;
            var id = ProjectId.CreateNewId(name);
            solution = solution.AddProject(ProjectInfo.Create(id, VersionStamp.Create(), name, name, LanguageNames.CSharp,
                parseOptions: _parseOptions,
                compilationOptions: compilationOptions.WithScriptClassName(name),
                metadataReferences: DefaultReferences));
            return solution.GetProject(id);
        }

        #endregion
    }
}
