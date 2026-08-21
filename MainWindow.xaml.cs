using Microsoft.Win32;
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

    private LabelDocument _document = DocumentService.CreateStarterDocument();
    private LabelElement? _selected;
    private string? _currentPath;
    private bool _isDirty;
    private bool _updatingInspector;
    private bool _dragging;
    private Point _dragStart;
    private double _dragStartX;
    private double _dragStartY;

    public MainWindow()
    {
        InitializeComponent();
        LoadDocumentIntoEditor();
        Loaded += async (_, _) => await RefreshPrintersAsync();
    }

    private void LoadDocumentIntoEditor()
    {
        _selected = null;
        DocumentNameBox.Text = _document.Name;
        DocumentWidthBox.Text = FormatNumber(_document.WidthMm);
        DocumentHeightBox.Text = FormatNumber(_document.HeightMm);
        RenderDesign();
        UpdateInspector();
        UpdateTitle();
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

        _document = new LabelDocument();
        _currentPath = null;
        _isDirty = false;
        LoadDocumentIntoEditor();
        StatusText.Text = "New label created.";
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
            _document = await DocumentService.LoadAsync(dialog.FileName);
            _currentPath = dialog.FileName;
            _isDirty = false;
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

    private async void RefreshPrinters_Click(object sender, RoutedEventArgs e) => await RefreshPrintersAsync();

    private async Task RefreshPrintersAsync()
    {
        RefreshPrintersButton.IsEnabled = false;
        UseLoadedMediaButton.IsEnabled = false;
        StatusText.Text = "Searching for SUPVAN USB and Bluetooth printers…";
        var selectedId = (PrinterCombo.SelectedItem as PrinterDevice)?.Id;
        try
        {
            var usbTask = Task.Run(UsbHidDiscovery.FindSupvanPrinters);
            var bleTask = BleDiscovery.ScanAsync(TimeSpan.FromSeconds(5));
            await Task.WhenAll(usbTask, bleTask);
            var usbDevices = await usbTask;
            var bleAdvertisements = (await bleTask).Where(item => item.LooksLikeE12).ToArray();
            var items = new List<PrinterDevice>
            {
                new("mock", "Preview / mock printer", null, null, true)
            };
            items.AddRange(usbDevices);
            foreach (var advertisement in bleAdvertisements)
            {
                try
                {
                    await using var protocol = await BleProtocol.ConnectAsync(advertisement.Address);
                    var information = await protocol.ReadInformationAsync();
                    var displayName = information.ProtocolDeviceName is { Length: > 0 } model
                        ? $"{model} / E12 · Bluetooth · {information.Material.GeometryDescription}"
                        : $"{information.BluetoothName} · Bluetooth · {information.Material.GeometryDescription}";
                    items.Add(new PrinterDevice(
                        $"ble:{advertisement.Address:X12}",
                        displayName,
                        PrinterProfiles.E12,
                        null,
                        BluetoothAddress: advertisement.Address,
                        BluetoothInformation: information));
                }
                catch (Exception exception)
                {
                    StatusText.Text = $"Found Bluetooth candidate {advertisement.Name}, but configuration query failed: {exception.Message}";
                }
            }

            PrinterCombo.ItemsSource = items;
            PrinterCombo.SelectedItem = items.FirstOrDefault(item => item.Id == selectedId) ?? items[0];
            var realCount = usbDevices.Count + items.Count(item => item.IsBluetooth);
            StatusText.Text = realCount == 0
                ? "No SUPVAN printer found; mock output is ready."
                : $"Found {realCount} SUPVAN printer{(realCount == 1 ? string.Empty : "s")}.";
        }
        catch (Exception exception)
        {
            PrinterCombo.ItemsSource = new[] { new PrinterDevice("mock", "Preview / mock printer", null, null, true) };
            PrinterCombo.SelectedIndex = 0;
            StatusText.Text = "Printer discovery failed; mock output is ready.";
            MessageBox.Show(this, exception.Message, "USB discovery failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshPrintersButton.IsEnabled = true;
            UseLoadedMediaButton.IsEnabled = true;
        }
    }

    private void PrinterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PrinterCombo.SelectedItem is not PrinterDevice device)
        {
            PrinterInformationText.Text = "Select a printer to see its configuration.";
            UseLoadedMediaButton.Content = "Use loaded media size";
            UseLoadedMediaButton.Visibility = Visibility.Collapsed;
            UpdatePrintRasterPreview();
            return;
        }

        if (device.BluetoothInformation is not { } information)
        {
            PrinterInformationText.Text = device.IsMock
                ? "Renders a PNG locally and sends nothing to hardware."
                : $"{device.ConnectionDescription} · {device.Profile?.Dpi} dpi · {device.Profile?.PrintheadDots} dots";
            UseLoadedMediaButton.Content = "Use loaded media size";
            UseLoadedMediaButton.Visibility = Visibility.Collapsed;
            RenderDesign();
            return;
        }

        var material = information.Material;
        var resolution = information.DotsPerMillimeter is double dpmm
            ? $"{dpmm:0.##} dots/mm ({information.Dpi:0.#} dpi)"
            : "resolution not returned";
        var blockingErrors = information.Status.BlockingErrors(ignoreDirectThermalRibbonEnd: true);
        var status = blockingErrors.Count > 0
            ? string.Join(", ", blockingErrors)
            : information.Status.RibbonEnd
                ? "ready · direct-thermal ribbon flag ignored"
                : "ready";
        PrinterInformationText.Text =
            $"Model {information.ProtocolDeviceName ?? "unknown"} · FW {information.FirmwareVersion?.ToString() ?? "?"} · revision raw {information.ProtocolRevisionRawHex ?? "?"}\n" +
            $"Loaded: {material.GeometryDescription} · editor {(material.IsContinuous ? $"variable length × {material.WidthMm} mm" : $"{material.HeightMm} × {material.WidthMm} mm")} · raw type {material.LabelType}\n" +
            $"Firmware counter {material.FirmwareCounter?.ToString() ?? "?"} (meaning unverified) · {resolution} · status {status}";
        UseLoadedMediaButton.Content = material.IsContinuous
            ? "Use loaded tape width (keep length)"
            : "Use loaded media size";
        UseLoadedMediaButton.Visibility = material.HasPlausibleGeometry ? Visibility.Visible : Visibility.Collapsed;
        RenderDesign();
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
        if (PrinterCombo.SelectedItem is not PrinterDevice
            {
                Profile: { } profile,
                BluetoothInformation: { DotsPerMillimeter: double dotsPerMillimeter } information
            })
        {
            return null;
        }

        var feedMarginMm = SupvanRasterEncoder.PageMarginDots / dotsPerMillimeter;
        var printheadWidthMm = profile.PrintheadDots / dotsPerMillimeter;
        var tapeEdgeMarginMm = Math.Max(feedMarginMm, (information.Material.WidthMm - printheadWidthMm) / 2);
        return (feedMarginMm, tapeEdgeMarginMm);
    }

    private void UpdatePrintRasterPreview()
    {
        if (!IsInitialized || PrintRasterPreview is null)
        {
            return;
        }

        try
        {
            var dpi = PrinterCombo.SelectedItem is PrinterDevice { BluetoothInformation.Dpi: double liveDpi }
                ? (int)Math.Round(liveDpi)
                : (PrinterCombo.SelectedItem as PrinterDevice)?.Profile?.Dpi ?? 203;
            PrintRasterPreview.Source = LabelRenderer.RenderMonochromePreview(_document, dpi);
            if (PrinterCombo.SelectedItem is PrinterDevice { BluetoothInformation.DotsPerMillimeter: double dotsPerMillimeter } &&
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
        if (PrinterCombo.SelectedItem is not PrinterDevice { BluetoothInformation.Material: { HasPlausibleGeometry: true } material })
        {
            return;
        }

        if (!material.IsContinuous)
        {
            _document.WidthMm = material.HeightMm;
        }
        _document.HeightMm = material.WidthMm;

        foreach (var element in _document.Elements)
        {
            element.WidthMm = Math.Min(element.WidthMm, _document.WidthMm);
            element.HeightMm = Math.Min(element.HeightMm, _document.HeightMm);
            element.XMm = Math.Min(element.XMm, Math.Max(0, _document.WidthMm - element.WidthMm));
            element.YMm = Math.Min(element.YMm, Math.Max(0, _document.HeightMm - element.HeightMm));
        }

        DocumentWidthBox.Text = FormatNumber(_document.WidthMm);
        DocumentHeightBox.Text = FormatNumber(_document.HeightMm);
        MarkDirty();
        RenderDesign();
        UpdateInspector();
        StatusText.Text = material.IsContinuous
            ? $"Tape width set to {material.WidthMm} mm; design length remains {_document.WidthMm:0.##} mm."
            : $"Design resized to {material.HeightMm} × {material.WidthMm} mm using printer-reported {material.GeometryDescription}.";
    }

    private async void Print_Click(object sender, RoutedEventArgs e)
    {
        if (PrinterCombo.SelectedItem is not PrinterDevice device)
        {
            MessageBox.Show(this, "Choose a printer first.", "Print label", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (!device.IsSupported)
        {
            MessageBox.Show(this, "This USB model is visible, but Etikra has no verified profile for its PID. No data was sent.", "Unsupported printer", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!device.IsMock)
        {
            var connection = device.IsBluetooth ? "Bluetooth" : "USB";
            var media = device.BluetoothInformation?.Material.GeometryDescription;
            var answer = MessageBox.Show(
                this,
                $"Send this label directly to {device.DisplayName} over {connection}?" +
                (media is null ? string.Empty : $"\n\nThe printer currently reports {media}; Etikra will query it again before sending raster data.") +
                $"\n\nClose the cover and keep the printer connected. This backend is based on independent reverse engineering.",
                $"Direct {connection} print",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);
            if (answer != MessageBoxResult.OK)
            {
                return;
            }
        }

        PrintButton.IsEnabled = false;
        RefreshPrintersButton.IsEnabled = false;
        try
        {
            var progress = new Progress<string>(message => StatusText.Text = message);
            var backend = PrinterBackendFactory.Create(device);
            var result = await backend.PrintAsync(_document, (byte)DensitySlider.Value, progress, CancellationToken.None);
            StatusText.Text = result;
            if (device.IsMock)
            {
                MessageBox.Show(this, $"Mock print saved to:\n{result}", "Print complete", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception exception)
        {
            StatusText.Text = "Print failed.";
            MessageBox.Show(this, exception.Message, "Print failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            PrintButton.IsEnabled = true;
            RefreshPrintersButton.IsEnabled = true;
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
        if (!_isDirty)
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
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        Title = $"{(_isDirty ? "• " : string.Empty)}{_document.Name} — Etikra";
        DocumentNameBox.Text = _document.Name;
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
