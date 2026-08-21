using Microsoft.Win32;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Etikra.Models;
using Etikra.Printing;
using Etikra.Printing.Bluetooth;
using Etikra.Services;

namespace Etikra;

public partial class MainWindow : Window
{
    private const double PreviewPixelsPerMm = 10;
    private static readonly Brush SelectionBrush = new SolidColorBrush(Color.FromRgb(113, 87, 232));

    private readonly PrinterSessionManager _printerSession = new();
    private readonly SettingsService _settingsService = new();
    private readonly CancellationTokenSource _windowLifetime = new();
    private EtikraSettings _settings = new();
    private LabelDocument? _currentDocument;
    private LabelDocument _document => _currentDocument ?? throw new InvalidOperationException("No label is open.");
    private LabelElement? _selected;
    private string? _currentPath;
    private bool _isDirty;
    private bool _updatingInspector;
    private bool _dragging;
    private Point _dragStart;
    private double _dragStartX;
    private double _dragStartY;
    private bool _documentPristine;
    private bool _documentCreatedFromMedia;
    private DateTimeOffset? _deactivatedAt;
    private bool _closeConfirmed;
    private bool _isPrinting;

    public MainWindow()
    {
        InitializeComponent();
        ShowEmptyWorkspace();
        _printerSession.StateChanged += PrinterSession_StateChanged;
        Loaded += MainWindow_Loaded;
        Activated += MainWindow_Activated;
        Deactivated += (_, _) => _deactivatedAt = DateTimeOffset.Now;
    }

    private void LoadDocumentIntoEditor()
    {
        EmptyWorkspacePanel.Visibility = Visibility.Collapsed;
        CanvasScrollViewer.Visibility = Visibility.Visible;
        NoDocumentToolsHint.Visibility = Visibility.Collapsed;
        EditorToolsPanel.Visibility = Visibility.Visible;
        SaveButton.IsEnabled = true;
        ExportButton.IsEnabled = true;
        MockPrintButton.IsEnabled = true;
        _selected = null;
        DocumentNameBox.Text = _document.Name;
        DocumentWidthBox.Text = FormatNumber(_document.WidthMm);
        DocumentHeightBox.Text = FormatNumber(_document.HeightMm);
        RenderDesign();
        UpdateInspector();
        UpdateTitle();
        UpdateDocumentMediaBindingText();
        UpdateReadiness();
    }

    private void ShowEmptyWorkspace()
    {
        _currentDocument = null;
        _selected = null;
        _currentPath = null;
        _isDirty = false;
        _documentPristine = false;
        _documentCreatedFromMedia = false;
        EmptyWorkspacePanel.Visibility = Visibility.Visible;
        CanvasScrollViewer.Visibility = Visibility.Collapsed;
        NoDocumentToolsHint.Visibility = Visibility.Visible;
        EditorToolsPanel.Visibility = Visibility.Collapsed;
        SaveButton.IsEnabled = false;
        ExportButton.IsEnabled = false;
        MockPrintButton.IsEnabled = false;
        DuplicateButton.IsEnabled = false;
        DeleteButton.IsEnabled = false;
        PrintRasterPreview.Source = null;
        PrintRasterPreviewCaption.Text = "Create a label to see its thermal-dot preview.";
        DocumentStatusText.Text = "No label";
        Title = "Etikra";
        UpdateInspector();
        UpdateReadiness();
    }

    private void RenderDesign()
    {
        var width = _document.WidthMm * PreviewPixelsPerMm;
        var height = _document.HeightMm * PreviewPixelsPerMm;
        DesignCanvas.Width = width;
        DesignCanvas.Height = height;
        LabelSurface.Width = width + 2;
        LabelSurface.Height = height + 2;
        DesignCanvas.Children.Clear();

        foreach (var element in _document.Elements)
        {
            var root = CreateElementVisual(element);
            Canvas.SetLeft(root, element.XMm * PreviewPixelsPerMm);
            Canvas.SetTop(root, element.YMm * PreviewPixelsPerMm);
            DesignCanvas.Children.Add(root);
        }

        AddPrintSafeAreaGuide();
        UpdatePrintRasterPreview();

        DocumentStatusText.Text = $"{_document.SizeDescription}  ·  {_document.Elements.Count} element{(_document.Elements.Count == 1 ? string.Empty : "s")}";
    }

    private FrameworkElement CreateElementVisual(LabelElement element)
    {
        var root = new Grid
        {
            Width = Math.Max(5, element.WidthMm * PreviewPixelsPerMm),
            Height = Math.Max(5, element.HeightMm * PreviewPixelsPerMm),
            Background = Brushes.Transparent,
            Tag = element,
            Cursor = Cursors.SizeAll,
            RenderTransformOrigin = new Point(0.5, 0.5),
            RenderTransform = new RotateTransform(element.Rotation)
        };

        root.Children.Add(CreateElementContent(element));
        root.Children.Add(new Border
        {
            BorderBrush = ReferenceEquals(element, _selected) ? SelectionBrush : Brushes.Transparent,
            BorderThickness = new Thickness(ReferenceEquals(element, _selected) ? 1.5 : 0),
            Margin = new Thickness(-2),
            IsHitTestVisible = false
        });

        if (ReferenceEquals(element, _selected))
        {
            var thumb = new Thumb
            {
                Width = 11,
                Height = 11,
                Background = SelectionBrush,
                BorderBrush = Brushes.White,
                BorderThickness = new Thickness(1),
                Cursor = Cursors.SizeNWSE,
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Bottom,
                Margin = new Thickness(0, 0, -6, -6)
            };
            thumb.DragDelta += (_, args) => ResizeElement(root, element, args.HorizontalChange, args.VerticalChange);
            thumb.DragCompleted += (_, _) =>
            {
                MarkDirty();
                RenderDesign();
            };
            root.Children.Add(thumb);
        }

        root.MouseLeftButtonDown += Element_MouseLeftButtonDown;
        root.MouseMove += Element_MouseMove;
        root.MouseLeftButtonUp += Element_MouseLeftButtonUp;
        return root;
    }

