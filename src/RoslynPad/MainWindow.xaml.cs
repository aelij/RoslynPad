﻿using System;
using System.ComponentModel;
using System.Composition.Hosting;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Xml.Linq;
using Avalon.Windows.Controls;
using Microsoft.Practices.ServiceLocation;
using RoslynPad.UI;
using RoslynPad.Utilities;
using Xceed.Wpf.AvalonDock;
using Xceed.Wpf.AvalonDock.Layout.Serialization;

namespace RoslynPad
{
    /// <summary>
    /// Interaction logic for MainWindow.xaml
    /// </summary>
    public partial class MainWindow
    {
        private readonly MainViewModel _viewModel;
        private bool _isClosing;
        private bool _isClosed;

        internal MainWindow()
        {
            var container = new ContainerConfiguration()
                .WithAssembly(typeof(MainViewModel).Assembly)   // RoslynPad.Common.UI
                .WithAssembly(typeof(MainWindow).Assembly);     // RoslynPad
            var locator = container.CreateContainer().GetExport<IServiceLocator>();

            _viewModel = locator.GetInstance<MainViewModel>();
            _viewModel.Initialize();

            DataContext = _viewModel;
            InitializeComponent();
            DocumentsPane.ToggleAutoHide();

            LoadWindowLayout();
            LoadDockLayout();
        }

        protected override async void OnClosing(CancelEventArgs e)
        {
            base.OnClosing(e);

            if (!_isClosing)
            {
                SaveDockLayout();
                SaveWindowLayout();
                Properties.Settings.Default.Save();

                _isClosing = true;
                IsEnabled = false;
                e.Cancel = true;

                try
                {
                    await Task.Run(() => _viewModel.OnExit()).ConfigureAwait(true);
                }
                catch
                {
                    // ignored
                }

                _isClosed = true;
                Close();
            }
            else
            {
                e.Cancel = !_isClosed;
            }
        }

        private void LoadWindowLayout()
        {
            var boundsString = Properties.Settings.Default.WindowBounds;
            if (!string.IsNullOrEmpty(boundsString))
            {
                var bounds = Rect.Parse(boundsString);
                if (bounds != default(Rect))
                {
                    Left = bounds.Left;
                    Top = bounds.Top;
                    Width = bounds.Width;
                    Height = bounds.Height;
                }
            }

            WindowState state;
            if (Enum.TryParse(Properties.Settings.Default.WindowState, out state) &&
                state != WindowState.Minimized)
            {
                WindowState = state;
            }
        }

        private void SaveWindowLayout()
        {
            Properties.Settings.Default.WindowBounds = RestoreBounds.ToString(CultureInfo.InvariantCulture);
            Properties.Settings.Default.WindowState = WindowState.ToString();
        }

        private void LoadDockLayout()
        {
            var layout = Properties.Settings.Default.DockLayout;
            if (string.IsNullOrEmpty(layout)) return;

            var serializer = new XmlLayoutSerializer(DockingManager);
            var reader = new StringReader(layout);
            serializer.Deserialize(reader);
        }

        private void SaveDockLayout()
        {
            var serializer = new XmlLayoutSerializer(DockingManager);
            var document = new XDocument();
            using (var writer = document.CreateWriter())
            {
                serializer.Serialize(writer);
            }
            document.Root?.Element("FloatingWindows")?.Remove();
            Properties.Settings.Default.DockLayout = document.ToString();
        }

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);

            Application.Current.Shutdown();
        }

        private void OnDocumentClick(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Left)
            {
                OpenDocument(e.Source);
            }
        }

        private void OnDocumentKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                OpenDocument(e.Source);
            }
        }

        private void OpenDocument(object source)
        {
            var documentViewModel = (DocumentViewModel)((FrameworkElement)source).DataContext;
            _viewModel.OpenDocument(documentViewModel);
        }

        private async void DockingManager_OnDocumentClosing(object sender, DocumentClosingEventArgs e)
        {
            e.Cancel = true;
            var document = (OpenDocumentViewModel)e.Document.Content;
            await _viewModel.CloseDocument(document).ConfigureAwait(false);
        }

        private void ViewErrorDetails_OnClick(object sender, RoutedEventArgs e)
        {
            if (!_viewModel.HasError) return;

            TaskDialog.ShowInline(this, "Unhandled Exception",
                _viewModel.LastError.ToAsyncString(), string.Empty, TaskDialogButtons.Close);
        }

        private void ViewUpdateClick(object sender, RoutedEventArgs e)
        {
            Task.Run(() => Process.Start("https://roslynpad.net/"));
        }

        private void DocumentsContextMenu_OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            var documentViewModel = ((FrameworkElement)e.Source).DataContext as DocumentViewModel;
            if (documentViewModel != null)
            {
                if (documentViewModel.IsFolder)
                {
                    Task.Run(() => Process.Start(documentViewModel.Path));
                }
                else
                {
                    Task.Run(() => Process.Start("explorer.exe", "/select," + documentViewModel.Path));
                }
            }
        }
    }
}