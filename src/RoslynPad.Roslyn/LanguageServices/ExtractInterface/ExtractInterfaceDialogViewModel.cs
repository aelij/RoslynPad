﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Glyph = RoslynPad.Roslyn.Completion.Glyph;

namespace RoslynPad.Roslyn.LanguageServices.ExtractInterface
{
    internal class ExtractInterfaceDialogViewModel : INotifyPropertyChanged
    {
        private readonly object _syntaxFactsService;
        private readonly List<string> _conflictingTypeNames;
        private readonly string _defaultNamespace;
        private readonly string _generatedNameTypeParameterSuffix;
        private readonly string _languageName;
        private readonly string _fileExtension;

        internal ExtractInterfaceDialogViewModel(
            object syntaxFactsService,
            string defaultInterfaceName,
            List<ISymbol> extractableMembers,
            List<string> conflictingTypeNames,
            string defaultNamespace,
            string generatedNameTypeParameterSuffix,
            string languageName,
            string fileExtension)
        {
            _syntaxFactsService = syntaxFactsService;
            _interfaceName = defaultInterfaceName;
            _conflictingTypeNames = conflictingTypeNames;
            _fileExtension = fileExtension;
            _fileName = $"{defaultInterfaceName}{fileExtension}";
            _defaultNamespace = defaultNamespace;
            _generatedNameTypeParameterSuffix = generatedNameTypeParameterSuffix;
            _languageName = languageName;

            MemberContainers = extractableMembers.Select(m => new MemberSymbolViewModel(m)).OrderBy(s => s.MemberName).ToList();
        }

        public bool TrySubmit()
        {
            var trimmedInterfaceName = InterfaceName.Trim();
            var trimmedFileName = FileName.Trim();

            if (!MemberContainers.Any(c => c.IsChecked))
            {
                SendFailureNotification("YouMustSelectAtLeastOneMember");
                return false;
            }

            if (_conflictingTypeNames.Contains(trimmedInterfaceName))
            {
                SendFailureNotification("InterfaceNameConflictsWithTypeName");
                return false;
            }

            //if (!_syntaxFactsService.IsValidIdentifier(trimmedInterfaceName))
            //{
            //    SendFailureNotification($"InterfaceNameIsNotAValidIdentifier {_languageName}");
            //    return false;
            //}

            if (trimmedFileName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                SendFailureNotification("IllegalCharactersInPath");
                return false;
            }

            if (!Path.GetExtension(trimmedFileName).Equals(_fileExtension, StringComparison.OrdinalIgnoreCase))
            {
                SendFailureNotification($"FileNameMustHaveTheExtension {_fileExtension}");
                return false;
            }

            // TODO: Deal with filename already existing

            return true;
        }

        private void SendFailureNotification(string message)
        {
            //_notificationService.SendNotification(message, severity: NotificationSeverity.Information);
        }

        public void DeselectAll()
        {
            foreach (var memberContainer in MemberContainers)
            {
                memberContainer.IsChecked = false;
            }
        }

        public void SelectAll()
        {
            foreach (var memberContainer in MemberContainers)
            {
                memberContainer.IsChecked = true;
            }
        }

        public List<MemberSymbolViewModel> MemberContainers { get; set; }

        private string _interfaceName;
        public string InterfaceName
        {
            get => _interfaceName;

            set
            {
                if (SetProperty(ref _interfaceName, value))
                {
                    FileName = $"{value.Trim()}{_fileExtension}";
                    OnPropertyChanged(nameof(GeneratedName));
                }
            }
        }

        public string GeneratedName =>
            $"{(string.IsNullOrEmpty(_defaultNamespace) ? string.Empty : _defaultNamespace + ".")}{_interfaceName.Trim()}{_generatedNameTypeParameterSuffix}"
            ;

        private string _fileName;
        public string FileName
        {
            get => _fileName;
            set => SetProperty(ref _fileName, value);
        }

        public event PropertyChangedEventHandler PropertyChanged;

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (!EqualityComparer<T>.Default.Equals(field, value))
            {
                field = value;
                OnPropertyChanged(propertyName);
                return true;
            }
            return false;
        }

        protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        public class MemberSymbolViewModel : INotifyPropertyChanged
        {
            public ISymbol MemberSymbol { get; }

            private static readonly SymbolDisplayFormat _memberDisplayFormat = new SymbolDisplayFormat(
                genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
                memberOptions: SymbolDisplayMemberOptions.IncludeParameters,
                parameterOptions: SymbolDisplayParameterOptions.IncludeType | SymbolDisplayParameterOptions.IncludeParamsRefOut | SymbolDisplayParameterOptions.IncludeOptionalBrackets,
                miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers | SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

            public MemberSymbolViewModel(ISymbol symbol)
            {
                MemberSymbol = symbol;
                _isChecked = true;
            }

            private bool _isChecked;
            public bool IsChecked
            {
                get => _isChecked;
                set => SetProperty(ref _isChecked, value);
            }

            public string MemberName => MemberSymbol.ToDisplayString(_memberDisplayFormat);

            public Glyph Glyph => MemberSymbol.GetGlyph();
            
            public event PropertyChangedEventHandler PropertyChanged;

            protected virtual void OnPropertyChanged([CallerMemberName] string propertyName = null)
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
            }

            protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
            {
                if (!EqualityComparer<T>.Default.Equals(field, value))
                {
                    field = value;
                    OnPropertyChanged(propertyName);
                    return true;
                }
                return false;
            }
        }
    }
}