    private FrameworkElement CreateElementContent(LabelElement element)
    {
        switch (element.Kind)
        {
            case LabelElementKind.Text:
                return new TextBlock
                {
                    Text = element.Content,
                    FontFamily = new FontFamily(element.FontFamily),
                    FontWeight = element.Bold ? FontWeights.Bold : FontWeights.Normal,
                    FontSize = element.FontSizePt * PreviewPixelsPerMm / 2.834645669,
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    VerticalAlignment = VerticalAlignment.Center
                };

            case LabelElementKind.Barcode:
                return CreateBarcodePreview(element.Content);

            case LabelElementKind.Rectangle:
                return new Border
                {
                    BorderBrush = Brushes.Black,
                    BorderThickness = new Thickness(Math.Max(1, element.StrokeThicknessMm * PreviewPixelsPerMm))
                };

            case LabelElementKind.Line:
                return new Border
                {
                    Height = Math.Max(1, element.StrokeThicknessMm * PreviewPixelsPerMm),
                    Background = Brushes.Black,
                    VerticalAlignment = VerticalAlignment.Center
                };

            case LabelElementKind.Image:
                return CreateImagePreview(element.ImageData);

            default:
                return new Border();
        }
    }

    private static FrameworkElement CreateBarcodePreview(string content)
    {
        var canvas = new Canvas { ClipToBounds = true };
        canvas.SizeChanged += (_, _) =>
        {
            canvas.Children.Clear();
            var runs = Code128Encoder.GetRuns(content);
            var total = runs.Sum(run => run.Modules);
            var x = 0d;
            foreach (var run in runs)
            {
                var width = canvas.ActualWidth * run.Modules / total;
                if (run.IsBar)
                {
                    var bar = new Rectangle { Width = Math.Max(0.5, width), Height = canvas.ActualHeight, Fill = Brushes.Black };
                    Canvas.SetLeft(bar, x);
                    canvas.Children.Add(bar);
                }

                x += width;
            }
        };
        return canvas;
    }

    private static FrameworkElement CreateImagePreview(string? imageData)
    {
        if (string.IsNullOrWhiteSpace(imageData))
        {
            return new TextBlock { Text = "Image", Foreground = Brushes.Gray, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        }

        try
        {
            var image = new BitmapImage();
            using var stream = new MemoryStream(Convert.FromBase64String(imageData));
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return new Image { Source = image, Stretch = Stretch.Uniform };
        }
        catch
        {
            return new TextBlock { Text = "Invalid image", Foreground = Brushes.Gray };
        }
    }

    private void Element_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Grid root || root.Tag is not LabelElement element || e.OriginalSource is Thumb)
        {
            return;
        }

        if (!ReferenceEquals(_selected, element))
        {
            _selected = element;
            RenderDesign();
            UpdateInspector();
            root = DesignCanvas.Children.OfType<Grid>().First(item => ReferenceEquals(item.Tag, element));
        }

        _dragging = true;
        _dragStart = e.GetPosition(DesignCanvas);
        _dragStartX = element.XMm;
        _dragStartY = element.YMm;
        root.CaptureMouse();
        e.Handled = true;
    }

    private void Element_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging || e.LeftButton != MouseButtonState.Pressed || sender is not Grid root || root.Tag is not LabelElement element)
        {
            return;
        }

        var point = e.GetPosition(DesignCanvas);
        var snap = Keyboard.Modifiers.HasFlag(ModifierKeys.Control) ? 0.1 : 0.5;
        element.XMm = ClampToDocument(Snap(_dragStartX + (point.X - _dragStart.X) / PreviewPixelsPerMm, snap), 0, _document.WidthMm - element.WidthMm);
        element.YMm = ClampToDocument(Snap(_dragStartY + (point.Y - _dragStart.Y) / PreviewPixelsPerMm, snap), 0, _document.HeightMm - element.HeightMm);
        Canvas.SetLeft(root, element.XMm * PreviewPixelsPerMm);
        Canvas.SetTop(root, element.YMm * PreviewPixelsPerMm);
        UpdateInspectorValues();
    }

    private void Element_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging || sender is not Grid root)
        {
            return;
        }

        _dragging = false;
        root.ReleaseMouseCapture();
        MarkDirty();
        RenderDesign();
        e.Handled = true;
    }

    private void ResizeElement(Grid root, LabelElement element, double horizontal, double vertical)
    {
        element.WidthMm = Math.Min(_document.WidthMm - element.XMm, Math.Max(1, element.WidthMm + horizontal / PreviewPixelsPerMm));
        element.HeightMm = Math.Min(_document.HeightMm - element.YMm, Math.Max(1, element.HeightMm + vertical / PreviewPixelsPerMm));
        root.Width = element.WidthMm * PreviewPixelsPerMm;
        root.Height = element.HeightMm * PreviewPixelsPerMm;
        UpdateInspectorValues();
    }

    private void DesignCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ReferenceEquals(e.OriginalSource, DesignCanvas))
        {
            _selected = null;
            RenderDesign();
            UpdateInspector();
        }
    }

    private async void New_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        ShowEmptyWorkspace();
        StatusText.Text = "Choose installed media or a custom size for the new label.";
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "Open an Etikra label",
            Filter = "Etikra labels (*.etikra)|*.etikra|JSON files (*.json)|*.json|All files (*.*)|*.*"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            _currentDocument = await DocumentService.LoadAsync(dialog.FileName);
            _currentPath = dialog.FileName;
            _isDirty = false;
            _documentPristine = false;
            _documentCreatedFromMedia = _document.MediaRequirement is not null;
            LoadDocumentIntoEditor();
            StatusText.Text = $"Opened {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not open label", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveDocumentAsync();

    private async Task<bool> SaveDocumentAsync()
    {
        if (_currentDocument is null)
        {
            return false;
        }

        if (_currentPath is null)
        {
            var dialog = new SaveFileDialog
            {
                Title = "Save Etikra label",
                Filter = "Etikra labels (*.etikra)|*.etikra",
                FileName = SafeFileName(_document.Name) + ".etikra",
                AddExtension = true,
                DefaultExt = ".etikra"
            };
            if (dialog.ShowDialog(this) != true)
            {
                return false;
            }

            _currentPath = dialog.FileName;
        }

        try
        {
            await DocumentService.SaveAsync(_document, _currentPath);
            _isDirty = false;
            UpdateTitle();
            StatusText.Text = $"Saved {System.IO.Path.GetFileName(_currentPath)}.";
            return true;
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not save label", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void Export_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocument is null)
        {
            return;
        }

        var dialog = new SaveFileDialog
        {
            Title = "Export print-ready PNG",
            Filter = "PNG image (*.png)|*.png",
            FileName = SafeFileName(_document.Name) + ".png",
            AddExtension = true,
            DefaultExt = ".png"
        };
        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        try
        {
            LabelRenderer.SavePng(_document, dialog.FileName, 300);
            StatusText.Text = $"Exported 300 DPI PNG to {System.IO.Path.GetFileName(dialog.FileName)}.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not export label", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void AddElement_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string tag } || !Enum.TryParse<LabelElementKind>(tag, out var kind))
        {
            return;
        }

        var element = CreateDefaultElement(kind);
        if (kind == LabelElementKind.Image)
        {
            var dialog = new OpenFileDialog { Title = "Choose an image", Filter = "Images (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp" };
            if (dialog.ShowDialog(this) != true)
            {
                return;
            }

            try
            {
                element.ImageData = Convert.ToBase64String(File.ReadAllBytes(dialog.FileName));
            }
            catch (Exception exception)
            {
                MessageBox.Show(this, exception.Message, "Could not import image", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
        }

        _document.Elements.Add(element);
        _selected = element;
        MarkDirty();
        RenderDesign();
        UpdateInspector();
    }

    private LabelElement CreateDefaultElement(LabelElementKind kind)
    {
        var element = new LabelElement
        {
            Kind = kind,
            XMm = 3,
            YMm = 3,
            WidthMm = Math.Min(30, _document.WidthMm - 6),
            HeightMm = kind switch
            {
                LabelElementKind.Barcode => 10,
                LabelElementKind.Line => 1,
                LabelElementKind.Rectangle => 12,
                LabelElementKind.Image => 14,
                _ => 8
            },
            Content = kind == LabelElementKind.Barcode ? "ETIKRA-001" : "Label text"
        };
        element.HeightMm = Math.Min(element.HeightMm, _document.HeightMm - 6);
        return element;
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null)
        {
            return;
        }

        var clone = _selected.Clone();
        clone.XMm = Math.Min(clone.XMm, _document.WidthMm - clone.WidthMm);
        clone.YMm = Math.Min(clone.YMm, _document.HeightMm - clone.HeightMm);
        _document.Elements.Add(clone);
        _selected = clone;
        MarkDirty();
        RenderDesign();
        UpdateInspector();
    }

    private void Delete_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        if (_selected is null)
        {
            return;
        }

        _document.Elements.Remove(_selected);
        _selected = null;
        MarkDirty();
        RenderDesign();
        UpdateInspector();
    }

    private void DocumentField_LostFocus(object sender, RoutedEventArgs e)
    {
        _document.Name = string.IsNullOrWhiteSpace(DocumentNameBox.Text) ? "Untitled label" : DocumentNameBox.Text.Trim();
        if (TryReadNumber(DocumentWidthBox.Text, out var width)) _document.WidthMm = width;
        if (TryReadNumber(DocumentHeightBox.Text, out var height)) _document.HeightMm = height;
        if (_document.MediaRequirement is { } requirement &&
            (Math.Abs(_document.HeightMm - requirement.TapeWidthMm) > 0.1 ||
             (requirement.Kind == LabelMediaKind.Fixed &&
              requirement.FixedLengthMm is double fixedLength &&
              Math.Abs(_document.WidthMm - fixedLength) > 0.1)))
        {
            _document.MediaRequirement = null;
            _documentCreatedFromMedia = false;
        }
        DocumentWidthBox.Text = FormatNumber(_document.WidthMm);
        DocumentHeightBox.Text = FormatNumber(_document.HeightMm);

        foreach (var element in _document.Elements)
        {
            element.XMm = Math.Min(element.XMm, Math.Max(0, _document.WidthMm - element.WidthMm));
            element.YMm = Math.Min(element.YMm, Math.Max(0, _document.HeightMm - element.HeightMm));
        }

        MarkDirty();
        RenderDesign();
    }

    private void ElementField_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selected is null)
        {
            return;
        }

        _selected.Content = ElementContentBox.Text;
        if (TryReadNumber(ElementXBox.Text, out var x)) _selected.XMm = ClampToDocument(x, 0, _document.WidthMm - _selected.WidthMm);
        if (TryReadNumber(ElementYBox.Text, out var y)) _selected.YMm = ClampToDocument(y, 0, _document.HeightMm - _selected.HeightMm);
        if (TryReadNumber(ElementWidthBox.Text, out var width)) _selected.WidthMm = Math.Min(width, _document.WidthMm - _selected.XMm);
        if (TryReadNumber(ElementHeightBox.Text, out var height)) _selected.HeightMm = Math.Min(height, _document.HeightMm - _selected.YMm);
        if (TryReadNumber(ElementRotationBox.Text, out var rotation)) _selected.Rotation = rotation;
        if (TryReadNumber(ElementStrokeBox.Text, out var stroke)) _selected.StrokeThicknessMm = stroke;
        if (TryReadNumber(ElementFontSizeBox.Text, out var fontSize)) _selected.FontSizePt = fontSize;
        MarkDirty();
        RenderDesign();
        UpdateInspectorValues();
    }

    private void ElementBold_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selected is null)
        {
            return;
        }

        _selected.Bold = ElementBoldBox.IsChecked == true;
        MarkDirty();
        RenderDesign();
    }

    private void UpdateInspector()
    {
        var hasSelection = _selected is not null;
        ElementInspector.IsEnabled = hasSelection;
        ElementInspector.Visibility = hasSelection ? Visibility.Visible : Visibility.Collapsed;
        NoSelectionHint.Visibility = hasSelection ? Visibility.Collapsed : Visibility.Visible;
        DuplicateButton.IsEnabled = hasSelection;
        DeleteButton.IsEnabled = hasSelection;
        if (hasSelection)
        {
            UpdateInspectorValues();
        }
    }

    private void UpdateInspectorValues()
    {
        if (_selected is null)
        {
            return;
        }

        _updatingInspector = true;
        ElementKindText.Text = _selected.Kind.ToString();
        ElementContentBox.Text = _selected.Content;
        ElementXBox.Text = FormatNumber(_selected.XMm);
        ElementYBox.Text = FormatNumber(_selected.YMm);
        ElementWidthBox.Text = FormatNumber(_selected.WidthMm);
        ElementHeightBox.Text = FormatNumber(_selected.HeightMm);
        ElementRotationBox.Text = FormatNumber(_selected.Rotation);
        ElementStrokeBox.Text = FormatNumber(_selected.StrokeThicknessMm);
        ElementFontSizeBox.Text = FormatNumber(_selected.FontSizePt);
        ElementBoldBox.IsChecked = _selected.Bold;
        ContentFieldPanel.Visibility = _selected.Kind is LabelElementKind.Text or LabelElementKind.Barcode ? Visibility.Visible : Visibility.Collapsed;
        FontFieldPanel.Visibility = _selected.Kind == LabelElementKind.Text ? Visibility.Visible : Visibility.Collapsed;
        StrokeFieldPanel.Visibility = _selected.Kind is LabelElementKind.Rectangle or LabelElementKind.Line ? Visibility.Visible : Visibility.Collapsed;
        _updatingInspector = false;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        _settings = await _settingsService.LoadAsync(_windowLifetime.Token);
        DensitySlider.Value = _settings.Density;
        if (_settings.LastPrinter is { } remembered)
        {
            StatusText.Text = $"Reconnecting to {remembered.DisplayName}…";
            try
            {
                await ConnectPrinterAsync(remembered.ToCandidate());
            }
            catch
            {
                StatusText.Text = "Remembered printer is unavailable. Editing remains available offline.";
            }
        }
        else
        {
            await ScanForPrintersAsync();
        }
    }

    private async void MainWindow_Activated(object? sender, EventArgs e)
    {
        if (_deactivatedAt is not DateTimeOffset deactivated || DateTimeOffset.Now - deactivated < TimeSpan.FromSeconds(5))
        {
            return;
        }

        _deactivatedAt = null;
        if (_printerSession.ConnectionState == PrinterConnectionState.Ready &&
            _printerSession.ActivePrinter?.Transport == PrinterTransport.BluetoothLe)
        {
            try
            {
                await _printerSession.RefreshAsync(_windowLifetime.Token);
            }
            catch
            {
                // The live cards expose the error and keep offline editing available.
            }
        }
    }

    private void PrinterSession_StateChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() =>
            {
                UpdatePrinterPanels();
                HandleAutomaticMediaChange();
            });
            return;
        }

        UpdatePrinterPanels();
        HandleAutomaticMediaChange();
    }

    private async void FindPrinter_Click(object sender, RoutedEventArgs e) => await ScanForPrintersAsync();

    private async Task ScanForPrintersAsync()
    {
        PrinterCandidatesPanel.Visibility = Visibility.Visible;
        PrinterScanStatusText.Text = "Searching USB and Bluetooth for 5 seconds…";
        ConnectSelectedPrinterButton.IsEnabled = false;
        try
        {
            var candidates = await _printerSession.ScanAsync(TimeSpan.FromSeconds(5), _windowLifetime.Token);
            PrinterCandidatesList.ItemsSource = candidates;
            PrinterCandidatesList.SelectedIndex = candidates.Count > 0 ? 0 : -1;
            PrinterScanStatusText.Text = candidates.Count == 0
                ? "No SUPVAN/KATASYMBOL label maker found."
                : $"Found {candidates.Count} label maker{(candidates.Count == 1 ? string.Empty : "s")}.";
            ConnectSelectedPrinterButton.IsEnabled = candidates.Count > 0;
            StatusText.Text = PrinterScanStatusText.Text;
        }
        catch (OperationCanceledException)
        {
            PrinterScanStatusText.Text = "Search cancelled.";
        }
        catch (Exception exception)
        {
            PrinterScanStatusText.Text = exception.Message;
            StatusText.Text = "Printer search failed.";
        }
    }

    private async void ConnectSelectedPrinter_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterCandidatesList.SelectedItem is PrinterCandidate candidate)
        {
            await TryConnectPrinterAsync(candidate);
        }
    }

    private async void RetryPrinter_Click(object sender, RoutedEventArgs e)
    {
        if (_printerSession.ActivePrinter is { } candidate)
        {
            await TryConnectPrinterAsync(candidate);
        }
    }

    private async Task TryConnectPrinterAsync(PrinterCandidate candidate)
    {
        ConnectSelectedPrinterButton.IsEnabled = false;
        RetryPrinterButton.IsEnabled = false;
        try
        {
            await ConnectPrinterAsync(candidate);
            PrinterCandidatesPanel.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Printer connection cancelled.";
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Could not connect", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            ConnectSelectedPrinterButton.IsEnabled = PrinterCandidatesList.Items.Count > 0;
            RetryPrinterButton.IsEnabled = true;
        }
    }

    private async Task ConnectPrinterAsync(PrinterCandidate candidate)
    {
        StatusText.Text = $"Connecting to {candidate.DisplayName}…";
        await _printerSession.ConnectAsync(candidate, _windowLifetime.Token);
        _settings.LastPrinter = RememberedPrinter.FromCandidate(candidate);
        await _settingsService.SaveAsync(_settings, _windowLifetime.Token);
        StatusText.Text = $"Connected to {candidate.DisplayName}.";
        UpdatePrinterPanels();
    }

    private async void DisconnectPrinter_Click(object sender, RoutedEventArgs e)
    {
        await _printerSession.DisconnectAsync();
        StatusText.Text = "Label maker disconnected; cached media was cleared.";
    }

    private async void ForgetPrinter_Click(object sender, RoutedEventArgs e)
    {
        await _printerSession.DisconnectAsync(forget: true);
        _settings.LastPrinter = null;
        await _settingsService.SaveAsync(_settings, _windowLifetime.Token);
        StatusText.Text = "Forgot the remembered label maker.";
        UpdatePrinterPanels();
    }

    private async void RefreshMedia_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _printerSession.RefreshAsync(_windowLifetime.Token);
            StatusText.Text = "Printer health and installed media refreshed.";
            HandleAutomaticMediaChange();
        }
        catch (Exception exception)
        {
            StatusText.Text = "Media refresh failed.";
            MessageBox.Show(this, exception.Message, "Media refresh failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void UpdatePrinterPanels()
    {
        var candidate = _printerSession.ActivePrinter;
        var information = _printerSession.DeviceInformation;
        var health = _printerSession.Health;
        var media = _printerSession.Media;

        PrinterConnectionText.Text = _printerSession.ConnectionState switch
        {
            PrinterConnectionState.Scanning => "Searching…",
            PrinterConnectionState.Connecting => "Connecting…",
            PrinterConnectionState.Reading => "Reading printer…",
            PrinterConnectionState.Ready => "Connected",
            PrinterConnectionState.Faulted => "Connection problem",
            _ => "Disconnected"
        };
        PrinterIdentityText.Text = candidate is null
            ? "No label maker selected."
            : information is null
                ? $"{candidate.DisplayName} · {candidate.TransportDescription}"
                : $"{information.ProtocolModel ?? candidate.DisplayName} · {candidate.TransportDescription}\n" +
                  $"FW {information.FirmwareVersion?.ToString() ?? "?"} · {information.DotsPerMillimeter?.ToString("0.##") ?? "?"} dots/mm · " +
                  $"{information.PrintheadDots?.ToString() ?? "?"} head dots";

        if (candidate?.Transport == PrinterTransport.UsbHid)
        {
            PrinterHealthText.Text = "Live USB health interrogation is unavailable. Direct printing remains experimental.";
        }
        else if (health is null)
        {
            PrinterHealthText.Text = _printerSession.LastError ?? "Connect the printer to read hardware health.";
        }
        else
        {
            var hardwareState = new List<string>();
            if (health.CoverOpen) hardwareState.Add("cover open");
            if (health.LowBattery) hardwareState.Add("low battery");
            if (health.PrintheadTooHot) hardwareState.Add("printhead too hot");
            if (health.IsPrinting) hardwareState.Add("printing");
            else if (health.IsBusy) hardwareState.Add("busy");
            PrinterHealthText.Text = hardwareState.Count == 0
                ? $"Hardware ready · print count {health.PrintCount?.ToString() ?? "?"} · checked {health.ReadAt:T}"
                : $"Hardware: {string.Join(", ", hardwareState)} · checked {health.ReadAt:T}";
        }

        MediaStateText.Text = media.State switch
        {
            MediaReadState.Reading => "Reading…",
            MediaReadState.Ready => "Installed",
            MediaReadState.Absent => "No media",
            MediaReadState.Unsupported => "Unsupported media",
            MediaReadState.Faulted => "Media read failed",
            _ => candidate?.Transport == PrinterTransport.UsbHid ? "Unverified" : "Unknown"
        };
        if (candidate?.Transport == PrinterTransport.UsbHid)
        {
            MediaInformationText.Text = "Etikra cannot yet interrogate installed USB media. Enter dimensions manually and review the warning before printing.";
        }
        else if (media.Material is not { } material)
        {
            MediaInformationText.Text = media.Error ?? "No current installed-media information. Stale readings are never retained.";
        }
        else
        {
            var printableWidth = information?.PrintheadWidthMm;
            var tapeEdgeMargin = printableWidth is double headWidth ? Math.Max(0, (material.WidthMm - headWidth) / 2) : 0;
            MediaInformationText.Text =
                $"{(material.IsContinuous ? "Continuous tape" : "Fixed-size labels")} · {material.GeometryDescription}\n" +
                $"Printable width {printableWidth?.ToString("0.#") ?? "?"} mm" +
                (tapeEdgeMargin > 0 ? $" · {tapeEdgeMargin:0.#} mm tape-edge margins" : string.Empty) +
                $"\nLabel serial {material.LabelSerial} · read {media.ReadAt:T}" +
                (_currentDocument is not null && !MediaCompatibility.IsCompatible(_document.MediaRequirement, material)
                    ? "\nCurrent label is not bound to compatible media. Use the action below to resolve it."
                    : string.Empty);
        }

        var hasReadyMedia = media is { State: MediaReadState.Ready, Material: not null };
        RefreshMediaButton.IsEnabled = !_isPrinting && _printerSession.ConnectionState == PrinterConnectionState.Ready &&
                                       candidate?.Transport == PrinterTransport.BluetoothLe;
        UseInstalledMediaButton.IsEnabled = hasReadyMedia;
        EmptyUseMediaButton.IsEnabled = hasReadyMedia;
        if (media.Material is { } buttonMaterial)
        {
            var buttonText = buttonMaterial.IsContinuous ? "Use tape width / bind label" : "Use installed media / bind label";
            UseInstalledMediaButton.Content = buttonText;
            EmptyUseMediaButton.Content = buttonText;
            EmptyWorkspaceMessage.Text = $"Installed: {buttonMaterial.GeometryDescription}. Create a blank label from it or choose another size.";
        }
        else
        {
            UseInstalledMediaButton.Content = "Use installed media";
            EmptyUseMediaButton.Content = "Use installed media";
            EmptyWorkspaceMessage.Text = "No label is open. Connect a label maker, use a custom size, or open an existing file.";
        }

        RetryPrinterButton.Visibility = candidate is not null && _printerSession.ConnectionState is PrinterConnectionState.Faulted or PrinterConnectionState.Disconnected
            ? Visibility.Visible : Visibility.Collapsed;
        DisconnectPrinterButton.Visibility = candidate is not null && _printerSession.ConnectionState is PrinterConnectionState.Connecting or PrinterConnectionState.Ready or PrinterConnectionState.Reading
            ? Visibility.Visible : Visibility.Collapsed;
        ForgetPrinterButton.Visibility = candidate is not null ? Visibility.Visible : Visibility.Collapsed;

        DiagnosticsText.Text = candidate is null
            ? "No live diagnostics."
            : $"id: {candidate.Id}\naddress: {(candidate.BluetoothAddress is ulong address ? BleDiscovery.FormatAddress(address) : "n/a")}\n" +
              $"ATT MTU: {information?.AttMtu.ToString() ?? "n/a"}\nwrite: {information?.CommandWriteMode ?? "n/a"}\n" +
              $"media raw type: {media.Material?.LabelType.ToString() ?? "n/a"}\n" +
              $"status raw: {health?.RawStatus?.ToString() ?? "n/a"}\n" +
              $"material raw: {media.Material?.RawHex ?? "n/a"}";

        if (_currentDocument is not null)
        {
            RenderDesign();
        }
        UpdateReadiness();
    }

    private void HandleAutomaticMediaChange()
    {
        if (_currentDocument is null || !_documentCreatedFromMedia || _printerSession.Media.Material is not { } material ||
            !DocumentMediaAdapter.CanAutoAdapt(_document, _documentPristine, material))
        {
            return;
        }

        ApplyMediaToDocument(material, preserveContinuousLength: true);
        _documentPristine = true;
        StatusText.Text = "Untouched blank label adapted to the newly installed media.";
    }

    private void UpdateDocumentMediaBindingText()
    {
        if (_currentDocument is null)
        {
            return;
        }

        DocumentMediaBindingText.Text = _document.MediaRequirement switch
        {
            { Kind: LabelMediaKind.Continuous, TapeWidthMm: var width } => $"Bound to {width:0.#} mm continuous tape · length is document-controlled",
            { Kind: LabelMediaKind.Fixed, TapeWidthMm: var width, FixedLengthMm: var length, GapMm: var gap } =>
                $"Bound to {width:0.#} × {length:0.#} mm fixed media · {gap:0.#} mm gap",
            _ => "Custom/unbound label · bind compatible installed media before Bluetooth printing"
        };
    }

    private void UpdateReadiness()
    {
        if (!IsInitialized || ReadinessText is null)
        {
            return;
        }

        var readiness = PrintSafety.Evaluate(
            _currentDocument,
            _printerSession.ActivePrinter,
            _printerSession.ConnectionState,
            _printerSession.Health,
            _printerSession.Media,
            _printerSession.DeviceInformation);
        ReadinessText.Text = string.Join("\n", readiness.Checks.Select(check =>
            $"{(check.Level == ReadinessLevel.Ready ? "✓" : check.Level == ReadinessLevel.Warning ? "⚠" : "●")} {check.Name} — {check.Message}"));
        PrintButton.IsEnabled = readiness.CanPrint && !_isPrinting;
    }

    private void AddPrintSafeAreaGuide()
    {
        if (GetPrintSafeMargins() is not { } margins)
        {
            return;
        }

        var width = Math.Max(0, (_document.WidthMm - margins.HorizontalMm * 2) * PreviewPixelsPerMm);
        var height = Math.Max(0, (_document.HeightMm - margins.VerticalMm * 2) * PreviewPixelsPerMm);
        var guide = new Rectangle
        {
            Width = width,
            Height = height,
            Stroke = new SolidColorBrush(Color.FromRgb(224, 126, 40)),
            StrokeThickness = 1,
            StrokeDashArray = [4, 3],
            IsHitTestVisible = false,
            ToolTip = $"E12 print-safe boundary ({margins.HorizontalMm:0.#} mm feed ends, {margins.VerticalMm:0.#} mm tape edges)"
        };
        Canvas.SetLeft(guide, margins.HorizontalMm * PreviewPixelsPerMm);
        Canvas.SetTop(guide, margins.VerticalMm * PreviewPixelsPerMm);
        Panel.SetZIndex(guide, int.MaxValue);
        DesignCanvas.Children.Add(guide);
    }

    private (double HorizontalMm, double VerticalMm)? GetPrintSafeMargins()
    {
        if (_currentDocument is null ||
            _printerSession.ActivePrinter is not { Profile: { } profile } ||
            _printerSession.DeviceInformation?.DotsPerMillimeter is not double dotsPerMillimeter)
        {
            return null;
        }

        return PrintSafety.GetE12Margins(_document, profile, dotsPerMillimeter);
    }

    private void UpdatePrintRasterPreview()
    {
        if (!IsInitialized || PrintRasterPreview is null)
        {
            return;
        }

        try
        {
            if (_currentDocument is null)
            {
                PrintRasterPreview.Source = null;
                PrintRasterPreviewCaption.Text = "Create a label to see its thermal-dot preview.";
                return;
            }

            var dpi = _printerSession.DeviceInformation?.Dpi is double liveDpi
                ? (int)Math.Round(liveDpi)
                : _printerSession.ActivePrinter?.Profile?.Dpi ?? 203;
            PrintRasterPreview.Source = LabelRenderer.RenderMonochromePreview(_document, dpi);
            if (_printerSession.DeviceInformation?.DotsPerMillimeter is double dotsPerMillimeter &&
                GetPrintSafeMargins() is { } margins)
            {
                PrintRasterPreviewCaption.Text =
                    $"Exact {dotsPerMillimeter:0.##} dots/mm threshold preview. Dashed safe area: " +
                    $"{margins.HorizontalMm:0.#} mm feed ends, {margins.VerticalMm:0.#} mm tape edges.";
            }
            else
            {
                PrintRasterPreviewCaption.Text = $"Monochrome {dpi} dpi threshold preview.";
            }
        }
        catch
        {
            PrintRasterPreview.Source = null;
            PrintRasterPreviewCaption.Text = "Raster preview unavailable for the current design.";
        }
    }

    private void UseLoadedMedia_Click(object sender, RoutedEventArgs e)
    {
        if (_printerSession.Media is not { State: MediaReadState.Ready, Material: { } material })
        {
            return;
        }

        if (_currentDocument is null)
        {
            var length = material.IsContinuous && TryReadNumber(NewLabelLengthBox.Text, out var requestedLength)
                ? requestedLength
                : material.HeightMm;
            _currentDocument = new LabelDocument
            {
                Name = "Untitled label",
                WidthMm = length,
                HeightMm = material.WidthMm,
                MediaRequirement = MediaCompatibility.ToRequirement(material)
            };
            _currentPath = null;
            _isDirty = true;
            _documentPristine = true;
            _documentCreatedFromMedia = true;
            LoadDocumentIntoEditor();
            StatusText.Text = $"Created blank label from {material.GeometryDescription}.";
            return;
        }

        if (MediaCompatibility.IsCompatible(_document.MediaRequirement, material) &&
            Math.Abs(_document.HeightMm - material.WidthMm) <= 0.1 &&
            (material.IsContinuous || Math.Abs(_document.WidthMm - material.HeightMm) <= 0.1))
        {
            StatusText.Text = "This label already matches the installed media.";
            return;
        }

        var geometryMatches = Math.Abs(_document.HeightMm - material.WidthMm) <= 0.1 &&
                              (material.IsContinuous || Math.Abs(_document.WidthMm - material.HeightMm) <= 0.1);
        if (_document.MediaRequirement is null && geometryMatches)
        {
            _document.MediaRequirement = MediaCompatibility.ToRequirement(material);
            _documentCreatedFromMedia = true;
            MarkDirty();
            UpdateDocumentMediaBindingText();
            StatusText.Text = "Bound the current label to compatible installed media.";
            return;
        }

        if (DocumentMediaAdapter.CanAutoAdapt(_document, _documentPristine, material))
        {
            ApplyMediaToDocument(material, preserveContinuousLength: true);
            _documentPristine = true;
            _documentCreatedFromMedia = true;
            StatusText.Text = "Untouched blank label adapted to installed media.";
            return;
        }

        var answer = MessageBox.Show(
            this,
            "The installed media differs from this label.\n\nYes: resize and rebind the current label (elements are clamped into the physical canvas).\nNo: replace it with a new blank label.\nCancel: keep the current incompatible label.",
            "Installed media changed",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Warning);
        if (answer == MessageBoxResult.Cancel)
        {
            return;
        }

        if (answer == MessageBoxResult.No)
        {
            _currentDocument = new LabelDocument
            {
                Name = "Untitled label",
                WidthMm = material.IsContinuous && TryReadNumber(NewLabelLengthBox.Text, out var requestedLength) ? requestedLength : material.HeightMm,
                HeightMm = material.WidthMm,
                MediaRequirement = MediaCompatibility.ToRequirement(material)
            };
            _currentPath = null;
            _documentPristine = true;
            _documentCreatedFromMedia = true;
        }
        else
        {
            ApplyMediaToDocument(material, preserveContinuousLength: true);
            _documentPristine = false;
            _documentCreatedFromMedia = true;
        }

        _isDirty = true;
        LoadDocumentIntoEditor();
        StatusText.Text = answer == MessageBoxResult.No
            ? "Created a new blank label for the installed media."
            : "Resized and rebound the current label; review artwork and safe-area warnings.";
    }

    private void ApplyMediaToDocument(BleMaterialReport material, bool preserveContinuousLength)
    {
        if (_currentDocument is null)
        {
            return;
        }

        DocumentMediaAdapter.ResizeAndBind(_document, material, preserveContinuousLength);

        DocumentWidthBox.Text = FormatNumber(_document.WidthMm);
        DocumentHeightBox.Text = FormatNumber(_document.HeightMm);
        MarkDirty();
        RenderDesign();
        UpdateInspector();
        UpdateDocumentMediaBindingText();
    }

    private async void CreateCustomLabel_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadNumber(NewLabelLengthBox.Text, out var length) ||
            !TryReadNumber(NewLabelWidthBox.Text, out var width))
        {
            MessageBox.Show(this, "Enter valid label dimensions in millimetres.", "Custom label", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        _currentDocument = new LabelDocument { Name = "Untitled label", WidthMm = length, HeightMm = width };
        _currentPath = null;
        _isDirty = true;
        _documentPristine = true;
        _documentCreatedFromMedia = false;
        LoadDocumentIntoEditor();
        StatusText.Text = "Created an unbound custom-size label.";
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocument is null || _printerSession.ActivePrinter is not { } printer)
        {
            MessageBox.Show(this, "Create a label and connect a printer first.", "Print label", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var readiness = PrintSafety.Evaluate(
            _document,
            printer,
            _printerSession.ConnectionState,
            _printerSession.Health,
            _printerSession.Media,
            _printerSession.DeviceInformation);
        if (!readiness.CanPrint)
        {
            var reason = readiness.Checks.First(check => check.Level == ReadinessLevel.Blocking);
            MessageBox.Show(this, $"{reason.Name}: {reason.Message}", "Print is not ready", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var isUsb = printer.Transport == PrinterTransport.UsbHid;
        var confirmation = MessageBox.Show(
            this,
            isUsb
                ? $"Send this label directly to {printer.DisplayName} over experimental USB HID?\n\nEtikra cannot interrogate installed USB media. Confirm that the manually entered dimensions match the loaded stock."
                : $"Send this label directly to {printer.DisplayName} over Bluetooth?\n\nEtikra will re-read health and installed media on this same connection before sending any raster bytes.",
            isUsb ? "Experimental USB print" : "Bluetooth print",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Warning);
        if (confirmation != MessageBoxResult.OK)
        {
            return;
        }

        _isPrinting = true;
        PrintButton.IsEnabled = false;
        RefreshMediaButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var printSnapshot = DocumentService.CreateSnapshot(_document);
            var result = await _printerSession.PrintAsync(printSnapshot, (byte)DensitySlider.Value, progress, _windowLifetime.Token);
            StatusText.Text = result;
        }
        catch (Exception exception)
        {
            StatusText.Text = "Print failed.";
            MessageBox.Show(this, exception.Message, "Print failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _isPrinting = false;
            UpdatePrinterPanels();
        }
    }

    private async void MockPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_currentDocument is null)
        {
            return;
        }

        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var path = await new MockPrinterBackend().PrintAsync(DocumentService.CreateSnapshot(_document), (byte)DensitySlider.Value, progress, _windowLifetime.Token);
            MessageBox.Show(this, $"Mock print saved to:\n{path}", "Mock print complete", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception exception)
        {
            MessageBox.Show(this, exception.Message, "Mock print failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private async void DensitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded)
        {
            return;
        }

        _settings.Density = (byte)e.NewValue;
        try
        {
            await _settingsService.SaveAsync(_settings, _windowLifetime.Token);
        }
        catch
        {
            // A density preference failure must not interrupt editing or printing.
        }
    }

    private async void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            switch (e.Key)
            {
                case Key.S:
                    await SaveDocumentAsync();
                    e.Handled = true;
                    return;
                case Key.O:
                    Open_Click(sender, e);
                    e.Handled = true;
                    return;
                case Key.N:
                    New_Click(sender, e);
                    e.Handled = true;
                    return;
                case Key.D when _selected is not null:
                    Duplicate_Click(sender, e);
                    e.Handled = true;
                    return;
            }
        }

        if (e.Key is Key.Delete or Key.Back && _selected is not null && !IsTextInputFocused())
        {
            DeleteSelection();
            e.Handled = true;
            return;
        }

        if (_selected is not null && e.Key is Key.Left or Key.Right or Key.Up or Key.Down && !IsTextInputFocused())
        {
            var step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 1d : 0.5;
            if (e.Key == Key.Left) _selected.XMm = ClampToDocument(_selected.XMm - step, 0, _document.WidthMm - _selected.WidthMm);
            if (e.Key == Key.Right) _selected.XMm = ClampToDocument(_selected.XMm + step, 0, _document.WidthMm - _selected.WidthMm);
            if (e.Key == Key.Up) _selected.YMm = ClampToDocument(_selected.YMm - step, 0, _document.HeightMm - _selected.HeightMm);
            if (e.Key == Key.Down) _selected.YMm = ClampToDocument(_selected.YMm + step, 0, _document.HeightMm - _selected.HeightMm);
            MarkDirty();
            RenderDesign();
            UpdateInspectorValues();
            e.Handled = true;
        }
    }

    private async Task<bool> ConfirmDiscardAsync()
    {
        if (_currentDocument is null || !_isDirty)
        {
            return true;
        }

        var answer = MessageBox.Show(this, "Save the current label before continuing?", "Unsaved label", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        return answer switch
        {
            MessageBoxResult.Yes => await SaveDocumentAsync(),
            MessageBoxResult.No => true,
            _ => false
        };
    }

    private void MarkDirty()
    {
        _isDirty = true;
        _documentPristine = false;
        UpdateTitle();
        UpdateDocumentMediaBindingText();
        UpdateReadiness();
    }

    private void UpdateTitle()
    {
        if (_currentDocument is null)
        {
            Title = "Etikra";
            return;
        }

        Title = $"{(_isDirty ? "• " : string.Empty)}{_document.Name} — Etikra";
        DocumentNameBox.Text = _document.Name;
    }

    private async void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (_closeConfirmed)
        {
            return;
        }

        e.Cancel = true;
        if (!await ConfirmDiscardAsync())
        {
            return;
        }

        _closeConfirmed = true;
        _windowLifetime.Cancel();
        _printerSession.StateChanged -= PrinterSession_StateChanged;
        await _printerSession.DisposeAsync();
        Close();
    }

    private static bool TryReadNumber(string text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out value) ||
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value);

    private static string FormatNumber(double value) => value.ToString("0.##", CultureInfo.CurrentCulture);
    private static double Snap(double value, double step) => Math.Round(value / step) * step;
    private static double ClampToDocument(double value, double min, double max) => Math.Clamp(value, min, Math.Max(min, max));
    private static string SafeFileName(string value) => string.Concat(value.Select(ch => System.IO.Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
    private static bool IsTextInputFocused() => Keyboard.FocusedElement is TextBoxBase;
}
